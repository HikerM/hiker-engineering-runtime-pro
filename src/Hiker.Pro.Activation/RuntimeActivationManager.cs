using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Activation;

public sealed class RuntimeActivationManager(string hikerHome, IRuntimePackageProbe probe)
{
    private const int MaxFiles = 10_000;
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly RuntimePointerStore pointerStore = new(hikerHome);

    public async Task<RuntimeActivationResult> ActivateAsync(
        VerifiedRuntimePackage package,
        CancellationToken cancellationToken = default)
    {
        ValidatePackageFacts(package);
        var current = await pointerStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        ValidateCompatibility(current, package);
        var files = EnumerateBoundedFiles(package.StagingDirectory);
        await VerifyHashesAsync(package, files, cancellationToken).ConfigureAwait(false);
        var executable = Path.Combine(Path.GetFullPath(package.StagingDirectory), "hiker.exe");
        var probeResult = await probe.ProbeAsync(executable, cancellationToken).ConfigureAwait(false);
        ValidateProbe(package.Identity, probeResult);

        var runtimesRoot = Path.Combine(pointerStore.Home, "runtimes");
        Directory.CreateDirectory(runtimesRoot);
        var target = RuntimePathPolicy.ResolveWithin(runtimesRoot, package.Identity.RuntimeIdentityValue);
        if (Directory.Exists(target))
        {
            var existing = await ReadIdentityAsync(target, cancellationToken).ConfigureAwait(false);
            if (!Equals(existing, package.Identity)) throw new IOException("Immutable runtime directory already contains different content");
        }
        else
        {
            var promote = Path.Combine(runtimesRoot, $".staging-{Guid.NewGuid():N}");
            try
            {
                CopyVerifiedFiles(package.StagingDirectory, promote, files);
                await WriteIdentityAsync(promote, package.Identity, cancellationToken).ConfigureAwait(false);
                Directory.Move(promote, target);
            }
            finally
            {
                if (Directory.Exists(promote)) Directory.Delete(promote, recursive: true);
            }
        }

        var pointer = new ActiveRuntimePointer(
            "hiker-active-runtime/v1",
            package.Identity.RuntimeIdentityValue,
            current?.ActiveRuntime,
            package.Identity.RuntimeIdentityValue,
            package.Identity.ProductVersion,
            package.Identity.RuntimeApiVersion,
            package.Identity.StateSchemaVersion,
            package.Identity.LiveAdoptionProtocol,
            package.Identity.BuildCommit,
            (current?.InstallGeneration ?? 0) + 1,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            package.Identity.Channel);
        if (current is not null && string.Equals(current.ActiveRuntime, pointer.ActiveRuntime, StringComparison.Ordinal))
            return new RuntimeActivationResult("ALREADY_ACTIVE", current, target, files.Count, false);
        await pointerStore.WriteAtomicAsync(pointer, cancellationToken).ConfigureAwait(false);
        return new RuntimeActivationResult("ACTIVATED", pointer, target, files.Count, true);
    }

    public async Task<RuntimeActivationResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        var current = await pointerStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active runtime is configured");
        if (current.PreviousRuntime is null) throw new InvalidOperationException("No previous verified runtime is available");
        var previousRoot = RuntimePathPolicy.ResolveWithin(Path.Combine(pointerStore.Home, "runtimes"), current.PreviousRuntime);
        var identity = await ReadIdentityAsync(previousRoot, cancellationToken).ConfigureAwait(false);
        var rollback = new ActiveRuntimePointer(
            "hiker-active-runtime/v1",
            current.PreviousRuntime,
            current.ActiveRuntime,
            identity.RuntimeIdentityValue,
            identity.ProductVersion,
            identity.RuntimeApiVersion,
            identity.StateSchemaVersion,
            identity.LiveAdoptionProtocol,
            identity.BuildCommit,
            current.InstallGeneration + 1,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            identity.Channel);
        await pointerStore.WriteAtomicAsync(rollback, cancellationToken).ConfigureAwait(false);
        return new RuntimeActivationResult("ROLLED_BACK", rollback, previousRoot, 0, true);
    }

    private static void ValidatePackageFacts(VerifiedRuntimePackage package)
    {
        if (!string.Equals(package.PackageVerificationStatus, "VERIFIED", StringComparison.Ordinal) ||
            !string.Equals(package.EntitlementVerificationStatus, "VERIFIED", StringComparison.Ordinal))
            throw new InvalidDataException("Unverified runtime package cannot be activated");
        if (!RuntimePathPolicy.IsSafeToken(package.Identity.RuntimeIdentityValue) ||
            package.Identity.BuildCommit.Length != 40 || !package.Identity.BuildCommit.All(Uri.IsHexDigit))
            throw new InvalidDataException("Runtime identity is invalid");
        var staging = new DirectoryInfo(Path.GetFullPath(package.StagingDirectory));
        if (!staging.Exists || (staging.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Runtime staging directory is missing or unsafe");
    }

    private static void ValidateCompatibility(ActiveRuntimePointer? current, VerifiedRuntimePackage package)
    {
        if (current is null) return;
        if (!string.Equals(current.RuntimeApiVersion, package.Identity.RuntimeApiVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime API mismatch");
        if (current.LiveAdoptionProtocol != package.Identity.LiveAdoptionProtocol)
            throw new InvalidDataException("Live adoption protocol mismatch");
        if (string.Equals(current.StateSchemaVersion, package.Identity.StateSchemaVersion, StringComparison.Ordinal)) return;
        var receipt = package.SchemaCompatibility;
        if (receipt is null || !receipt.Verified ||
            !string.Equals(receipt.SourceSchema, current.StateSchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.TargetSchema, package.Identity.StateSchemaVersion, StringComparison.Ordinal) ||
            receipt.BackupSha256.Length != 64 || !receipt.BackupSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("State schema change lacks a verified backup-bound compatibility receipt");
    }

    private static void ValidateProbe(RuntimeIdentity expected, RuntimeProbeResult actual)
    {
        if (!string.Equals(expected.ProductVersion, actual.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(expected.RuntimeApiVersion, actual.RuntimeApiVersion, StringComparison.Ordinal) ||
            !string.Equals(expected.StateSchemaVersion, actual.StateSchemaVersion, StringComparison.Ordinal) ||
            expected.LiveAdoptionProtocol != actual.LiveAdoptionProtocol)
            throw new InvalidDataException("Runtime version probe does not match the verified package identity");
    }

    private static IReadOnlyList<string> EnumerateBoundedFiles(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var files = new List<string>();
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            if (files.Count >= MaxFiles) throw new InvalidDataException("Runtime package exceeds the file budget");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Runtime package contains a reparse point");
            bytes = checked(bytes + info.Length);
            if (bytes > MaxBytes) throw new InvalidDataException("Runtime package exceeds the byte budget");
            var relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new InvalidDataException("Runtime package path escapes staging");
            files.Add(relative);
        }
        if (!files.Contains("hiker.exe", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime package is missing hiker.exe");
        return files.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static async Task VerifyHashesAsync(VerifiedRuntimePackage package, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (package.FileSha256.Count != files.Count) throw new InvalidDataException("Runtime package hash inventory is incomplete");
        foreach (var relative in files)
        {
            if (!package.FileSha256.TryGetValue(relative, out var expected) || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Runtime package hash is missing: {relative}");
            await using var stream = File.OpenRead(Path.Combine(package.StagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToLowerInvariant()), Encoding.ASCII.GetBytes(actual)))
                throw new InvalidDataException($"Runtime package hash mismatch: {relative}");
        }
    }

    private static void CopyVerifiedFiles(string sourceRoot, string targetRoot, IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var relative in files)
        {
            var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    private static async Task WriteIdentityAsync(string root, RuntimeIdentity identity, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(root, "runtime-identity.json"),
            JsonSerializer.Serialize(identity, Json),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RuntimeIdentity> ReadIdentityAsync(string root, CancellationToken cancellationToken)
    {
        RuntimePathPolicy.RejectReparsePath(Path.GetPathRoot(root)!, root);
        var path = Path.Combine(root, "runtime-identity.json");
        if (!File.Exists(path)) throw new InvalidDataException("Verified runtime identity manifest is missing");
        return JsonSerializer.Deserialize<RuntimeIdentity>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Verified runtime identity manifest is invalid");
    }
}

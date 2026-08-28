using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Activation;

public sealed class FlatRuntimeBootstrapper(string hikerHome, IRuntimePackageProbe probe)
{
    private const int MaxFiles = 1_000;
    private const long MaxBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly RuntimePointerStore pointerStore = new(hikerHome);

    public async Task<FlatRuntimeBootstrapResult> BootstrapAsync(
        RuntimeIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        var current = await pointerStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            var currentRoot = RuntimePathPolicy.ResolveWithin(Path.Combine(pointerStore.Home, "runtimes"), current.ActiveRuntime);
            return new FlatRuntimeBootstrapResult("ALREADY_BOOTSTRAPPED", current, currentRoot, 0, false);
        }

        RuntimePathPolicy.RejectReparsePath(Path.GetPathRoot(pointerStore.Home)!, pointerStore.Home);
        var sourceFiles = EnumerateFlatFiles(pointerStore.Home);
        var executable = sourceFiles.SingleOrDefault(static path => string.Equals(Path.GetFileName(path), "hiker.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Flat runtime is missing hiker.exe");
        foreach (var required in new[] { "hiker.deps.json", "hiker.runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll" })
        {
            if (!sourceFiles.Any(path => string.Equals(Path.GetFileName(path), required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Flat runtime is incomplete: {required}");
        }

        var probeResult = await probe.ProbeAsync(executable, cancellationToken).ConfigureAwait(false);
        ValidateProbe(identity, probeResult);
        var hashes = await HashFilesAsync(sourceFiles, cancellationToken).ConfigureAwait(false);
        var runtimesRoot = Path.Combine(pointerStore.Home, "runtimes");
        Directory.CreateDirectory(runtimesRoot);
        var target = RuntimePathPolicy.ResolveWithin(runtimesRoot, identity.RuntimeIdentityValue);
        if (Directory.Exists(target))
        {
            await VerifyExistingAsync(target, identity, hashes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var staging = Path.Combine(runtimesRoot, $".staging-flat-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var source in sourceFiles)
                    File.Copy(source, Path.Combine(staging, Path.GetFileName(source)), overwrite: false);
                await WriteMetadataAsync(staging, identity, hashes, cancellationToken).ConfigureAwait(false);
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }

        var pointer = new ActiveRuntimePointer(
            "hiker-active-runtime/v1",
            identity.RuntimeIdentityValue,
            null,
            identity.RuntimeIdentityValue,
            identity.ProductVersion,
            identity.RuntimeApiVersion,
            identity.StateSchemaVersion,
            identity.LiveAdoptionProtocol,
            identity.BuildCommit,
            1,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            identity.Channel);
        await pointerStore.WriteAtomicAsync(pointer, cancellationToken).ConfigureAwait(false);
        return new FlatRuntimeBootstrapResult("BOOTSTRAPPED", pointer, target, sourceFiles.Count, true);
    }

    private static IReadOnlyList<string> EnumerateFlatFiles(string home)
    {
        var files = new List<string>();
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(home, "*", SearchOption.TopDirectoryOnly))
        {
            if (files.Count >= MaxFiles) throw new InvalidDataException("Flat runtime exceeds the file budget");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Flat runtime contains a reparse point");
            bytes = checked(bytes + info.Length);
            if (bytes > MaxBytes) throw new InvalidDataException("Flat runtime exceeds the byte budget");
            files.Add(path);
        }
        return files.OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashFilesAsync(
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            await using var stream = File.OpenRead(path);
            hashes[Path.GetFileName(path)] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }
        return hashes;
    }

    private static async Task VerifyExistingAsync(
        string target,
        RuntimeIdentity identity,
        IReadOnlyDictionary<string, string> hashes,
        CancellationToken cancellationToken)
    {
        var identityPath = Path.Combine(target, "runtime-identity.json");
        var manifestPath = Path.Combine(target, "local-runtime-snapshot.json");
        if (!File.Exists(identityPath) || !File.Exists(manifestPath))
            throw new InvalidDataException("Existing flat runtime snapshot is incomplete");
        var existingIdentity = JsonSerializer.Deserialize<RuntimeIdentity>(await File.ReadAllTextAsync(identityPath, cancellationToken).ConfigureAwait(false), Json);
        var existingHashes = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), Json);
        if (!Equals(existingIdentity, identity) || existingHashes is null || existingHashes.Count != hashes.Count)
            throw new InvalidDataException("Existing flat runtime snapshot conflicts with current runtime");
        foreach (var item in hashes)
        {
            if (!existingHashes.TryGetValue(item.Key, out var recorded) || !FixedEquals(recorded, item.Value))
                throw new InvalidDataException("Existing flat runtime snapshot hash inventory conflicts with current runtime");
            var targetFile = Path.Combine(target, item.Key);
            if (!File.Exists(targetFile)) throw new InvalidDataException("Existing flat runtime snapshot is missing a file");
            await using var stream = File.OpenRead(targetFile);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!FixedEquals(actual, item.Value)) throw new InvalidDataException("Existing flat runtime snapshot was modified");
        }
    }

    private static async Task WriteMetadataAsync(
        string staging,
        RuntimeIdentity identity,
        IReadOnlyDictionary<string, string> hashes,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Combine(staging, "runtime-identity.json"),
            JsonSerializer.Serialize(identity, Json),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "local-runtime-snapshot.json"),
            JsonSerializer.Serialize(hashes, Json),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateIdentity(RuntimeIdentity identity)
    {
        if (!RuntimePathPolicy.IsSafeToken(identity.RuntimeIdentityValue) ||
            identity.BuildCommit.Length != 40 || !identity.BuildCommit.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(identity.ProductVersion) || identity.LiveAdoptionProtocol <= 0)
            throw new InvalidDataException("Flat runtime identity is invalid");
    }

    private static void ValidateProbe(RuntimeIdentity identity, RuntimeProbeResult probeResult)
    {
        if (!string.Equals(identity.ProductVersion, probeResult.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(identity.RuntimeApiVersion, probeResult.RuntimeApiVersion, StringComparison.Ordinal) ||
            !string.Equals(identity.StateSchemaVersion, probeResult.StateSchemaVersion, StringComparison.Ordinal) ||
            identity.LiveAdoptionProtocol != probeResult.LiveAdoptionProtocol)
            throw new InvalidDataException("Flat runtime probe does not match its identity");
    }

    private static bool FixedEquals(string left, string right) => left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
}

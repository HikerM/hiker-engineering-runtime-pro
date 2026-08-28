using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hiker.Pro.Activation;
using Hiker.Pro.Trust;

namespace Hiker.Pro.ReleaseTool;

public sealed partial class ReleaseRuntimeInstaller
{
    private const int MaxInfrastructureFiles = 5_000;
    private const long MaxInfrastructureBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<ReleaseVerifyOptions, CancellationToken, Task<ReleaseVerificationResult>> verifyPackage;
    private readonly Func<string, CancellationToken, Task<RuntimeIdentity>> readFlatIdentity;
    private readonly IRuntimePackageProbe runtimeProbe;

    public ReleaseRuntimeInstaller()
        : this(
            static (options, cancellationToken) => new ReleasePackageVerifier().VerifyAsync(options, cancellationToken),
            ReadFlatIdentityAsync,
            new ProcessRuntimePackageProbe())
    {
    }

    internal ReleaseRuntimeInstaller(
        Func<ReleaseVerifyOptions, CancellationToken, Task<ReleaseVerificationResult>> verifyPackage,
        Func<string, CancellationToken, Task<RuntimeIdentity>>? readFlatIdentity = null,
        IRuntimePackageProbe? runtimeProbe = null)
    {
        this.verifyPackage = verifyPackage;
        this.readFlatIdentity = readFlatIdentity ?? ReadFlatIdentityAsync;
        this.runtimeProbe = runtimeProbe ?? new ProcessRuntimePackageProbe();
    }

    public async Task<ReleaseInstallResult> InstallAsync(
        ReleaseInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var home = Path.GetFullPath(options.HikerHome);
        var package = Path.GetFullPath(options.PackagePath);
        var verification = await verifyPackage(
            new ReleaseVerifyOptions(package, options.PublicKeyPath, options.KeyId, options.ExpectedPublicKeyFingerprint, null),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(verification.Status, "PASS", StringComparison.Ordinal))
            throw new InvalidDataException("Release package verification did not pass");

        var extraction = Path.Combine(Path.GetTempPath(), $"hiker-pro-activate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        try
        {
            ReleasePackageVerifier.ExtractSafely(package, extraction);
            var packageSha = await FileSha256Async(package, cancellationToken).ConfigureAwait(false);
            if (!FixedEquals(packageSha, verification.PackageSha256))
                throw new InvalidDataException("Release package changed after verification");
            var materialized = await ReadVerifiedPayloadAsync(extraction, options, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(home);
            using var activationLease = RuntimeUseLock.AcquireActivation(home);

            var pointerStore = new RuntimePointerStore(home);
            var current = await pointerStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var flatBootstrapped = false;
            if (current is null && File.Exists(Path.Combine(home, "hiker.exe")))
            {
                var flatIdentity = await readFlatIdentity(home, cancellationToken).ConfigureAwait(false);
                var bootstrap = await new FlatRuntimeBootstrapper(home, runtimeProbe)
                    .BootstrapAsync(flatIdentity, cancellationToken).ConfigureAwait(false);
                flatBootstrapped = bootstrap.PointerChanged;
            }

            var binSource = Path.Combine(extraction, "payload", "bin");
            var toolsSource = Path.Combine(extraction, "payload", "tools");
            var compatibilitySource = Path.Combine(extraction, "payload", "compat", "hiker.exe");
            if (!Directory.Exists(binSource) || !Directory.Exists(toolsSource) || !File.Exists(compatibilitySource))
                throw new InvalidDataException("Release package is missing stable installation infrastructure");
            await InstallDirectoryAsync(binSource, Path.Combine(home, "bin"), cancellationToken).ConfigureAwait(false);
            await InstallDirectoryAsync(toolsSource, Path.Combine(home, "tools"), cancellationToken, preserveExisting: true).ConfigureAwait(false);
            await InstallCompatibilityLauncherAsync(compatibilitySource, Path.Combine(home, "hiker.exe"), cancellationToken).ConfigureAwait(false);

            var activation = await new RuntimeActivationManager(home, runtimeProbe)
                .ActivateAsync(materialized, cancellationToken).ConfigureAwait(false);
            var locatorPath = ResolveLocatorPath(options.LocatorPath);
            string? diagnostic = null;
            var status = activation.Status;
            var ok = true;
            try
            {
                await WriteLocatorAsync(locatorPath, home, activation.Pointer, cancellationToken).ConfigureAwait(false);
                if (options.UpdateUserPath) AddToUserPath(Path.Combine(home, "bin"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                status = "ACTIVE_LOCATOR_PENDING";
                ok = false;
                diagnostic = $"{exception.GetType().Name}: {exception.Message}";
            }

            return new ReleaseInstallResult(
                "hiker-pro-install/v1",
                ok,
                status,
                activation.Pointer.ProductVersion,
                activation.Pointer.RuntimeIdentity,
                activation.Pointer.ActiveRuntime,
                activation.Pointer.PreviousRuntime,
                activation.Pointer.InstallGeneration,
                Path.Combine(home, "bin", "hiker.exe"),
                Path.Combine(home, "hiker.exe"),
                locatorPath,
                flatBootstrapped,
                activation.PointerChanged,
                activation.VerifiedFiles,
                packageSha,
                "NOT_CONFIGURED_RC",
                diagnostic);
        }
        finally
        {
            if (Directory.Exists(extraction)) Directory.Delete(extraction, recursive: true);
        }
    }

    private static async Task<VerifiedRuntimePackage> ReadVerifiedPayloadAsync(
        string extraction,
        ReleaseInstallOptions options,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(extraction, "manifest.json");
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Release manifest is empty");
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(Path.GetFullPath(options.PublicKeyPath), cancellationToken).ConfigureAwait(false));
        var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        if (!FixedEquals(fingerprint, options.ExpectedPublicKeyFingerprint)) throw new InvalidDataException("Release trust root fingerprint mismatch");
        var now = DateTimeOffset.UtcNow;
        var verifier = new SignedEnvelopeVerifier(new[]
        {
            new TrustKey(options.KeyId, TrustPurposes.Update, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), now.AddDays(-1), now.AddYears(20), false)
        });
        var signed = verifier.Verify<SignedUpdateManifest>(envelope, TrustPurposes.Update);
        if (!signed.Valid || signed.Payload is null) throw new InvalidDataException($"Release manifest signature is invalid: {signed.Status}");
        var integrity = await new PackageIntegrityVerifier(verifier).VerifyUpdateAsync(
            envelope,
            extraction,
            cancellationToken,
            new HashSet<string>(new[] { "manifest.json" }, StringComparer.Ordinal)).ConfigureAwait(false);
        if (!string.Equals(integrity.Status, "VERIFIED", StringComparison.Ordinal))
            throw new InvalidDataException($"Release package integrity is invalid: {string.Join(',', integrity.Failures)}");

        var packagePointer = JsonSerializer.Deserialize<ActiveRuntimePointer>(
            await File.ReadAllTextAsync(Path.Combine(extraction, "payload", "state", "current-runtime.json"), cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Packaged runtime pointer is empty");
        RuntimePointerStore.Validate(packagePointer);
        var runtimePrefix = $"payload/runtimes/{packagePointer.ActiveRuntime}/";
        var runtimeRoot = Path.Combine(extraction, "payload", "runtimes", packagePointer.ActiveRuntime);
        var identity = JsonSerializer.Deserialize<RuntimeIdentity>(
            await File.ReadAllTextAsync(Path.Combine(runtimeRoot, "runtime-identity.json"), cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Packaged runtime identity is empty");
        if (!string.Equals(identity.RuntimeIdentityValue, packagePointer.RuntimeIdentity, StringComparison.Ordinal) ||
            !string.Equals(identity.ProductVersion, signed.Payload.Version, StringComparison.Ordinal) ||
            !string.Equals(identity.BuildCommit, signed.Payload.SourceCommit, StringComparison.Ordinal))
            throw new InvalidDataException("Packaged runtime identity is inconsistent with the signed release manifest");
        var hashes = signed.Payload.Artifacts
            .Where(artifact => artifact.RelativePath.StartsWith(runtimePrefix, StringComparison.Ordinal))
            .ToDictionary(
                artifact => artifact.RelativePath[runtimePrefix.Length..],
                artifact => artifact.Sha256,
                StringComparer.Ordinal);
        if (hashes.Count == 0) throw new InvalidDataException("Signed release manifest has no runtime artifacts");
        return new VerifiedRuntimePackage(runtimeRoot, identity, hashes, "VERIFIED", "NOT_CONFIGURED_RC");
    }

    private static async Task<RuntimeIdentity> ReadFlatIdentityAsync(string home, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(home, "hiker.exe");
        var probe = await new ProcessRuntimePackageProbe().ProbeAsync(executable, cancellationToken).ConfigureAwait(false);
        var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? string.Empty;
        var match = BuildCommitRegex().Match(productVersion);
        if (!match.Success) throw new InvalidDataException("Flat runtime build commit cannot be proven from executable metadata");
        var commit = match.Groups[1].Value.ToLowerInvariant();
        var token = $"{probe.ProductVersion}-{commit[..12]}-flat";
        return new RuntimeIdentity(probe.ProductVersion, probe.RuntimeApiVersion, probe.StateSchemaVersion, probe.LiveAdoptionProtocol, commit, token, "legacy-flat");
    }

    private static async Task InstallDirectoryAsync(
        string source,
        string target,
        CancellationToken cancellationToken,
        bool preserveExisting = false)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        if (preserveExisting && Directory.Exists(target)) return;
        var staging = Path.Combine(parent, $".staging-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
        var previous = Path.Combine(parent, $".previous-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
        try
        {
            await CopyDirectoryAsync(source, staging, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (Directory.Exists(previous) && !Directory.Exists(target)) Directory.Move(previous, target);
                throw;
            }
            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        var count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaxInfrastructureFiles) throw new InvalidDataException("Installation infrastructure exceeds the file budget");
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Installation infrastructure contains a reparse point");
            bytes = checked(bytes + info.Length);
            if (bytes > MaxInfrastructureBytes) throw new InvalidDataException("Installation infrastructure exceeds the byte budget");
            var relative = Path.GetRelativePath(source, path);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private static async Task InstallCompatibilityLauncherAsync(string source, string target, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".hiker-launcher-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteLocatorAsync(string path, string home, ActiveRuntimePointer pointer, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".pro-runtime-{Guid.NewGuid():N}.tmp");
        var payload = JsonSerializer.Serialize(new
        {
            contract_version = "hiker-pro-locator/v1",
            hiker_home = home,
            executable = Path.Combine(home, "bin", "hiker.exe"),
            runtime_identity = pointer.RuntimeIdentity,
            install_generation = pointer.InstallGeneration,
            activation_epoch = pointer.ActivationEpoch
        }, Json);
        await File.WriteAllTextAsync(temporary, payload, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, full, overwrite: true);
    }

    private static string ResolveLocatorPath(string? configured) => Path.GetFullPath(configured ?? Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Hiker",
        "pro-runtime.json"));

    private static void AddToUserPath(string bin)
    {
        if (!OperatingSystem.IsWindows()) return;
        var current = System.Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!entries.Contains(bin, StringComparer.OrdinalIgnoreCase))
            System.Environment.SetEnvironmentVariable("Path", string.Join(Path.PathSeparator, new[] { bin }.Concat(entries)), EnvironmentVariableTarget.User);
    }

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool FixedEquals(string left, string right) => left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    [GeneratedRegex(@"\+([0-9a-fA-F]{40})(?:$|[.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex BuildCommitRegex();
}

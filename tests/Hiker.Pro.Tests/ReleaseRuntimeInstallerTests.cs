using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Activation;
using Hiker.Pro.Context;
using Hiker.Pro.Release;
using Hiker.Pro.ReleaseTool;
using Hiker.Pro.Trust;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ReleaseRuntimeInstallerTests
{
    private const string KeyId = "field-update-key";
    private const string Commit = "dddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public async Task SignedPackageInstallsStableLauncherAndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await Fixture.CreateAsync();
        try
        {
            var installer = fixture.CreateInstaller();
            var options = fixture.Options();
            var first = await installer.InstallAsync(options);
            var second = await installer.InstallAsync(options);
            var version = await RunAsync(first.StableLauncher, fixture.Home, "version", "--json");
            using var document = JsonDocument.Parse(version.Output);

            Assert.True(first.Ok);
            Assert.Equal("ACTIVATED", first.Status);
            Assert.False(first.FlatRuntimeBootstrapped);
            Assert.Equal("ALREADY_ACTIVE", second.Status);
            Assert.False(second.PointerChanged);
            Assert.Equal(first.InstallGeneration, second.InstallGeneration);
            Assert.Equal(0, version.ExitCode);
            Assert.Equal(BuildVersionFacts.Current.Product, document.RootElement.GetProperty("product_version").GetString());
            Assert.Equal("hiker-pro-locator/v1", JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Locator)).RootElement.GetProperty("contract_version").GetString());
            Assert.True(File.Exists(Path.Combine(fixture.Home, "tools", "hiker-pro-release.exe")));
            Assert.True(File.Exists(first.CompatibilityLauncher));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task TamperedPackageCannotChangeActiveRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await Fixture.CreateAsync();
        try
        {
            var installer = fixture.CreateInstaller();
            await installer.InstallAsync(fixture.Options());
            var before = await new RuntimePointerStore(fixture.Home).ReadAsync();
            await fixture.TamperRuntimeAsync();

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(fixture.Options()));
            var after = await new RuntimePointerStore(fixture.Home).ReadAsync();

            Assert.Equal(before, after);
            Assert.Equal(1, after!.InstallGeneration);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task FlatInstallationIsPreservedAsPreviousRuntimeAndRollbackRemainsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await Fixture.CreateAsync(withFlatRuntime: true);
        try
        {
            var result = await fixture.CreateInstaller(withFlatIdentity: true).InstallAsync(fixture.Options());
            var rollback = await new RuntimeActivationManager(fixture.Home, new ProcessRuntimePackageProbe()).RollbackAsync();

            Assert.True(result.FlatRuntimeBootstrapped);
            Assert.Equal("flat-field-runtime", result.PreviousRuntime);
            Assert.Equal(2, result.InstallGeneration);
            Assert.Equal("flat-field-runtime", rollback.Pointer.ActiveRuntime);
            Assert.Equal(3, rollback.Pointer.InstallGeneration);
            Assert.True(File.Exists(Path.Combine(fixture.Home, "runtimes", "flat-field-runtime", "local-runtime-snapshot.json")));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable, string home, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["HIKER_HOME"] = home;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private readonly string root;
        private readonly ECDsa key;
        public string Home { get; }
        public string Locator { get; }
        public string Package { get; }
        public string PublicKey { get; }
        public string PublicKeyFingerprint { get; }

        private Fixture(string root, ECDsa key, string package, string publicKey, string fingerprint)
        {
            this.root = root;
            this.key = key;
            Package = package;
            PublicKey = publicKey;
            PublicKeyFingerprint = fingerprint;
            Home = Path.Combine(root, "installed");
            Locator = Path.Combine(root, "locator", "pro-runtime.json");
        }

        public static async Task<Fixture> CreateAsync(bool withFlatRuntime = false)
        {
            var repository = FindRepositoryRoot();
            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var root = Path.Combine(Path.GetTempPath(), $"hiker-release-installer-{Guid.NewGuid():N}");
            var stage = Path.Combine(root, "stage");
            var payload = Path.Combine(stage, "payload");
            var identityValue = $"{BuildVersionFacts.Current.Product}-{Commit[..12]}";
            var runtimeRoot = Path.Combine(payload, "runtimes", identityValue);
            CopyDirectory(Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0"), runtimeRoot);
            CopyDirectory(Path.Combine(repository, "src", "Hiker.Pro.Launcher", "bin", configuration, "net9.0"), Path.Combine(payload, "bin"));
            CopyDirectory(Path.Combine(repository, "tools", "Hiker.Pro.Release", "bin", configuration, "net9.0"), Path.Combine(payload, "tools"));
            Directory.CreateDirectory(Path.Combine(payload, "compat"));
            File.Copy(Path.Combine(repository, "src", "Hiker.Pro.Launcher", "bin", configuration, "net9.0", "hiker.exe"), Path.Combine(payload, "compat", "hiker.exe"));
            var identity = new RuntimeIdentity(
                BuildVersionFacts.Current.Product,
                BuildVersionFacts.Current.Runtime,
                BuildVersionFacts.Current.StateSchema,
                LiveAdoptionProtocol.CurrentVersion,
                Commit,
                identityValue,
                "rc");
            await File.WriteAllTextAsync(Path.Combine(runtimeRoot, "runtime-identity.json"), JsonSerializer.Serialize(identity, Json));
            Directory.CreateDirectory(Path.Combine(payload, "state"));
            var pointer = new ActiveRuntimePointer(
                "hiker-active-runtime/v1",
                identityValue,
                null,
                identityValue,
                identity.ProductVersion,
                identity.RuntimeApiVersion,
                identity.StateSchemaVersion,
                identity.LiveAdoptionProtocol,
                identity.BuildCommit,
                1,
                Commit[..32],
                DateTimeOffset.UtcNow,
                identity.Channel);
            await File.WriteAllTextAsync(Path.Combine(payload, "state", "current-runtime.json"), JsonSerializer.Serialize(pointer, Json));
            await File.WriteAllTextAsync(Path.Combine(payload, "VERSION"), BuildVersionFacts.Current.Product);
            await File.WriteAllTextAsync(Path.Combine(payload, "release-versions.json"), JsonSerializer.Serialize(new
            {
                product = BuildVersionFacts.Current.Product,
                runtime = BuildVersionFacts.Current.Runtime,
                state_schema = BuildVersionFacts.Current.StateSchema
            }, Json));

            var artifacts = new List<UpdateArtifact>();
            foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).OrderBy(static value => value, StringComparer.Ordinal))
            {
                await using var stream = File.OpenRead(path);
                artifacts.Add(new UpdateArtifact(
                    $"payload/{Path.GetRelativePath(payload, path).Replace('\\', '/')}",
                    Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant(),
                    new FileInfo(path).Length,
                    "win",
                    "x64"));
            }
            var manifest = new SignedUpdateManifest(
                "Hiker Pro",
                BuildVersionFacts.Current.Product,
                "rc",
                BuildVersionFacts.Current.Runtime,
                DateTimeOffset.UtcNow,
                artifacts,
                null,
                Commit);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var manifestBytes = CanonicalJson.Serialize(manifest);
            var envelope = new SignedEnvelope(
                "hiker-signed-envelope/v1",
                SignedEnvelopeVerifier.Algorithm,
                KeyId,
                TrustPurposes.Update,
                Encode(manifestBytes),
                Encode(key.SignData(manifestBytes, HashAlgorithmName.SHA256)));
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(envelope, Json));
            var package = Path.Combine(root, "hiker-pro-test.zip");
            ZipFile.CreateFromDirectory(stage, package, CompressionLevel.Fastest, includeBaseDirectory: false);
            await using (var stream = File.OpenRead(package))
            {
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
                await File.WriteAllTextAsync(Path.ChangeExtension(package, ".sha256"), $"{sha}  {Path.GetFileName(package)}\n");
            }
            var publicKey = Path.Combine(root, "release.public.pem");
            await File.WriteAllTextAsync(publicKey, key.ExportSubjectPublicKeyInfoPem());
            var fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            var fixture = new Fixture(root, key, package, publicKey, fingerprint);
            if (withFlatRuntime)
            {
                CopyDirectory(Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0"), fixture.Home);
                await File.WriteAllTextAsync(Path.Combine(fixture.Home, "hostfxr.dll"), "fixture");
                await File.WriteAllTextAsync(Path.Combine(fixture.Home, "hostpolicy.dll"), "fixture");
            }
            return fixture;
        }

        public ReleaseRuntimeInstaller CreateInstaller(bool withFlatIdentity = false) => new(async (options, cancellationToken) =>
        {
            await using var stream = File.OpenRead(options.PackagePath);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            return new ReleaseVerificationResult("test/v1", "PASS", Path.GetFileName(options.PackagePath), sha, BuildVersionFacts.Current.Product, Commit, Array.Empty<ReleaseGateCheck>());
        }, withFlatIdentity
            ? static (_, _) => Task.FromResult(new RuntimeIdentity(
                BuildVersionFacts.Current.Product,
                BuildVersionFacts.Current.Runtime,
                BuildVersionFacts.Current.StateSchema,
                LiveAdoptionProtocol.CurrentVersion,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "flat-field-runtime",
                "legacy-flat"))
            : null,
            withFlatIdentity ? new FixedProbe() : null);

        public ReleaseInstallOptions Options() => new(Package, PublicKey, KeyId, PublicKeyFingerprint, Home, Locator, false);

        public async Task TamperRuntimeAsync()
        {
            var replacement = Path.Combine(root, "replacement");
            ZipFile.ExtractToDirectory(Package, replacement);
            var runtime = Directory.EnumerateDirectories(Path.Combine(replacement, "payload", "runtimes")).Single();
            await File.AppendAllTextAsync(Path.Combine(runtime, "hiker.dll"), "tampered");
            File.Delete(Package);
            ZipFile.CreateFromDirectory(replacement, Package, CompressionLevel.Fastest, includeBaseDirectory: false);
        }

        public void Dispose()
        {
            key.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("Repository root was not found");
        }

        private static void CopyDirectory(string source, string target)
        {
            foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(path, destination);
            }
        }

        private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed class FixedProbe : IRuntimePackageProbe
        {
            public Task<RuntimeProbeResult> ProbeAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(new RuntimeProbeResult(
                BuildVersionFacts.Current.Product,
                BuildVersionFacts.Current.Runtime,
                BuildVersionFacts.Current.StateSchema,
                LiveAdoptionProtocol.CurrentVersion));
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

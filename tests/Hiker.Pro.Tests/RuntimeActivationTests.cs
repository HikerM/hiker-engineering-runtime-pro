using System.Diagnostics;
using System.Security.Cryptography;
using Hiker.Pro.Activation;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class RuntimeActivationTests
{
    private const string CommitA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CommitB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task StableLauncherResolvesActiveRuntime_AndHotPathReadsOnlyPointerAndIdentity()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var activated = await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var target = await new RuntimeLauncherResolver(fixture.Home).ResolveAsync();

            Assert.Equal("ACTIVATED", activated.Status);
            Assert.Equal("runtime-a", target.Pointer.ActiveRuntime);
            Assert.Equal(2, target.PointerFilesRead);
            Assert.Equal(Path.Combine(fixture.Home, "runtimes", "runtime-a", "hiker.exe"), target.Executable);
            Assert.DoesNotContain("5.19.0-rc.7", Path.Combine(fixture.Home, "bin", "hiker.exe"), StringComparison.Ordinal);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task AtomicActivationKeepsRunningTarget_AndNextResolveUsesNewRuntime()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var turnA = await new RuntimeLauncherResolver(fixture.Home).ResolveAsync();
            var activated = await fixture.ActivateAsync("runtime-b", "5.19.0-rc.8", CommitB);
            var turnB = await new RuntimeLauncherResolver(fixture.Home).ResolveAsync();

            Assert.Equal("runtime-a", Path.GetFileName(Path.GetDirectoryName(turnA.Executable)));
            Assert.True(File.Exists(turnA.Executable));
            Assert.Equal("runtime-b", Path.GetFileName(Path.GetDirectoryName(turnB.Executable)));
            Assert.Equal("runtime-a", activated.Pointer.PreviousRuntime);
            Assert.Equal(2, activated.Pointer.InstallGeneration);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task PartialOrFailedActivationPreservesPreviousRuntime()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var staging = await fixture.StageAsync("runtime-b");
            var identity = Fixture.Identity("runtime-b", "5.19.0-rc.8", CommitB);
            var hashes = await Fixture.HashesAsync(staging);
            File.Delete(Path.Combine(staging, "runtime.dat"));
            var package = new VerifiedRuntimePackage(staging, identity, hashes, "VERIFIED", "VERIFIED");

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.ActivateAsync(package));
            var current = await new RuntimePointerStore(fixture.Home).ReadAsync();
            Assert.Equal("runtime-a", current!.ActiveRuntime);
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, "runtimes", "runtime-b")));
            Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(fixture.Home, "runtimes")), path => Path.GetFileName(path).StartsWith(".staging", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ProtocolAndRuntimeApiMismatchFailClosed()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var protocol = Fixture.Identity("runtime-protocol", "5.19.0-rc.8", CommitB) with { LiveAdoptionProtocol = 99 };
            var api = Fixture.Identity("runtime-api", "5.19.0-rc.8", CommitB) with { RuntimeApiVersion = "99.0.0" };

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ActivateAsync(protocol));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ActivateAsync(api));
            Assert.Equal("runtime-a", (await new RuntimePointerStore(fixture.Home).ReadAsync())!.ActiveRuntime);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task SchemaChangeRequiresVerifiedBackupBoundReceipt()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var next = Fixture.Identity("runtime-schema", "5.19.0-rc.8", CommitB) with { StateSchemaVersion = "4.0.0" };
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ActivateAsync(next));

            var receipt = new SchemaCompatibilityReceipt("3.0.0", "4.0.0", new string('a', 64), true);
            var activated = await fixture.ActivateAsync(next, receipt);
            Assert.Equal("runtime-schema", activated.Pointer.ActiveRuntime);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task RollbackAtomicallyRestoresPreviousVerifiedRuntime()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            await fixture.ActivateAsync("runtime-b", "5.19.0-rc.8", CommitB);
            var rollback = await fixture.Manager.RollbackAsync();

            Assert.Equal("ROLLED_BACK", rollback.Status);
            Assert.Equal("runtime-a", rollback.Pointer.ActiveRuntime);
            Assert.Equal("runtime-b", rollback.Pointer.PreviousRuntime);
            Assert.Equal(3, rollback.Pointer.InstallGeneration);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task InvalidPointerAndPathEscapeAreRejected()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.Home, "state"));
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Home, "state", "current-runtime.json"),
                "{\"format\":\"hiker-active-runtime/v1\",\"activeRuntime\":\"..\\\\outside\",\"runtimeIdentity\":\"bad\",\"productVersion\":\"x\",\"runtimeApiVersion\":\"x\",\"stateSchemaVersion\":\"x\",\"liveAdoptionProtocol\":2,\"buildCommit\":\"x\",\"installGeneration\":1,\"activationEpoch\":\"x\",\"verifiedAt\":\"2026-08-28T00:00:00Z\",\"runtimeChannel\":\"rc\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => new RuntimeLauncherResolver(fixture.Home).ResolveAsync());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FakeProbe probe = new();
        public string Home { get; }
        public RuntimeActivationManager Manager { get; }

        private Fixture(string home)
        {
            Home = home;
            Manager = new RuntimeActivationManager(home, probe);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var home = Path.Combine(Path.GetTempPath(), $"hiker-activation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            await File.WriteAllTextAsync(Path.Combine(home, "bin", "hiker.exe"), "stable-launcher");
            return new Fixture(home);
        }

        public async Task<RuntimeActivationResult> ActivateAsync(string runtime, string version, string commit) =>
            await ActivateAsync(Identity(runtime, version, commit));

        public async Task<RuntimeActivationResult> ActivateAsync(RuntimeIdentity identity, SchemaCompatibilityReceipt? receipt = null)
        {
            var staging = await StageAsync(identity.RuntimeIdentityValue);
            probe.Result = new RuntimeProbeResult(identity.ProductVersion, identity.RuntimeApiVersion, identity.StateSchemaVersion, identity.LiveAdoptionProtocol);
            return await Manager.ActivateAsync(new VerifiedRuntimePackage(
                staging,
                identity,
                await HashesAsync(staging),
                "VERIFIED",
                "VERIFIED",
                receipt));
        }

        public async Task<string> StageAsync(string name)
        {
            var root = Path.Combine(Home, "incoming", $".staging-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "hiker.exe"), $"runtime:{name}");
            await File.WriteAllTextAsync(Path.Combine(root, "runtime.dat"), $"payload:{name}");
            return root;
        }

        public static RuntimeIdentity Identity(string runtime, string version, string commit) =>
            new(version, "0.1.0", "3.0.0", 2, commit, runtime, "rc");

        public static async Task<IReadOnlyDictionary<string, string>> HashesAsync(string root)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                await using var stream = File.OpenRead(path);
                values[Path.GetRelativePath(root, path).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            }
            return values;
        }

        public void Dispose()
        {
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }

    private sealed class FakeProbe : IRuntimePackageProbe
    {
        public RuntimeProbeResult Result { get; set; } = new("5.19.0-rc.7", "0.1.0", "3.0.0", 2);
        public Task<RuntimeProbeResult> ProbeAsync(string executable, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
}

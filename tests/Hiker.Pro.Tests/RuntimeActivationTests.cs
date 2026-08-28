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

    [Fact]
    public async Task FlatRuntimeBootstrapPreservesCurrentRuntimeAsImmutableRollbackTarget()
    {
        var home = Path.Combine(Path.GetTempPath(), $"hiker-flat-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            foreach (var file in new[] { "hiker.exe", "hiker.deps.json", "hiker.runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll", "runtime.dat" })
                await File.WriteAllTextAsync(Path.Combine(home, file), $"flat:{file}");
            var identity = Fixture.Identity("runtime-flat", "5.19.0-rc.7", CommitA);
            var probe = new FakeProbe { Result = new RuntimeProbeResult("5.19.0-rc.7", "0.1.0", "3.0.0", 2) };
            var bootstrap = new FlatRuntimeBootstrapper(home, probe);

            var first = await bootstrap.BootstrapAsync(identity);
            var second = await bootstrap.BootstrapAsync(identity);
            var manager = new RuntimeActivationManager(home, probe);
            var nextStaging = Path.Combine(home, "incoming", ".staging-next");
            Directory.CreateDirectory(nextStaging);
            await File.WriteAllTextAsync(Path.Combine(nextStaging, "hiker.exe"), "next:hiker.exe");
            await File.WriteAllTextAsync(Path.Combine(nextStaging, "runtime.dat"), "next:runtime.dat");
            var next = Fixture.Identity("runtime-next", "5.19.0-rc.8", CommitB);
            probe.Result = new RuntimeProbeResult("5.19.0-rc.8", "0.1.0", "3.0.0", 2);
            var activated = await manager.ActivateAsync(new VerifiedRuntimePackage(nextStaging, next, await Fixture.HashesAsync(nextStaging), "VERIFIED", "VERIFIED"));
            var rollback = await manager.RollbackAsync();

            Assert.Equal("BOOTSTRAPPED", first.Status);
            Assert.Equal("ALREADY_BOOTSTRAPPED", second.Status);
            Assert.Equal("runtime-flat", activated.Pointer.PreviousRuntime);
            Assert.Equal("runtime-flat", rollback.Pointer.ActiveRuntime);
            Assert.True(File.Exists(Path.Combine(first.RuntimeDirectory, "local-runtime-snapshot.json")));
            Assert.Equal("flat:hiker.exe", await File.ReadAllTextAsync(Path.Combine(home, "hiker.exe")));
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task PreexistingImmutableRuntimeMustMatchSignedHashInventory()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await fixture.ActivateAsync("runtime-a", "5.19.0-rc.7", CommitA);
            var staging = await fixture.StageAsync("runtime-b");
            var identity = Fixture.Identity("runtime-b", "5.19.0-rc.8", CommitB);
            var hashes = await Fixture.HashesAsync(staging);
            var target = Path.Combine(fixture.Home, "runtimes", "runtime-b");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(staging)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            await File.WriteAllTextAsync(Path.Combine(target, "runtime-identity.json"), System.Text.Json.JsonSerializer.Serialize(identity, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            await File.WriteAllTextAsync(Path.Combine(target, "runtime.dat"), "tampered");
            fixture.SetProbe(new RuntimeProbeResult("5.19.0-rc.8", "0.1.0", "3.0.0", 2));

            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Manager.ActivateAsync(new VerifiedRuntimePackage(staging, identity, hashes, "VERIFIED", "VERIFIED")));
            Assert.Equal("runtime-a", (await new RuntimePointerStore(fixture.Home).ReadAsync())!.ActiveRuntime);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void ActivationLockFailsClosedWhileRuntimeInvocationIsActive()
    {
        var home = Path.Combine(Path.GetTempPath(), $"hiker-runtime-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        try
        {
            using var first = RuntimeUseLock.AcquireExecution(home);
            using var second = RuntimeUseLock.AcquireExecution(home);
            Assert.Throws<InvalidOperationException>(() => RuntimeUseLock.AcquireActivation(home));
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
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

        public void SetProbe(RuntimeProbeResult result) => probe.Result = result;

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

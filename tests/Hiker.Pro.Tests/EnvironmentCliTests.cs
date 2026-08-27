using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Environment;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

[Collection(EnvironmentCliCollection.Name)]
public sealed class EnvironmentCliTests
{
    [Fact]
    public async Task StatusAndDoctorAutomaticallyIncludeActiveEnvironmentHealth()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-environment-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var manifest = Path.Combine(root, "package.json");
        await File.WriteAllTextAsync(manifest, "{\"version\":1}");
        try
        {
            var layout = new ProjectLayout(root);
            await using (var store = new SqliteStateStore(layout))
            {
                await store.InitializeAsync();
                var state = new StateCoordinator(store);
                var bootstrap = new ProjectBootstrapService(layout, store, state);
                _ = await bootstrap.InitializeAsync("environment-cli");
                var seeded = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                    "environment-cli-goal-task",
                    new[]
                    {
                        Mutation("environment-cli", EntityKinds.Goal, "goal-1", "{\"status\":\"ACTIVE\"}"),
                        Mutation("environment-cli", EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}")
                    },
                    "ENVIRONMENT_TEST_CONTEXT_CREATED",
                    "GOAL_TASK_SEEDED",
                    "{}"));
                Assert.True(seeded.Committed);
                var provider = new LocalProjectEnvironmentProvider(root);
                var registry = new EnvironmentProviderRegistry();
                registry.Register(provider);
                var profile = await new EnvironmentProfileCoordinator(state, registry).ResolveAndSelectAsync(
                    "environment-cli-select", "environment-cli", 1, new EnvironmentDetectionRequest(root, "development"));
                var descriptor = profile.Profile!.Graph.Nodes.Single().Resources.Single();
                var resources = new EnvironmentResourceLeaseCoordinator(state, new ProcessIdentityProbe());
                var activated = await new EnvironmentInstanceCoordinator(state, registry, resources).ActivateAsync(new EnvironmentInstanceRequest(
                    "environment-cli-activate", root, "environment-cli", "goal-1", 1, "task-1", "worktree-1", "runtime-1", profile.Profile.ProfileId,
                    new[] { new ResourceRequirement(descriptor.ResourceType, descriptor.ResourceId, descriptor.ProviderId, descriptor.Scope, descriptor.SharingMode, 1, descriptor.Capacity) },
                    new HashSet<string>(StringComparer.Ordinal), null, TimeSpan.FromMinutes(5), "source"));
                Assert.Equal(EnvironmentStatuses.Active, activated.Status);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var healthy = await RunCliAsync(root, "status");
            Assert.True(healthy.ExitCode == 0, $"stdout={healthy.Output}\nstderr={healthy.Error}");
            using (var document = JsonDocument.Parse(healthy.Output))
            {
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(EnvironmentStatuses.Healthy, document.RootElement.GetProperty("environment").GetProperty("status").GetString());
            }

            await File.WriteAllTextAsync(manifest, "{\"version\":2}");
            var drift = await RunCliAsync(root, "doctor");
            Assert.Equal(2, drift.ExitCode);
            using var blocked = JsonDocument.Parse(drift.Output);
            Assert.Equal("BLOCKED", blocked.RootElement.GetProperty("status").GetString());
            Assert.Contains(blocked.RootElement.GetProperty("environment").GetProperty("findings").EnumerateArray(),
                finding => finding.GetProperty("code").GetString() == EnvironmentStatuses.ToolchainDrift);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static StateMutation Mutation(string projectId, string kind, string id, string payload) => new(
        new StateKey(projectId, kind, id), 0, 1, StateLifecycle.Valid, "TEST", "3.0.0", "source", "dependency", payload);

    private static async Task<CliResult> RunCliAsync(string root, params string[] arguments)
    {
        var repository = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var cli = Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0", "hiker.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Hiker CLI");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Unable to locate repository root");
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

[CollectionDefinition(EnvironmentCliCollection.Name, DisableParallelization = true)]
public sealed class EnvironmentCliCollection
{
    public const string Name = "Environment CLI integration";
}

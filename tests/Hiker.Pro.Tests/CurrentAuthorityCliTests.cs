using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Context;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class CurrentAuthorityCliTests
{
    [Fact]
    public async Task EstablishThenAttachUsesSingleJsonMachineContractAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-current-authority-cli-{Guid.NewGuid():N}");
        const string projectId = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff";
        const string session = "cli-provider-session-secret";
        const string goal = "Keep the current business goal active";
        const string task = "Continue the unique active execution unit";
        var goalFingerprint = LiveAdoptionStateCodec.Hash($"CONTROLLER_CURRENT_GOAL\0{goal}\0ACTIVE\01");
        var taskFingerprint = LiveAdoptionStateCodec.Hash($"CONTROLLER_CURRENT_ACTIVE_TASK\0{task}\0IN_PROGRESS\01");
        try
        {
            Directory.CreateDirectory(root);
            Assert.Equal(0, (await RunCliAsync(root, "init", "--project-id", projectId)).ExitCode);
            var arguments = new[]
            {
                "establish-current-authority",
                "--project-id", projectId,
                "--provider", "codex",
                "--provider-session-id", session,
                "--boundary-proof", "NEW_TASK",
                "--goal-statement", goal,
                "--goal-state", "ACTIVE",
                "--goal-authority-source", "CONTROLLER_CURRENT_GOAL",
                "--goal-source-fingerprint", goalFingerprint,
                "--goal-authority-generation", "1",
                "--task-statement", task,
                "--task-state", "IN_PROGRESS",
                "--task-authority-source", "CONTROLLER_CURRENT_ACTIVE_TASK",
                "--task-source-fingerprint", taskFingerprint,
                "--task-authority-generation", "1"
            };
            var established = await RunCliAsync(root, arguments);
            var establishedJson = Json(established.Output);
            Assert.Equal(0, established.ExitCode);
            Assert.Equal("hiker-cli/v1", establishedJson.GetProperty("contract_version").GetString());
            Assert.Equal(CurrentAuthorityStatuses.Established, establishedJson.GetProperty("status").GetString());
            Assert.Equal(2, establishedJson.GetProperty("state_writes").GetInt32());
            Assert.DoesNotContain(session, established.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(goal, established.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(task, established.Output, StringComparison.Ordinal);

            var replay = await RunCliAsync(root, arguments);
            Assert.Equal(0, replay.ExitCode);
            Assert.Equal(CurrentAuthorityStatuses.AlreadyEstablished, Json(replay.Output).GetProperty("status").GetString());
            Assert.Equal(0, Json(replay.Output).GetProperty("state_writes").GetInt32());

            var attach = await RunCliAsync(
                root,
                "attach",
                "--project-id", projectId,
                "--provider", "codex",
                "--provider-session-id", session,
                "--boundary-proof", "NEW_TASK");
            Assert.Equal(0, attach.ExitCode);
            Assert.Equal(LiveAdoptionStatuses.Adopted, Json(attach.Output).GetProperty("status").GetString());

            var attachReplay = await RunCliAsync(
                root,
                "attach",
                "--project-id", projectId,
                "--provider", "codex",
                "--provider-session-id", session,
                "--boundary-proof", "TURN_TERMINAL");
            Assert.Equal(LiveAdoptionStatuses.AlreadyAdopted, Json(attachReplay.Output).GetProperty("status").GetString());
            Assert.Equal(0, Json(attachReplay.Output).GetProperty("state_writes").GetInt32());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task<CliResult> RunCliAsync(string root, params string[] arguments)
    {
        var repository = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var cli = Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0", "hiker.dll");
        Assert.True(File.Exists(cli), $"CLI assembly was not built: {cli}");
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
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Hiker.Pro.Cli");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate Hiker.Pro repository root");
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

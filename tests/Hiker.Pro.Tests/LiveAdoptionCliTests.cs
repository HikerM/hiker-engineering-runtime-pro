using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class LiveAdoptionCliTests
{
    [Fact]
    public async Task AttachCheckpointResume_AutoResolvesLegacyWorkWithoutRawProviderIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-live-cli-{Guid.NewGuid():N}");
        try
        {
            var governance = Path.Combine(root, ".ai", "governance");
            var runtime = Path.Combine(root, ".ai", "runtime");
            Directory.CreateDirectory(governance);
            Directory.CreateDirectory(runtime);
            await File.WriteAllTextAsync(
                Path.Combine(governance, "goal-contract.json"),
                "{\"goal_id\":\"cli-goal\",\"status\":\"CURRENT\",\"revision\":2}");
            await File.WriteAllTextAsync(
                Path.Combine(runtime, "task.json"),
                "{\"task_id\":\"cli-task\",\"goal_id\":\"cli-goal\",\"status\":\"RUNNING\",\"goal_revision\":2}");

            var attach = await RunCliAsync(
                root,
                "attach",
                "--provider",
                "codex",
                "--provider-session-id",
                "raw-provider-session-one",
                "--boundary-proof",
                "NEW_TASK");
            var attached = Json(attach.Output);
            Assert.Equal(0, attach.ExitCode);
            Assert.Equal("LIVE_SESSION_ADOPTED", attached.GetProperty("status").GetString());
            Assert.Equal("cli-goal", attached.GetProperty("goal_id").GetString());
            Assert.Equal("cli-task", attached.GetProperty("task_id").GetString());
            Assert.DoesNotContain("raw-provider-session-one", attach.Output, StringComparison.Ordinal);

            var checkpoint = await RunCliAsync(
                root,
                "checkpoint",
                "--provider",
                "codex",
                "--provider-session-id",
                "raw-provider-session-one",
                "--changed-surface",
                "src/runtime",
                "--test-ref",
                "LiveAdoptionCliTests");
            Assert.Equal(0, checkpoint.ExitCode);
            Assert.Equal("TURN_CHECKPOINTED", Json(checkpoint.Output).GetProperty("status").GetString());
            Assert.DoesNotContain("raw-provider-session-one", checkpoint.Output, StringComparison.Ordinal);

            var retry = await RunCliAsync(
                root,
                "checkpoint",
                "--provider",
                "codex",
                "--provider-session-id",
                "raw-provider-session-one");
            Assert.Equal(0, retry.ExitCode);
            Assert.Equal("ALREADY_CHECKPOINTED", Json(retry.Output).GetProperty("status").GetString());

            var resume = await RunCliAsync(
                root,
                "resume",
                "--provider",
                "codex",
                "--provider-session-id",
                "raw-provider-session-two",
                "--boundary-proof",
                "RECOVERY_CHECKPOINT");
            var resumed = Json(resume.Output);
            Assert.Equal(0, resume.ExitCode);
            Assert.Equal("LIVE_SESSION_ADOPTED", resumed.GetProperty("status").GetString());
            Assert.Equal("cli-goal", resumed.GetProperty("goal_id").GetString());
            Assert.Equal("cli-task", resumed.GetProperty("task_id").GetString());
            Assert.DoesNotContain("raw-provider-session-two", resume.Output, StringComparison.Ordinal);

            var resumeRetry = await RunCliAsync(
                root,
                "attach",
                "--provider",
                "codex",
                "--provider-session-id",
                "raw-provider-session-two",
                "--boundary-proof",
                "TURN_TERMINAL");
            Assert.Equal(0, resumeRetry.ExitCode);
            Assert.Equal("ALREADY_ADOPTED", Json(resumeRetry.Output).GetProperty("status").GetString());
            Assert.Equal(0, Json(resumeRetry.Output).GetProperty("state_writes").GetInt32());

            var status = await RunCliAsync(root, "status");
            Assert.Equal(0, status.ExitCode);
            Assert.True(Json(status.Output).GetProperty("ok").GetBoolean());
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

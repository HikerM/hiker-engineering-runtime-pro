using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class CliBridgeContractTests
{
    [Fact]
    public async Task JsonMode_UsesSingleStdoutDocument_AndDeterministicExitContract()
    {
        var root = NewRoot();
        try
        {
            var blocked = await RunCliAsync(root, "status");
            var blockedJson = ParseSingleDocument(blocked.Output);

            Assert.Equal(2, blocked.ExitCode);
            Assert.Equal(2, blockedJson.GetProperty("exit_code").GetInt32());
            Assert.Equal("PRO_REQUIRED_BLOCKED", blockedJson.GetProperty("pro_state").GetString());
            Assert.Equal("status", blockedJson.GetProperty("command").GetString());
            Assert.False(blockedJson.GetProperty("ok").GetBoolean());
            Assert.NotEmpty(blocked.Error);
            Assert.False(IsJson(blocked.Error));

            var initialized = await RunCliAsync(root, "init");
            var initializedJson = ParseSingleDocument(initialized.Output);
            Assert.Equal(0, initialized.ExitCode);
            Assert.Equal(0, initializedJson.GetProperty("exit_code").GetInt32());
            Assert.True(initializedJson.GetProperty("ok").GetBoolean());
            Assert.True(string.IsNullOrWhiteSpace(initialized.Error));

            var usage = await RunCliAsync(root, "attach");
            var usageJson = ParseSingleDocument(usage.Output);
            Assert.Equal(64, usage.ExitCode);
            Assert.Equal("USAGE_ERROR", usageJson.GetProperty("status").GetString());
            Assert.Equal(64, usageJson.GetProperty("exit_code").GetInt32());
            Assert.False(IsJson(usage.Error));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Bridge_ParsesEveryMachineCommand_WithoutReadingPrimaryPayloadFromStderr()
    {
        var root = NewRoot();
        try
        {
            var commands = new[]
            {
                new[] { "version" },
                new[] { "init" },
                new[] { "status" },
                new[] { "doctor" },
                new[] { "project-facts" },
                new[] { "migrate-community" },
                new[] { "finalize-legacy" },
                new[] { "attach" },
                new[] { "resume" },
                new[] { "checkpoint" }
            };

            foreach (var command in commands)
            {
                var result = await RunCliAsync(root, command);
                var envelope = ParseSingleDocument(result.Output);
                Assert.Equal("hiker-cli/v1", envelope.GetProperty("contract_version").GetString());
                Assert.Equal(command[0], envelope.GetProperty("command").GetString());
                Assert.Equal(result.ExitCode, envelope.GetProperty("exit_code").GetInt32());
                Assert.False(IsJson(result.Error));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Attach_AutoConvergesKnownLegacyProject_AndPreservesUntrustedInertContent()
    {
        var root = NewRoot();
        try
        {
            var goal = Path.Combine(root, ".ai", "governance", "goal-contract.json");
            var task = Path.Combine(root, ".ai", "runtime", "task.json");
            var script = Path.Combine(root, ".ai", "tmp", "legacy-tool.py");
            var executionMarker = Path.Combine(root, "legacy-tool-executed.txt");
            await WriteAsync(goal, "{\"goal_id\":\"field-goal\",\"revision\":7,\"status\":\"ACTIVE\"}");
            await WriteAsync(task, "{\"task_id\":\"field-task\",\"goal_id\":\"field-goal\",\"status\":\"RUNNING\"}");
            await WriteAsync(script, $"from pathlib import Path\nPath(r'{executionMarker.Replace("'", "''")}').write_text('executed')\n");
            var scriptHash = await Sha256Async(script);

            var first = await RunCliAsync(
                root,
                "attach",
                "--provider", "codex",
                "--provider-session-id", "existing-field-session",
                "--boundary-proof", "TURN_TERMINAL");
            var adopted = ParseSingleDocument(first.Output);
            var projectId = adopted.GetProperty("project_id").GetString();

            Assert.Equal(0, first.ExitCode);
            Assert.Equal("LIVE_SESSION_ADOPTED", adopted.GetProperty("status").GetString());
            Assert.Equal("PRO_ACTIVE", adopted.GetProperty("pro_state").GetString());
            Assert.True(Guid.TryParseExact(projectId, "D", out _));
            Assert.Equal("ACTIVE_EXISTING_PROJECT", adopted.GetProperty("project").GetProperty("state").GetString());

            var retry = await RunCliAsync(
                root,
                "attach",
                "--provider", "codex",
                "--provider-session-id", "existing-field-session",
                "--boundary-proof", "TURN_TERMINAL");
            var retryJson = ParseSingleDocument(retry.Output);
            Assert.Equal(0, retry.ExitCode);
            Assert.Equal("ALREADY_ADOPTED", retryJson.GetProperty("status").GetString());
            Assert.Equal(0, retryJson.GetProperty("state_writes").GetInt32());

            var migrationRetry = await RunCliAsync(root, "migrate-community");
            var migrationJson = ParseSingleDocument(migrationRetry.Output);
            Assert.Equal(0, migrationRetry.ExitCode);
            Assert.Equal("ALREADY_COMPLETED", migrationJson.GetProperty("operation_status").GetString());
            Assert.Equal(0, migrationJson.GetProperty("state_writes").GetInt64());

            var finalizationRetry = await RunCliAsync(root, "finalize-legacy");
            var finalizationJson = ParseSingleDocument(finalizationRetry.Output);
            Assert.Equal(0, finalizationRetry.ExitCode);
            Assert.Equal("ALREADY_COMPLETED", finalizationJson.GetProperty("operation_status").GetString());
            Assert.Equal(0, finalizationJson.GetProperty("state_writes").GetInt64());

            var status = ParseSingleDocument((await RunCliAsync(root, "status")).Output);
            var doctor = ParseSingleDocument((await RunCliAsync(root, "doctor")).Output);
            Assert.Equal(projectId, status.GetProperty("project").GetProperty("project_id").GetString());
            Assert.True(status.GetProperty("ok").GetBoolean());
            Assert.Equal("HEALTHY", doctor.GetProperty("status").GetString());
            Assert.Equal(scriptHash, await Sha256Async(script));
            Assert.False(File.Exists(executionMarker));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Checkpoint_FieldCommand_RejectsInvalidOutcomePrecisely_ThenCommitsBoundedReferences()
    {
        var root = NewRoot();
        try
        {
            await WriteAsync(
                Path.Combine(root, ".ai", "governance", "goal-contract.json"),
                "{\"goal_id\":\"field-goal\",\"revision\":2,\"status\":\"CURRENT\"}");
            await WriteAsync(
                Path.Combine(root, ".ai", "runtime", "task.json"),
                "{\"task_id\":\"field-task\",\"goal_id\":\"field-goal\",\"status\":\"RUNNING\",\"goal_revision\":2}");
            var attach = ParseSingleDocument((await RunCliAsync(
                root,
                "attach",
                "--provider", "codex",
                "--provider-session-id", "field-provider-session",
                "--boundary-proof", "NEW_TASK")).Output);
            var projectId = attach.GetProperty("project_id").GetString()!;
            var before = ParseSingleDocument((await RunCliAsync(root, "status")).Output)
                .GetProperty("metrics").GetProperty("generation").GetInt64();

            var invalid = await RunCliAsync(
                root,
                "checkpoint",
                "--provider", "codex",
                "--provider-session-id", "field-provider-session",
                "--outcome", "FIELD_ADOPTION_VERIFIED",
                "--evidence-ref", "field://rc11-live-adoption",
                "--test-ref", "field://runtime-context-check");
            var invalidJson = ParseSingleDocument(invalid.Output);
            var afterInvalid = ParseSingleDocument((await RunCliAsync(root, "status")).Output)
                .GetProperty("metrics").GetProperty("generation").GetInt64();

            Assert.Equal(64, invalid.ExitCode);
            Assert.Equal("USAGE_ERROR", invalidJson.GetProperty("status").GetString());
            Assert.Equal("INVALID_CHECKPOINT_OUTCOME", invalidJson.GetProperty("reason_code").GetString());
            Assert.Equal("outcome", invalidJson.GetProperty("invalid_field").GetString());
            Assert.Contains(
                invalidJson.GetProperty("accepted_values").EnumerateArray().Select(static item => item.GetString()),
                static value => value == "NATURAL_CHECKPOINT");
            Assert.Equal(before, afterInvalid);

            var checkpoint = await RunCliAsync(
                root,
                "checkpoint",
                "--provider", "codex",
                "--provider-session-id", "field-provider-session",
                "--outcome", "NATURAL_CHECKPOINT",
                "--evidence-ref", "field://rc11-live-adoption",
                "--test-ref", "field://runtime-context-check");
            var checkpointJson = ParseSingleDocument(checkpoint.Output);

            Assert.Equal(0, checkpoint.ExitCode);
            Assert.Equal("TURN_CHECKPOINTED", checkpointJson.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(checkpointJson.GetProperty("context_fingerprint").GetString()));
            await using var store = new Hiker.Pro.State.SqliteStateStore(new Hiker.Pro.State.ProjectLayout(root));
            var evidence = await store.ReadAsync(projectId, Hiker.Pro.State.EntityKinds.Evidence, Hiker.Pro.State.StateLifecycle.Valid, 16);
            Assert.Contains(evidence, item =>
                item.PayloadJson.Contains("field://rc11-live-adoption", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("field://runtime-context-check", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

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
            WorkingDirectory = root
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Hiker.Pro.Cli");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static JsonElement ParseSingleDocument(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static bool IsJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-cli-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

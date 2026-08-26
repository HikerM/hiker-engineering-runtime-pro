using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.Verification;

internal sealed class CodexRuntimeScenario(VerificationOptions options)
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        if (!options.CodexExecuteEnabled)
        {
            return new[]
            {
                new VerificationCheck(
                    "REAL-CODEX-RUNTIME",
                    "BLOCKED",
                    "REAL_INTEGRATION_PROVEN",
                    "Real Codex execution requires explicit --codex-execute",
                    new Dictionary<string, object?>())
            };
        }

        var root = Path.Combine(Path.GetTempPath(), $"hiker-pro-codex-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var worktree = Path.Combine(root, "worktree-task");
        Directory.CreateDirectory(repository);
        try
        {
            await GitAsync(repository, "init", "-b", "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), ".ai/\n").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Isolated Hiker Codex runtime fixture\n").ConfigureAwait(false);
            await GitAsync(repository, "add", ".gitignore", "README.md").ConfigureAwait(false);
            await GitAsync(repository, "-c", "user.name=Hiker Verification", "-c", "user.email=verification@example.invalid", "commit", "-m", "fixture").ConfigureAwait(false);
            var inspector = new GitWorkspaceInspector();
            var worktrees = new GitWorktreeManager(inspector);
            var created = await worktrees.CreateAsync(new WorktreeCreateRequest(repository, worktree, "task/codex-runtime", "HEAD")).ConfigureAwait(false);
            var layout = new ProjectLayout(worktree);
            await using var store = new SqliteStateStore(layout);
            await store.InitializeAsync().ConfigureAwait(false);
            var inputDirectory = Path.Combine(layout.AiRoot, "runtime-input");
            Directory.CreateDirectory(inputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(inputDirectory, "instruction.txt"),
                "Create exactly one file named codex-integration-marker.txt in the repository root with exact content HIKER_CODEX_INTEGRATION_OK followed by one newline. Do not modify any other file. Then finish.").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(inputDirectory, "context.txt"),
                "This is an isolated disposable Git worktree used only for Hiker runtime integration verification.").ConfigureAwait(false);
            await using var adapter = new CodexCliRuntimeAdapter(new CodexCliAdapterOptions(StartTimeout: TimeSpan.FromMinutes(1), MaxInstructionBytes: 8_192));
            var probe = await adapter.ProbeAsync().ConfigureAwait(false);
            if (!probe.Available)
            {
                throw new InvalidOperationException($"Codex CLI unavailable: {probe.Status}");
            }

            var coordinator = new RuntimeCoordinator(store, inspector, new ProcessIdentityProbe());
            var startedAt = Stopwatch.StartNew();
            var started = await coordinator.StartAsync(
                "codex-e2e-start",
                new RuntimeStartRequest(
                    "codex-e2e",
                    "task-1",
                    "runtime-codex-1",
                    worktree,
                    "runtime-input/context.txt",
                    "runtime-input/instruction.txt",
                    1,
                    created.Snapshot.StatusFingerprint,
                    WorktreeId: "worktree-task-1",
                    PredictedAffectedScopes: new[] { "codex-integration-marker.txt" }),
                adapter).ConfigureAwait(false);
            var firstTerminal = await WaitForTerminalAsync(adapter, started.Handle, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
            startedAt.Stop();
            if (firstTerminal.Lifecycle != RuntimeLifecycle.Completed)
            {
                throw new InvalidOperationException($"Codex initial execution ended as {firstTerminal.Lifecycle}: {firstTerminal.DetailCode}");
            }
            var markerPath = Path.Combine(worktree, "codex-integration-marker.txt");
            var marker = File.Exists(markerPath) ? await File.ReadAllTextAsync(markerPath).ConfigureAwait(false) : string.Empty;
            if (marker != "HIKER_CODEX_INTEGRATION_OK\n")
            {
                throw new InvalidDataException("Codex did not produce the exact integration marker");
            }

            var checkpoint = await adapter.CheckpointAsync(started.Handle, "task-1").ConfigureAwait(false);
            var currentWorkspace = await inspector.InspectAsync(worktree).ConfigureAwait(false);
            var recoveryWatch = Stopwatch.StartNew();
            var recovered = await adapter.RecoverAsync(new RuntimeRecoveryRequest(
                started.Handle with { Lifecycle = RuntimeLifecycle.Interrupted },
                checkpoint,
                "runtime-input/context.txt",
                currentWorkspace.StatusFingerprint)).ConfigureAwait(false);
            var recoveredTerminal = await WaitForTerminalAsync(adapter, recovered, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
            recoveryWatch.Stop();
            if (recoveredTerminal.Lifecycle != RuntimeLifecycle.Completed)
            {
                throw new InvalidOperationException($"Codex recovery execution ended as {recoveredTerminal.Lifecycle}: {recoveredTerminal.DetailCode}");
            }
            if (await File.ReadAllTextAsync(markerPath).ConfigureAwait(false) != marker)
            {
                throw new InvalidDataException("Codex recovery replayed or changed the completed domain action");
            }

            var events = await adapter.CollectEventsAsync(recovered).ConfigureAwait(false);
            await adapter.ShutdownAsync(recovered).ConfigureAwait(false);
            var health = await store.HealthCheckAsync().ConfigureAwait(false);
            File.Delete(markerPath);
            await store.DisposeAsync().ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            var removed = await worktrees.RemoveIfCleanAsync(repository, worktree).ConfigureAwait(false);
            return new[]
            {
                new VerificationCheck(
                    "REAL-CODEX-RUNTIME",
                    removed == "REMOVED" && health.Status == "HEALTHY" ? "PASS" : "BLOCKED",
                    "REAL_INTEGRATION_PROVEN",
                    "Real installed Codex CLI completed Task → isolated Worktree → Checkpoint → session Recover without replaying the domain write",
                    new Dictionary<string, object?>
                    {
                        ["codex_version"] = probe.Version,
                        ["initial_runtime_ms"] = startedAt.Elapsed.TotalMilliseconds,
                        ["recovery_runtime_ms"] = recoveryWatch.Elapsed.TotalMilliseconds,
                        ["checkpoint_id"] = checkpoint.CheckpointId,
                        ["checkpoint_fingerprint"] = checkpoint.SourceFingerprint,
                        ["marker_sha256"] = Hash(marker),
                        ["collected_event_metadata"] = events.Count,
                        ["worktree_release"] = removed,
                        ["state_health"] = health.Status,
                        ["full_prompt_persisted_in_report"] = false
                    })
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            NormalizeAndDelete(root);
        }
    }

    private static async Task<RuntimeStatus> WaitForTerminalAsync(
        IExecutionRuntimeAdapter adapter,
        RuntimeHandle handle,
        TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            var status = await adapter.StatusAsync(handle).ConfigureAwait(false);
            if (status.Lifecycle is RuntimeLifecycle.Completed or RuntimeLifecycle.Failed or RuntimeLifecycle.Cancelled or RuntimeLifecycle.Interrupted)
            {
                return status;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new TimeoutException("Codex runtime did not reach a terminal state within the bounded integration timeout");
    }

    private static Task<ProcessResult> GitAsync(string workingDirectory, params string[] arguments) =>
        ProcessTools.RequireAsync("git", workingDirectory, arguments, TimeSpan.FromMinutes(2));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void NormalizeAndDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}

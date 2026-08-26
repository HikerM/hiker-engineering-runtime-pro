using System.Collections.Concurrent;
using System.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;

namespace Hiker.Pro.Verification;

internal sealed record RuntimeWorkerResult(int Started, int Completed, int Failures, double DurationMs);

internal sealed class RuntimeWorkerSimulation(StateCoordinator state, string root)
{
    public async Task<IReadOnlyDictionary<int, RuntimeWorkerResult>> RunAsync()
    {
        var results = new Dictionary<int, RuntimeWorkerResult>();
        foreach (var workers in new[] { 1, 2, 4, 8 })
        {
            var stopwatch = Stopwatch.StartNew();
            var runs = await Task.WhenAll(Enumerable.Range(0, workers).Select(index => RunWorkerAsync(workers, index))).ConfigureAwait(false);
            stopwatch.Stop();
            results[workers] = new RuntimeWorkerResult(
                runs.Count(static result => result.Started),
                runs.Count(static result => result.Completed),
                runs.Count(static result => !result.Started || !result.Completed),
                stopwatch.Elapsed.TotalMilliseconds);
        }
        return results;
    }

    private async Task<WorkerRun> RunWorkerAsync(int groupSize, int index)
    {
        var taskId = $"runtime-worker-{groupSize}-{index}";
        var runtimeId = $"runtime-{groupSize}-{index}";
        var worktreeId = $"worktree-{groupSize}-{index}";
        var workspace = Path.Combine(root, "runtime-workers", groupSize.ToString(System.Globalization.CultureInfo.InvariantCulture), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workspace);
        var adapter = new VerificationRuntimeAdapter($"verification-adapter-{groupSize}-{index}");
        var coordinator = new RuntimeCoordinator(state, new VerificationWorkspaceInspector(), new ProcessIdentityProbe());
        var start = await coordinator.StartAsync(
            $"runtime-worker-start-{groupSize}-{index}",
            new RuntimeStartRequest(
                "aging-project",
                taskId,
                runtimeId,
                workspace,
                "artifact:bounded-context",
                "artifact:bounded-instruction",
                1,
                $"runtime-worker-source-{groupSize}-{index}",
                WorktreeId: worktreeId,
                PredictedAffectedScopes: new[] { $"services/worker-{groupSize}-{index}" }),
            adapter).ConfigureAwait(false);
        if (start.Status != "RUNNING")
        {
            return new WorkerRun(false, false);
        }

        var complete = await new RuntimeLifecycleCoordinator(state).CompleteAsync(new RuntimeCompletionRequest(
            $"runtime-worker-complete-{groupSize}-{index}",
            "aging-project",
            taskId,
            worktreeId,
            start.Handle,
            adapter,
            RuntimeLifecycle.Completed,
            1,
            $"runtime-worker-source-{groupSize}-{index}")).ConfigureAwait(false);
        return new WorkerRun(true, complete.Status == "COMPLETED");
    }

    private sealed record WorkerRun(bool Started, bool Completed);
}

internal sealed class VerificationWorkspaceInspector : IWorkspaceInspector
{
    public Task<WorkspaceSnapshot> InspectAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspacePath);
        return Task.FromResult(new WorkspaceSnapshot(
            root,
            Path.Combine(root, ".git"),
            "verification-head",
            "verification",
            false,
            $"workspace-{Path.GetFileName(root)}",
            DateTimeOffset.UtcNow));
    }
}

internal sealed class VerificationRuntimeAdapter(string adapterId) : IExecutionRuntimeAdapter
{
    private readonly ConcurrentDictionary<string, RuntimeLifecycle> _states = new(StringComparer.Ordinal);

    public string AdapterId { get; } = adapterId;
    public RuntimeCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

    public Task<RuntimeHandle> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken = default)
    {
        _states[request.RuntimeId] = RuntimeLifecycle.Starting;
        return Task.FromResult(Handle(request.RuntimeId, RuntimeLifecycle.Starting));
    }

    public Task<RuntimeHandle> ResumeAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Transition(handle, RuntimeLifecycle.Running);
    public Task<RuntimeHandle> PauseAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Transition(handle, RuntimeLifecycle.Paused);
    public Task<RuntimeHandle> InterruptAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Transition(handle, RuntimeLifecycle.Interrupted);
    public Task<RuntimeHandle> CancelAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Transition(handle, RuntimeLifecycle.Cancelled);

    public Task<RuntimeStatus> StatusAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeStatus(handle.RuntimeId, _states.GetValueOrDefault(handle.RuntimeId, RuntimeLifecycle.Interrupted), null, DateTimeOffset.UtcNow, "VERIFICATION_RUNTIME"));

    public Task<RuntimeCheckpoint> CheckpointAsync(RuntimeHandle handle, string taskId, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeCheckpoint($"checkpoint-{handle.RuntimeId}", handle.RuntimeId, taskId, "artifact:checkpoint", "checkpoint-source", DateTimeOffset.UtcNow));

    public Task<RuntimeCollectedOutput> CollectOutputAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeCollectedOutput(handle.RuntimeId, "BOUNDED_WORKER_OUTPUT", Array.Empty<string>(), false));

    public Task<IReadOnlyList<string>> CollectArtifactsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<RuntimeCollectedEvent>> CollectEventsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RuntimeCollectedEvent>>(Array.Empty<RuntimeCollectedEvent>());

    public Task<RuntimeHandle> RecoverAsync(RuntimeRecoveryRequest request, CancellationToken cancellationToken = default) =>
        Transition(request.PriorHandle, RuntimeLifecycle.Running);

    public Task ShutdownAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        _states[handle.RuntimeId] = RuntimeLifecycle.Completed;
        return Task.CompletedTask;
    }

    private Task<RuntimeHandle> Transition(RuntimeHandle handle, RuntimeLifecycle lifecycle)
    {
        _states[handle.RuntimeId] = lifecycle;
        return Task.FromResult(handle with { Lifecycle = lifecycle });
    }

    private RuntimeHandle Handle(string runtimeId, RuntimeLifecycle lifecycle) => new(
        runtimeId,
        AdapterId,
        lifecycle,
        null,
        DateTimeOffset.UtcNow,
        $"recovery-{runtimeId}");
}

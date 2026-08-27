using Hiker.Pro.Environment;
using Hiker.Pro.State;

namespace Hiker.Pro.Execution;

public sealed class RuntimeLifecycleCoordinator(
    StateCoordinator coordinator,
    TimeProvider? timeProvider = null,
    EnvironmentReleaseCoordinator? environmentRelease = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RuntimeCompletionResult> CompleteAsync(RuntimeCompletionRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var finalOperation = $"{request.OperationId}:release";
        var receipt = await coordinator.ReadOperationReceiptAsync(finalOperation, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            return new RuntimeCompletionResult("IDEMPOTENT_REPLAY", receipt.Generation ?? 0, null, Array.Empty<string>(), Array.Empty<RuntimeCollectedEvent>());
        }

        var prepareOperation = $"{request.OperationId}:drain";
        var preparedReceipt = await coordinator.ReadOperationReceiptAsync(prepareOperation, cancellationToken).ConfigureAwait(false);
        if (preparedReceipt?.Status == "COMPLETE")
        {
            return new RuntimeCompletionResult("RECOVERY_REQUIRED", preparedReceipt.Generation ?? 0, null, Array.Empty<string>(), Array.Empty<RuntimeCollectedEvent>());
        }

        var state = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateOwner(request, state);
        var output = await request.Adapter.CollectOutputAsync(request.Handle, cancellationToken).ConfigureAwait(false);
        var artifacts = (await request.Adapter.CollectArtifactsAsync(request.Handle, cancellationToken).ConfigureAwait(false)).Take(256).ToArray();
        var events = (await request.Adapter.CollectEventsAsync(request.Handle, cancellationToken).ConfigureAwait(false)).Take(256).ToArray();
        var now = _clock.GetUtcNow();
        var lease = RuntimeStateCodec.Parse<RuntimeLease>(state.Lease!);
        var task = RuntimeStateCodec.Parse<RuntimeTaskState>(state.Task!);
        var agent = RuntimeStateCodec.Parse<RuntimeAgentState>(state.Agent!);
        var worktree = RuntimeStateCodec.Parse<RuntimeWorktreeState>(state.Worktree!);
        var drain = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            prepareOperation,
            new[]
            {
                RuntimeStateCodec.Mutation(state.TaskKey, state.Task, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                    task with { Status = RuntimeLifecycle.Waiting, UpdatedAt = now }),
                RuntimeStateCodec.Mutation(state.AgentKey, state.Agent, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                    agent with { Status = RuntimeLifecycle.Waiting, UpdatedAt = now }),
                RuntimeStateCodec.Mutation(state.LeaseKey, state.Lease, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                    lease with { Status = "DRAINING", HeartbeatAt = now, ExpiresAt = now.AddMinutes(2) }),
                RuntimeStateCodec.Mutation(state.WorktreeKey, state.Worktree, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                    worktree with { Status = "DRAINING", UpdatedAt = now })
            },
            "TASK_DRAINING",
            "RUNTIME_OUTPUT_COLLECTED",
            RuntimeStateCodec.Json(new { request.TaskId, output.SummaryCode, artifact_count = artifacts.Length, event_count = events.Length })), cancellationToken).ConfigureAwait(false);
        if (!drain.Committed)
        {
            return new RuntimeCompletionResult(drain.Status, drain.CurrentGeneration, output, artifacts, events);
        }

        try
        {
            await request.Adapter.ShutdownAsync(request.Handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RuntimeCompletionResult("RECOVERY_REQUIRED", drain.CurrentGeneration, output, artifacts, events);
        }

        var terminal = await request.Adapter.StatusAsync(request.Handle, cancellationToken).ConfigureAwait(false);
        if (terminal.Lifecycle is RuntimeLifecycle.Starting or RuntimeLifecycle.Running or RuntimeLifecycle.Waiting)
        {
            return new RuntimeCompletionResult("RECOVERY_REQUIRED", drain.CurrentGeneration, output, artifacts, events);
        }

        if (request.EnvironmentInstanceId is not null)
        {
            if (environmentRelease is null)
            {
                return new RuntimeCompletionResult("RECOVERY_REQUIRED", drain.CurrentGeneration, output, artifacts, events);
            }
            var environment = await environmentRelease.ReleaseAsync(new EnvironmentReleaseRequest(
                $"{request.OperationId}:environment",
                request.ProjectId,
                request.TaskId,
                request.Handle.RuntimeId,
                request.EnvironmentInstanceId,
                request.SourceFingerprint), cancellationToken).ConfigureAwait(false);
            if (environment.Status is not "RELEASED" and not "ALREADY_RELEASED")
            {
                return new RuntimeCompletionResult("RECOVERY_REQUIRED", environment.Generation, output, artifacts, events);
            }
        }

        var drained = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
        var drainedLease = RuntimeStateCodec.Parse<RuntimeLease>(drained.Lease!);
        var drainedTask = RuntimeStateCodec.Parse<RuntimeTaskState>(drained.Task!);
        var drainedAgent = RuntimeStateCodec.Parse<RuntimeAgentState>(drained.Agent!);
        var drainedOwnership = RuntimeStateCodec.Parse<RuntimeOwnershipState>(drained.Ownership!);
        var drainedWorktree = RuntimeStateCodec.Parse<RuntimeWorktreeState>(drained.Worktree!);
        var completedAt = _clock.GetUtcNow();
        var eventType = request.Outcome switch
        {
            RuntimeLifecycle.Completed => "TASK_COMPLETED",
            RuntimeLifecycle.Failed => "TASK_FAILED",
            RuntimeLifecycle.Cancelled => "TASK_CANCELLED",
            RuntimeLifecycle.Interrupted => "TASK_INTERRUPTED",
            _ => throw new InvalidOperationException("Unsupported terminal outcome")
        };
        var release = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            finalOperation,
            new[]
            {
                RuntimeStateCodec.Mutation(drained.TaskKey, drained.Task, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                    drainedTask with { Status = request.Outcome, UpdatedAt = completedAt }, RetentionTier.Hot),
                RuntimeStateCodec.Mutation(drained.AgentKey, drained.Agent, request.GoalRevision, StateLifecycle.Archived, request.SourceFingerprint,
                    drainedAgent with { Status = request.Outcome, UpdatedAt = completedAt }, RetentionTier.Warm),
                RuntimeStateCodec.Mutation(drained.LeaseKey, drained.Lease, request.GoalRevision, StateLifecycle.Archived, request.SourceFingerprint,
                    drainedLease with { Status = "RELEASED", HeartbeatAt = completedAt, ExpiresAt = completedAt }, RetentionTier.Warm),
                RuntimeStateCodec.Mutation(drained.OwnershipKey, drained.Ownership, request.GoalRevision, StateLifecycle.Archived, request.SourceFingerprint,
                    drainedOwnership with { UpdatedAt = completedAt }, RetentionTier.Warm),
                RuntimeStateCodec.Mutation(drained.WorktreeKey, drained.Worktree, request.GoalRevision, StateLifecycle.Archived, request.SourceFingerprint,
                    drainedWorktree with { Status = "RELEASED", UpdatedAt = completedAt }, RetentionTier.Warm)
            },
            eventType,
            "RUNTIME_RESOURCES_RELEASED",
            RuntimeStateCodec.Json(new { request.TaskId, outcome = request.Outcome, output.SummaryCode, artifact_refs = artifacts }),
            ResultJson: RuntimeStateCodec.Json(new { outcome = request.Outcome, artifact_refs = artifacts })), cancellationToken).ConfigureAwait(false);
        return new RuntimeCompletionResult(release.Committed ? request.Outcome.ToString().ToUpperInvariant() : "RECOVERY_REQUIRED", release.CurrentGeneration, output, artifacts, events);
    }

    private async Task<CompletionState> ReadAsync(RuntimeCompletionRequest request, CancellationToken cancellationToken)
    {
        var state = CompletionState.Keys(request.ProjectId, request.TaskId, request.Handle.RuntimeId, request.WorktreeId);
        return state with
        {
            Task = await coordinator.ReadAsync(state.TaskKey, cancellationToken).ConfigureAwait(false),
            Agent = await coordinator.ReadAsync(state.AgentKey, cancellationToken).ConfigureAwait(false),
            Lease = await coordinator.ReadAsync(state.LeaseKey, cancellationToken).ConfigureAwait(false),
            Ownership = await coordinator.ReadAsync(state.OwnershipKey, cancellationToken).ConfigureAwait(false),
            Worktree = await coordinator.ReadAsync(state.WorktreeKey, cancellationToken).ConfigureAwait(false)
        };
    }

    private static void ValidateOwner(RuntimeCompletionRequest request, CompletionState state)
    {
        if (state.Task is null || state.Agent is null || state.Lease is null || state.Ownership is null || state.Worktree is null)
        {
            throw new InvalidOperationException("Completion requires all writer facts");
        }

        if (!string.Equals(RuntimeStateCodec.Parse<RuntimeTaskState>(state.Task).RuntimeId, request.Handle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(RuntimeStateCodec.Parse<RuntimeLease>(state.Lease).RuntimeId, request.Handle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(RuntimeStateCodec.Parse<RuntimeOwnershipState>(state.Ownership).WriterRuntimeId, request.Handle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(RuntimeStateCodec.Parse<RuntimeWorktreeState>(state.Worktree).WriterRuntimeId, request.Handle.RuntimeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime does not own all completion facts");
        }
    }

    private static void Validate(RuntimeCompletionRequest request)
    {
        if (request.Outcome is not (RuntimeLifecycle.Completed or RuntimeLifecycle.Failed or RuntimeLifecycle.Interrupted or RuntimeLifecycle.Cancelled))
        {
            throw new ArgumentException("Completion outcome must be terminal", nameof(request));
        }

        _ = new StateKey(request.ProjectId, EntityKinds.Worktree, request.WorktreeId).Normalize();
    }

    private sealed record CompletionState(
        StateKey TaskKey,
        StateKey AgentKey,
        StateKey LeaseKey,
        StateKey OwnershipKey,
        StateKey WorktreeKey,
        StateSnapshot? Task,
        StateSnapshot? Agent,
        StateSnapshot? Lease,
        StateSnapshot? Ownership,
        StateSnapshot? Worktree)
    {
        public static CompletionState Keys(string projectId, string taskId, string runtimeId, string worktreeId) => new(
            new StateKey(projectId, EntityKinds.Task, taskId),
            new StateKey(projectId, EntityKinds.Agent, runtimeId),
            new StateKey(projectId, EntityKinds.RuntimeLease, taskId),
            new StateKey(projectId, EntityKinds.Ownership, taskId),
            new StateKey(projectId, EntityKinds.Worktree, worktreeId),
            null, null, null, null, null);
    }
}

using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Artifacts;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;

namespace Hiker.Pro.Execution;

public sealed class HandoffCoordinator(
    StateCoordinator coordinator,
    ContentAddressedArtifactStore artifactStore,
    IWorkspaceInspector workspaceInspector,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HandoffResult> HandoffAsync(HandoffRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var finalOperationId = $"{request.OperationId}:activate";
        var finalReceipt = await coordinator.ReadOperationReceiptAsync(finalOperationId, cancellationToken).ConfigureAwait(false);
        if (finalReceipt?.Status == "COMPLETE")
        {
            return new HandoffResult("IDEMPOTENT_REPLAY", null, null, null, finalReceipt.Generation ?? 0);
        }

        var transferOperationId = $"{request.OperationId}:transfer";
        var transferReceipt = await coordinator.ReadOperationReceiptAsync(transferOperationId, cancellationToken).ConfigureAwait(false);
        if (transferReceipt?.Status == "COMPLETE")
        {
            return new HandoffResult("RECOVERY_REQUIRED", null, null, null, transferReceipt.Generation ?? 0);
        }

        var currentStates = await ReadStatesAsync(request.ProjectId, request.TaskId, request.CurrentHandle.RuntimeId, request.TargetRuntimeId, request.WorktreeId, cancellationToken).ConfigureAwait(false);
        ValidateCurrentOwner(request, currentStates);
        var status = await request.CurrentAdapter.StatusAsync(request.CurrentHandle, cancellationToken).ConfigureAwait(false);
        if (status.Lifecycle is not (RuntimeLifecycle.Running or RuntimeLifecycle.Waiting or RuntimeLifecycle.Paused))
        {
            throw new InvalidOperationException("Current runtime is not in a handoff-safe lifecycle");
        }

        var paused = status.Lifecycle == RuntimeLifecycle.Paused
            ? request.CurrentHandle with { Lifecycle = RuntimeLifecycle.Paused }
            : await request.CurrentAdapter.PauseAsync(request.CurrentHandle, cancellationToken).ConfigureAwait(false);
        if (paused.Lifecycle != RuntimeLifecycle.Paused)
        {
            throw new InvalidOperationException("Current runtime did not pause for handoff");
        }

        var workspace = await workspaceInspector.InspectAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
        var checkpoint = await request.CurrentAdapter.CheckpointAsync(paused, request.TaskId, cancellationToken).ConfigureAwait(false);
        ValidateCheckpoint(request, checkpoint);
        var handoffArtifact = await WriteHandoffArtifactAsync(request, workspace, checkpoint, cancellationToken).ConfigureAwait(false);

        var targetStartRequest = new RuntimeStartRequest(
            request.ProjectId,
            request.TaskId,
            request.TargetRuntimeId,
            workspace.Root,
            handoffArtifact.RelativeRef,
            handoffArtifact.RelativeRef,
            request.GoalRevision,
            request.SourceFingerprint,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["handoff_format"] = "hiker-minimal-handoff/v1" },
            request.WorktreeId);
        var target = await request.TargetAdapter.StartAsync(targetStartRequest, cancellationToken).ConfigureAwait(false);
        if (target.Lifecycle != RuntimeLifecycle.Starting)
        {
            await request.TargetAdapter.ShutdownAsync(target, cancellationToken).ConfigureAwait(false);
            await BestEffortResumeAsync(request.CurrentAdapter, paused, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Target runtime must start suspended");
        }

        var now = _clock.GetUtcNow();
        var transfer = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            transferOperationId,
            TransferMutations(request, currentStates, workspace, checkpoint, paused, target, now),
            "AGENT_HANDOFF",
            "HANDOFF_OWNERSHIP_TRANSFERRED",
            RuntimeStateCodec.Json(new
            {
                request.TaskId,
                from_runtime = paused.RuntimeId,
                to_runtime = target.RuntimeId,
                checkpoint_id = checkpoint.CheckpointId,
                handoff_ref = handoffArtifact.RelativeRef
            }),
            ResultJson: RuntimeStateCodec.Json(new { checkpoint_id = checkpoint.CheckpointId, handoff_ref = handoffArtifact.RelativeRef })), cancellationToken).ConfigureAwait(false);
        if (!transfer.Committed)
        {
            await request.TargetAdapter.ShutdownAsync(target, cancellationToken).ConfigureAwait(false);
            await BestEffortResumeAsync(request.CurrentAdapter, paused, cancellationToken).ConfigureAwait(false);
            return new HandoffResult(transfer.Status, null, checkpoint, handoffArtifact, transfer.CurrentGeneration);
        }

        RuntimeHandle recovered;
        try
        {
            recovered = await request.TargetAdapter.RecoverAsync(new RuntimeRecoveryRequest(
                target,
                checkpoint,
                handoffArtifact.RelativeRef,
                workspace.StatusFingerprint), cancellationToken).ConfigureAwait(false);
            if (recovered.Lifecycle != RuntimeLifecycle.Running)
            {
                throw new InvalidOperationException("Target runtime did not recover into RUNNING");
            }
        }
        catch
        {
            await BestEffortInterruptAsync(request.TargetAdapter, target, cancellationToken).ConfigureAwait(false);
            return new HandoffResult("RECOVERY_REQUIRED", null, checkpoint, handoffArtifact, transfer.CurrentGeneration);
        }

        var transferredStates = await ReadStatesAsync(request.ProjectId, request.TaskId, request.CurrentHandle.RuntimeId, request.TargetRuntimeId, request.WorktreeId, cancellationToken).ConfigureAwait(false);
        var activate = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            finalOperationId,
            ActivationMutations(request, transferredStates, workspace, checkpoint, recovered, _clock.GetUtcNow()),
            "TASK_RECOVERED",
            "HANDOFF_TARGET_RUNNING",
            RuntimeStateCodec.Json(new { request.TaskId, runtime_id = recovered.RuntimeId, checkpoint_id = checkpoint.CheckpointId }),
            ResultJson: RuntimeStateCodec.Json(new { checkpoint_id = checkpoint.CheckpointId, handoff_ref = handoffArtifact.RelativeRef })), cancellationToken).ConfigureAwait(false);
        if (!activate.Committed)
        {
            await BestEffortInterruptAsync(request.TargetAdapter, recovered, cancellationToken).ConfigureAwait(false);
            return new HandoffResult("RECOVERY_REQUIRED", null, checkpoint, handoffArtifact, activate.CurrentGeneration);
        }

        await request.CurrentAdapter.ShutdownAsync(paused, cancellationToken).ConfigureAwait(false);
        return new HandoffResult("RUNNING", recovered, checkpoint, handoffArtifact, activate.CurrentGeneration);
    }

    private async Task<ArtifactRecord> WriteHandoffArtifactAsync(
        HandoffRequest request,
        WorkspaceSnapshot workspace,
        RuntimeCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var payload = RuntimeStateCodec.Json(new
        {
            format = "hiker-minimal-handoff/v1",
            request.ProjectId,
            request.TaskId,
            goal_revision = request.GoalRevision,
            from_runtime = request.CurrentHandle.RuntimeId,
            to_runtime = request.TargetRuntimeId,
            checkpoint = new
            {
                checkpoint.CheckpointId,
                checkpoint.StateArtifactRef,
                checkpoint.SourceFingerprint,
                checkpoint.CreatedAt
            },
            workspace = new
            {
                workspace.Head,
                workspace.Branch,
                workspace.Dirty,
                workspace.StatusFingerprint
            },
            relevant_state_refs = request.RelevantStateRefs.Take(64).ToArray(),
            acceptance_ids = request.AcceptanceIds.Take(64).ToArray(),
            request.SourceFingerprint
        });
        return await artifactStore.PutAsync(new ArtifactPutRequest(
            request.ProjectId,
            "MINIMAL_HANDOFF",
            new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false),
            request.TaskId,
            null,
            request.SourceFingerprint,
            RetentionTier.Warm), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<StateMutation> TransferMutations(
        HandoffRequest request,
        HandoffStates states,
        WorkspaceSnapshot workspace,
        RuntimeCheckpoint checkpoint,
        RuntimeHandle paused,
        RuntimeHandle target,
        DateTimeOffset now)
    {
        var epoch = RuntimeStateCodec.Parse<RuntimeLease>(states.Lease!).Epoch + 1;
        return new[]
        {
            RuntimeStateCodec.Mutation(states.TaskKey, states.Task, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeTaskState(request.TaskId, target.RuntimeId, target.AdapterId, RuntimeLifecycle.Starting, request.GoalRevision, workspace.StatusFingerprint, checkpoint.CheckpointId, now)),
            RuntimeStateCodec.Mutation(states.CurrentAgentKey, states.CurrentAgent, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeAgentState(paused.RuntimeId, paused.AdapterId, RuntimeLifecycle.Paused, paused.ProcessIdentity, Hash(paused.RecoveryToken), now)),
            RuntimeStateCodec.Mutation(states.TargetAgentKey, states.TargetAgent, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeAgentState(target.RuntimeId, target.AdapterId, RuntimeLifecycle.Starting, target.ProcessIdentity, Hash(target.RecoveryToken), now)),
            RuntimeStateCodec.Mutation(states.LeaseKey, states.Lease, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeLease(request.ProjectId, request.TaskId, target.RuntimeId, target.AdapterId, target.ProcessIdentity, now, now, now.AddMinutes(2), "RESERVED", epoch)),
            RuntimeStateCodec.Mutation(states.OwnershipKey, states.Ownership, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeOwnershipState(request.TaskId, target.RuntimeId, workspace.Root, workspace.StatusFingerprint, now)),
            RuntimeStateCodec.Mutation(states.WorktreeKey, states.Worktree, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                RuntimeStateCodec.Parse<RuntimeWorktreeState>(states.Worktree!) with { WriterRuntimeId = target.RuntimeId, UpdatedAt = now }),
            RuntimeStateCodec.Mutation(states.CheckpointKey(checkpoint.CheckpointId), null, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, checkpoint, RetentionTier.Warm)
        };
    }

    private static IReadOnlyList<StateMutation> ActivationMutations(
        HandoffRequest request,
        HandoffStates states,
        WorkspaceSnapshot workspace,
        RuntimeCheckpoint checkpoint,
        RuntimeHandle recovered,
        DateTimeOffset now)
    {
        var lease = RuntimeStateCodec.Parse<RuntimeLease>(states.Lease!);
        return new[]
        {
            RuntimeStateCodec.Mutation(states.TaskKey, states.Task, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeTaskState(request.TaskId, recovered.RuntimeId, recovered.AdapterId, RuntimeLifecycle.Running, request.GoalRevision, workspace.StatusFingerprint, checkpoint.CheckpointId, now)),
            RuntimeStateCodec.Mutation(states.TargetAgentKey, states.TargetAgent, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeAgentState(recovered.RuntimeId, recovered.AdapterId, RuntimeLifecycle.Running, recovered.ProcessIdentity, Hash(recovered.RecoveryToken), now)),
            RuntimeStateCodec.Mutation(states.LeaseKey, states.Lease, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                lease with { ProcessIdentity = recovered.ProcessIdentity, HeartbeatAt = now, ExpiresAt = now.AddMinutes(2), Status = "ACTIVE" }),
            RuntimeStateCodec.Mutation(states.OwnershipKey, states.Ownership, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                new RuntimeOwnershipState(request.TaskId, recovered.RuntimeId, workspace.Root, workspace.StatusFingerprint, now)),
            RuntimeStateCodec.Mutation(states.WorktreeKey, states.Worktree, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint,
                RuntimeStateCodec.Parse<RuntimeWorktreeState>(states.Worktree!) with { WriterRuntimeId = recovered.RuntimeId, UpdatedAt = now })
        };
    }

    private async Task<HandoffStates> ReadStatesAsync(
        string projectId,
        string taskId,
        string currentRuntimeId,
        string targetRuntimeId,
        string worktreeId,
        CancellationToken cancellationToken)
    {
        var state = HandoffStates.Keys(projectId, taskId, currentRuntimeId, targetRuntimeId, worktreeId);
        return state with
        {
            Task = await coordinator.ReadAsync(state.TaskKey, cancellationToken).ConfigureAwait(false),
            CurrentAgent = await coordinator.ReadAsync(state.CurrentAgentKey, cancellationToken).ConfigureAwait(false),
            TargetAgent = await coordinator.ReadAsync(state.TargetAgentKey, cancellationToken).ConfigureAwait(false),
            Lease = await coordinator.ReadAsync(state.LeaseKey, cancellationToken).ConfigureAwait(false),
            Ownership = await coordinator.ReadAsync(state.OwnershipKey, cancellationToken).ConfigureAwait(false),
            Worktree = await coordinator.ReadAsync(state.WorktreeKey, cancellationToken).ConfigureAwait(false)
        };
    }

    private static void ValidateCurrentOwner(HandoffRequest request, HandoffStates states)
    {
        if (states.Task is null || states.CurrentAgent is null || states.Lease is null || states.Ownership is null || states.Worktree is null)
        {
            throw new InvalidOperationException("Handoff requires complete task, agent, lease, and ownership state");
        }

        var task = RuntimeStateCodec.Parse<RuntimeTaskState>(states.Task);
        var lease = RuntimeStateCodec.Parse<RuntimeLease>(states.Lease);
        var ownership = RuntimeStateCodec.Parse<RuntimeOwnershipState>(states.Ownership);
        var worktree = RuntimeStateCodec.Parse<RuntimeWorktreeState>(states.Worktree);
        if (!string.Equals(task.RuntimeId, request.CurrentHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(lease.RuntimeId, request.CurrentHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(ownership.WriterRuntimeId, request.CurrentHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(worktree.WriterRuntimeId, request.CurrentHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(worktree.WorktreeId, request.WorktreeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Current runtime does not own all writer facts");
        }
    }

    private static void Validate(HandoffRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Agent, request.TargetRuntimeId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Worktree, request.WorktreeId).Normalize();
        if (string.Equals(request.CurrentHandle.RuntimeId, request.TargetRuntimeId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Handoff target runtime must be different from current runtime");
        }

        if (!request.CurrentAdapter.Capabilities.Pause || !request.CurrentAdapter.Capabilities.Checkpoint || !request.TargetAdapter.Capabilities.Recover)
        {
            throw new InvalidOperationException("Adapters do not satisfy safe handoff capabilities");
        }

        if (request.RelevantStateRefs.Count > 256 || request.AcceptanceIds.Count > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Handoff inputs exceed bounded context limits");
        }
    }

    private static void ValidateCheckpoint(HandoffRequest request, RuntimeCheckpoint checkpoint)
    {
        if (!string.Equals(checkpoint.TaskId, request.TaskId, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.RuntimeId, request.CurrentHandle.RuntimeId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(checkpoint.StateArtifactRef) ||
            string.IsNullOrWhiteSpace(checkpoint.SourceFingerprint))
        {
            throw new InvalidDataException("Runtime returned an invalid checkpoint");
        }
    }

    private static async Task BestEffortResumeAsync(IExecutionRuntimeAdapter adapter, RuntimeHandle handle, CancellationToken cancellationToken)
    {
        try { _ = await adapter.ResumeAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private static async Task BestEffortInterruptAsync(IExecutionRuntimeAdapter adapter, RuntimeHandle handle, CancellationToken cancellationToken)
    {
        try { _ = await adapter.InterruptAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record HandoffStates(
        StateKey TaskKey,
        StateKey CurrentAgentKey,
        StateKey TargetAgentKey,
        StateKey LeaseKey,
        StateKey OwnershipKey,
        StateKey WorktreeKey,
        StateSnapshot? Task,
        StateSnapshot? CurrentAgent,
        StateSnapshot? TargetAgent,
        StateSnapshot? Lease,
        StateSnapshot? Ownership,
        StateSnapshot? Worktree)
    {
        public StateKey CheckpointKey(string checkpointId) => new(TaskKey.ProjectId, EntityKinds.Checkpoint, checkpointId);

        public static HandoffStates Keys(string projectId, string taskId, string currentRuntimeId, string targetRuntimeId, string worktreeId) => new(
            new StateKey(projectId, EntityKinds.Task, taskId),
            new StateKey(projectId, EntityKinds.Agent, currentRuntimeId),
            new StateKey(projectId, EntityKinds.Agent, targetRuntimeId),
            new StateKey(projectId, EntityKinds.RuntimeLease, taskId),
            new StateKey(projectId, EntityKinds.Ownership, taskId),
            new StateKey(projectId, EntityKinds.Worktree, worktreeId),
            null, null, null, null, null, null);
    }
}

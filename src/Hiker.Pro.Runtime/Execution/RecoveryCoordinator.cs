using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Environment;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;

namespace Hiker.Pro.Execution;

public sealed class RecoveryCoordinator(
    StateCoordinator coordinator,
    IWorkspaceInspector workspaceInspector,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null,
    IEnvironmentRecoveryGate? environmentGate = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TaskRecoveryResult> RecoverAsync(TaskRecoveryPlan plan, CancellationToken cancellationToken = default)
    {
        Validate(plan);
        var activateOperation = $"{plan.OperationId}:activate";
        var complete = await coordinator.ReadOperationReceiptAsync(activateOperation, cancellationToken).ConfigureAwait(false);
        if (complete?.Status == "COMPLETE")
        {
            return new TaskRecoveryResult("IDEMPOTENT_REPLAY", null, complete.Generation ?? 0, "ALREADY_RECOVERED");
        }

        var reserveOperation = $"{plan.OperationId}:reserve";
        var reserved = await coordinator.ReadOperationReceiptAsync(reserveOperation, cancellationToken).ConfigureAwait(false);
        if (reserved?.Status == "COMPLETE")
        {
            return new TaskRecoveryResult("RECOVERY_REQUIRED", null, reserved.Generation ?? 0, "RESERVATION_ALREADY_COMMITTED");
        }

        var states = await ReadAsync(plan, cancellationToken).ConfigureAwait(false);
        ValidateState(plan, states);
        var workspace = await workspaceInspector.InspectAsync(plan.WorkspacePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(workspace.StatusFingerprint, plan.ExpectedWorkspaceFingerprint, StringComparison.Ordinal))
        {
            return new TaskRecoveryResult("REQUIRES_REVIEW", null, states.Task!.Generation, "WORKSPACE_DRIFT");
        }

        var lease = RuntimeStateCodec.Parse<RuntimeLease>(states.Lease!);
        RuntimeStatus? priorStatus = null;
        try
        {
            priorStatus = await plan.PriorAdapter.StatusAsync(plan.PriorHandle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The process identity and lease expiry below are the deterministic fallback.
        }

        var liveIdentity = lease.ProcessIdentity is not null &&
            await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false);
        if (liveIdentity || priorStatus?.Lifecycle is RuntimeLifecycle.Running or RuntimeLifecycle.Waiting or RuntimeLifecycle.Starting)
        {
            return new TaskRecoveryResult("REQUIRES_REVIEW", null, states.Task!.Generation, "PRIOR_WRITER_MAY_BE_LIVE");
        }

        if (priorStatus is null && lease.ExpiresAt > _clock.GetUtcNow())
        {
            return new TaskRecoveryResult("REQUIRES_REVIEW", null, states.Task!.Generation, "LEASE_NOT_EXPIRED");
        }

        if (plan.EnvironmentInstanceId is not null)
        {
            if (environmentGate is null)
            {
                return new TaskRecoveryResult("RECOVERY_REQUIRED", null, states.Task!.Generation, "ENVIRONMENT_RECOVERY_GATE_UNAVAILABLE");
            }
            var environment = await environmentGate.RevalidateAsync(
                workspace.Root,
                plan.ProjectId,
                plan.TaskId,
                plan.PriorHandle.RuntimeId,
                plan.EnvironmentInstanceId,
                cancellationToken).ConfigureAwait(false);
            if (!environment.Allowed)
            {
                return new TaskRecoveryResult("RECOVERY_REQUIRED", null, states.Task!.Generation, "ENVIRONMENT_RESOURCE_REVALIDATION_FAILED");
            }
        }

        var start = new RuntimeStartRequest(
            plan.ProjectId,
            plan.TaskId,
            plan.RecoveryRuntimeId,
            workspace.Root,
            plan.HandoffArtifactRef,
            plan.HandoffArtifactRef,
            plan.GoalRevision,
            plan.SourceFingerprint,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["recovery"] = "validated-checkpoint" },
            plan.WorktreeId);
        var target = await plan.RecoveryAdapter.StartAsync(start, cancellationToken).ConfigureAwait(false);
        if (target.Lifecycle != RuntimeLifecycle.Starting)
        {
            await plan.RecoveryAdapter.ShutdownAsync(target, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Recovery runtime must start suspended");
        }

        var now = _clock.GetUtcNow();
        var nextLease = lease with
        {
            RuntimeId = target.RuntimeId,
            AdapterId = target.AdapterId,
            ProcessIdentity = target.ProcessIdentity,
            AcquiredAt = now,
            HeartbeatAt = now,
            ExpiresAt = now.AddMinutes(2),
            Status = "RESERVED",
            Epoch = lease.Epoch + 1
        };
        var reserve = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            reserveOperation,
            new[]
            {
                RuntimeStateCodec.Mutation(states.TaskKey, states.Task, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeTaskState(plan.TaskId, target.RuntimeId, target.AdapterId, RuntimeLifecycle.Starting, plan.GoalRevision, workspace.StatusFingerprint, plan.Checkpoint.CheckpointId, now)),
                RuntimeStateCodec.Mutation(states.RecoveryAgentKey, states.RecoveryAgent, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeAgentState(target.RuntimeId, target.AdapterId, RuntimeLifecycle.Starting, target.ProcessIdentity, Hash(target.RecoveryToken), now)),
                RuntimeStateCodec.Mutation(states.LeaseKey, states.Lease, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint, nextLease),
                RuntimeStateCodec.Mutation(states.OwnershipKey, states.Ownership, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeOwnershipState(plan.TaskId, target.RuntimeId, workspace.Root, workspace.StatusFingerprint, now)),
                RuntimeStateCodec.Mutation(states.WorktreeKey, states.Worktree, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    RuntimeStateCodec.Parse<RuntimeWorktreeState>(states.Worktree!) with { WriterRuntimeId = target.RuntimeId, UpdatedAt = now })
            },
            "AGENT_CRASHED",
            "RECOVERY_WRITER_RESERVED",
            RuntimeStateCodec.Json(new { plan.TaskId, prior_runtime = plan.PriorHandle.RuntimeId, recovery_runtime = target.RuntimeId, checkpoint_id = plan.Checkpoint.CheckpointId })), cancellationToken).ConfigureAwait(false);
        if (!reserve.Committed)
        {
            await plan.RecoveryAdapter.ShutdownAsync(target, cancellationToken).ConfigureAwait(false);
            return new TaskRecoveryResult(reserve.Status, null, reserve.CurrentGeneration, "RESERVATION_CONFLICT");
        }

        if (plan.EnvironmentInstanceId is not null)
        {
            var transfer = await environmentGate!.TransferRuntimeAsync(
                $"{plan.OperationId}:environment-transfer",
                plan.ProjectId,
                plan.TaskId,
                plan.PriorHandle.RuntimeId,
                target.RuntimeId,
                plan.EnvironmentInstanceId,
                plan.GoalRevision,
                plan.SourceFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (!transfer.Committed)
            {
                await plan.RecoveryAdapter.ShutdownAsync(target, cancellationToken).ConfigureAwait(false);
                return new TaskRecoveryResult("RECOVERY_REQUIRED", null, transfer.CurrentGeneration, "ENVIRONMENT_RUNTIME_TRANSFER_FAILED");
            }
        }

        RuntimeHandle recovered;
        try
        {
            recovered = await plan.RecoveryAdapter.RecoverAsync(new RuntimeRecoveryRequest(
                target,
                plan.Checkpoint,
                plan.HandoffArtifactRef,
                workspace.StatusFingerprint), cancellationToken).ConfigureAwait(false);
            if (recovered.Lifecycle != RuntimeLifecycle.Running)
            {
                throw new InvalidOperationException("Recovery adapter did not enter RUNNING");
            }
        }
        catch
        {
            await BestEffortInterruptAsync(plan.RecoveryAdapter, target, cancellationToken).ConfigureAwait(false);
            return new TaskRecoveryResult("RECOVERY_REQUIRED", null, reserve.CurrentGeneration, "ADAPTER_RECOVERY_FAILED");
        }

        var afterReserve = await ReadAsync(plan, cancellationToken).ConfigureAwait(false);
        var reservedLease = RuntimeStateCodec.Parse<RuntimeLease>(afterReserve.Lease!);
        var activeAt = _clock.GetUtcNow();
        var activate = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            activateOperation,
            new[]
            {
                RuntimeStateCodec.Mutation(afterReserve.TaskKey, afterReserve.Task, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeTaskState(plan.TaskId, recovered.RuntimeId, recovered.AdapterId, RuntimeLifecycle.Running, plan.GoalRevision, workspace.StatusFingerprint, plan.Checkpoint.CheckpointId, activeAt)),
                RuntimeStateCodec.Mutation(afterReserve.RecoveryAgentKey, afterReserve.RecoveryAgent, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeAgentState(recovered.RuntimeId, recovered.AdapterId, RuntimeLifecycle.Running, recovered.ProcessIdentity, Hash(recovered.RecoveryToken), activeAt)),
                RuntimeStateCodec.Mutation(afterReserve.LeaseKey, afterReserve.Lease, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    reservedLease with { ProcessIdentity = recovered.ProcessIdentity, HeartbeatAt = activeAt, ExpiresAt = activeAt.AddMinutes(2), Status = "ACTIVE" }),
                RuntimeStateCodec.Mutation(afterReserve.OwnershipKey, afterReserve.Ownership, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    new RuntimeOwnershipState(plan.TaskId, recovered.RuntimeId, workspace.Root, workspace.StatusFingerprint, activeAt)),
                RuntimeStateCodec.Mutation(afterReserve.WorktreeKey, afterReserve.Worktree, plan.GoalRevision, StateLifecycle.Valid, plan.SourceFingerprint,
                    RuntimeStateCodec.Parse<RuntimeWorktreeState>(afterReserve.Worktree!) with { WriterRuntimeId = recovered.RuntimeId, UpdatedAt = activeAt })
            },
            "TASK_RECOVERED",
            "TASK_RECOVERY_COMPLETED",
            RuntimeStateCodec.Json(new { plan.TaskId, runtime_id = recovered.RuntimeId, checkpoint_id = plan.Checkpoint.CheckpointId })), cancellationToken).ConfigureAwait(false);
        if (!activate.Committed)
        {
            await BestEffortInterruptAsync(plan.RecoveryAdapter, recovered, cancellationToken).ConfigureAwait(false);
            return new TaskRecoveryResult("RECOVERY_REQUIRED", null, activate.CurrentGeneration, "ACTIVATION_CONFLICT");
        }

        return new TaskRecoveryResult("RUNNING", recovered, activate.CurrentGeneration, "RECOVERED_FROM_VALID_CHECKPOINT");
    }

    private async Task<RecoveryStates> ReadAsync(TaskRecoveryPlan plan, CancellationToken cancellationToken)
    {
        var state = RecoveryStates.Keys(plan.ProjectId, plan.TaskId, plan.RecoveryRuntimeId, plan.WorktreeId, plan.Checkpoint.CheckpointId);
        return state with
        {
            Task = await coordinator.ReadAsync(state.TaskKey, cancellationToken).ConfigureAwait(false),
            Lease = await coordinator.ReadAsync(state.LeaseKey, cancellationToken).ConfigureAwait(false),
            Ownership = await coordinator.ReadAsync(state.OwnershipKey, cancellationToken).ConfigureAwait(false),
            RecoveryAgent = await coordinator.ReadAsync(state.RecoveryAgentKey, cancellationToken).ConfigureAwait(false),
            Checkpoint = await coordinator.ReadAsync(state.CheckpointKey, cancellationToken).ConfigureAwait(false),
            Worktree = await coordinator.ReadAsync(state.WorktreeKey, cancellationToken).ConfigureAwait(false)
        };
    }

    private static void ValidateState(TaskRecoveryPlan plan, RecoveryStates states)
    {
        if (states.Task is null || states.Lease is null || states.Ownership is null || states.Checkpoint is null || states.Worktree is null)
        {
            throw new InvalidOperationException("Recovery requires task, lease, ownership, and checkpoint facts");
        }

        var lease = RuntimeStateCodec.Parse<RuntimeLease>(states.Lease);
        var ownership = RuntimeStateCodec.Parse<RuntimeOwnershipState>(states.Ownership);
        var worktree = RuntimeStateCodec.Parse<RuntimeWorktreeState>(states.Worktree);
        var checkpoint = RuntimeStateCodec.Parse<RuntimeCheckpoint>(states.Checkpoint);
        if (!string.Equals(lease.RuntimeId, plan.PriorHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(ownership.WriterRuntimeId, plan.PriorHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(worktree.WriterRuntimeId, plan.PriorHandle.RuntimeId, StringComparison.Ordinal) ||
            !string.Equals(worktree.WorktreeId, plan.WorktreeId, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.CheckpointId, plan.Checkpoint.CheckpointId, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.SourceFingerprint, plan.Checkpoint.SourceFingerprint, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.StateArtifactRef, plan.Checkpoint.StateArtifactRef, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Recovery facts do not match the prior writer or checkpoint");
        }
    }

    private static void Validate(TaskRecoveryPlan plan)
    {
        _ = new StateKey(plan.ProjectId, EntityKinds.Task, plan.TaskId).Normalize();
        _ = new StateKey(plan.ProjectId, EntityKinds.Agent, plan.RecoveryRuntimeId).Normalize();
        _ = new StateKey(plan.ProjectId, EntityKinds.Worktree, plan.WorktreeId).Normalize();
        if (!plan.RecoveryAdapter.Capabilities.Recover || string.IsNullOrWhiteSpace(plan.ExpectedWorkspaceFingerprint))
        {
            throw new InvalidOperationException("Recovery adapter or workspace fingerprint is invalid");
        }
    }

    private static async Task BestEffortInterruptAsync(IExecutionRuntimeAdapter adapter, RuntimeHandle handle, CancellationToken cancellationToken)
    {
        try { _ = await adapter.InterruptAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record RecoveryStates(
        StateKey TaskKey,
        StateKey LeaseKey,
        StateKey OwnershipKey,
        StateKey RecoveryAgentKey,
        StateKey WorktreeKey,
        StateKey CheckpointKey,
        StateSnapshot? Task,
        StateSnapshot? Lease,
        StateSnapshot? Ownership,
        StateSnapshot? RecoveryAgent,
        StateSnapshot? Worktree,
        StateSnapshot? Checkpoint)
    {
        public static RecoveryStates Keys(string projectId, string taskId, string recoveryRuntimeId, string worktreeId, string checkpointId) => new(
            new StateKey(projectId, EntityKinds.Task, taskId),
            new StateKey(projectId, EntityKinds.RuntimeLease, taskId),
            new StateKey(projectId, EntityKinds.Ownership, taskId),
            new StateKey(projectId, EntityKinds.Agent, recoveryRuntimeId),
            new StateKey(projectId, EntityKinds.Worktree, worktreeId),
            new StateKey(projectId, EntityKinds.Checkpoint, checkpointId),
            null, null, null, null, null, null);
    }
}

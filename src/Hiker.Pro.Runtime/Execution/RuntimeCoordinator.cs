using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Environment;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;

namespace Hiker.Pro.Execution;

public sealed class RuntimeCoordinator(
    StateCoordinator coordinator,
    IWorkspaceInspector workspaceInspector,
    ProcessIdentityProbe processProbe,
    ProjectBootstrapService bootstrap,
    TimeProvider? timeProvider = null,
    IEnvironmentRuntimeGate? environmentGate = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RuntimeStartResult> StartAsync(
        string operationId,
        RuntimeStartRequest request,
        IExecutionRuntimeAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ValidateStart(request, adapter);
        var project = await bootstrap.RequireActiveAsync(
            ProjectBootstrapIntent.RuntimeStart,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(project.ProjectId, request.ProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime project identity does not match the active project");
        }

        var workspace = await workspaceInspector.InspectAsync(request.WorkspacePath, cancellationToken).ConfigureAwait(false);
        await ValidateWorktreeConflictsAsync(request, workspace, cancellationToken).ConfigureAwait(false);
        if (request.EnvironmentProfileId is not null || request.EnvironmentInstanceId is not null)
        {
            if (environmentGate is null)
            {
                throw new InvalidOperationException("Environment-bound runtime requires the environment control plane");
            }
            var environment = await environmentGate.ValidateStartAsync(request, cancellationToken).ConfigureAwait(false);
            if (!environment.Allowed)
            {
                throw new InvalidOperationException($"Environment runtime gate blocked start: {string.Join(',', environment.Findings)}");
            }
        }
        var keys = RuntimeKeys(request.ProjectId, request.TaskId, request.RuntimeId, request.WorktreeId);
        var current = await ReadAsync(keys, cancellationToken).ConfigureAwait(false);
        if (current.Lease is not null && !await CanReplaceLeaseAsync(current.Lease, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Task already has a live or unexpired writer lease");
        }

        RuntimeHandle handle;
        try
        {
            handle = await adapter.StartAsync(request with { WorkspacePath = workspace.Root }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }

        if (handle.Lifecycle != RuntimeLifecycle.Starting || !string.Equals(handle.AdapterId, adapter.AdapterId, StringComparison.Ordinal))
        {
            await adapter.ShutdownAsync(handle, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Runtime adapter must start in STARTING without performing task writes");
        }

        var now = _clock.GetUtcNow();
        var lease = new RuntimeLease(
            request.ProjectId,
            request.TaskId,
            request.RuntimeId,
            adapter.AdapterId,
            handle.ProcessIdentity,
            now,
            now,
            now.AddMinutes(2),
            "RESERVED",
            (current.Lease is null ? 0 : RuntimeStateCodec.Parse<RuntimeLease>(current.Lease).Epoch) + 1);
        var reserve = await CommitRuntimeSetAsync(
            $"{operationId}:reserve",
            request,
            workspace,
            handle,
            lease,
            RuntimeLifecycle.Starting,
            current,
            "RUNTIME_STARTING",
            cancellationToken).ConfigureAwait(false);
        if (!reserve.Committed)
        {
            await adapter.ShutdownAsync(handle, cancellationToken).ConfigureAwait(false);
            return new RuntimeStartResult(reserve.Status, handle with { Lifecycle = RuntimeLifecycle.Interrupted }, reserve.CurrentGeneration);
        }

        RuntimeHandle running;
        try
        {
            running = await adapter.ResumeAsync(handle, cancellationToken).ConfigureAwait(false);
            if (running.Lifecycle != RuntimeLifecycle.Running)
            {
                throw new InvalidOperationException("Runtime adapter did not enter RUNNING after resume");
            }
        }
        catch
        {
            await BestEffortInterruptAsync(adapter, handle, cancellationToken).ConfigureAwait(false);
            throw;
        }

        var reserved = await ReadAsync(keys, cancellationToken).ConfigureAwait(false);
        var activeLease = lease with
        {
            ProcessIdentity = running.ProcessIdentity,
            HeartbeatAt = _clock.GetUtcNow(),
            ExpiresAt = _clock.GetUtcNow().AddMinutes(2),
            Status = "ACTIVE"
        };
        var activate = await CommitRuntimeSetAsync(
            $"{operationId}:activate",
            request,
            workspace,
            running,
            activeLease,
            RuntimeLifecycle.Running,
            reserved,
            "TASK_STARTED",
            cancellationToken).ConfigureAwait(false);
        if (!activate.Committed)
        {
            await BestEffortInterruptAsync(adapter, running, cancellationToken).ConfigureAwait(false);
            return new RuntimeStartResult("RECOVERY_REQUIRED", running with { Lifecycle = RuntimeLifecycle.Interrupted }, activate.CurrentGeneration);
        }

        return new RuntimeStartResult("RUNNING", running, activate.CurrentGeneration);
    }

    public async Task<BatchTransactionResult> HeartbeatAsync(
        string operationId,
        string projectId,
        string taskId,
        string runtimeId,
        string sourceFingerprint,
        long expectedLeaseGeneration,
        DateTimeOffset heartbeatAt,
        CancellationToken cancellationToken = default)
    {
        var leaseKey = new StateKey(projectId, EntityKinds.RuntimeLease, taskId);
        var leaseState = await coordinator.ReadAsync(leaseKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime lease does not exist");
        var lease = RuntimeStateCodec.Parse<RuntimeLease>(leaseState);
        if (!string.Equals(lease.RuntimeId, runtimeId, StringComparison.Ordinal) || lease.Status != "ACTIVE")
        {
            throw new InvalidOperationException("Heartbeat does not own the active lease");
        }

        if (heartbeatAt < lease.HeartbeatAt)
        {
            throw new InvalidOperationException("Heartbeat cannot move backwards");
        }

        var updated = lease with { HeartbeatAt = heartbeatAt, ExpiresAt = heartbeatAt.AddMinutes(2) };
        return await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { RuntimeStateCodec.Mutation(leaseKey, leaseState, 0, StateLifecycle.Valid, sourceFingerprint, updated) with { ExpectedGeneration = expectedLeaseGeneration } },
            "LEASE_HEARTBEAT",
            "LEASE_RENEWED",
            "{}"), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CanReplaceLeaseAsync(StateSnapshot leaseState, CancellationToken cancellationToken)
    {
        if (leaseState.Lifecycle is StateLifecycle.Archived or StateLifecycle.Superseded or StateLifecycle.Orphaned)
        {
            return true;
        }

        var lease = RuntimeStateCodec.Parse<RuntimeLease>(leaseState);
        if (lease.ProcessIdentity is not null && await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return lease.ExpiresAt <= _clock.GetUtcNow();
    }

    private async Task<BatchTransactionResult> CommitRuntimeSetAsync(
        string operationId,
        RuntimeStartRequest request,
        WorkspaceSnapshot workspace,
        RuntimeHandle handle,
        RuntimeLease lease,
        RuntimeLifecycle lifecycle,
        RuntimeStateSet current,
        string eventType,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var task = new RuntimeTaskState(request.TaskId, request.RuntimeId, handle.AdapterId, lifecycle, request.GoalRevision, workspace.StatusFingerprint, null, now);
        var agent = new RuntimeAgentState(request.RuntimeId, handle.AdapterId, lifecycle, handle.ProcessIdentity, Hash(handle.RecoveryToken), now);
        var ownership = new RuntimeOwnershipState(request.TaskId, request.RuntimeId, workspace.Root, workspace.StatusFingerprint, now);
        var worktree = new RuntimeWorktreeState(
            request.WorktreeId,
            request.TaskId,
            request.RuntimeId,
            workspace.Root,
            workspace.GitCommonDirectory,
            workspace.Head,
            workspace.Branch,
            AffectedScopePolicy.Normalize(request.PredictedAffectedScopes),
            "ACTIVE",
            now);
        return await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[]
            {
                RuntimeStateCodec.Mutation(current.TaskKey, current.Task, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, task),
                RuntimeStateCodec.Mutation(current.AgentKey, current.Agent, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, agent),
                RuntimeStateCodec.Mutation(current.LeaseKey, current.Lease, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, lease),
                RuntimeStateCodec.Mutation(current.OwnershipKey, current.Ownership, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, ownership),
                RuntimeStateCodec.Mutation(current.WorktreeKey, current.Worktree, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, worktree)
            },
            eventType,
            eventType,
            RuntimeStateCodec.Json(new { request.TaskId, request.RuntimeId, adapter_id = handle.AdapterId })), cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeStateSet> ReadAsync(RuntimeStateSet keys, CancellationToken cancellationToken) => keys with
    {
        Task = await coordinator.ReadAsync(keys.TaskKey, cancellationToken).ConfigureAwait(false),
        Agent = await coordinator.ReadAsync(keys.AgentKey, cancellationToken).ConfigureAwait(false),
        Lease = await coordinator.ReadAsync(keys.LeaseKey, cancellationToken).ConfigureAwait(false),
        Ownership = await coordinator.ReadAsync(keys.OwnershipKey, cancellationToken).ConfigureAwait(false),
        Worktree = await coordinator.ReadAsync(keys.WorktreeKey, cancellationToken).ConfigureAwait(false)
    };

    private static RuntimeStateSet RuntimeKeys(string projectId, string taskId, string runtimeId, string worktreeId) => new(
        new StateKey(projectId, EntityKinds.Task, taskId),
        new StateKey(projectId, EntityKinds.Agent, runtimeId),
        new StateKey(projectId, EntityKinds.RuntimeLease, taskId),
        new StateKey(projectId, EntityKinds.Ownership, taskId),
        new StateKey(projectId, EntityKinds.Worktree, worktreeId),
        null,
        null,
        null,
        null,
        null);

    private static void ValidateStart(RuntimeStartRequest request, IExecutionRuntimeAdapter adapter)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Agent, request.RuntimeId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Worktree, request.WorktreeId).Normalize();
        if (request.NonSecretOptions?.Keys.Any(static key => key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new ArgumentException("Runtime options must not carry secrets");
        }

        if ((request.EnvironmentProfileId is null) != (request.EnvironmentInstanceId is null))
        {
            throw new ArgumentException("Environment profile and instance bindings must be supplied together");
        }

        if (!adapter.Capabilities.Resume)
        {
            throw new InvalidOperationException("Runtime adapter must support suspended start and resume");
        }

        _ = AffectedScopePolicy.Normalize(request.PredictedAffectedScopes);
    }

    private async Task ValidateWorktreeConflictsAsync(RuntimeStartRequest request, WorkspaceSnapshot workspace, CancellationToken cancellationToken)
    {
        var requestedScopes = AffectedScopePolicy.Normalize(request.PredictedAffectedScopes);
        var worktrees = await coordinator.ReadAsync(request.ProjectId, EntityKinds.Worktree, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        foreach (var state in worktrees)
        {
            var active = RuntimeStateCodec.Parse<RuntimeWorktreeState>(state);
            if (string.Equals(active.WorktreeId, request.WorktreeId, StringComparison.Ordinal) &&
                string.Equals(active.TaskId, request.TaskId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(Path.GetFullPath(active.Root), workspace.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                string.Equals(active.TaskId, request.TaskId, StringComparison.Ordinal) ||
                AffectedScopePolicy.Overlap(active.PredictedAffectedScopes, requestedScopes))
            {
                throw new InvalidOperationException($"Worktree or predicted affected scope conflicts with active task {active.TaskId}");
            }
        }
    }

    private static async Task BestEffortInterruptAsync(IExecutionRuntimeAdapter adapter, RuntimeHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await adapter.InterruptAsync(handle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // State remains RECOVERY_REQUIRED; callers reconcile the adapter on the next recovery pass.
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record RuntimeStateSet(
        StateKey TaskKey,
        StateKey AgentKey,
        StateKey LeaseKey,
        StateKey OwnershipKey,
        StateKey WorktreeKey,
        StateSnapshot? Task,
        StateSnapshot? Agent,
        StateSnapshot? Lease,
        StateSnapshot? Ownership,
        StateSnapshot? Worktree);
}

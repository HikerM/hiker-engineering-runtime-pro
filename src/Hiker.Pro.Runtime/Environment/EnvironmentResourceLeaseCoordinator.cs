using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentResourceLeaseRequest(
    string OperationId,
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string WorktreeId,
    string EnvironmentInstanceId,
    IReadOnlyList<ResourceRequirement> Requirements,
    RuntimeProcessIdentity? ProcessIdentity,
    TimeSpan Ttl,
    string SourceFingerprint);

public sealed record EnvironmentResourceLeaseResult(
    string Status,
    IReadOnlyList<EnvironmentResourceLease> Leases,
    long Generation,
    IReadOnlyList<string> Findings);

public sealed class EnvironmentResourceLeaseCoordinator(
    StateCoordinator state,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EnvironmentResourceLeaseResult> AcquireAsync(
        EnvironmentResourceLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var receipt = await state.ReadOperationReceiptAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE" && receipt.ResultJson is not null)
        {
            using var result = JsonDocument.Parse(receipt.ResultJson);
            var ids = result.RootElement.GetProperty("lease_ids").EnumerateArray().Select(static item => item.GetString()!).ToArray();
            var stored = new List<EnvironmentResourceLease>(ids.Length);
            foreach (var id in ids)
            {
                var snapshot = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ResourceLease, id), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Completed environment lease operation has no state");
                stored.Add(EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot));
            }
            return new EnvironmentResourceLeaseResult("IDEMPOTENT_REPLAY", EnvironmentCollections.Freeze(stored), receipt.Generation ?? 0, Array.Empty<string>());
        }

        var activeSnapshots = await state.ReadAsync(request.ProjectId, EntityKinds.ResourceLease, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        if (activeSnapshots.Count == 1000)
        {
            return new EnvironmentResourceLeaseResult(
                EnvironmentStatuses.ResourceExhausted,
                Array.Empty<EnvironmentResourceLease>(),
                activeSnapshots[^1].Generation,
                new[] { "ACTIVE_RESOURCE_INDEX_BUDGET_EXHAUSTED" });
        }
        var active = new List<EnvironmentResourceLease>();
        foreach (var snapshot in activeSnapshots)
        {
            if (!string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            {
                return new EnvironmentResourceLeaseResult(
                    EnvironmentStatuses.ResourceConflict,
                    Array.Empty<EnvironmentResourceLease>(),
                    snapshot.Generation,
                    new[] { $"UNKNOWN_ACTIVE_RESOURCE_AUTHORITY:{snapshot.Key.EntityId}" });
            }
            active.Add(EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot));
        }

        var admitted = new List<ResourceRequirement>();
        foreach (var requirement in request.Requirements)
        {
            var assessment = ResourceConflictPolicy.Evaluate(requirement, active, admitted);
            if (assessment.Status != "AVAILABLE")
            {
                return new EnvironmentResourceLeaseResult(
                    assessment.Status,
                    Array.Empty<EnvironmentResourceLease>(),
                    0,
                    new[] { assessment.ReasonCode });
            }
            admitted.Add(requirement);
        }

        var now = _clock.GetUtcNow();
        var leases = request.Requirements.Select(requirement => new EnvironmentResourceLease(
            LeaseId(request, requirement),
            request.ProjectId,
            request.TaskId,
            request.RuntimeId,
            request.WorktreeId,
            request.EnvironmentInstanceId,
            requirement,
            request.ProcessIdentity,
            now,
            now,
            now.Add(request.Ttl),
            "ACTIVE")).ToArray();
        var mutations = new List<StateMutation>(leases.Length);
        var edges = new List<DependencyEdge>(leases.Length * 2);
        foreach (var lease in leases)
        {
            var key = new StateKey(request.ProjectId, EntityKinds.ResourceLease, lease.LeaseId);
            var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            mutations.Add(EnvironmentStateCodec.Mutation(key, current, 0, StateLifecycle.Valid, request.SourceFingerprint, lease));
            edges.Add(new DependencyEdge(request.ProjectId, EntityKinds.Task, request.TaskId, EntityKinds.ResourceLease, lease.LeaseId, "HOLDS_RESOURCE", request.SourceFingerprint));
            edges.Add(new DependencyEdge(request.ProjectId, EntityKinds.EnvironmentInstance, request.EnvironmentInstanceId, EntityKinds.ResourceLease, lease.LeaseId, "ALLOCATES_RESOURCE", request.SourceFingerprint));
        }

        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            request.OperationId,
            mutations,
            "ENVIRONMENT_RESOURCE_LEASED",
            "RESOURCE_REQUIREMENTS_RESERVED",
            EnvironmentStateCodec.Json(new { request.TaskId, request.EnvironmentInstanceId, count = leases.Length }),
            edges,
            EnvironmentStateCodec.Json(new { lease_ids = leases.Select(static lease => lease.LeaseId) })), cancellationToken).ConfigureAwait(false);
        return new EnvironmentResourceLeaseResult(commit.Status, EnvironmentCollections.Freeze(leases), commit.CurrentGeneration, Array.Empty<string>());
    }

    public async Task<EnvironmentResourceLeaseResult> HeartbeatAsync(
        string operationId,
        string projectId,
        string taskId,
        string runtimeId,
        IReadOnlyList<string> leaseIds,
        TimeSpan ttl,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (leaseIds.Count is < 1 or > 128 || ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(24))
            throw new ArgumentException("Environment lease heartbeat is invalid or unbounded", nameof(leaseIds));
        var now = _clock.GetUtcNow();
        var states = new List<StateSnapshot>(leaseIds.Count);
        var leases = new List<EnvironmentResourceLease>(leaseIds.Count);
        foreach (var leaseId in leaseIds.Distinct(StringComparer.Ordinal))
        {
            var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ResourceLease, leaseId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Environment resource lease is missing");
            var lease = EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot);
            if (lease.TaskId != taskId || lease.RuntimeId != runtimeId || lease.Status != "ACTIVE")
                throw new InvalidOperationException("Heartbeat does not own the active environment resource lease");
            states.Add(snapshot);
            leases.Add(lease with { HeartbeatAt = now, ExpiresAt = now.Add(ttl) });
        }
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            states.Zip(leases, (snapshot, lease) => EnvironmentStateCodec.Mutation(snapshot.Key, snapshot, snapshot.GoalRevision, StateLifecycle.Valid, sourceFingerprint, lease)).ToArray(),
            "ENVIRONMENT_RESOURCE_HEARTBEAT",
            "RESOURCE_LEASES_RENEWED",
            EnvironmentStateCodec.Json(new { taskId, count = leases.Count })), cancellationToken).ConfigureAwait(false);
        return new EnvironmentResourceLeaseResult(commit.Status, EnvironmentCollections.Freeze(leases), commit.CurrentGeneration, Array.Empty<string>());
    }

    public async Task<EnvironmentResourceLeaseResult> ReleaseAsync(
        string operationId,
        string projectId,
        string taskId,
        string runtimeId,
        IReadOnlyList<string> leaseIds,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
            return new EnvironmentResourceLeaseResult("IDEMPOTENT_REPLAY", Array.Empty<EnvironmentResourceLease>(), receipt.Generation ?? 0, Array.Empty<string>());
        if (leaseIds.Count is < 1 or > 128) throw new ArgumentException("Environment release is invalid or unbounded", nameof(leaseIds));
        var now = _clock.GetUtcNow();
        var states = new List<StateSnapshot>(leaseIds.Count);
        var released = new List<EnvironmentResourceLease>(leaseIds.Count);
        foreach (var leaseId in leaseIds.Distinct(StringComparer.Ordinal))
        {
            var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ResourceLease, leaseId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Environment resource lease is missing");
            var lease = EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot);
            if (lease.TaskId != taskId || lease.RuntimeId != runtimeId)
                throw new InvalidOperationException("Environment resource release is not owned by this runtime");
            states.Add(snapshot);
            released.Add(lease with { Status = "RELEASED", HeartbeatAt = now, ExpiresAt = now });
        }
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            states.Zip(released, (snapshot, lease) => EnvironmentStateCodec.Mutation(snapshot.Key, snapshot, snapshot.GoalRevision, StateLifecycle.Archived, sourceFingerprint, lease, RetentionTier.Warm)).ToArray(),
            "ENVIRONMENT_RESOURCE_RELEASED",
            "RESOURCE_LEASES_RELEASED",
            EnvironmentStateCodec.Json(new { taskId, count = released.Count })), cancellationToken).ConfigureAwait(false);
        return new EnvironmentResourceLeaseResult(commit.Status, EnvironmentCollections.Freeze(released), commit.CurrentGeneration, Array.Empty<string>());
    }

    public async Task<IReadOnlyList<EnvironmentResourceLease>> ActiveAsync(
        string projectId,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var snapshots = await state.ReadAsync(projectId, EntityKinds.ResourceLease, StateLifecycle.Valid, limit, cancellationToken).ConfigureAwait(false);
        return snapshots
            .Where(snapshot => string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            .Select(EnvironmentStateCodec.Parse<EnvironmentResourceLease>)
            .Where(static lease => lease.Status == "ACTIVE")
            .ToArray();
    }

    public async Task<bool> OwnerProcessIsStillLiveAsync(
        EnvironmentResourceLease lease,
        CancellationToken cancellationToken = default) =>
        lease.ProcessIdentity is not null && await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false);

    private static string LeaseId(EnvironmentResourceLeaseRequest request, ResourceRequirement requirement) =>
        $"resource-{EnvironmentStateCodec.Hash(string.Join('\0', request.ProjectId, request.TaskId, request.RuntimeId, request.EnvironmentInstanceId, requirement.ProviderId, requirement.ResourceType, requirement.ResourceId, requirement.Scope))[..32]}";

    private static void Validate(EnvironmentResourceLeaseRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.EnvironmentInstance, request.EnvironmentInstanceId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Worktree, request.WorktreeId).Normalize();
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.RuntimeId) ||
            string.IsNullOrWhiteSpace(request.SourceFingerprint) || request.Requirements.Count is < 1 or > 128 ||
            request.Ttl <= TimeSpan.Zero || request.Ttl > TimeSpan.FromHours(24))
            throw new ArgumentException("Environment resource lease request is invalid or unbounded", nameof(request));
        foreach (var requirement in request.Requirements)
        {
            EnvironmentContractValidation.Requirement(requirement);
            if (requirement.Destructive && requirement.SharingMode is ResourceSharingMode.Shared or ResourceSharingMode.Capacity)
                throw new ArgumentException("Destructive resource requirements must be exclusive or isolated", nameof(request));
        }
        if (request.Requirements.Select(requirement => string.Join('|', requirement.ProviderId, requirement.ResourceType, requirement.ResourceId, requirement.Scope))
            .Distinct(StringComparer.Ordinal).Count() != request.Requirements.Count)
        {
            throw new ArgumentException("Environment resource requirements must identify unique concrete resources", nameof(request));
        }
    }
}

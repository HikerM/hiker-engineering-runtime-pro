using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentInstanceRequest(
    string OperationId,
    string ProjectRoot,
    string ProjectId,
    string GoalId,
    long GoalRevision,
    string TaskId,
    string WorktreeId,
    string RuntimeId,
    string EnvironmentProfileId,
    IReadOnlyList<ResourceRequirement> Requirements,
    IReadOnlySet<string> ApprovedActionIds,
    RuntimeProcessIdentity? ProcessIdentity,
    TimeSpan LeaseTtl,
    string SourceFingerprint);

public sealed record EnvironmentInstanceResult(
    string Status,
    EnvironmentInstance? Instance,
    IReadOnlyList<EnvironmentResourceLease> Leases,
    long Generation,
    IReadOnlyList<string> Findings);

public sealed class EnvironmentInstanceCoordinator(
    StateCoordinator state,
    EnvironmentProviderRegistry providers,
    EnvironmentResourceLeaseCoordinator resources,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EnvironmentInstanceResult> ActivateAsync(
        EnvironmentInstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var instanceId = InstanceId(request);
        var instanceKey = new StateKey(request.ProjectId, EntityKinds.EnvironmentInstance, instanceId);
        var completed = await state.ReadOperationReceiptAsync($"{request.OperationId}:activate", cancellationToken).ConfigureAwait(false);
        if (completed?.Status == "COMPLETE")
        {
            var existing = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed environment activation has no instance state");
            var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(existing);
            var leases = await ReadLeasesAsync(request.ProjectId, instance.ResourceLeaseIds, cancellationToken).ConfigureAwait(false);
            return new EnvironmentInstanceResult("IDEMPOTENT_REPLAY", instance, leases, existing.Generation, Array.Empty<string>());
        }

        var profileState = await state.ReadAsync(
            new StateKey(request.ProjectId, EntityKinds.EnvironmentProfile, request.EnvironmentProfileId),
            cancellationToken).ConfigureAwait(false);
        if (profileState is null || profileState.Lifecycle != StateLifecycle.Valid ||
            !string.Equals(profileState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
        {
            return Block("ENVIRONMENT_PROFILE_NOT_ACTIVE");
        }
        var profile = EnvironmentStateCodec.Parse<EnvironmentProfile>(profileState);
        if (profile.Status != EnvironmentStatuses.Selected || profile.ProjectId != request.ProjectId)
        {
            return Block("ENVIRONMENT_PROFILE_NOT_SELECTED");
        }
        var taskState = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId), cancellationToken).ConfigureAwait(false);
        var goalState = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Goal, request.GoalId), cancellationToken).ConfigureAwait(false);
        if (taskState is null || taskState.Lifecycle != StateLifecycle.Valid || goalState is null || goalState.Lifecycle != StateLifecycle.Valid ||
            taskState.GoalRevision != request.GoalRevision || goalState.GoalRevision != request.GoalRevision)
        {
            return Block("TASK_GOAL_BINDING_NOT_ACTIVE");
        }

        var instanceStates = await state.ReadAsync(
                request.ProjectId,
                EntityKinds.EnvironmentInstance,
                StateLifecycle.Valid,
                65,
                cancellationToken).ConfigureAwait(false);
        if (instanceStates.Count > 64)
        {
            return Block("ACTIVE_ENVIRONMENT_INSTANCE_BUDGET_EXCEEDED");
        }
        if (instanceStates.Any(snapshot => !string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal)))
        {
            return Block("UNKNOWN_ACTIVE_ENVIRONMENT_AUTHORITY");
        }
        var activeForTask = instanceStates
            .Where(snapshot => string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            .Select(snapshot => (Snapshot: snapshot, Instance: EnvironmentStateCodec.Parse<EnvironmentInstance>(snapshot)))
            .Where(item => item.Instance.TaskId == request.TaskId && item.Instance.Status is EnvironmentStatuses.Active or "PREPARING" or "RECOVERY_REQUIRED")
            .ToArray();
        if (activeForTask.Any(item => item.Instance.EnvironmentInstanceId != instanceId))
        {
            return Block("TASK_HAS_DIFFERENT_ACTIVE_ENVIRONMENT");
        }

        var now = _clock.GetUtcNow();
        var existingForRequest = activeForTask.SingleOrDefault(item => item.Instance.EnvironmentInstanceId == instanceId);
        var preparing = existingForRequest.Instance
            ?? new EnvironmentInstance(
                instanceId,
                request.ProjectId,
                request.GoalId,
                request.GoalRevision,
                request.TaskId,
                request.WorktreeId,
                request.RuntimeId,
                request.EnvironmentProfileId,
                profile.Version,
                profile.EnvironmentFingerprint,
                Array.Empty<EnvironmentBinding>(),
                Array.Empty<string>(),
                "PREPARING",
                false,
                now,
                now);
        if (existingForRequest.Instance is not null &&
            (preparing.EnvironmentProfileVersion != profile.Version || preparing.EnvironmentFingerprint != profile.EnvironmentFingerprint))
        {
            return Block("ENVIRONMENT_PROFILE_CHANGED_DURING_INSTANCE_LIFECYCLE");
        }
        if (preparing.Status == EnvironmentStatuses.Active)
        {
            var existingLeases = await ReadLeasesAsync(request.ProjectId, preparing.ResourceLeaseIds, cancellationToken).ConfigureAwait(false);
            var valid = existingLeases.Count == preparing.ResourceLeaseIds.Count && existingLeases.All(lease =>
                lease.Status == "ACTIVE" && lease.TaskId == request.TaskId && lease.RuntimeId == request.RuntimeId && lease.EnvironmentInstanceId == instanceId);
            return valid
                ? new EnvironmentInstanceResult("ACTIVE_EXISTING_INSTANCE", preparing, existingLeases, existingForRequest.Snapshot!.Generation, Array.Empty<string>())
                : new EnvironmentInstanceResult("RECOVERY_REQUIRED", preparing, existingLeases, existingForRequest.Snapshot!.Generation, new[] { "ACTIVE_INSTANCE_RESOURCE_BINDING_DRIFT" });
        }
        if (existingForRequest.Instance is null)
        {
            var priorInstance = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false);
            if (priorInstance is not null && !string.Equals(priorInstance.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            {
                return Block("UNKNOWN_ENVIRONMENT_INSTANCE_AUTHORITY");
            }
            var prepare = await state.CommitBatchAsync(new BatchTransactionRequest(
                $"{request.OperationId}:prepare",
                new[] { EnvironmentStateCodec.Mutation(instanceKey, priorInstance, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, preparing) },
                "ENVIRONMENT_INSTANCE_PREPARED",
                "PROVIDER_ACTIONS_NOT_YET_COMMITTED",
                EnvironmentStateCodec.Json(new { request.TaskId, instance_id = instanceId }),
                new[]
                {
                    new DependencyEdge(request.ProjectId, EntityKinds.Task, request.TaskId, EntityKinds.EnvironmentProfile, request.EnvironmentProfileId, "USES_ENVIRONMENT", request.SourceFingerprint),
                    new DependencyEdge(request.ProjectId, EntityKinds.Task, request.TaskId, EntityKinds.EnvironmentInstance, instanceId, "BINDS_ENVIRONMENT", request.SourceFingerprint)
                }), cancellationToken).ConfigureAwait(false);
            if (!prepare.Committed)
            {
                return new EnvironmentInstanceResult(prepare.Status, null, Array.Empty<EnvironmentResourceLease>(), prepare.CurrentGeneration, new[] { "ENVIRONMENT_PREPARE_CONFLICT" });
            }
        }

        try
        {
            var execution = await InspectPlanAndExecuteAsync(request, profile, cancellationToken).ConfigureAwait(false);
            if (execution.BlockReason is not null)
            {
                return await MarkRecoveryRequiredAsync(request, instanceKey, preparing, execution.BlockReason, cancellationToken).ConfigureAwait(false);
            }

            var leaseResult = await resources.AcquireAsync(new EnvironmentResourceLeaseRequest(
                $"{request.OperationId}:resources",
                request.ProjectId,
                request.TaskId,
                request.RuntimeId,
                request.WorktreeId,
                instanceId,
                execution.Requirements,
                request.ProcessIdentity,
                request.LeaseTtl,
                request.SourceFingerprint), cancellationToken).ConfigureAwait(false);
            if (leaseResult.Status is not "COMMITTED" and not "IDEMPOTENT_REPLAY")
            {
                return await MarkRecoveryRequiredAsync(request, instanceKey, preparing, leaseResult.Status, cancellationToken).ConfigureAwait(false);
            }

            var active = preparing with
            {
                Bindings = EnvironmentCollections.Freeze(execution.Bindings),
                ResourceLeaseIds = EnvironmentCollections.Freeze(leaseResult.Leases.Select(static lease => lease.LeaseId)),
                Status = EnvironmentStatuses.Active,
                AdoptedExisting = execution.AdoptedExisting,
                UpdatedAt = _clock.GetUtcNow()
            };
            var current = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Prepared environment instance is missing");
            var activate = await state.CommitBatchAsync(new BatchTransactionRequest(
                $"{request.OperationId}:activate",
                new[] { EnvironmentStateCodec.Mutation(instanceKey, current, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, active) },
                "ENVIRONMENT_INSTANCE_ACTIVATED",
                execution.AdoptedExisting ? "EXISTING_ENVIRONMENT_ADOPTED" : "ENVIRONMENT_PROVISIONED",
                EnvironmentStateCodec.Json(new { request.TaskId, instance_id = instanceId, adopted = execution.AdoptedExisting }),
                ResultJson: EnvironmentStateCodec.Json(new { instance_id = instanceId })), cancellationToken).ConfigureAwait(false);
            if (!activate.Committed)
            {
                return new EnvironmentInstanceResult("RECOVERY_REQUIRED", active, leaseResult.Leases, activate.CurrentGeneration, new[] { "INSTANCE_ACTIVATION_CONFLICT" });
            }
            return new EnvironmentInstanceResult(EnvironmentStatuses.Active, active, leaseResult.Leases, activate.CurrentGeneration, Array.Empty<string>());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await MarkRecoveryRequiredAsync(request, instanceKey, preparing, exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionFacts> InspectPlanAndExecuteAsync(
        EnvironmentInstanceRequest request,
        EnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        var bindings = new List<EnvironmentBinding>();
        var allocated = new List<ResourceRequirement>();
        var adoptedAll = true;
        foreach (var providerId in profile.Graph.Nodes.Select(static node => node.ProviderId).Distinct(StringComparer.Ordinal))
        {
            if (!providers.TryResolve(providerId, out var provider) || provider is null)
            {
                return ExecutionFacts.Blocked($"PROVIDER_UNAVAILABLE:{providerId}");
            }
            var observations = await provider.InspectAsync(new EnvironmentInspectionRequest(
                request.ProjectRoot,
                profile,
                null,
                TaskId: request.TaskId,
                RuntimeId: request.RuntimeId,
                Requirements: request.Requirements.Where(requirement => requirement.ProviderId == providerId).ToArray()), cancellationToken).ConfigureAwait(false);
            var providerNodes = profile.Graph.Nodes.Where(node => node.ProviderId == providerId).ToArray();
            if (observations.Count > 128 || observations.Any(observation =>
                    observation.ProviderId != providerId || providerNodes.All(node => node.NodeId != observation.NodeId)))
            {
                return ExecutionFacts.Blocked($"PROVIDER_OBSERVATION_INVALID:{providerId}");
            }
            foreach (var observation in observations) EnvironmentContractValidation.NonSecret(observation.NonSecretFacts);

            var requested = request.Requirements.Where(requirement => requirement.ProviderId == providerId).ToArray();
            var plan = await provider.PlanAsync(new EnvironmentPlanRequest(
                request.ProjectId,
                request.GoalId,
                request.GoalRevision,
                request.TaskId,
                request.WorktreeId,
                request.RuntimeId,
                profile,
                requested), cancellationToken).ConfigureAwait(false);
            if (plan.Status == EnvironmentStatuses.ConfigurationIncompatible)
            {
                return ExecutionFacts.Blocked($"PROVIDER_PLAN_UNAVAILABLE:{providerId}");
            }
            if (plan.AllocatedRequirements.Count > 128 || plan.Actions.Count > 128 ||
                plan.AllocatedRequirements.Any(requirement => requirement.ProviderId != providerId) ||
                plan.Actions.Any(action => action.ProviderId != providerId || providerNodes.All(node => node.NodeId != action.NodeId)))
            {
                return ExecutionFacts.Blocked($"PROVIDER_PLAN_INVALID:{providerId}");
            }
            foreach (var requirement in plan.AllocatedRequirements) EnvironmentContractValidation.Requirement(requirement);
            foreach (var action in plan.Actions)
            {
                EnvironmentContractValidation.NonSecret(action.NonSecretArguments);
                if (action.Destructive && !request.ApprovedActionIds.Contains(action.ActionId))
                {
                    return ExecutionFacts.Blocked($"DESTRUCTIVE_ACTION_REQUIRES_APPROVAL:{action.ActionId}");
                }
            }

            var adopt = plan.AdoptExisting && providerNodes.All(node => observations.Any(observation =>
                observation.NodeId == node.NodeId && observation.ExistingInstanceCanBeAdopted));
            adoptedAll &= adopt;
            var actionResources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (!adopt)
            {
                foreach (var action in plan.Actions)
                {
                    var result = await provider.ExecuteActionAsync(action, cancellationToken).ConfigureAwait(false);
                    EnvironmentContractValidation.NonSecret(result.NonSecretFacts);
                    if (string.IsNullOrWhiteSpace(result.EvidenceFingerprint) || result.ResourceIds.Count > 128)
                    {
                        return ExecutionFacts.Blocked($"PROVIDER_ACTION_RESULT_INVALID:{action.ActionId}");
                    }
                    if (result.Status is not EnvironmentStatuses.Active and not "COMPLETED")
                    {
                        return ExecutionFacts.Blocked($"PROVIDER_ACTION_FAILED:{action.ActionId}");
                    }
                    actionResources[action.NodeId] = result.ResourceIds;
                }
            }

            allocated.AddRange(plan.AllocatedRequirements);
            foreach (var node in providerNodes)
            {
                var observation = observations.FirstOrDefault(item => item.NodeId == node.NodeId);
                bindings.Add(new EnvironmentBinding(
                    node.NodeId,
                    providerId,
                    observation?.ResourceIds.FirstOrDefault() ??
                    (actionResources.TryGetValue(node.NodeId, out var resultResources) ? resultResources.FirstOrDefault() : null) ??
                    plan.AllocatedRequirements.FirstOrDefault()?.ResourceId ??
                    $"binding-{node.NodeId}",
                    EnvironmentStatuses.Active,
                    observation?.ObservationFingerprint ?? profile.EnvironmentFingerprint,
                    adopt));
            }
        }

        if (!SameRequirementSet(request.Requirements, allocated))
        {
            return ExecutionFacts.Blocked("PROVIDER_ALLOCATED_REQUIREMENTS_MISMATCH");
        }
        return new ExecutionFacts(bindings, allocated, adoptedAll, null);
    }

    private async Task<EnvironmentInstanceResult> MarkRecoveryRequiredAsync(
        EnvironmentInstanceRequest request,
        StateKey key,
        EnvironmentInstance preparing,
        string reason,
        CancellationToken cancellationToken)
    {
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new EnvironmentInstanceResult("RECOVERY_REQUIRED", null, Array.Empty<EnvironmentResourceLease>(), 0, new[] { reason });
        }
        var recovery = preparing with { Status = "RECOVERY_REQUIRED", UpdatedAt = _clock.GetUtcNow() };
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            $"{request.OperationId}:recovery:{EnvironmentStateCodec.Hash(reason)[..12]}",
            new[] { EnvironmentStateCodec.Mutation(key, current, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, recovery) },
            "ENVIRONMENT_RECOVERY_REQUIRED",
            "PROVIDER_OR_STATE_COMMIT_INCOMPLETE",
            EnvironmentStateCodec.Json(new { request.TaskId, reason })), cancellationToken).ConfigureAwait(false);
        return new EnvironmentInstanceResult("RECOVERY_REQUIRED", recovery, Array.Empty<EnvironmentResourceLease>(), commit.CurrentGeneration, new[] { reason });
    }

    private async Task<IReadOnlyList<EnvironmentResourceLease>> ReadLeasesAsync(
        string projectId,
        IReadOnlyList<string> leaseIds,
        CancellationToken cancellationToken)
    {
        var values = new List<EnvironmentResourceLease>(leaseIds.Count);
        foreach (var leaseId in leaseIds.Take(128))
        {
            var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ResourceLease, leaseId), cancellationToken).ConfigureAwait(false);
            if (snapshot is not null) values.Add(EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot));
        }
        return EnvironmentCollections.Freeze(values);
    }

    private static bool SameRequirementSet(IReadOnlyList<ResourceRequirement> requested, IReadOnlyList<ResourceRequirement> allocated)
    {
        if (requested.Count != allocated.Count) return false;
        var remaining = allocated.ToList();
        foreach (var expected in requested)
        {
            var index = remaining.FindIndex(actual => RequirementMatches(expected, actual));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static bool RequirementMatches(ResourceRequirement expected, ResourceRequirement actual) =>
        expected.ProviderId == actual.ProviderId && expected.ResourceType == actual.ResourceType &&
        expected.Scope == actual.Scope && expected.SharingMode == actual.SharingMode &&
        expected.Units == actual.Units && expected.Capacity == actual.Capacity &&
        expected.Destructive == actual.Destructive &&
        (expected.ResourceId == actual.ResourceId ||
         expected.SharingMode == ResourceSharingMode.Isolated && expected.SupportsIsolation && actual.SupportsIsolation);

    private static string InstanceId(EnvironmentInstanceRequest request) =>
        $"instance-{EnvironmentStateCodec.Hash(string.Join('\0', request.ProjectId, request.TaskId, request.RuntimeId, request.EnvironmentProfileId))[..24]}";

    private static void Validate(EnvironmentInstanceRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Goal, request.GoalId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Worktree, request.WorktreeId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.EnvironmentProfile, request.EnvironmentProfileId).Normalize();
        if (!Directory.Exists(request.ProjectRoot) || string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.RuntimeId) || string.IsNullOrWhiteSpace(request.SourceFingerprint) ||
            request.Requirements.Count is < 1 or > 128 || request.ApprovedActionIds.Count > 128 ||
            request.LeaseTtl <= TimeSpan.Zero || request.LeaseTtl > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("Environment instance request is invalid or unbounded", nameof(request));
        }
        foreach (var requirement in request.Requirements) EnvironmentContractValidation.Requirement(requirement);
    }

    private EnvironmentInstanceResult Block(string reason) =>
        new(EnvironmentStatuses.ConfigurationIncompatible, null, Array.Empty<EnvironmentResourceLease>(), 0, new[] { reason });

    private sealed record ExecutionFacts(
        IReadOnlyList<EnvironmentBinding> Bindings,
        IReadOnlyList<ResourceRequirement> Requirements,
        bool AdoptedExisting,
        string? BlockReason)
    {
        public static ExecutionFacts Blocked(string reason) => new(
            Array.Empty<EnvironmentBinding>(),
            Array.Empty<ResourceRequirement>(),
            false,
            reason);
    }
}

using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentRuntimeValidation(
    string Status,
    bool Allowed,
    EnvironmentInstance? Instance,
    IReadOnlyList<EnvironmentResourceLease> Leases,
    IReadOnlyList<string> Findings);

public interface IEnvironmentRuntimeGate
{
    Task<EnvironmentRuntimeValidation> ValidateStartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken = default);
}

public interface IEnvironmentRecoveryGate
{
    Task<EnvironmentRuntimeValidation> ValidateCurrentAsync(
        string projectId,
        string taskId,
        string runtimeId,
        string environmentInstanceId,
        CancellationToken cancellationToken = default);

    Task<EnvironmentRuntimeValidation> RevalidateAsync(
        string projectRoot,
        string projectId,
        string taskId,
        string priorRuntimeId,
        string environmentInstanceId,
        CancellationToken cancellationToken = default);

    Task<BatchTransactionResult> TransferRuntimeAsync(
        string operationId,
        string projectId,
        string taskId,
        string priorRuntimeId,
        string nextRuntimeId,
        string environmentInstanceId,
        long goalRevision,
        string sourceFingerprint,
        CancellationToken cancellationToken = default);
}

public sealed class EnvironmentRuntimeControl(
    StateCoordinator state,
    EnvironmentProviderRegistry providers,
    EnvironmentProcessRegistry processRegistry) : IEnvironmentRuntimeGate, IEnvironmentRecoveryGate
{
    public async Task<EnvironmentRuntimeValidation> ValidateCurrentAsync(
        string projectId,
        string taskId,
        string runtimeId,
        string environmentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var instanceState = await state.ReadAsync(new StateKey(projectId, EntityKinds.EnvironmentInstance, environmentInstanceId), cancellationToken).ConfigureAwait(false);
        if (instanceState is null || !string.Equals(instanceState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            return Block("ENVIRONMENT_INSTANCE_NOT_ACTIVE");
        var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(instanceState);
        var facts = await ReadAsync(projectId, instance.EnvironmentProfileId, environmentInstanceId, cancellationToken).ConfigureAwait(false);
        if (facts.Finding is not null) return Block(facts.Finding);
        return instance.Status == EnvironmentStatuses.Active && instance.TaskId == taskId && instance.RuntimeId == runtimeId &&
               instance.EnvironmentProfileVersion == facts.Profile!.Version &&
               instance.EnvironmentFingerprint == facts.Profile.EnvironmentFingerprint &&
               LeasesMatch(instance, facts.Leases, taskId, runtimeId)
            ? new EnvironmentRuntimeValidation(EnvironmentStatuses.Healthy, true, instance, facts.Leases, Array.Empty<string>())
            : Block("ENVIRONMENT_RUNTIME_BINDING_DRIFT", instance, facts.Leases);
    }

    public async Task<EnvironmentRuntimeValidation> ValidateStartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentProfileId) || string.IsNullOrWhiteSpace(request.EnvironmentInstanceId))
        {
            return Block("ENVIRONMENT_BINDING_REQUIRED");
        }
        var facts = await ReadAsync(request.ProjectId, request.EnvironmentProfileId, request.EnvironmentInstanceId, cancellationToken).ConfigureAwait(false);
        if (facts.Finding is not null) return Block(facts.Finding);
        var instance = facts.Instance!;
        if (instance.Status != EnvironmentStatuses.Active || instance.TaskId != request.TaskId ||
            instance.RuntimeId != request.RuntimeId || instance.WorktreeId != request.WorktreeId ||
            instance.EnvironmentProfileId != request.EnvironmentProfileId ||
            instance.EnvironmentProfileVersion != facts.Profile!.Version ||
            instance.EnvironmentFingerprint != facts.Profile.EnvironmentFingerprint)
        {
            return Block("ENVIRONMENT_RUNTIME_BINDING_DRIFT", instance, facts.Leases);
        }
        if (!LeasesMatch(instance, facts.Leases, request.TaskId, request.RuntimeId))
        {
            return Block("ENVIRONMENT_RESOURCE_BINDING_DRIFT", instance, facts.Leases);
        }
        return new EnvironmentRuntimeValidation(EnvironmentStatuses.Healthy, true, instance, facts.Leases, Array.Empty<string>());
    }

    public async Task<EnvironmentRuntimeValidation> RevalidateAsync(
        string projectRoot,
        string projectId,
        string taskId,
        string priorRuntimeId,
        string environmentInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(projectRoot)) return Block("ENVIRONMENT_PROJECT_ROOT_UNAVAILABLE");
        var instanceState = await state.ReadAsync(new StateKey(projectId, EntityKinds.EnvironmentInstance, environmentInstanceId), cancellationToken).ConfigureAwait(false);
        if (instanceState is null || !string.Equals(instanceState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            return Block("ENVIRONMENT_INSTANCE_NOT_ACTIVE");
        var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(instanceState);
        var facts = await ReadAsync(projectId, instance.EnvironmentProfileId, environmentInstanceId, cancellationToken).ConfigureAwait(false);
        if (facts.Finding is not null) return Block(facts.Finding);
        if (instance.Status != EnvironmentStatuses.Active || instance.TaskId != taskId || instance.RuntimeId != priorRuntimeId ||
            instance.EnvironmentProfileVersion != facts.Profile!.Version || instance.EnvironmentFingerprint != facts.Profile.EnvironmentFingerprint ||
            !LeasesMatch(instance, facts.Leases, taskId, priorRuntimeId))
        {
            return Block("ENVIRONMENT_RECOVERY_BINDING_DRIFT", instance, facts.Leases);
        }

        var processes = await processRegistry.ActiveForInstanceAsync(projectId, environmentInstanceId, cancellationToken).ConfigureAwait(false);
        var findings = new List<string>();
        foreach (var providerId in facts.Profile!.Graph.Nodes.Select(static node => node.ProviderId).Distinct(StringComparer.Ordinal))
        {
            if (!providers.TryResolve(providerId, out var provider) || provider is null)
            {
                findings.Add($"PROVIDER_UNAVAILABLE:{providerId}");
                continue;
            }
            var result = await provider.RecoverAsync(new EnvironmentRecoveryRequest(
                projectRoot,
                facts.Profile,
                instance,
                facts.Leases.Where(lease => lease.Requirement.ProviderId == providerId).ToArray(),
                processes.Where(process => process.ProviderId == providerId).ToArray()), cancellationToken).ConfigureAwait(false);
            if (!result.ResourcesRevalidated || !result.SafeToRecover)
            {
                findings.AddRange(result.Findings.Take(128));
                if (result.Findings.Count == 0) findings.Add($"PROVIDER_RECOVERY_REJECTED:{providerId}");
            }
        }
        return findings.Count == 0
            ? new EnvironmentRuntimeValidation(EnvironmentStatuses.Healthy, true, instance, facts.Leases, Array.Empty<string>())
            : new EnvironmentRuntimeValidation("RECOVERY_REQUIRED", false, instance, facts.Leases, EnvironmentCollections.Freeze(findings));
    }

    public async Task<BatchTransactionResult> TransferRuntimeAsync(
        string operationId,
        string projectId,
        string taskId,
        string priorRuntimeId,
        string nextRuntimeId,
        string environmentInstanceId,
        long goalRevision,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var instanceKey = new StateKey(projectId, EntityKinds.EnvironmentInstance, environmentInstanceId);
        var instanceState = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Environment instance is missing during runtime transfer");
        var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(instanceState);
        if (instance.TaskId != taskId || instance.RuntimeId != priorRuntimeId || instance.Status != EnvironmentStatuses.Active)
            throw new InvalidOperationException("Environment runtime transfer does not own the active instance");
        var mutations = new List<StateMutation>
        {
            EnvironmentStateCodec.Mutation(
                instanceKey,
                instanceState,
                goalRevision,
                StateLifecycle.Valid,
                sourceFingerprint,
                instance with { RuntimeId = nextRuntimeId, UpdatedAt = DateTimeOffset.UtcNow })
        };
        foreach (var leaseId in instance.ResourceLeaseIds.Take(128))
        {
            var key = new StateKey(projectId, EntityKinds.ResourceLease, leaseId);
            var snapshot = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Environment resource lease is missing during runtime transfer");
            var lease = EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot);
            if (lease.TaskId != taskId || lease.RuntimeId != priorRuntimeId || lease.EnvironmentInstanceId != environmentInstanceId)
                throw new InvalidOperationException("Environment resource lease does not match runtime transfer");
            mutations.Add(EnvironmentStateCodec.Mutation(key, snapshot, goalRevision, StateLifecycle.Valid, sourceFingerprint, lease with { RuntimeId = nextRuntimeId }));
        }
        var processStates = await state.ReadAsync(projectId, EntityKinds.ServiceProcess, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        if (processStates.Count == 1000)
            throw new InvalidOperationException("Environment process registry budget is exhausted during runtime transfer");
        foreach (var snapshot in processStates.Where(snapshot => string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal)))
        {
            var process = EnvironmentStateCodec.Parse<ServiceProcessRegistration>(snapshot);
            if (process.EnvironmentInstanceId != environmentInstanceId) continue;
            if (process.TaskId != taskId || process.RuntimeId != priorRuntimeId)
                throw new InvalidOperationException("Environment process registration does not match runtime transfer");
            mutations.Add(EnvironmentStateCodec.Mutation(snapshot.Key, snapshot, goalRevision, StateLifecycle.Valid, sourceFingerprint,
                process with { RuntimeId = nextRuntimeId, UpdatedAt = DateTimeOffset.UtcNow }));
        }
        return await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "ENVIRONMENT_RUNTIME_TRANSFERRED",
            "RECOVERY_REBOUND_EXISTING_ENVIRONMENT",
            EnvironmentStateCodec.Json(new { taskId, prior_runtime_id = priorRuntimeId, next_runtime_id = nextRuntimeId, environment_instance_id = environmentInstanceId })), cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentFacts> ReadAsync(
        string projectId,
        string profileId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var profileState = await state.ReadAsync(new StateKey(projectId, EntityKinds.EnvironmentProfile, profileId), cancellationToken).ConfigureAwait(false);
        var instanceState = await state.ReadAsync(new StateKey(projectId, EntityKinds.EnvironmentInstance, instanceId), cancellationToken).ConfigureAwait(false);
        if (profileState is null || instanceState is null || profileState.Lifecycle != StateLifecycle.Valid || instanceState.Lifecycle != StateLifecycle.Valid)
            return new EnvironmentFacts(null, null, Array.Empty<EnvironmentResourceLease>(), "ENVIRONMENT_STATE_NOT_ACTIVE");
        if (!string.Equals(profileState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal) ||
            !string.Equals(instanceState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            return new EnvironmentFacts(null, null, Array.Empty<EnvironmentResourceLease>(), "UNKNOWN_ENVIRONMENT_STATE_AUTHORITY");
        var profile = EnvironmentStateCodec.Parse<EnvironmentProfile>(profileState);
        var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(instanceState);
        var leases = new List<EnvironmentResourceLease>();
        foreach (var leaseId in instance.ResourceLeaseIds.Take(128))
        {
            var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ResourceLease, leaseId), cancellationToken).ConfigureAwait(false);
            if (snapshot is null || !string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
                return new EnvironmentFacts(profile, instance, EnvironmentCollections.Freeze(leases), "ENVIRONMENT_RESOURCE_STATE_INVALID");
            leases.Add(EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot));
        }
        return new EnvironmentFacts(profile, instance, EnvironmentCollections.Freeze(leases), null);
    }

    private static bool LeasesMatch(EnvironmentInstance instance, IReadOnlyList<EnvironmentResourceLease> leases, string taskId, string runtimeId) =>
        leases.Count == instance.ResourceLeaseIds.Count && leases.All(lease =>
            lease.Status == "ACTIVE" && lease.TaskId == taskId && lease.RuntimeId == runtimeId &&
            lease.EnvironmentInstanceId == instance.EnvironmentInstanceId);

    private static EnvironmentRuntimeValidation Block(
        string reason,
        EnvironmentInstance? instance = null,
        IReadOnlyList<EnvironmentResourceLease>? leases = null) =>
        new("BLOCKED", false, instance, leases ?? Array.Empty<EnvironmentResourceLease>(), new[] { reason });

    private sealed record EnvironmentFacts(
        EnvironmentProfile? Profile,
        EnvironmentInstance? Instance,
        IReadOnlyList<EnvironmentResourceLease> Leases,
        string? Finding);
}

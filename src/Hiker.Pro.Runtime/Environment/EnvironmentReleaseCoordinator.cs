using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentReleaseRequest(
    string OperationId,
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string EnvironmentInstanceId,
    string SourceFingerprint);

public sealed record EnvironmentReleaseResult(
    string Status,
    bool ExternalResourcesReleased,
    bool AdoptedResourcesPreserved,
    long Generation,
    IReadOnlyList<string> Findings);

public sealed class EnvironmentReleaseCoordinator(
    StateCoordinator state,
    EnvironmentProviderRegistry providers,
    EnvironmentResourceLeaseCoordinator resources,
    EnvironmentProcessRegistry processRegistry,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EnvironmentReleaseResult> ReleaseAsync(
        EnvironmentReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var finalReceipt = await state.ReadOperationReceiptAsync($"{request.OperationId}:instance", cancellationToken).ConfigureAwait(false);
        if (finalReceipt?.Status == "COMPLETE")
        {
            return new EnvironmentReleaseResult("ALREADY_RELEASED", true, true, finalReceipt.Generation ?? 0, Array.Empty<string>());
        }
        var instanceKey = new StateKey(request.ProjectId, EntityKinds.EnvironmentInstance, request.EnvironmentInstanceId);
        var instanceState = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Environment instance does not exist");
        if (!string.Equals(instanceState.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            return Block("UNKNOWN_ENVIRONMENT_STATE_AUTHORITY", instanceState.Generation);
        var instance = EnvironmentStateCodec.Parse<EnvironmentInstance>(instanceState);
        if (instance.TaskId != request.TaskId || instance.RuntimeId != request.RuntimeId)
            return Block("CROSS_TASK_ENVIRONMENT_RELEASE_DENIED", instanceState.Generation);

        var processes = await processRegistry.ActiveForInstanceAsync(request.ProjectId, request.EnvironmentInstanceId, cancellationToken).ConfigureAwait(false);
        foreach (var process in processes.Where(static process => process.StartedByHiker))
        {
            var decision = await processRegistry.AssessTerminationAsync(
                request.ProjectId,
                process.RegistrationId,
                request.TaskId,
                request.RuntimeId,
                cancellationToken).ConfigureAwait(false);
            if (!decision.MayTerminate && decision.ReasonCode != "PROCESS_IDENTITY_NOT_LIVE")
                return Block(decision.ReasonCode, instanceState.Generation);
        }

        var leases = new List<EnvironmentResourceLease>();
        foreach (var leaseId in instance.ResourceLeaseIds.Take(128))
        {
            var snapshot = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ResourceLease, leaseId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Environment resource lease is missing");
            var lease = EnvironmentStateCodec.Parse<EnvironmentResourceLease>(snapshot);
            if (lease.TaskId != request.TaskId || lease.RuntimeId != request.RuntimeId)
                return Block("CROSS_TASK_RESOURCE_RELEASE_DENIED", snapshot.Generation);
            leases.Add(lease);
        }

        var preserved = false;
        foreach (var providerGroup in leases.GroupBy(static lease => lease.Requirement.ProviderId, StringComparer.Ordinal))
        {
            var providerBindings = instance.Bindings.Where(binding => binding.ProviderId == providerGroup.Key).ToArray();
            if (providerBindings.Length > 0 && providerBindings.All(static binding => binding.AdoptedExisting))
            {
                preserved = true;
                continue;
            }
            if (!providers.TryResolve(providerGroup.Key, out var provider) || provider is null)
                return Block($"PROVIDER_UNAVAILABLE:{providerGroup.Key}", instanceState.Generation);
            foreach (var lease in providerGroup)
            {
                var action = new EnvironmentAction(
                    $"release-{lease.LeaseId}",
                    providerGroup.Key,
                    providerBindings.FirstOrDefault()?.NodeId ?? "resource-release",
                    "release-resource",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["resource_type"] = lease.Requirement.ResourceType,
                        ["resource_id"] = lease.Requirement.ResourceId,
                        ["scope"] = lease.Requirement.Scope,
                        ["task_fingerprint"] = EnvironmentStateCodec.Hash(lease.TaskId),
                        ["runtime_fingerprint"] = EnvironmentStateCodec.Hash(lease.RuntimeId)
                    },
                    true,
                    "HIGH");
                var result = await provider.ReleaseAsync(action, cancellationToken).ConfigureAwait(false);
                if (result.Status is not "RELEASED" and not "COMPLETED")
                    return Block($"PROVIDER_RELEASE_FAILED:{lease.LeaseId}", instanceState.Generation);
            }
        }

        foreach (var process in processes.Where(static process => process.StartedByHiker))
        {
            var after = await processRegistry.AssessTerminationAsync(
                request.ProjectId,
                process.RegistrationId,
                request.TaskId,
                request.RuntimeId,
                cancellationToken).ConfigureAwait(false);
            if (after.MayTerminate)
                return Block($"SERVICE_PROCESS_STILL_LIVE:{process.RegistrationId}", instanceState.Generation);
        }

        long generation = instanceState.Generation;
        foreach (var process in processes)
        {
            var archived = await processRegistry.ArchiveAsync(
                $"{request.OperationId}:process:{EnvironmentStateCodec.Hash(process.RegistrationId)[..16]}",
                request.ProjectId,
                process.RegistrationId,
                request.TaskId,
                request.RuntimeId,
                request.SourceFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (!archived.Committed) return Block(archived.Status, archived.CurrentGeneration);
            generation = archived.CurrentGeneration;
        }
        if (leases.Count > 0)
        {
            var released = await resources.ReleaseAsync(
                $"{request.OperationId}:resources",
                request.ProjectId,
                request.TaskId,
                request.RuntimeId,
                leases.Select(static lease => lease.LeaseId).ToArray(),
                request.SourceFingerprint,
                cancellationToken).ConfigureAwait(false);
            if (released.Status is not "COMMITTED" and not "IDEMPOTENT_REPLAY") return Block(released.Status, released.Generation);
            generation = released.Generation;
        }

        var current = await state.ReadAsync(instanceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Environment instance disappeared during release");
        var archivedInstance = instance with { Status = "RELEASED", UpdatedAt = _clock.GetUtcNow() };
        var final = await state.CommitBatchAsync(new BatchTransactionRequest(
            $"{request.OperationId}:instance",
            new[] { EnvironmentStateCodec.Mutation(instanceKey, current, instance.GoalRevision, StateLifecycle.Archived, request.SourceFingerprint, archivedInstance, RetentionTier.Warm) },
            "ENVIRONMENT_INSTANCE_RELEASED",
            preserved ? "ADOPTED_RESOURCES_PRESERVED" : "PROVISIONED_RESOURCES_RELEASED",
            EnvironmentStateCodec.Json(new { request.TaskId, request.EnvironmentInstanceId, preserved })), cancellationToken).ConfigureAwait(false);
        return final.Committed
            ? new EnvironmentReleaseResult("RELEASED", true, preserved, final.CurrentGeneration, Array.Empty<string>())
            : Block("ENVIRONMENT_INSTANCE_ARCHIVE_CONFLICT", final.CurrentGeneration);
    }

    private static void Validate(EnvironmentReleaseRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.EnvironmentInstance, request.EnvironmentInstanceId).Normalize();
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100 || string.IsNullOrWhiteSpace(request.RuntimeId) || string.IsNullOrWhiteSpace(request.SourceFingerprint))
            throw new ArgumentException("Environment release request is invalid", nameof(request));
    }

    private static EnvironmentReleaseResult Block(string reason, long generation) =>
        new("RECOVERY_REQUIRED", false, false, generation, new[] { reason });
}

using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record ProcessTerminationDecision(
    string Status,
    bool MayTerminate,
    ServiceProcessRegistration? Registration,
    string ReasonCode);

public sealed class EnvironmentProcessRegistry(
    StateCoordinator state,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<BatchTransactionResult> RegisterAsync(
        string operationId,
        ServiceProcessRegistration registration,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        Validate(registration, sourceFingerprint);
        if (registration.Status != EnvironmentStatuses.Active)
        {
            throw new ArgumentException("Only an active observed process can enter the registry", nameof(registration));
        }
        var active = await state.ReadAsync(registration.ProjectId, EntityKinds.ServiceProcess, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        if (active.Count == 1000)
        {
            return new BatchTransactionResult(false, true, active[^1].Generation, Array.Empty<StateSnapshot>(), "PROCESS_REGISTRY_BUDGET_EXHAUSTED");
        }
        foreach (var snapshot in active)
        {
            if (!string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            {
                return new BatchTransactionResult(false, true, snapshot.Generation, Array.Empty<StateSnapshot>(), "UNKNOWN_ACTIVE_PROCESS_AUTHORITY");
            }
            var other = EnvironmentStateCodec.Parse<ServiceProcessRegistration>(snapshot);
            if (SameProcess(other.ProcessIdentity, registration.ProcessIdentity) &&
                (other.TaskId != registration.TaskId || other.RuntimeId != registration.RuntimeId))
            {
                return new BatchTransactionResult(false, true, snapshot.Generation, new[] { snapshot }, "PROCESS_ALREADY_OWNED_BY_ANOTHER_TASK");
            }
        }

        var key = new StateKey(registration.ProjectId, EntityKinds.ServiceProcess, registration.RegistrationId);
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            var existing = EnvironmentStateCodec.Parse<ServiceProcessRegistration>(current);
            if (!SameProcess(existing.ProcessIdentity, registration.ProcessIdentity) || existing.TaskId != registration.TaskId || existing.RuntimeId != registration.RuntimeId)
            {
                return new BatchTransactionResult(false, true, current.Generation, new[] { current }, "PROCESS_REGISTRATION_ID_CONFLICT");
            }
        }
        return await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { EnvironmentStateCodec.Mutation(key, current, 0, StateLifecycle.Valid, sourceFingerprint, registration) },
            "SERVICE_PROCESS_REGISTERED",
            registration.StartedByHiker ? "HIKER_STARTED_PROCESS_OBSERVED" : "EXISTING_PROCESS_ADOPTED",
            EnvironmentStateCodec.Json(new
            {
                registration.TaskId,
                registration.RuntimeId,
                registration.EnvironmentInstanceId,
                registration.ProcessIdentity.ProcessId,
                identity_fingerprint = ProcessFingerprint(registration.ProcessIdentity)
            }),
            new[]
            {
                new DependencyEdge(registration.ProjectId, EntityKinds.Task, registration.TaskId, EntityKinds.ServiceProcess, registration.RegistrationId, "OWNS_PROCESS_REGISTRATION", sourceFingerprint),
                new DependencyEdge(registration.ProjectId, EntityKinds.EnvironmentInstance, registration.EnvironmentInstanceId, EntityKinds.ServiceProcess, registration.RegistrationId, "OBSERVES_PROCESS", sourceFingerprint)
            }), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessTerminationDecision> AssessTerminationAsync(
        string projectId,
        string registrationId,
        string taskId,
        string runtimeId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ServiceProcess, registrationId), cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Lifecycle != StateLifecycle.Valid)
        {
            return new ProcessTerminationDecision("DENIED", false, null, "PROCESS_REGISTRATION_NOT_ACTIVE");
        }
        if (!string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
        {
            return new ProcessTerminationDecision("DENIED", false, null, "UNKNOWN_PROCESS_AUTHORITY");
        }
        var registration = EnvironmentStateCodec.Parse<ServiceProcessRegistration>(snapshot);
        if (registration.TaskId != taskId || registration.RuntimeId != runtimeId)
        {
            return new ProcessTerminationDecision("DENIED", false, registration, "CROSS_TASK_PROCESS_TERMINATION_DENIED");
        }
        if (registration.AdoptedByHiker || !registration.StartedByHiker)
        {
            return new ProcessTerminationDecision("DENIED", false, registration, "ADOPTED_PROCESS_MUST_NOT_BE_TERMINATED");
        }
        if (!await processProbe.IsSameLiveProcessAsync(registration.ProcessIdentity, cancellationToken).ConfigureAwait(false))
        {
            return new ProcessTerminationDecision("STALE", false, registration, "PROCESS_IDENTITY_NOT_LIVE");
        }
        return new ProcessTerminationDecision("AUTHORIZED", true, registration, "EXACT_OWNER_AND_PROCESS_IDENTITY");
    }

    public async Task<BatchTransactionResult> ArchiveAsync(
        string operationId,
        string projectId,
        string registrationId,
        string taskId,
        string runtimeId,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var key = new StateKey(projectId, EntityKinds.ServiceProcess, registrationId);
        var snapshot = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Process registration does not exist");
        var registration = EnvironmentStateCodec.Parse<ServiceProcessRegistration>(snapshot);
        if (registration.TaskId != taskId || registration.RuntimeId != runtimeId)
        {
            throw new InvalidOperationException("Process registration archive is not owned by this task runtime");
        }
        var archived = registration with { Status = "RELEASED", UpdatedAt = _clock.GetUtcNow() };
        return await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { EnvironmentStateCodec.Mutation(key, snapshot, 0, StateLifecycle.Archived, sourceFingerprint, archived, RetentionTier.Warm) },
            "SERVICE_PROCESS_RELEASED",
            "PROCESS_REGISTRATION_ARCHIVED",
            EnvironmentStateCodec.Json(new { registration.TaskId, registration.RuntimeId, registration.ProcessIdentity.ProcessId })), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServiceProcessRegistration>> ActiveForInstanceAsync(
        string projectId,
        string environmentInstanceId,
        CancellationToken cancellationToken = default) =>
        (await state.ReadAsync(projectId, EntityKinds.ServiceProcess, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false))
            .Where(snapshot => string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
            .Select(EnvironmentStateCodec.Parse<ServiceProcessRegistration>)
            .Where(registration => registration.EnvironmentInstanceId == environmentInstanceId && registration.Status == EnvironmentStatuses.Active)
            .Take(128)
            .ToArray();

    internal static bool SameProcess(RuntimeProcessIdentity left, RuntimeProcessIdentity right) =>
        left.ProcessId == right.ProcessId && left.StartTime == right.StartTime &&
        left.ExecutablePathFingerprint == right.ExecutablePathFingerprint && left.HostFingerprint == right.HostFingerprint;

    internal static string ProcessFingerprint(RuntimeProcessIdentity identity) => EnvironmentStateCodec.Hash(string.Join('|',
        identity.ProcessId,
        identity.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        identity.ExecutablePathFingerprint,
        identity.HostFingerprint));

    private static void Validate(ServiceProcessRegistration registration, string sourceFingerprint)
    {
        _ = new StateKey(registration.ProjectId, EntityKinds.Task, registration.TaskId).Normalize();
        _ = new StateKey(registration.ProjectId, EntityKinds.Worktree, registration.WorktreeId).Normalize();
        _ = new StateKey(registration.ProjectId, EntityKinds.EnvironmentInstance, registration.EnvironmentInstanceId).Normalize();
        _ = new StateKey(registration.ProjectId, EntityKinds.ServiceProcess, registration.RegistrationId).Normalize();
        if (string.IsNullOrWhiteSpace(registration.GoalId) || string.IsNullOrWhiteSpace(registration.RuntimeId) ||
            string.IsNullOrWhiteSpace(registration.ProviderId) || string.IsNullOrWhiteSpace(sourceFingerprint) ||
            registration.ResourceLeaseIds.Count > 128 || registration.StartedByHiker == registration.AdoptedByHiker ||
            registration.ProcessIdentity.ProcessId <= 0 || registration.ProcessIdentity.StartTime == default)
        {
            throw new ArgumentException("Service process registration is invalid or ambiguous", nameof(registration));
        }
    }
}

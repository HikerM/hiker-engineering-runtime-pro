using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Diagnostics;

public sealed class AdvancedDoctor(
    IStateStore stateStore,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HealthReport> CheckProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var baseHealth = await stateStore.HealthCheckAsync(cancellationToken).ConfigureAwait(false);
        var findings = baseHealth.Findings.ToList();
        var leases = await stateStore.ReadAsync(projectId, EntityKinds.RuntimeLease, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        await CheckProjectAsync(projectId, leases, findings, cancellationToken).ConfigureAwait(false);
        return findings.Count == 0
            ? HealthReport.Healthy
            : new HealthReport(findings.All(static finding => finding.Repairable) ? "REPAIRABLE" : "BLOCKED", ContractCollections.Freeze(findings));
    }

    private async Task CheckProjectAsync(
        string projectId,
        IEnumerable<StateSnapshot> leases,
        List<HealthFinding> findings,
        CancellationToken cancellationToken)
    {
        var ownership = await stateStore.ReadAsync(projectId, EntityKinds.Ownership, lifecycle: null, 1000, cancellationToken).ConfigureAwait(false);
        var worktrees = await stateStore.ReadAsync(projectId, EntityKinds.Worktree, lifecycle: null, 1000, cancellationToken).ConfigureAwait(false);
        var ownershipByTask = ParseByTask<RuntimeOwnershipState>(ownership, static state => state.TaskId, findings, "INVALID_OWNERSHIP_STATE");
        var worktreesByTask = ParseByTask<RuntimeWorktreeState>(worktrees, static state => state.TaskId, findings, "INVALID_WORKTREE_STATE");
        foreach (var duplicate in worktreesByTask.Where(static item => item.Value.Count > 1))
        {
            findings.Add(new HealthFinding("DUPLICATE_WORKTREE_WRITER", "BLOCKED", duplicate.Key, duplicate.Value.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
        }

        foreach (var leaseState in leases)
        {
            RuntimeLease lease;
            try
            {
                lease = RuntimeStateCodec.Parse<RuntimeLease>(leaseState);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                findings.Add(new HealthFinding("INVALID_RUNTIME_LEASE", "BLOCKED", leaseState.Key.EntityId, exception.GetType().Name, false));
                continue;
            }

            if (!ownershipByTask.TryGetValue(lease.TaskId, out var ownerValues) || ownerValues.Count != 1 ||
                !string.Equals(ownerValues[0].WriterRuntimeId, lease.RuntimeId, StringComparison.Ordinal))
            {
                findings.Add(new HealthFinding("LEASE_OWNERSHIP_DRIFT", "BLOCKED", lease.TaskId, lease.RuntimeId, false));
            }

            if (!worktreesByTask.TryGetValue(lease.TaskId, out var worktreeValues) || worktreeValues.Count != 1 ||
                !string.Equals(worktreeValues[0].WriterRuntimeId, lease.RuntimeId, StringComparison.Ordinal))
            {
                findings.Add(new HealthFinding("LEASE_WORKTREE_DRIFT", "BLOCKED", lease.TaskId, lease.RuntimeId, false));
            }

            if (lease.ExpiresAt <= _clock.GetUtcNow())
            {
                var live = lease.ProcessIdentity is not null &&
                    await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false);
                findings.Add(new HealthFinding(
                    live ? "EXPIRED_LIVE_OWNER" : "ORPHAN_RUNTIME_LEASE",
                    "BLOCKED",
                    lease.TaskId,
                    live ? "identity still live; age is not proof of death" : "owner identity is not live; explicit recovery required",
                    false));
            }
        }
    }

    private static Dictionary<string, List<T>> ParseByTask<T>(
        IEnumerable<StateSnapshot> snapshots,
        Func<T, string> taskId,
        List<HealthFinding> findings,
        string findingCode)
    {
        var values = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (RuntimeStateCodec.IsLegacyImportedFact(snapshot))
            {
                findings.Add(new HealthFinding(
                    "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW",
                    "BLOCKED",
                    snapshot.Key.EntityId,
                    $"{snapshot.Key.Kind} authority={snapshot.Authority} schema={snapshot.SchemaVersion} lifecycle={snapshot.Lifecycle}",
                    false));
                continue;
            }

            if (snapshot.Lifecycle != StateLifecycle.Valid)
            {
                continue;
            }

            try
            {
                var value = RuntimeStateCodec.Parse<T>(snapshot);
                var key = taskId(value);
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new InvalidDataException($"{snapshot.Key.Kind} requires task_id");
                }
                if (!values.TryGetValue(key, out var items))
                {
                    items = new List<T>();
                    values[key] = items;
                }

                items.Add(value);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                findings.Add(new HealthFinding(findingCode, "BLOCKED", snapshot.Key.EntityId, exception.Message, false));
            }
        }

        return values;
    }
}

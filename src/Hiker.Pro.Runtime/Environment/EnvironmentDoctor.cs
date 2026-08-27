using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentHealthFinding(
    string Code,
    string EntityId,
    string Detail);

public sealed record EnvironmentDoctorReport(
    string Status,
    int Profiles,
    int Instances,
    int ResourceLeases,
    int ServiceProcesses,
    int ProviderCalls,
    bool FullSystemScan,
    IReadOnlyList<EnvironmentHealthFinding> Findings);

public sealed class EnvironmentDoctor(
    StateCoordinator state,
    EnvironmentProviderRegistry providers,
    ProcessIdentityProbe processProbe)
{
    private const int StateBudget = 128;

    public async Task<EnvironmentDoctorReport> CheckAsync(
        string projectRoot,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(projectRoot)) throw new DirectoryNotFoundException(projectRoot);
        _ = new StateKey(projectId, EntityKinds.Project, "project").Normalize();
        var findings = new List<EnvironmentHealthFinding>();
        var profiles = await ReadBoundedAsync(projectId, EntityKinds.EnvironmentProfile, findings, cancellationToken).ConfigureAwait(false);
        var instances = await ReadBoundedAsync(projectId, EntityKinds.EnvironmentInstance, findings, cancellationToken).ConfigureAwait(false);
        var leases = await ReadBoundedAsync(projectId, EntityKinds.ResourceLease, findings, cancellationToken).ConfigureAwait(false);
        var processes = await ReadBoundedAsync(projectId, EntityKinds.ServiceProcess, findings, cancellationToken).ConfigureAwait(false);

        RequireAuthority(profiles, findings);
        RequireAuthority(instances, findings);
        RequireAuthority(leases, findings);
        RequireAuthority(processes, findings);

        var parsedProfiles = Parse<EnvironmentProfile>(profiles, findings).ToDictionary(static item => item.ProfileId, StringComparer.Ordinal);
        foreach (var duplicate in parsedProfiles.Values.Where(static profile => profile.Status == EnvironmentStatuses.Selected)
                     .GroupBy(static profile => profile.Purpose, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            findings.Add(new EnvironmentHealthFinding("ENVIRONMENT_AMBIGUOUS", duplicate.Key, "multiple selected profiles share one purpose"));
        }
        foreach (var profile in parsedProfiles.Values.Where(static profile => profile.Status == EnvironmentStatuses.Selected))
        {
            foreach (var providerId in profile.Graph.Nodes.Select(static node => node.ProviderId).Distinct(StringComparer.Ordinal))
            {
                if (!providers.TryResolve(providerId, out _))
                {
                    findings.Add(new EnvironmentHealthFinding("PROVIDER_UNAVAILABLE", profile.ProfileId, providerId));
                }
            }
        }

        var parsedInstances = Parse<EnvironmentInstance>(instances, findings).ToArray();
        var parsedLeases = Parse<EnvironmentResourceLease>(leases, findings).ToDictionary(static lease => lease.LeaseId, StringComparer.Ordinal);
        var parsedProcesses = Parse<ServiceProcessRegistration>(processes, findings).ToArray();
        foreach (var instance in parsedInstances.Where(static value => value.Status == EnvironmentStatuses.Active))
        {
            if (!parsedProfiles.TryGetValue(instance.EnvironmentProfileId, out var profile) || profile.Status != EnvironmentStatuses.Selected ||
                instance.EnvironmentProfileVersion != profile.Version || instance.EnvironmentFingerprint != profile.EnvironmentFingerprint)
            {
                findings.Add(new EnvironmentHealthFinding("ENVIRONMENT_PROFILE_NOT_ACTIVE", instance.EnvironmentInstanceId, instance.EnvironmentProfileId));
                continue;
            }
            foreach (var leaseId in instance.ResourceLeaseIds)
            {
                if (!parsedLeases.TryGetValue(leaseId, out var lease) || lease.Status != "ACTIVE" ||
                    lease.TaskId != instance.TaskId || lease.RuntimeId != instance.RuntimeId ||
                    lease.EnvironmentInstanceId != instance.EnvironmentInstanceId)
                {
                    findings.Add(new EnvironmentHealthFinding("RESOURCE_LEASE_BINDING_DRIFT", instance.EnvironmentInstanceId, leaseId));
                }
            }
        }

        foreach (var lease in parsedLeases.Values.Where(static value => value.Status == "ACTIVE"))
        {
            if (lease.ProcessIdentity is not null && !await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new EnvironmentHealthFinding("RESOURCE_OWNER_PROCESS_NOT_LIVE", lease.LeaseId, "exact process identity is unavailable"));
            }
        }
        foreach (var process in parsedProcesses.Where(static value => value.Status == EnvironmentStatuses.Active))
        {
            if (!await processProbe.IsSameLiveProcessAsync(process.ProcessIdentity, cancellationToken).ConfigureAwait(false))
            {
                findings.Add(new EnvironmentHealthFinding("SERVICE_UNHEALTHY", process.RegistrationId, "registered process identity is not live"));
            }
            if (parsedInstances.All(instance => instance.EnvironmentInstanceId != process.EnvironmentInstanceId || instance.TaskId != process.TaskId))
            {
                findings.Add(new EnvironmentHealthFinding("SERVICE_PROCESS_BINDING_DRIFT", process.RegistrationId, process.EnvironmentInstanceId));
            }
        }

        var providerCalls = 0;
        foreach (var instance in parsedInstances.Where(static value => value.Status == EnvironmentStatuses.Active).Take(StateBudget))
        {
            if (!parsedProfiles.TryGetValue(instance.EnvironmentProfileId, out var profile)) continue;
            foreach (var providerId in profile.Graph.Nodes.Select(static node => node.ProviderId).Distinct(StringComparer.Ordinal))
            {
                if (providerCalls >= StateBudget)
                {
                    findings.Add(new EnvironmentHealthFinding("ENVIRONMENT_HEALTH_BUDGET_EXHAUSTED", projectId, StateBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                }
                if (!providers.TryResolve(providerId, out var provider) || provider is null)
                {
                    findings.Add(new EnvironmentHealthFinding("PROVIDER_UNAVAILABLE", instance.EnvironmentInstanceId, providerId));
                    continue;
                }
                var nodes = profile.Graph.Nodes.Where(node => node.ProviderId == providerId).Select(static node => node.NodeId).ToArray();
                var health = await provider.HealthAsync(new EnvironmentHealthRequest(projectRoot, profile, instance, nodes), cancellationToken).ConfigureAwait(false);
                providerCalls++;
                if (health.CheckedNodeIds.Except(nodes, StringComparer.Ordinal).Any() || health.Findings.Count > 128)
                {
                    findings.Add(new EnvironmentHealthFinding("PROVIDER_HEALTH_RESULT_INVALID", instance.EnvironmentInstanceId, providerId));
                    continue;
                }
                if (health.Status != EnvironmentStatuses.Healthy)
                {
                    findings.AddRange(health.Findings.Take(128).Select(finding => new EnvironmentHealthFinding(health.Status, instance.EnvironmentInstanceId, finding)));
                }
            }
        }

        return new EnvironmentDoctorReport(
            findings.Count == 0 ? EnvironmentStatuses.Healthy : "BLOCKED",
            profiles.Count,
            instances.Count,
            leases.Count,
            processes.Count,
            providerCalls,
            false,
            EnvironmentCollections.Freeze(findings));
    }

    private async Task<IReadOnlyList<StateSnapshot>> ReadBoundedAsync(
        string projectId,
        string kind,
        List<EnvironmentHealthFinding> findings,
        CancellationToken cancellationToken)
    {
        var values = await state.ReadAsync(projectId, kind, StateLifecycle.Valid, StateBudget + 1, cancellationToken).ConfigureAwait(false);
        if (values.Count > StateBudget)
        {
            findings.Add(new EnvironmentHealthFinding("ENVIRONMENT_STATE_BUDGET_EXHAUSTED", kind, StateBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return values.Take(StateBudget).ToArray();
    }

    private static void RequireAuthority(IEnumerable<StateSnapshot> snapshots, List<EnvironmentHealthFinding> findings)
    {
        foreach (var snapshot in snapshots.Where(snapshot => !string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal)))
        {
            findings.Add(new EnvironmentHealthFinding("UNKNOWN_ENVIRONMENT_STATE_AUTHORITY", snapshot.Key.EntityId, snapshot.Authority));
        }
    }

    private static IEnumerable<T> Parse<T>(IEnumerable<StateSnapshot> snapshots, List<EnvironmentHealthFinding> findings)
    {
        foreach (var snapshot in snapshots.Where(snapshot => string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal)))
        {
            T value;
            try
            {
                value = EnvironmentStateCodec.Parse<T>(snapshot);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
            {
                findings.Add(new EnvironmentHealthFinding("ENVIRONMENT_STATE_INVALID", snapshot.Key.EntityId, exception.GetType().Name));
                continue;
            }
            yield return value;
        }
    }
}

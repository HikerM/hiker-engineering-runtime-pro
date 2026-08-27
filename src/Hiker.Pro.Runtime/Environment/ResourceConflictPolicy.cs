namespace Hiker.Pro.Environment;

public sealed record ResourceConflictAssessment(
    string Status,
    string ReasonCode,
    int UsedCapacity,
    int AvailableCapacity);

public static class ResourceConflictPolicy
{
    public static ResourceConflictAssessment Evaluate(
        ResourceRequirement requested,
        IEnumerable<EnvironmentResourceLease> activeLeases,
        IEnumerable<ResourceRequirement>? admittedRequirements = null)
    {
        EnvironmentContractValidation.Requirement(requested);
        var active = activeLeases
            .Where(lease => string.Equals(lease.Status, "ACTIVE", StringComparison.Ordinal) && SameResource(requested, lease.Requirement))
            .Select(static lease => lease.Requirement)
            .Concat((admittedRequirements ?? Array.Empty<ResourceRequirement>()).Where(requirement => SameResource(requested, requirement)))
            .ToArray();
        if (active.Length == 0)
        {
            return new ResourceConflictAssessment("AVAILABLE", "RESOURCE_AVAILABLE", 0, requested.Capacity);
        }

        if (requested.SharingMode == ResourceSharingMode.Isolated && requested.SupportsIsolation)
        {
            return new ResourceConflictAssessment(EnvironmentStatuses.AutoIsolated, "PROVIDER_ISOLATION_REQUIRED", SumUnits(active), requested.Capacity);
        }

        if (requested.SharingMode == ResourceSharingMode.Shared &&
            active.All(static requirement => requirement.SharingMode == ResourceSharingMode.Shared && !requirement.Destructive) &&
            !requested.Destructive)
        {
            return new ResourceConflictAssessment("AVAILABLE", "SHARED_RESOURCE_COMPATIBLE", SumUnits(active), requested.Capacity);
        }

        if (requested.SharingMode == ResourceSharingMode.Capacity &&
            active.All(static requirement => requirement.SharingMode is ResourceSharingMode.Capacity or ResourceSharingMode.Shared))
        {
            var used = SumUnits(active);
            return used + requested.Units <= requested.Capacity
                ? new ResourceConflictAssessment("AVAILABLE", "RESOURCE_CAPACITY_AVAILABLE", used, requested.Capacity - used)
                : new ResourceConflictAssessment(EnvironmentStatuses.CapacityExhausted, "RESOURCE_CAPACITY_EXHAUSTED", used, Math.Max(0, requested.Capacity - used));
        }

        if (requested.SharingMode is ResourceSharingMode.Exclusive or ResourceSharingMode.Isolated ||
            active.Any(static requirement => requirement.SharingMode is ResourceSharingMode.Exclusive or ResourceSharingMode.Isolated))
        {
            return new ResourceConflictAssessment(EnvironmentStatuses.WaitingResource, "ACTIVE_EXCLUSIVE_RESOURCE_LEASE", SumUnits(active), 0);
        }

        return new ResourceConflictAssessment(EnvironmentStatuses.ResourceConflict, "RESOURCE_LEASE_CONFLICT", SumUnits(active), 0);
    }

    public static bool SameResource(ResourceRequirement left, ResourceRequirement right) =>
        string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal) &&
        string.Equals(left.ResourceType, right.ResourceType, StringComparison.Ordinal) &&
        string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal) &&
        string.Equals(left.Scope, right.Scope, StringComparison.Ordinal);

    private static int SumUnits(IEnumerable<ResourceRequirement> requirements) =>
        requirements.Aggregate(0, static (sum, requirement) => checked(sum + requirement.Units));
}

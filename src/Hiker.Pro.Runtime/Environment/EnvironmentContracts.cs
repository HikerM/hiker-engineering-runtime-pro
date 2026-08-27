using System.Collections.ObjectModel;

namespace Hiker.Pro.Environment;

public static class EnvironmentStatuses
{
    public const string Discovered = "DISCOVERED";
    public const string Verified = "VERIFIED";
    public const string Selected = "SELECTED";
    public const string Active = "ACTIVE";
    public const string Stale = "STALE";
    public const string Incompatible = "INCOMPATIBLE";
    public const string Ambiguous = "ENVIRONMENT_AMBIGUOUS";
    public const string Drift = "ENVIRONMENT_DRIFT";
    public const string Healthy = "ENVIRONMENT_HEALTHY";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ToolchainDrift = "TOOLCHAIN_DRIFT";
    public const string ResourceConflict = "RESOURCE_CONFLICT";
    public const string ResourceExhausted = "RESOURCE_EXHAUSTED";
    public const string WaitingResource = "WAITING_RESOURCE";
    public const string AutoIsolated = "AUTO_ISOLATED";
    public const string CapacityExhausted = "CAPACITY_EXHAUSTED";
    public const string ServiceUnhealthy = "SERVICE_UNHEALTHY";
    public const string DependencyUnhealthy = "DEPENDENCY_UNHEALTHY";
    public const string ConfigurationIncompatible = "CONFIGURATION_INCOMPATIBLE";
}

public enum ResourceSharingMode
{
    Shared,
    Exclusive,
    Isolated,
    Capacity
}

public sealed record SecretReference(
    string ProviderId,
    string Reference,
    string Fingerprint,
    bool Available);

public sealed record EnvironmentResourceDescriptor(
    string ResourceType,
    string ResourceId,
    string ProviderId,
    string Scope,
    IReadOnlyDictionary<string, string> Attributes,
    ResourceSharingMode SharingMode,
    int Capacity,
    bool SupportsIsolation,
    bool Destructive);

public sealed record ResourceRequirement(
    string ResourceType,
    string ResourceId,
    string ProviderId,
    string Scope,
    ResourceSharingMode SharingMode,
    int Units = 1,
    int Capacity = 1,
    bool SupportsIsolation = false,
    bool Destructive = false);

public sealed record EnvironmentNode(
    string NodeId,
    string ProviderId,
    string Purpose,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Actions,
    IReadOnlyList<EnvironmentResourceDescriptor> Resources,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<SecretReference> SecretReferences);

public sealed record EnvironmentEdge(
    string FromNodeId,
    string ToNodeId,
    string Relation,
    bool Required,
    string Fingerprint);

public sealed record EnvironmentGraph(
    IReadOnlyList<EnvironmentNode> Nodes,
    IReadOnlyList<EnvironmentEdge> Edges);

public sealed record EnvironmentProviderDescriptor(
    string ProviderId,
    string ContractVersion,
    IReadOnlyList<string> SupportedCapabilities,
    IReadOnlyList<string> SupportedActions,
    IReadOnlyList<string> SupportedResourceTypes,
    IReadOnlyList<string> Privileges,
    IReadOnlyList<string> DestructiveActions);

public sealed record EnvironmentDetectionRequest(
    string ProjectRoot,
    string Purpose,
    int MaximumCandidates = 32,
    int MaximumEvidenceItems = 128);

public sealed record EnvironmentCandidate(
    string CandidateId,
    string ProviderId,
    string Purpose,
    EnvironmentGraph Graph,
    IReadOnlyDictionary<string, string> ToolchainFacts,
    IReadOnlyList<string> HealthProbes,
    IReadOnlyList<string> StartOrAttachActions,
    IReadOnlyList<string> EvidenceRefs,
    string SourceFingerprint,
    string Status = EnvironmentStatuses.Discovered);

public sealed record EnvironmentValidationRequest(
    string ProjectRoot,
    EnvironmentCandidate Candidate);

public sealed record EnvironmentValidationResult(
    string Status,
    bool Verified,
    string EnvironmentFingerprint,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> Findings);

public sealed record EnvironmentPlanRequest(
    string ProjectId,
    string GoalId,
    long GoalRevision,
    string TaskId,
    string WorktreeId,
    string RuntimeId,
    EnvironmentProfile Profile,
    IReadOnlyList<ResourceRequirement> Requirements);

public sealed record EnvironmentPlan(
    string Status,
    IReadOnlyList<EnvironmentAction> Actions,
    IReadOnlyList<ResourceRequirement> AllocatedRequirements,
    bool AdoptExisting,
    IReadOnlyList<string> Findings);

public sealed record EnvironmentInspectionRequest(
    string ProjectRoot,
    EnvironmentProfile Profile,
    string? EnvironmentInstanceId,
    int MaximumObservations = 128,
    string? TaskId = null,
    string? RuntimeId = null,
    IReadOnlyList<ResourceRequirement>? Requirements = null);

public sealed record EnvironmentObservation(
    string Status,
    string ProviderId,
    string NodeId,
    string ObservationFingerprint,
    IReadOnlyDictionary<string, string> NonSecretFacts,
    IReadOnlyList<string> ResourceIds,
    bool ExistingInstanceCanBeAdopted,
    DateTimeOffset ObservedAt);

public sealed record EnvironmentHealthRequest(
    string ProjectRoot,
    EnvironmentProfile Profile,
    EnvironmentInstance? Instance,
    IReadOnlyList<string> NodeIds);

public sealed record EnvironmentHealthResult(
    string Status,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> CheckedNodeIds,
    string ObservationFingerprint);

public sealed record EnvironmentAction(
    string ActionId,
    string ProviderId,
    string NodeId,
    string Action,
    IReadOnlyDictionary<string, string> NonSecretArguments,
    bool Destructive,
    string Risk);

public sealed record EnvironmentActionResult(
    string Status,
    string ActionId,
    IReadOnlyDictionary<string, string> NonSecretFacts,
    IReadOnlyList<string> ResourceIds,
    string EvidenceFingerprint);

public sealed record EnvironmentRecoveryRequest(
    string ProjectRoot,
    EnvironmentProfile Profile,
    EnvironmentInstance Instance,
    IReadOnlyList<EnvironmentResourceLease> Leases,
    IReadOnlyList<ServiceProcessRegistration> Processes);

public sealed record EnvironmentRecoveryResult(
    string Status,
    bool ResourcesRevalidated,
    bool SafeToRecover,
    IReadOnlyList<string> Findings);

public sealed record EnvironmentProfile(
    string ProfileId,
    int Version,
    string ProjectId,
    string Purpose,
    EnvironmentGraph Graph,
    IReadOnlyDictionary<string, string> ToolchainFacts,
    IReadOnlyList<string> HealthProbes,
    IReadOnlyList<string> StartOrAttachActions,
    IReadOnlyList<string> VerificationEvidence,
    string EnvironmentFingerprint,
    string SourceFingerprint,
    string Status,
    DateTimeOffset VerifiedAt);

public sealed record EnvironmentBinding(
    string NodeId,
    string ProviderId,
    string BindingId,
    string Status,
    string ObservationFingerprint,
    bool AdoptedExisting = false);

public sealed record EnvironmentInstance(
    string EnvironmentInstanceId,
    string ProjectId,
    string GoalId,
    long GoalRevision,
    string TaskId,
    string WorktreeId,
    string RuntimeId,
    string EnvironmentProfileId,
    int EnvironmentProfileVersion,
    string EnvironmentFingerprint,
    IReadOnlyList<EnvironmentBinding> Bindings,
    IReadOnlyList<string> ResourceLeaseIds,
    string Status,
    bool AdoptedExisting,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnvironmentResourceLease(
    string LeaseId,
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string WorktreeId,
    string EnvironmentInstanceId,
    ResourceRequirement Requirement,
    Hiker.Pro.Execution.RuntimeProcessIdentity? ProcessIdentity,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    string Status);

public sealed record ServiceProcessRegistration(
    string RegistrationId,
    string ProjectId,
    string GoalId,
    string TaskId,
    string RuntimeId,
    string EnvironmentInstanceId,
    string WorktreeId,
    string ProviderId,
    IReadOnlyList<string> ResourceLeaseIds,
    Hiker.Pro.Execution.RuntimeProcessIdentity ProcessIdentity,
    bool StartedByHiker,
    bool AdoptedByHiker,
    string Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt);

public static class EnvironmentCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    public static IReadOnlyDictionary<string, string> Freeze(IReadOnlyDictionary<string, string> values) =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal));
}

public static class EnvironmentFingerprint
{
    public static string Calculate(EnvironmentCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        EnvironmentContractValidation.Candidate(candidate);
        return EnvironmentStateCodec.Fingerprint(candidate);
    }
}

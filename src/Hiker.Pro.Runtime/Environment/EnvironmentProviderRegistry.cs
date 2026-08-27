namespace Hiker.Pro.Environment;

public interface IEnvironmentProvider
{
    EnvironmentProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(
        EnvironmentDetectionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvironmentCandidate>>(Array.Empty<EnvironmentCandidate>());

    Task<EnvironmentValidationResult> ValidateAsync(
        EnvironmentValidationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentValidationResult(
            EnvironmentStatuses.ConfigurationIncompatible,
            false,
            string.Empty,
            Array.Empty<string>(),
            new[] { "VALIDATION_CAPABILITY_NOT_SUPPORTED" }));

    Task<EnvironmentPlan> PlanAsync(
        EnvironmentPlanRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentPlan(
            EnvironmentStatuses.ConfigurationIncompatible,
            Array.Empty<EnvironmentAction>(),
            Array.Empty<ResourceRequirement>(),
            false,
            new[] { "PLAN_CAPABILITY_NOT_SUPPORTED" }));

    Task<IReadOnlyList<EnvironmentObservation>> InspectAsync(
        EnvironmentInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvironmentObservation>>(Array.Empty<EnvironmentObservation>());

    Task<IReadOnlyList<EnvironmentObservation>> ObserveAsync(
        EnvironmentInspectionRequest request,
        CancellationToken cancellationToken = default) =>
        InspectAsync(request, cancellationToken);

    Task<EnvironmentActionResult> AttachAsync(
        EnvironmentAction action,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(action, cancellationToken);

    Task<EnvironmentActionResult> AllocateAsync(
        EnvironmentAction action,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(action, cancellationToken);

    Task<EnvironmentActionResult> ProvisionAsync(
        EnvironmentAction action,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(action, cancellationToken);

    Task<EnvironmentActionResult> ExecuteActionAsync(
        EnvironmentAction action,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentActionResult(
            EnvironmentStatuses.ConfigurationIncompatible,
            action.ActionId,
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            string.Empty));

    Task<EnvironmentHealthResult> HealthAsync(
        EnvironmentHealthRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentHealthResult(
            EnvironmentStatuses.ProviderUnavailable,
            new[] { "HEALTH_CAPABILITY_NOT_SUPPORTED" },
            Array.Empty<string>(),
            string.Empty));

    Task<EnvironmentRecoveryResult> RecoverAsync(
        EnvironmentRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentRecoveryResult(
            EnvironmentStatuses.ProviderUnavailable,
            false,
            false,
            new[] { "RECOVERY_CAPABILITY_NOT_SUPPORTED" }));

    Task<EnvironmentActionResult> ReleaseAsync(
        EnvironmentAction action,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(action, cancellationToken);
}

public sealed class EnvironmentProviderRegistry
{
    private readonly Dictionary<string, IEnvironmentProvider> _providers = new(StringComparer.Ordinal);

    public IReadOnlyList<EnvironmentProviderDescriptor> Descriptors =>
        _providers.Values
            .Select(static provider => provider.Descriptor)
            .OrderBy(static descriptor => descriptor.ProviderId, StringComparer.Ordinal)
            .ToArray();

    public void Register(IEnvironmentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        EnvironmentContractValidation.Provider(provider.Descriptor);
        if (_providers.Count >= 128)
        {
            throw new InvalidOperationException("Environment provider registry capacity is bounded at 128 providers");
        }
        if (!_providers.TryAdd(provider.Descriptor.ProviderId, provider))
        {
            throw new InvalidOperationException($"Environment provider already registered: {provider.Descriptor.ProviderId}");
        }
    }

    public IEnvironmentProvider Require(string providerId) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new InvalidOperationException($"Environment provider is unavailable: {providerId}");

    public bool TryResolve(string providerId, out IEnvironmentProvider? provider) =>
        _providers.TryGetValue(providerId, out provider);
}

internal static class EnvironmentContractValidation
{
    private static readonly string[] SensitiveNames =
    {
        "secret", "token", "password", "credential", "private-key", "private_key", "api-key", "api_key"
    };

    public static void Provider(EnvironmentProviderDescriptor descriptor)
    {
        Token(descriptor.ProviderId, nameof(descriptor.ProviderId));
        Token(descriptor.ContractVersion, nameof(descriptor.ContractVersion));
        Bounded(descriptor.SupportedCapabilities, 256, "capabilities");
        Bounded(descriptor.SupportedActions, 256, "actions");
        Bounded(descriptor.SupportedResourceTypes, 256, "resource types");
        Bounded(descriptor.Privileges, 128, "privileges");
        Bounded(descriptor.DestructiveActions, 128, "destructive actions");
    }

    public static void Candidate(EnvironmentCandidate candidate)
    {
        Token(candidate.CandidateId, nameof(candidate.CandidateId));
        Token(candidate.ProviderId, nameof(candidate.ProviderId));
        Token(candidate.Purpose, nameof(candidate.Purpose));
        Token(candidate.SourceFingerprint, nameof(candidate.SourceFingerprint));
        if (candidate.Graph.Nodes.Count is < 1 or > 256 || candidate.Graph.Edges.Count > 1024 ||
            candidate.ToolchainFacts.Count > 256 || candidate.HealthProbes.Count > 256 ||
            candidate.StartOrAttachActions.Count > 256 || candidate.EvidenceRefs.Count > 256)
        {
            throw new ArgumentException("Environment candidate exceeds bounded graph limits", nameof(candidate));
        }

        NonSecret(candidate.ToolchainFacts);
        foreach (var node in candidate.Graph.Nodes)
        {
            Token(node.NodeId, nameof(node.NodeId));
            Token(node.ProviderId, nameof(node.ProviderId));
            Token(node.Purpose, nameof(node.Purpose));
            Bounded(node.Capabilities, 256, "node capabilities");
            Bounded(node.Actions, 256, "node actions");
            NonSecret(node.Attributes);
            if (node.Resources.Count > 256 || node.SecretReferences.Count > 128)
            {
                throw new ArgumentException("Environment node exceeds resource or secret-reference limits", nameof(candidate));
            }

            foreach (var resource in node.Resources) Resource(resource);
            foreach (var secret in node.SecretReferences)
            {
                Token(secret.ProviderId, nameof(secret.ProviderId));
                Token(secret.Reference, nameof(secret.Reference));
                Token(secret.Fingerprint, nameof(secret.Fingerprint));
            }
        }

        if (candidate.Graph.Nodes.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != candidate.Graph.Nodes.Count)
        {
            throw new ArgumentException("Environment graph node IDs must be unique", nameof(candidate));
        }

        var nodes = candidate.Graph.Nodes.Select(static node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in candidate.Graph.Edges)
        {
            if (!nodes.Contains(edge.FromNodeId) || !nodes.Contains(edge.ToNodeId))
            {
                throw new ArgumentException("Environment graph edge references an unknown node", nameof(candidate));
            }
            Token(edge.Relation, nameof(edge.Relation));
            Token(edge.Fingerprint, nameof(edge.Fingerprint));
        }
    }

    public static void Requirement(ResourceRequirement requirement)
    {
        Token(requirement.ResourceType, nameof(requirement.ResourceType));
        Token(requirement.ResourceId, nameof(requirement.ResourceId));
        Token(requirement.ProviderId, nameof(requirement.ProviderId));
        Token(requirement.Scope, nameof(requirement.Scope));
        if (requirement.Units is < 1 or > 1_000_000 || requirement.Capacity is < 1 or > 1_000_000 ||
            requirement.Units > requirement.Capacity)
        {
            throw new ArgumentException("Resource requirement capacity is invalid", nameof(requirement));
        }
    }

    public static void NonSecret(IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            Token(pair.Key, "attribute key");
            if (SensitiveNames.Any(name => pair.Key.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
                pair.Value.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                Uri.TryCreate(pair.Value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new ArgumentException("Environment state must contain secret references, never secret values");
            }

            if (System.Text.Encoding.UTF8.GetByteCount(pair.Value) > 4096)
            {
                throw new ArgumentException("Environment attribute exceeds bounded size");
            }
        }
    }

    private static void Resource(EnvironmentResourceDescriptor resource)
    {
        Token(resource.ResourceType, nameof(resource.ResourceType));
        Token(resource.ResourceId, nameof(resource.ResourceId));
        Token(resource.ProviderId, nameof(resource.ProviderId));
        Token(resource.Scope, nameof(resource.Scope));
        NonSecret(resource.Attributes);
        if (resource.Capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentException("Environment resource capacity is invalid");
        }
    }

    private static void Bounded(IReadOnlyCollection<string> values, int maximum, string name)
    {
        if (values.Count > maximum || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"Environment provider {name} are invalid or unbounded");
        }
    }

    private static void Token(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException($"{name} must contain 1-256 characters", name);
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.Environment;

public sealed class LocalProjectEnvironmentProvider : IEnvironmentProvider
{
    public const string Id = "local-project";
    private static readonly string[] ManifestNames =
    {
        "global.json", "package.json", "composer.json", "CMakeLists.txt", "ProjectSettings/ProjectVersion.txt"
    };
    private readonly string _projectRoot;

    public LocalProjectEnvironmentProvider(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    public EnvironmentProviderDescriptor Descriptor { get; } = new(
        Id,
        "hiker-environment-provider/v1",
        new[] { "detect", "inspect", "validate", "plan", "attach", "observe", "health", "recover", "release" },
        new[] { "attach-existing" },
        new[] { "project-workspace", "process-slot", "network-endpoint", "database", "device", "gpu" },
        Array.Empty<string>(),
        Array.Empty<string>());

    public async Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(
        EnvironmentDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SameRoot(request.ProjectRoot) || !Directory.Exists(_projectRoot)) return Array.Empty<EnvironmentCandidate>();
        var facts = await FactsAsync(cancellationToken).ConfigureAwait(false);
        return new[] { Candidate(request.Purpose, facts) };
    }

    public Task<EnvironmentValidationResult> ValidateAsync(
        EnvironmentValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var valid = SameRoot(request.ProjectRoot) && Directory.Exists(_projectRoot) && request.Candidate.ProviderId == Id;
        return Task.FromResult(new EnvironmentValidationResult(
            valid ? EnvironmentStatuses.Verified : EnvironmentStatuses.ConfigurationIncompatible,
            valid,
            valid ? EnvironmentFingerprint.Calculate(request.Candidate) : string.Empty,
            valid ? request.Candidate.EvidenceRefs : Array.Empty<string>(),
            valid ? Array.Empty<string>() : new[] { "LOCAL_PROJECT_ROOT_UNAVAILABLE" }));
    }

    public async Task<IReadOnlyList<EnvironmentObservation>> InspectAsync(
        EnvironmentInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SameRoot(request.ProjectRoot) || !Directory.Exists(_projectRoot)) return Array.Empty<EnvironmentObservation>();
        var source = SourceFingerprint(await FactsAsync(cancellationToken).ConfigureAwait(false));
        return new[]
        {
            new EnvironmentObservation(
                EnvironmentStatuses.Healthy,
                Id,
                "local-project-root",
                source,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["root_fingerprint"] = RootFingerprint(),
                    ["availability"] = "available"
                },
                new[] { WorkspaceResourceId() },
                true,
                DateTimeOffset.UtcNow)
        };
    }

    public Task<EnvironmentPlan> PlanAsync(EnvironmentPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Requirements.Any(requirement => requirement.ProviderId != Id || requirement.SharingMode == ResourceSharingMode.Isolated))
        {
            return Task.FromResult(new EnvironmentPlan(
                EnvironmentStatuses.ConfigurationIncompatible,
                Array.Empty<EnvironmentAction>(),
                Array.Empty<ResourceRequirement>(),
                false,
                new[] { "LOCAL_PROJECT_PROVIDER_CANNOT_SYNTHESIZE_ISOLATION" }));
        }
        return Task.FromResult(new EnvironmentPlan(
            "PLANNED",
            Array.Empty<EnvironmentAction>(),
            request.Requirements,
            true,
            Array.Empty<string>()));
    }

    public async Task<EnvironmentHealthResult> HealthAsync(
        EnvironmentHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        var available = SameRoot(request.ProjectRoot) && Directory.Exists(_projectRoot);
        var source = available ? SourceFingerprint(await FactsAsync(cancellationToken).ConfigureAwait(false)) : string.Empty;
        var bindings = request.Instance?.Bindings.Where(static binding => binding.ProviderId == Id).ToArray() ?? Array.Empty<EnvironmentBinding>();
        var healthy = available && bindings.Length > 0 && bindings.All(binding => binding.ObservationFingerprint == source);
        return new EnvironmentHealthResult(
            healthy ? EnvironmentStatuses.Healthy : available ? EnvironmentStatuses.ToolchainDrift : EnvironmentStatuses.ProviderUnavailable,
            healthy ? Array.Empty<string>() : new[] { available ? "LOCAL_PROJECT_FACTS_CHANGED" : "LOCAL_PROJECT_ROOT_UNAVAILABLE" },
            request.NodeIds,
            source);
    }

    public async Task<EnvironmentRecoveryResult> RecoverAsync(
        EnvironmentRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var health = await HealthAsync(new EnvironmentHealthRequest(
            request.ProjectRoot,
            request.Profile,
            request.Instance,
            request.Profile.Graph.Nodes.Where(static node => node.ProviderId == Id).Select(static node => node.NodeId).ToArray()), cancellationToken).ConfigureAwait(false);
        var leasesValid = request.Leases.All(lease => lease.Requirement.ProviderId == Id && lease.Status == "ACTIVE");
        var safe = health.Status == EnvironmentStatuses.Healthy && leasesValid;
        return new EnvironmentRecoveryResult(
            safe ? EnvironmentStatuses.Healthy : "RECOVERY_REQUIRED",
            leasesValid,
            safe,
            safe ? Array.Empty<string>() : health.Findings.Concat(leasesValid ? Array.Empty<string>() : new[] { "LOCAL_RESOURCE_LEASE_INVALID" }).ToArray());
    }

    public Task<EnvironmentActionResult> ReleaseAsync(EnvironmentAction action, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentActionResult("RELEASED", action.ActionId, new Dictionary<string, string> { ["adopted"] = "preserved" }, Array.Empty<string>(), RootFingerprint()));

    private EnvironmentCandidate Candidate(string purpose, IReadOnlyDictionary<string, string> facts)
    {
        var source = SourceFingerprint(facts);
        return new EnvironmentCandidate(
            $"local-{RootFingerprint()[..24]}",
            Id,
            purpose,
            new EnvironmentGraph(
                new[]
                {
                    new EnvironmentNode(
                        "local-project-root",
                        Id,
                        purpose,
                        Descriptor.SupportedCapabilities,
                        Descriptor.SupportedActions,
                        new[]
                        {
                            new EnvironmentResourceDescriptor(
                                "project-workspace",
                                WorkspaceResourceId(),
                                Id,
                                "project",
                                new Dictionary<string, string> { ["root_fingerprint"] = RootFingerprint() },
                                ResourceSharingMode.Shared,
                                8,
                                false,
                                false)
                        },
                        new Dictionary<string, string> { ["root_fingerprint"] = RootFingerprint() },
                        Array.Empty<SecretReference>())
                },
                Array.Empty<EnvironmentEdge>()),
            facts,
            new[] { "project-root", "toolchain-fingerprint" },
            new[] { "attach-existing" },
            new[] { $"local-facts:{source}" },
            source);
    }

    private async Task<IReadOnlyDictionary<string, string>> FactsAsync(CancellationToken cancellationToken)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host_os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["host_architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["runtime_version"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["root_fingerprint"] = RootFingerprint()
        };
        foreach (var name in ManifestNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_projectRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            var key = $"manifest:{name}";
            if (info.Length <= 1_048_576)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                values[key] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }
            else
            {
                values[key] = Hash($"{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
        return values;
    }

    private bool SameRoot(string value) => string.Equals(
        Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar),
        _projectRoot.TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string RootFingerprint() => Hash(_projectRoot.ToUpperInvariant());
    private string WorkspaceResourceId() => $"workspace-{RootFingerprint()[..24]}";
    private static string SourceFingerprint(IReadOnlyDictionary<string, string> facts) => Hash(string.Join('\n', facts.OrderBy(static item => item.Key, StringComparer.Ordinal).Select(static item => $"{item.Key}={item.Value}")));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

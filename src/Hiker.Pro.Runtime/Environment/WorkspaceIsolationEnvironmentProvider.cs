using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Environment;

public sealed class WorkspaceIsolationEnvironmentProvider : IEnvironmentProvider
{
    public const string Id = "workspace-isolation";
    private const string MarkerName = "hiker-environment.json";
    private readonly string _projectRoot;
    private readonly string _environmentRoot;

    public WorkspaceIsolationEnvironmentProvider(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _environmentRoot = Path.Combine(_projectRoot, ".ai", "runtime", "environments");
    }

    public EnvironmentProviderDescriptor Descriptor { get; } = new(
        Id,
        "hiker-environment-provider/v1",
        new[] { "detect", "inspect", "validate", "plan", "allocate", "provision", "observe", "health", "recover", "release" },
        new[] { "create-isolated-workspace", "release-resource" },
        new[] { "isolated-workspace" },
        new[] { "project-local-state-write" },
        new[] { "release-resource" });

    public Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(EnvironmentDetectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!SameRoot(request.ProjectRoot)) return Task.FromResult<IReadOnlyList<EnvironmentCandidate>>(Array.Empty<EnvironmentCandidate>());
        var rootFingerprint = RootFingerprint();
        var candidate = new EnvironmentCandidate(
            $"isolation-{rootFingerprint[..24]}",
            Id,
            request.Purpose,
            new EnvironmentGraph(
                new[]
                {
                    new EnvironmentNode(
                        "isolated-workspace",
                        Id,
                        request.Purpose,
                        Descriptor.SupportedCapabilities,
                        Descriptor.SupportedActions,
                        new[]
                        {
                            new EnvironmentResourceDescriptor(
                                "isolated-workspace", "allocate-per-task", Id, "task", new Dictionary<string, string> { ["root_fingerprint"] = rootFingerprint },
                                ResourceSharingMode.Isolated, 1, true, true)
                        },
                        new Dictionary<string, string> { ["root_fingerprint"] = rootFingerprint },
                        Array.Empty<SecretReference>())
                },
                Array.Empty<EnvironmentEdge>()),
            new Dictionary<string, string> { ["isolation_contract"] = "project-local-directory/v1", ["root_fingerprint"] = rootFingerprint },
            new[] { "ownership-marker", "bounded-path" },
            new[] { "create-isolated-workspace" },
            new[] { $"isolation-root:{rootFingerprint}" },
            SourceFingerprint());
        return Task.FromResult<IReadOnlyList<EnvironmentCandidate>>(new[] { candidate });
    }

    public Task<EnvironmentValidationResult> ValidateAsync(EnvironmentValidationRequest request, CancellationToken cancellationToken = default)
    {
        var valid = SameRoot(request.ProjectRoot) && request.Candidate.ProviderId == Id;
        return Task.FromResult(new EnvironmentValidationResult(
            valid ? EnvironmentStatuses.Verified : EnvironmentStatuses.ConfigurationIncompatible,
            valid,
            valid ? EnvironmentFingerprint.Calculate(request.Candidate) : string.Empty,
            valid ? request.Candidate.EvidenceRefs : Array.Empty<string>(),
            valid ? Array.Empty<string>() : new[] { "ISOLATION_ROOT_MISMATCH" }));
    }

    public Task<IReadOnlyList<EnvironmentObservation>> InspectAsync(EnvironmentInspectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!SameRoot(request.ProjectRoot) || request.TaskId is null || request.RuntimeId is null)
            return Task.FromResult<IReadOnlyList<EnvironmentObservation>>(Array.Empty<EnvironmentObservation>());
        var requirement = request.Requirements?.SingleOrDefault();
        if (requirement is null) return Task.FromResult<IReadOnlyList<EnvironmentObservation>>(Array.Empty<EnvironmentObservation>());
        var resourceId = ConcreteResourceId(request.TaskId, request.RuntimeId, requirement.Scope);
        var valid = MarkerIsValid(resourceId, request.TaskId, request.RuntimeId);
        return Task.FromResult<IReadOnlyList<EnvironmentObservation>>(new[]
        {
            new EnvironmentObservation(
                valid ? EnvironmentStatuses.Healthy : EnvironmentStatuses.Discovered,
                Id,
                "isolated-workspace",
                Hash($"{resourceId}|{valid}"),
                new Dictionary<string, string> { ["resource_id"] = resourceId, ["ownership"] = valid ? "verified" : "absent" },
                valid ? new[] { resourceId } : Array.Empty<string>(),
                valid,
                DateTimeOffset.UtcNow)
        });
    }

    public Task<EnvironmentPlan> PlanAsync(EnvironmentPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Requirements.Count != 1 || request.Requirements[0].ProviderId != Id ||
            request.Requirements[0].SharingMode != ResourceSharingMode.Isolated || !request.Requirements[0].SupportsIsolation)
        {
            return Task.FromResult(new EnvironmentPlan(EnvironmentStatuses.ConfigurationIncompatible, Array.Empty<EnvironmentAction>(), Array.Empty<ResourceRequirement>(), false, new[] { "ISOLATED_WORKSPACE_REQUIREMENT_INVALID" }));
        }
        var expected = request.Requirements[0];
        var resourceId = ConcreteResourceId(request.TaskId, request.RuntimeId, expected.Scope);
        var allocated = expected with { ResourceId = resourceId, Capacity = 1, Units = 1, SupportsIsolation = true };
        var existing = MarkerIsValid(resourceId, request.TaskId, request.RuntimeId);
        var action = new EnvironmentAction(
            $"create-{resourceId}",
            Id,
            "isolated-workspace",
            "create-isolated-workspace",
            new Dictionary<string, string>
            {
                ["resource_id"] = resourceId,
                ["task_fingerprint"] = Hash(request.TaskId),
                ["runtime_fingerprint"] = Hash(request.RuntimeId)
            },
            false,
            "MEDIUM");
        return Task.FromResult(new EnvironmentPlan(
            "PLANNED",
            existing ? Array.Empty<EnvironmentAction>() : new[] { action },
            new[] { allocated },
            existing,
            Array.Empty<string>()));
    }

    public async Task<EnvironmentActionResult> ExecuteActionAsync(EnvironmentAction action, CancellationToken cancellationToken = default)
    {
        ValidateAction(action, "create-isolated-workspace");
        var resourceId = action.NonSecretArguments["resource_id"];
        var path = ResourcePath(resourceId);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, MarkerName);
        var payload = JsonSerializer.Serialize(new
        {
            schema = "hiker-isolated-workspace/v1",
            resource_id = resourceId,
            task_fingerprint = action.NonSecretArguments["task_fingerprint"],
            runtime_fingerprint = action.NonSecretArguments["runtime_fingerprint"]
        });
        await File.WriteAllTextAsync(marker, payload, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return new EnvironmentActionResult("COMPLETED", action.ActionId, new Dictionary<string, string> { ["resource_id"] = resourceId }, new[] { resourceId }, Hash(payload));
    }

    public Task<EnvironmentHealthResult> HealthAsync(EnvironmentHealthRequest request, CancellationToken cancellationToken = default)
    {
        var bindings = request.Instance?.Bindings.Where(static binding => binding.ProviderId == Id).ToArray() ?? Array.Empty<EnvironmentBinding>();
        var healthy = SameRoot(request.ProjectRoot) && request.Instance is not null &&
                      bindings.All(binding => MarkerIsValid(binding.BindingId, request.Instance.TaskId, request.Instance.RuntimeId));
        return Task.FromResult(new EnvironmentHealthResult(
            healthy ? EnvironmentStatuses.Healthy : EnvironmentStatuses.ServiceUnhealthy,
            healthy ? Array.Empty<string>() : new[] { "ISOLATED_WORKSPACE_MARKER_INVALID" },
            request.NodeIds,
            Hash(string.Join('|', bindings.Select(static binding => binding.BindingId).Order(StringComparer.Ordinal)))));
    }

    public Task<EnvironmentRecoveryResult> RecoverAsync(EnvironmentRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        var valid = SameRoot(request.ProjectRoot) && request.Leases.All(lease =>
            lease.Requirement.ProviderId == Id && lease.Requirement.SharingMode == ResourceSharingMode.Isolated &&
            MarkerIsValid(lease.Requirement.ResourceId, lease.TaskId, lease.RuntimeId));
        return Task.FromResult(new EnvironmentRecoveryResult(
            valid ? EnvironmentStatuses.Healthy : "RECOVERY_REQUIRED",
            valid,
            valid,
            valid ? Array.Empty<string>() : new[] { "ISOLATED_RESOURCE_REVALIDATION_FAILED" }));
    }

    public Task<EnvironmentActionResult> ReleaseAsync(EnvironmentAction action, CancellationToken cancellationToken = default)
    {
        ValidateAction(action, "release-resource");
        var resourceId = action.NonSecretArguments["resource_id"];
        var path = ResourcePath(resourceId);
        var marker = Path.Combine(path, MarkerName);
        if (!File.Exists(marker))
            return Task.FromResult(new EnvironmentActionResult("RELEASED", action.ActionId, new Dictionary<string, string> { ["already_absent"] = "true" }, Array.Empty<string>(), Hash(resourceId)));
        if (!action.NonSecretArguments.TryGetValue("task_fingerprint", out var taskFingerprint) ||
            !action.NonSecretArguments.TryGetValue("runtime_fingerprint", out var runtimeFingerprint) ||
            !MarkerMatchesFingerprints(resourceId, taskFingerprint, runtimeFingerprint))
            return Task.FromResult(new EnvironmentActionResult("RECOVERY_REQUIRED", action.ActionId, new Dictionary<string, string> { ["ownership"] = "invalid" }, Array.Empty<string>(), Hash(resourceId)));
        Directory.Delete(path, recursive: true);
        return Task.FromResult(new EnvironmentActionResult("RELEASED", action.ActionId, new Dictionary<string, string> { ["resource_id"] = resourceId }, Array.Empty<string>(), Hash(resourceId)));
    }

    private bool MarkerIsValid(string resourceId, string taskId, string runtimeId)
    {
        var marker = Path.Combine(ResourcePath(resourceId), MarkerName);
        if (!File.Exists(marker)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var root = document.RootElement;
            return root.GetProperty("schema").GetString() == "hiker-isolated-workspace/v1" &&
                   root.GetProperty("resource_id").GetString() == resourceId &&
                   root.GetProperty("task_fingerprint").GetString() == Hash(taskId) &&
                   root.GetProperty("runtime_fingerprint").GetString() == Hash(runtimeId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool MarkerMatchesFingerprints(string resourceId, string taskFingerprint, string runtimeFingerprint)
    {
        var marker = Path.Combine(ResourcePath(resourceId), MarkerName);
        if (!File.Exists(marker)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            return document.RootElement.GetProperty("schema").GetString() == "hiker-isolated-workspace/v1" &&
                   document.RootElement.GetProperty("resource_id").GetString() == resourceId &&
                   document.RootElement.GetProperty("task_fingerprint").GetString() == taskFingerprint &&
                   document.RootElement.GetProperty("runtime_fingerprint").GetString() == runtimeFingerprint;
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private string ResourcePath(string resourceId)
    {
        if (!resourceId.StartsWith("sandbox-", StringComparison.Ordinal) || resourceId.Length != 32 || resourceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidDataException("Isolated workspace resource ID is invalid");
        var path = Path.GetFullPath(Path.Combine(_environmentRoot, resourceId));
        var prefix = Path.GetFullPath(_environmentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Isolated workspace path escaped the environment root");
        return path;
    }

    private void ValidateAction(EnvironmentAction action, string expected)
    {
        if (action.ProviderId != Id || action.Action != expected || !action.NonSecretArguments.TryGetValue("resource_id", out var resourceId))
            throw new InvalidOperationException("Workspace isolation action is invalid");
        _ = ResourcePath(resourceId);
    }

    private bool SameRoot(string value) => string.Equals(Path.GetFullPath(value), _projectRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private string ConcreteResourceId(string taskId, string runtimeId, string scope) => $"sandbox-{Hash(string.Join('|', RootFingerprint(), taskId, runtimeId, scope))[..24]}";
    private string RootFingerprint() => Hash(OperatingSystem.IsWindows() ? _projectRoot.ToUpperInvariant() : _projectRoot);
    private string SourceFingerprint() => Hash($"{RootFingerprint()}|hiker-isolated-workspace/v1");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

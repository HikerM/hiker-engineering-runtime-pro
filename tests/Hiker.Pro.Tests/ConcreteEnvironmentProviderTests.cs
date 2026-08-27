using Hiker.Pro.Environment;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ConcreteEnvironmentProviderTests
{
    [Fact]
    public async Task LocalProjectProviderAdoptsObservedWorkspaceWithoutProvisioning()
    {
        await using var project = await TestProject.CreateAsync("local-field");
        await File.WriteAllTextAsync(Path.Combine(project.Root, "package.json"), "{\"engines\":{\"node\":\">=22\"}}");
        var provider = new LocalProjectEnvironmentProvider(project.Root);
        var registry = Registry(provider);
        var profile = await SelectAsync(project, registry, "local-select");
        var resource = Assert.Single(Assert.Single(profile.Graph.Nodes).Resources);
        var requirement = new ResourceRequirement(
            resource.ResourceType, resource.ResourceId, resource.ProviderId, resource.Scope,
            resource.SharingMode, 1, resource.Capacity, resource.SupportsIsolation, resource.Destructive);
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var instances = new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources);

        var activated = await instances.ActivateAsync(Request(project, profile.ProfileId, "local-activate", requirement));
        var doctor = await new EnvironmentDoctor(project.Coordinator, registry, new ProcessIdentityProbe()).CheckAsync(project.Root, "local-field");

        Assert.Equal(EnvironmentStatuses.Active, activated.Status);
        Assert.True(activated.Instance?.AdoptedExisting);
        Assert.Equal(LocalProjectEnvironmentProvider.Id, Assert.Single(activated.Instance!.Bindings).ProviderId);
        Assert.Equal(EnvironmentStatuses.Healthy, doctor.Status);
        Assert.DoesNotContain(project.Root, profile.ToolchainFacts.Values);
    }

    [Fact]
    public async Task IsolatedWorkspaceProviderCreatesOwnedSandboxAndReleasesOnlyThatSandbox()
    {
        await using var project = await TestProject.CreateAsync("isolation-field");
        var preserved = Path.Combine(project.Root, ".ai", "preserved.txt");
        await File.WriteAllTextAsync(preserved, "keep");
        var provider = new WorkspaceIsolationEnvironmentProvider(project.Root);
        var registry = Registry(provider);
        var profile = await SelectAsync(project, registry, "isolation-select");
        var requirement = new ResourceRequirement(
            "isolated-workspace", "allocate-per-task", WorkspaceIsolationEnvironmentProvider.Id, "task",
            ResourceSharingMode.Isolated, 1, 1, true, true);
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var processes = new EnvironmentProcessRegistry(project.Coordinator, new ProcessIdentityProbe());
        var instances = new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources);

        var activated = await instances.ActivateAsync(Request(project, profile.ProfileId, "isolation-activate", requirement));
        var lease = Assert.Single(activated.Leases);
        var sandbox = Path.Combine(project.Root, ".ai", "runtime", "environments", lease.Requirement.ResourceId);
        Assert.True(File.Exists(Path.Combine(sandbox, "hiker-environment.json")));
        Assert.False(activated.Instance?.AdoptedExisting);
        var release = await new EnvironmentReleaseCoordinator(project.Coordinator, registry, resources, processes).ReleaseAsync(new EnvironmentReleaseRequest(
            "isolation-release", "isolation-field", "task-1", "runtime-1", activated.Instance!.EnvironmentInstanceId, "source"));

        Assert.Equal("RELEASED", release.Status);
        Assert.False(Directory.Exists(sandbox));
        Assert.True(File.Exists(preserved));
    }

    [Fact]
    public async Task MultiProviderGraphAdoptsLocalNodeAndProvisionsIsolatedNodeInOneTaskInstance()
    {
        await using var project = await TestProject.CreateAsync("multi-provider-field");
        var local = new LocalProjectEnvironmentProvider(project.Root);
        var isolation = new WorkspaceIsolationEnvironmentProvider(project.Root);
        var composition = new CompositionProvider(project.Root, local, isolation);
        var registry = Registry(composition);
        var profile = await SelectAsync(project, registry, "composition-select");
        registry.Register(local);
        registry.Register(isolation);
        var localResource = profile.Graph.Nodes.Single(node => node.ProviderId == LocalProjectEnvironmentProvider.Id).Resources.Single();
        var requirements = new[]
        {
            new ResourceRequirement(localResource.ResourceType, localResource.ResourceId, localResource.ProviderId, localResource.Scope, localResource.SharingMode, 1, localResource.Capacity),
            new ResourceRequirement("isolated-workspace", "allocate-per-task", WorkspaceIsolationEnvironmentProvider.Id, "task", ResourceSharingMode.Isolated, 1, 1, true, true)
        };
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var activated = await new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources)
            .ActivateAsync(Request(project, profile.ProfileId, "multi-activate", requirements));

        Assert.Equal(EnvironmentStatuses.Active, activated.Status);
        Assert.Equal(2, activated.Instance?.Bindings.Count);
        Assert.Contains(activated.Instance!.Bindings, static binding => binding.ProviderId == LocalProjectEnvironmentProvider.Id && binding.AdoptedExisting);
        Assert.Contains(activated.Instance.Bindings, static binding => binding.ProviderId == WorkspaceIsolationEnvironmentProvider.Id && !binding.AdoptedExisting);
        Assert.Equal(2, activated.Leases.Count);
    }

    [Fact]
    public async Task LocalToolchainDriftIsDetectedFromExplicitManifestWithoutRepositoryScan()
    {
        await using var project = await TestProject.CreateAsync("local-drift-field");
        var manifest = Path.Combine(project.Root, "package.json");
        await File.WriteAllTextAsync(manifest, "{\"version\":1}");
        var provider = new LocalProjectEnvironmentProvider(project.Root);
        var registry = Registry(provider);
        var profile = await SelectAsync(project, registry, "local-drift-select");
        var resource = Assert.Single(Assert.Single(profile.Graph.Nodes).Resources);
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        _ = await new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources).ActivateAsync(Request(
            project, profile.ProfileId, "local-drift-activate",
            new ResourceRequirement(resource.ResourceType, resource.ResourceId, resource.ProviderId, resource.Scope, resource.SharingMode, 1, resource.Capacity)));
        await File.WriteAllTextAsync(manifest, "{\"version\":2}");

        var doctor = await new EnvironmentDoctor(project.Coordinator, registry, new ProcessIdentityProbe()).CheckAsync(project.Root, project.ProjectId!);

        Assert.Equal("BLOCKED", doctor.Status);
        Assert.Contains(doctor.Findings, static finding => finding.Code == EnvironmentStatuses.ToolchainDrift);
        Assert.False(doctor.FullSystemScan);
    }

    [Fact]
    public async Task IsolationProviderRejectsTraversalAndPreservesSandboxWhenOwnershipMarkerIsCorrupt()
    {
        await using var project = await TestProject.CreateAsync("isolation-safety");
        var provider = new WorkspaceIsolationEnvironmentProvider(project.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ReleaseAsync(new EnvironmentAction(
            "unsafe-release", WorkspaceIsolationEnvironmentProvider.Id, "isolated-workspace", "release-resource",
            new Dictionary<string, string>
            {
                ["resource_id"] = "../outside",
                ["task_fingerprint"] = "task",
                ["runtime_fingerprint"] = "runtime"
            }, true, "HIGH")));
        var registry = Registry(provider);
        var profile = await SelectAsync(project, registry, "isolation-safety-select");
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var processes = new EnvironmentProcessRegistry(project.Coordinator, new ProcessIdentityProbe());
        var activated = await new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources).ActivateAsync(Request(
            project, profile.ProfileId, "isolation-safety-activate",
            new ResourceRequirement("isolated-workspace", "allocate-per-task", WorkspaceIsolationEnvironmentProvider.Id, "task", ResourceSharingMode.Isolated, 1, 1, true, true)));
        var lease = Assert.Single(activated.Leases);
        var sandbox = Path.Combine(project.Root, ".ai", "runtime", "environments", lease.Requirement.ResourceId);
        await File.WriteAllTextAsync(Path.Combine(sandbox, "hiker-environment.json"), "{\"schema\":\"unknown\"}");

        var release = await new EnvironmentReleaseCoordinator(project.Coordinator, registry, resources, processes).ReleaseAsync(new EnvironmentReleaseRequest(
            "isolation-safety-release", project.ProjectId!, "task-1", "runtime-1", activated.Instance!.EnvironmentInstanceId, "source"));

        Assert.Equal("RECOVERY_REQUIRED", release.Status);
        Assert.True(Directory.Exists(sandbox));
        Assert.Single(await resources.ActiveAsync(project.ProjectId!));
    }

    private static EnvironmentProviderRegistry Registry(params IEnvironmentProvider[] providers)
    {
        var registry = new EnvironmentProviderRegistry();
        foreach (var provider in providers) registry.Register(provider);
        return registry;
    }

    private static async Task<EnvironmentProfile> SelectAsync(TestProject project, EnvironmentProviderRegistry registry, string operationId)
    {
        var seeded = await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            $"{operationId}:goal-task",
            new[]
            {
                Mutation(project.ProjectId!, EntityKinds.Goal, "goal-1", "{\"status\":\"ACTIVE\"}"),
                Mutation(project.ProjectId!, EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}")
            },
            "ENVIRONMENT_TEST_CONTEXT_CREATED",
            "GOAL_TASK_SEEDED",
            "{}"));
        Assert.True(seeded.Committed);
        var selected = await new EnvironmentProfileCoordinator(project.Coordinator, registry).ResolveAndSelectAsync(
            operationId, project.ProjectId!, 1, new EnvironmentDetectionRequest(project.Root, "field-development"));
        Assert.Equal(EnvironmentStatuses.Selected, selected.Profile?.Status);
        return selected.Profile!;
    }

    private static StateMutation Mutation(string projectId, string kind, string id, string payload) => new(
        new StateKey(projectId, kind, id), 0, 1, StateLifecycle.Valid, "TEST", "3.0.0", "source", "dependency", payload);

    private static EnvironmentInstanceRequest Request(
        TestProject project,
        string profileId,
        string operationId,
        params ResourceRequirement[] requirements) => new(
        operationId, project.Root, project.ProjectId!, "goal-1", 1, "task-1", "worktree-1", "runtime-1", profileId,
        requirements, new HashSet<string>(StringComparer.Ordinal), null, TimeSpan.FromMinutes(5), "source");

    private sealed class CompositionProvider : IEnvironmentProvider
    {
        private readonly string _root;
        private readonly LocalProjectEnvironmentProvider _local;
        private readonly WorkspaceIsolationEnvironmentProvider _isolation;

        public CompositionProvider(string root, LocalProjectEnvironmentProvider local, WorkspaceIsolationEnvironmentProvider isolation)
        {
            _root = root;
            _local = local;
            _isolation = isolation;
        }

        public EnvironmentProviderDescriptor Descriptor { get; } = new(
            "project-composition", "hiker-environment-provider/v1", new[] { "detect", "validate" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        public async Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(EnvironmentDetectionRequest request, CancellationToken cancellationToken = default)
        {
            var local = Assert.Single(await _local.DetectAsync(request, cancellationToken));
            var isolated = Assert.Single(await _isolation.DetectAsync(request, cancellationToken));
            var nodes = local.Graph.Nodes.Concat(isolated.Graph.Nodes).ToArray();
            var source = EnvironmentFingerprint.Calculate(local) + EnvironmentFingerprint.Calculate(isolated);
            return new[]
            {
                new EnvironmentCandidate(
                    "composed-project", Descriptor.ProviderId, request.Purpose,
                    new EnvironmentGraph(nodes, new[]
                    {
                        new EnvironmentEdge("local-project-root", "isolated-workspace", "PROVISIONS", true, "composition-edge")
                    }),
                    local.ToolchainFacts.Concat(isolated.ToolchainFacts).GroupBy(static pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.Ordinal),
                    local.HealthProbes.Concat(isolated.HealthProbes).Distinct(StringComparer.Ordinal).ToArray(),
                    local.StartOrAttachActions.Concat(isolated.StartOrAttachActions).Distinct(StringComparer.Ordinal).ToArray(),
                    new[] { "evidence:composition" },
                    HikerHash(source))
            };
        }

        public Task<EnvironmentValidationResult> ValidateAsync(EnvironmentValidationRequest request, CancellationToken cancellationToken = default)
        {
            var valid = Path.GetFullPath(request.ProjectRoot) == Path.GetFullPath(_root);
            return Task.FromResult(new EnvironmentValidationResult(
                valid ? EnvironmentStatuses.Verified : EnvironmentStatuses.ConfigurationIncompatible,
                valid,
                valid ? EnvironmentFingerprint.Calculate(request.Candidate) : string.Empty,
                valid ? request.Candidate.EvidenceRefs : Array.Empty<string>(),
                valid ? Array.Empty<string>() : new[] { "COMPOSITION_ROOT_MISMATCH" }));
        }

        private static string HikerHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

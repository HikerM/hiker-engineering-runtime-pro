using Hiker.Pro.Environment;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class EnvironmentProviderTests
{
    [Fact]
    public async Task EnvironmentDiscoveryDoesNotImplySelection()
    {
        await using var project = await TestProject.CreateAsync("project-env-discovery");
        var provider = new SyntheticFutureProvider("future-provider", Candidate("future-provider", "source-a"));
        var registry = Registry(provider);
        var coordinator = new EnvironmentProfileCoordinator(project.Coordinator, registry);

        var discovered = await coordinator.DiscoverAsync(new EnvironmentDetectionRequest(project.Root, "development"));

        Assert.Equal(EnvironmentStatuses.Discovered, discovered.Status);
        Assert.Single(discovered.Candidates);
        Assert.Empty(await project.Coordinator.ReadAsync("project-env-discovery", EntityKinds.EnvironmentProfile));
        Assert.False(discovered.FullSystemScan);
    }

    [Fact]
    public async Task VerifiedProfileIsReusedWithoutAnotherStateWrite()
    {
        await using var project = await TestProject.CreateAsync("project-env-reuse");
        var provider = new SyntheticFutureProvider("future-provider", Candidate("future-provider", "source-a"));
        var coordinator = new EnvironmentProfileCoordinator(project.Coordinator, Registry(provider));
        var request = new EnvironmentDetectionRequest(project.Root, "test");

        var selected = await coordinator.ResolveAndSelectAsync("environment-select", "project-env-reuse", 1, request);
        var generation = (await project.Store.GetMetricsAsync()).Generation;
        var reused = await coordinator.ResolveAndSelectAsync("environment-select-other", "project-env-reuse", 1, request);

        Assert.True(selected.Committed);
        Assert.Equal(EnvironmentStatuses.Selected, selected.Profile?.Status);
        Assert.True(reused.Reused);
        Assert.False(reused.Committed);
        Assert.Equal(generation, (await project.Store.GetMetricsAsync()).Generation);
        Assert.Equal(selected.Profile?.EnvironmentFingerprint, reused.Profile?.EnvironmentFingerprint);
    }

    [Fact]
    public async Task EnvironmentDriftRequiresRevalidation()
    {
        await using var project = await TestProject.CreateAsync("project-env-drift");
        var provider = new SyntheticFutureProvider("future-provider", Candidate("future-provider", "source-a"));
        var coordinator = new EnvironmentProfileCoordinator(project.Coordinator, Registry(provider));
        var request = new EnvironmentDetectionRequest(project.Root, "build");
        var selected = await coordinator.ResolveAndSelectAsync("environment-select", "project-env-drift", 3, request);
        provider.CurrentCandidate = Candidate("future-provider", "source-b") with
        {
            ToolchainFacts = new Dictionary<string, string> { ["runtime"] = "future-2" }
        };

        var drift = await coordinator.ResolveAndSelectAsync("environment-drift", "project-env-drift", 3, request);
        var stored = await project.Coordinator.ReadAsync(new StateKey("project-env-drift", EntityKinds.EnvironmentProfile, selected.Profile!.ProfileId));
        var revalidated = await coordinator.ResolveAndSelectAsync("environment-revalidate", "project-env-drift", 3, request);

        Assert.Equal(EnvironmentStatuses.Drift, drift.Status);
        Assert.True(drift.Committed);
        Assert.Equal(StateLifecycle.Stale, stored?.Lifecycle);
        Assert.Contains("REVALIDATION_REQUIRED", drift.Findings);
        Assert.Equal(EnvironmentStatuses.Selected, revalidated.Profile?.Status);
        Assert.Equal(2, revalidated.Profile?.Version);
    }

    [Fact]
    public async Task MultipleVerifiedEnvironmentCandidatesAreAmbiguous()
    {
        await using var project = await TestProject.CreateAsync("project-env-ambiguous");
        var coordinator = new EnvironmentProfileCoordinator(project.Coordinator, Registry(
            new SyntheticFutureProvider("future-a", Candidate("future-a", "source-a")),
            new SyntheticFutureProvider("future-b", Candidate("future-b", "source-b"))));

        var result = await coordinator.ResolveAndSelectAsync(
            "environment-ambiguous",
            "project-env-ambiguous",
            1,
            new EnvironmentDetectionRequest(project.Root, "runtime"));

        Assert.Equal(EnvironmentStatuses.Ambiguous, result.Status);
        Assert.False(result.Committed);
        Assert.Empty(await project.Coordinator.ReadAsync("project-env-ambiguous", EntityKinds.EnvironmentProfile));
    }

    [Fact]
    public async Task SecretValuesNeverEnterEnvironmentState()
    {
        await using var project = await TestProject.CreateAsync("project-env-secret");
        var unsafeCandidate = Candidate("future-provider", "source-secret") with
        {
            ToolchainFacts = new Dictionary<string, string> { ["api_token"] = "not-allowed" }
        };
        var coordinator = new EnvironmentProfileCoordinator(
            project.Coordinator,
            Registry(new SyntheticFutureProvider("future-provider", unsafeCandidate)));

        var discovery = await coordinator.DiscoverAsync(new EnvironmentDetectionRequest(project.Root, "development"));

        Assert.Empty(discovery.Candidates);
        Assert.Contains("PROVIDER_CANDIDATE_INVALID:future-provider", discovery.Findings);
        Assert.Empty(await project.Coordinator.ReadAsync("project-env-secret", EntityKinds.EnvironmentProfile));
    }

    private static EnvironmentProviderRegistry Registry(params IEnvironmentProvider[] providers)
    {
        var registry = new EnvironmentProviderRegistry();
        foreach (var provider in providers) registry.Register(provider);
        return registry;
    }

    private static EnvironmentCandidate Candidate(string providerId, string source) => new(
        $"candidate-{providerId}",
        providerId,
        "development",
        new EnvironmentGraph(
            new[]
            {
                new EnvironmentNode(
                    "node-main",
                    providerId,
                    "development",
                    new[] { "detect", "validate", "plan", "attach", "health", "recover", "release" },
                    new[] { "future-action" },
                    new[]
                    {
                        new EnvironmentResourceDescriptor(
                            "future-resource",
                            "resource-main",
                            providerId,
                            "project",
                            new Dictionary<string, string> { ["class"] = "synthetic" },
                            ResourceSharingMode.Shared,
                            4,
                            true,
                            false)
                    },
                    new Dictionary<string, string> { ["source"] = "fixture" },
                    new[] { new SecretReference("fixture-secret-provider", "secret-ref", "secret-fingerprint", true) })
            },
            Array.Empty<EnvironmentEdge>()),
        new Dictionary<string, string> { ["runtime"] = "future-1" },
        new[] { "health:future" },
        new[] { "attach" },
        new[] { "evidence:future" },
        source);

    private sealed class SyntheticFutureProvider : IEnvironmentProvider
    {
        public SyntheticFutureProvider(string providerId, EnvironmentCandidate candidate)
        {
            Descriptor = new EnvironmentProviderDescriptor(
                providerId,
                "future-contract/v77",
                new[] { "detect", "validate", "plan", "attach", "health", "recover", "release" },
                new[] { "future-action" },
                new[] { "future-resource" },
                Array.Empty<string>(),
                Array.Empty<string>());
            CurrentCandidate = candidate;
        }

        public EnvironmentProviderDescriptor Descriptor { get; }
        public EnvironmentCandidate CurrentCandidate { get; set; }

        public Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(
            EnvironmentDetectionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnvironmentCandidate>>(new[] { CurrentCandidate with { Purpose = request.Purpose } });

        public Task<EnvironmentValidationResult> ValidateAsync(
            EnvironmentValidationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnvironmentValidationResult(
                EnvironmentStatuses.Verified,
                true,
                EnvironmentFingerprint.Calculate(request.Candidate),
                request.Candidate.EvidenceRefs,
                Array.Empty<string>()));
    }
}

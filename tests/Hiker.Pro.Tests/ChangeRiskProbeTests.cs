using Hiker.Pro.ChangeIntelligence;
using Hiker.Pro.Context;
using Hiker.Pro.Environment;
using Hiker.Pro.Graph;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ChangeRiskProbeTests
{
    [Fact]
    public async Task ProbeRejectsUnboundedOrSensitiveSemanticMetadata()
    {
        await using var project = await TestProject.CreateAsync("change-probe-metadata");
        var probe = new ChangeRiskProbe(project.Coordinator);
        var request = Probe(project, "metadata-change", EntityKinds.Code, "seed");

        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync(request with
        {
            Signals = request.Signals with { Uncertainty = "MAYBE" }
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.ProbeAsync(request with
        {
            Signals = request.Signals with { DeclaredDimensions = new[] { "Bearer should-not-enter-state" } }
        }));
    }

    [Fact]
    public async Task ThreeLinePublicContractChangeIsCrossCuttingBecauseConsumersMatter()
    {
        await using var project = await TestProject.CreateAsync("change-contract");
        await SeedAsync(project,
            new[]
            {
                State(project, EntityKinds.Task, "task-1"),
                State(project, EntityKinds.Contract, "public-api"),
                State(project, EntityKinds.Service, "web"),
                State(project, EntityKinds.Service, "desktop"),
                State(project, EntityKinds.Service, "worker")
            },
            new[]
            {
                Edge(project, EntityKinds.Service, "web", EntityKinds.Contract, "public-api", "CONSUMES"),
                Edge(project, EntityKinds.Service, "desktop", EntityKinds.Contract, "public-api", "CONSUMES"),
                Edge(project, EntityKinds.Service, "worker", EntityKinds.Contract, "public-api", "CONSUMES")
            });

        var result = await new ChangeRiskProbe(project.Coordinator).ProbeAsync(Probe(project, "contract-change", EntityKinds.Contract, "public-api"));

        Assert.Equal(ChangeRiskClasses.CrossCutting, result.Classification);
        Assert.True(result.RequiresFullAnalysis);
        Assert.Equal(3, result.Centrality.ContractConsumerCount);
        Assert.Contains("SHARED_CONTRACT_FANOUT", result.Reasons);
    }

    [Fact]
    public async Task IndependentFiveHundredLineModuleUsesZeroWriteFastPath()
    {
        await using var project = await TestProject.CreateAsync("change-local");
        await SeedAsync(project, new[]
        {
            State(project, EntityKinds.Task, "task-1"),
            State(project, EntityKinds.Code, "independent-module", "{\"logical_lines\":500}")
        });
        var request = new ChangePreflightRequest("local-preflight", Probe(project, "local-change", EntityKinds.Code, "independent-module"));
        var before = (await project.Store.GetMetricsAsync()).Generation;

        var result = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(request);
        var after = (await project.Store.GetMetricsAsync()).Generation;

        Assert.Equal(ChangeControlStatuses.FastPath, result.Status);
        Assert.Equal(ChangeRiskClasses.Local, result.Probe.Classification);
        Assert.False(result.FullAnalysisExecuted);
        Assert.False(result.StateWritten);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task GitCoChangeIsHistoricalRiskSignalNotAuthoritativeDependency()
    {
        await using var project = await TestProject.CreateAsync("change-history");
        await SeedAsync(project,
            new[]
            {
                State(project, EntityKinds.Task, "task-1"),
                State(project, EntityKinds.Code, "seed"),
                State(project, EntityKinds.Code, "historical-neighbor")
            },
            new[] { Edge(project, EntityKinds.Code, "historical-neighbor", EntityKinds.Code, "seed", "CO_CHANGED_WITH") });
        var probe = Probe(project, "history-change", EntityKinds.Code, "seed");

        var result = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "history-preflight", probe, Contract(project, "history-contract", "history-change", "src/history")));

        Assert.True(result.FullAnalysisExecuted);
        var relation = Assert.Single(result.Blast!.Relations);
        Assert.Equal("git-history", relation.Source);
        Assert.False(relation.AuthoritativeDependency);
        Assert.Contains(BlastDimensions.Historical, result.Blast.Dimensions);
        Assert.Contains(result.Blast.Nodes, static node => node.Key.EntityId == "historical-neighbor" && node.Disposition == ImpactDisposition.RequiresReview);
    }

    [Fact]
    public async Task ApiSemanticChangeDerivesConsumerAndEvidenceRegression()
    {
        await using var project = await TestProject.CreateAsync("change-api");
        await SeedAsync(project,
            new[]
            {
                State(project, EntityKinds.Task, "task-1"),
                State(project, EntityKinds.Contract, "api-field"),
                State(project, EntityKinds.Service, "frontend"),
                State(project, EntityKinds.Service, "sdk"),
                State(project, EntityKinds.Evidence, "consumer-contract-test")
            },
            new[]
            {
                Edge(project, EntityKinds.Service, "frontend", EntityKinds.Contract, "api-field", "CONSUMES"),
                Edge(project, EntityKinds.Service, "sdk", EntityKinds.Contract, "api-field", "CONSUMES"),
                Edge(project, EntityKinds.Evidence, "consumer-contract-test", EntityKinds.Contract, "api-field", "VALIDATES")
            });

        var result = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "api-preflight",
            Probe(project, "api-change", EntityKinds.Contract, "api-field"),
            Contract(project, "api-contract", "api-change", "contract/api-field")));

        Assert.Equal(ChangeRiskClasses.CrossCutting, result.Blast?.Classification);
        Assert.Contains(BlastDimensions.Contract, result.Blast!.Dimensions);
        Assert.Contains(result.Blast.Regression.BlastDerivedRegression, static key => key.EntityId == "consumer-contract-test");
        Assert.True(result.Policy.RequiresSingleWriter);
        Assert.True(result.Policy.RequiresIndependentAssurance);
        Assert.True(result.StateWritten);
    }

    [Fact]
    public async Task IrreversibleMigrationWindowIsSystemicAndRequiresRollback()
    {
        await using var project = await TestProject.CreateAsync("change-migration");
        await SeedAsync(project, new[]
        {
            State(project, EntityKinds.Task, "task-1"),
            State(project, EntityKinds.Database, "orders-schema")
        });
        var probe = Probe(project, "migration-change", EntityKinds.Database, "orders-schema") with
        {
            Signals = new ChangeRiskSignals(DatabaseSchema: true, Irreversible: true, TemporalCompatibilityWindow: true)
        };

        var result = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "migration-preflight", probe, Contract(project, "migration-contract", "migration-change", "database/orders-schema")));

        Assert.Equal(ChangeRiskClasses.Systemic, result.Blast?.Classification);
        Assert.Contains(BlastDimensions.Temporal, result.Blast!.Dimensions);
        Assert.Contains(BlastDimensions.DataState, result.Blast.Dimensions);
        Assert.True(result.Policy.RequiresExclusiveMutation);
        Assert.True(result.Policy.RequiresRollbackBoundary);
    }

    [Fact]
    public async Task SharedDatabaseResourceCreatesResourceBlastEvenForDisjointCode()
    {
        await using var project = await TestProject.CreateAsync("change-resource");
        await SeedAsync(project, new[]
        {
            State(project, EntityKinds.Task, "task-1"),
            State(project, EntityKinds.Code, "backend-change")
        });
        var probe = Probe(project, "resource-change", EntityKinds.Code, "backend-change") with
        {
            ResourceRequirements = new[]
            {
                new ResourceRequirement("database-schema", "shared-schema", "future-provider", "project", ResourceSharingMode.Exclusive, 1, 1, false, true)
            }
        };

        var result = await new ChangeRiskProbe(project.Coordinator).ProbeAsync(probe);

        Assert.Equal(ChangeRiskClasses.CrossCutting, result.Classification);
        Assert.Contains("DESTRUCTIVE_EXCLUSIVE_RESOURCE", result.Reasons);
    }

    [Fact]
    public async Task ChangeContextUsesOnlyCurrentTaskContractAndImpactNotColdHistory()
    {
        await using var project = await TestProject.CreateAsync("change-context");
        await SeedAsync(project,
            new[]
            {
                State(project, EntityKinds.Goal, "goal-1"),
                State(project, EntityKinds.Task, "task-1"),
                State(project, EntityKinds.Contract, "context-contract"),
                State(project, EntityKinds.Service, "consumer-a"),
                State(project, EntityKinds.Service, "consumer-b")
            },
            new[]
            {
                Edge(project, EntityKinds.Service, "consumer-a", EntityKinds.Contract, "context-contract", "CONSUMES"),
                Edge(project, EntityKinds.Service, "consumer-b", EntityKinds.Contract, "context-contract", "CONSUMES")
            });
        _ = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "context-preflight",
            Probe(project, "context-change", EntityKinds.Contract, "context-contract"),
            Contract(project, "context-change-contract", "context-change", "contract/context-contract")));
        var resolver = new ContextResolver(project.Store);
        var before = await resolver.ResolveAsync(new ContextRequest(project.ProjectId!, "task-1", "goal-1"));
        for (var batch = 0; batch < 4; batch++)
        {
            var cold = Enumerable.Range(batch * 125, 125).Select(index => new StateMutation(
                new StateKey(project.ProjectId!, EntityKinds.ChangeImpact, $"cold-impact-{index:D4}"),
                0, 1, StateLifecycle.Archived, "TEST", "3.0.0", "cold-source", "cold-dependency", "{\"status\":\"HISTORICAL\"}", RetentionTier.Cold)).ToArray();
            Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"change-cold-{batch}", cold, "CHANGE_HISTORY_ARCHIVED", "COLD_CHANGE_HISTORY", "{}"))).Committed);
        }
        var after = await resolver.ResolveAsync(new ContextRequest(project.ProjectId!, "task-1", "goal-1"));

        Assert.Equal(before.StateReads, after.StateReads);
        Assert.Equal(before.EncodedBytes, after.EncodedBytes);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.True(after.EncodedBytes <= 32_768);
        Assert.DoesNotContain(after.Items, static item => item.EntityId.StartsWith("cold-impact-", StringComparison.Ordinal));
    }

    internal static ChangeProbeRequest Probe(TestProject project, string changeId, string kind, string id) => new(
        project.ProjectId!,
        "task-1",
        changeId,
        new[] { new ImpactSeed(new StateKey(project.ProjectId!, kind, id), "MODIFIED", "source") },
        new ChangeRiskSignals(),
        Array.Empty<ResourceRequirement>(),
        "source");

    internal static ChangeContractDefinition Contract(TestProject project, string id, string changeId, params string[] scopes) => new(
        id,
        changeId,
        "task-1",
        1,
        "CHANGE_PROBLEM",
        "invariant-session-remains-consistent",
        scopes,
        new[] { "behavior-change" },
        new[] { "existing-behavior" },
        new[] { "no-second-authority" },
        new[] { "acceptance-primary" },
        "rollback-to-stable-checkpoint",
        "source");

    internal static async Task SeedAsync(TestProject project, IReadOnlyList<StateMutation> states, IReadOnlyList<DependencyEdge>? edges = null)
    {
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            $"change-seed-{Guid.NewGuid():N}", states, "CHANGE_FIXTURE_CREATED", "CHANGE_FIXTURE_CREATED", "{}", edges))).Committed);
    }

    internal static StateMutation State(TestProject project, string kind, string id, string payload = "{\"status\":\"ACTIVE\"}") => new(
        new StateKey(project.ProjectId!, kind, id), 0, 1, StateLifecycle.Valid, "TEST", "3.0.0", $"source-{id}", $"dependency-{id}", payload);

    internal static DependencyEdge Edge(TestProject project, string fromKind, string fromId, string toKind, string toId, string relation) => new(
        project.ProjectId!, fromKind, fromId, toKind, toId, relation, $"edge-{fromId}-{toId}-{relation}");
}

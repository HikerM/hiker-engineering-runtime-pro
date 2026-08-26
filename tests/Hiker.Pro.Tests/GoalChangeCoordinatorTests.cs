using Hiker.Pro.Graph;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class GoalChangeCoordinatorTests
{
    public static TheoryData<GoalChangeKind> SupportedChangeKinds => new()
    {
        GoalChangeKind.Add,
        GoalChangeKind.Remove,
        GoalChangeKind.Modify,
        GoalChangeKind.Architecture,
        GoalChangeKind.StackMigration,
        GoalChangeKind.Undo
    };

    [Theory]
    [MemberData(nameof(SupportedChangeKinds))]
    public async Task EveryChangeKindCreatesOneAtomicRevision(GoalChangeKind kind)
    {
        await using var project = await TestProject.CreateAsync();
        var from = kind == GoalChangeKind.Add ? 0 : 1;
        var seed = new List<StateMutation> { State(EntityKinds.Task, "task-1", 0, "{\"status\":\"COMPLETED\"}") };
        if (from > 0)
        {
            seed.Add(State(EntityKinds.Goal, "goal-1", from, "{\"status\":\"CURRENT\"}"));
        }
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            $"seed-{kind}", seed, "FIXTURE_CREATED", "FIXTURE_CREATED", "{}"))).Committed);

        var result = await new GoalChangeCoordinator(project.Coordinator).ApplyAsync(new GoalChangeRequest(
            $"change-{kind}",
            "project-1",
            "goal-1",
            from,
            from + 1,
            kind,
            $"CHANGE_{kind.ToString().ToUpperInvariant()}",
            $"source-{kind}",
            new[] { Classification("task-1", ImpactDisposition.Unaffected) }));

        Assert.Equal("COMMITTED", result.Status);
        var goal = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Goal, "goal-1"));
        var revision = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.GoalRevision, $"goal-1:r{from + 1:D8}"));
        var binding = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.TaskDependency, "goal-1:task-1"));
        Assert.Equal(from + 1, goal?.GoalRevision);
        Assert.Equal(from + 1, revision?.GoalRevision);
        Assert.Equal(from + 1, binding?.GoalRevision);
    }

    [Fact]
    public async Task PartialImpactPreservesUnaffectedFactsAndInvalidatesOnlyBoundEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedGoalTasksAndEvidenceAsync(project);
        var coordinator = new GoalChangeCoordinator(project.Coordinator);
        var request = new GoalChangeRequest(
            "goal-change-partial",
            "project-1",
            "goal-1",
            1,
            2,
            GoalChangeKind.Modify,
            "FRONTEND_FLOW_CHANGED",
            "goal-source-2",
            new[]
            {
                Classification("frontend", ImpactDisposition.Affected, "UI_FLOW_CHANGED", "frontend-evidence"),
                Classification("database", ImpactDisposition.Unaffected, "DATABASE_OUT_OF_SCOPE"),
                Classification("legacy", ImpactDisposition.Superseded, "REQUIREMENT_REMOVED"),
                Classification("ambiguous", ImpactDisposition.RequiresReview, "MODEL_UNCERTAIN")
            });

        var result = await coordinator.ApplyAsync(request);
        var replay = await coordinator.ApplyAsync(request);

        Assert.Equal("COMMITTED", result.Status);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(result.Generation, replay.Generation);
        Assert.Equal(StateLifecycle.Stale, (await Read(project, EntityKinds.Task, "frontend")).Lifecycle);
        Assert.Equal(StateLifecycle.Stale, (await Read(project, EntityKinds.Evidence, "frontend-evidence")).Lifecycle);
        Assert.Equal(StateLifecycle.Valid, (await Read(project, EntityKinds.Task, "database")).Lifecycle);
        Assert.Equal(StateLifecycle.Valid, (await Read(project, EntityKinds.Evidence, "database-evidence")).Lifecycle);
        Assert.Equal(1, (await Read(project, EntityKinds.Evidence, "database-evidence")).GoalRevision);
        Assert.Equal(StateLifecycle.Superseded, (await Read(project, EntityKinds.Task, "legacy")).Lifecycle);
        Assert.Equal(1, (await Read(project, EntityKinds.Task, "ambiguous")).GoalRevision);
        Assert.Equal(2, (await Read(project, EntityKinds.TaskDependency, "goal-1:ambiguous")).GoalRevision);

        var conflictingRetry = request with { ChangeCode = "DIFFERENT_CHANGE" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(conflictingRetry));
    }

    [Fact]
    public async Task ApiContractChangeInvalidatesConsumerEvidenceButNotDatabaseEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedGoalTasksAndEvidenceAsync(project);
        var coordinator = new GoalChangeCoordinator(project.Coordinator);
        var result = await coordinator.ApplyAsync(new GoalChangeRequest(
            "goal-change-contract",
            "project-1",
            "goal-1",
            1,
            2,
            GoalChangeKind.Architecture,
            "API_CONTRACT_V2",
            "contract-v2",
            new[]
            {
                Classification("frontend", ImpactDisposition.Affected, "CONSUMES_API", "frontend-evidence"),
                Classification("api-client", ImpactDisposition.Affected, "CONSUMES_API", "client-evidence"),
                Classification("database", ImpactDisposition.Unaffected, "STORAGE_CONTRACT_STABLE")
            }));

        Assert.Equal(2, result.EvidenceInvalidated);
        Assert.Equal(StateLifecycle.Stale, (await Read(project, EntityKinds.Evidence, "frontend-evidence")).Lifecycle);
        Assert.Equal(StateLifecycle.Stale, (await Read(project, EntityKinds.Evidence, "client-evidence")).Lifecycle);
        Assert.Equal(StateLifecycle.Valid, (await Read(project, EntityKinds.Evidence, "database-evidence")).Lifecycle);
    }

    [Fact]
    public async Task UnboundEvidenceAndNonAffectedInvalidationAreRejectedBeforeCommit()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedGoalTasksAndEvidenceAsync(project);
        var coordinator = new GoalChangeCoordinator(project.Coordinator);
        var unbound = Request(Classification("frontend", ImpactDisposition.Affected, "CHANGE", "database-evidence"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(unbound));
        Assert.Null(await project.Store.ReadOperationReceiptAsync(unbound.OperationId));

        var unaffected = Request(Classification("database", ImpactDisposition.Unaffected, "NO_CHANGE", "database-evidence")) with
        {
            OperationId = "invalid-unaffected"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(unaffected));
        Assert.Null(await project.Store.ReadOperationReceiptAsync(unaffected.OperationId));
    }

    private static GoalChangeRequest Request(TaskImpactClassification classification) => new(
        "invalid-bound-evidence",
        "project-1",
        "goal-1",
        1,
        2,
        GoalChangeKind.Modify,
        "TEST_CHANGE",
        "source-change",
        new[] { classification });

    private static TaskImpactClassification Classification(
        string taskId,
        ImpactDisposition disposition,
        string reason = "VALIDATED_CLASSIFICATION",
        params string[] evidenceIds) => new(taskId, disposition, reason, evidenceIds);

    private static async Task SeedGoalTasksAndEvidenceAsync(TestProject project)
    {
        var mutations = new[]
        {
            State(EntityKinds.Goal, "goal-1", 1, "{\"status\":\"CURRENT\"}"),
            State(EntityKinds.Task, "frontend", 1, "{\"status\":\"COMPLETED\"}"),
            State(EntityKinds.Task, "api-client", 1, "{\"status\":\"COMPLETED\"}"),
            State(EntityKinds.Task, "database", 1, "{\"status\":\"COMPLETED\"}"),
            State(EntityKinds.Task, "legacy", 1, "{\"status\":\"COMPLETED\"}"),
            State(EntityKinds.Task, "ambiguous", 1, "{\"status\":\"RUNNING\"}"),
            State(EntityKinds.Evidence, "frontend-evidence", 1, "{\"status\":\"PASS\"}"),
            State(EntityKinds.Evidence, "client-evidence", 1, "{\"status\":\"PASS\"}"),
            State(EntityKinds.Evidence, "database-evidence", 1, "{\"status\":\"PASS\"}")
        };
        var edges = new[]
        {
            Edge("frontend", "frontend-evidence"),
            Edge("api-client", "client-evidence"),
            Edge("database", "database-evidence")
        };
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "goal-change-seed", mutations, "FIXTURE_CREATED", "FIXTURE_CREATED", "{}", edges))).Committed);
    }

    private static DependencyEdge Edge(string task, string evidence) => new(
        "project-1", EntityKinds.Task, task, EntityKinds.Evidence, evidence, "PROVEN_BY", $"{task}-{evidence}");

    private static async Task<StateSnapshot> Read(TestProject project, string kind, string id) =>
        await project.Store.ReadAsync(new StateKey("project-1", kind, id))
        ?? throw new InvalidOperationException($"Missing test state: {kind}/{id}");

    private static StateMutation State(string kind, string id, long revision, string payload) => new(
        new StateKey("project-1", kind, id),
        0,
        revision,
        StateLifecycle.Valid,
        "TEST",
        "3.0.0",
        $"source-{id}",
        $"dependency-{id}",
        payload);
}

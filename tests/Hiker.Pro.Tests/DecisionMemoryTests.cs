using Hiker.Pro.Context;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class DecisionMemoryTests
{
    [Fact]
    public async Task RejectedDecisionRetainsReasonAndCurrentDecisionSupersedesAtomically()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedAsync(project);
        var memory = new DecisionMemoryCoordinator(project.Coordinator);
        var rejected = Request("record-rejected", Decision("decision-rejected", DecisionStatus.ValidButUserRejected, "USER_REJECTED_COST"));
        var approved = Request("record-approved", Decision("decision-a", DecisionStatus.ApprovedCurrent, "COMPATIBLE_WITH_DATABASE"));
        Assert.Equal("COMMITTED", (await memory.RecordAsync(rejected)).Status);
        Assert.Equal("COMMITTED", (await memory.RecordAsync(approved)).Status);

        var replacement = Request("supersede-approved", Decision("decision-b", DecisionStatus.ApprovedCurrent, "NEW_CONTRACT_ACCEPTED"));
        var result = await memory.SupersedeAsync(replacement, "decision-a");
        var replay = await memory.SupersedeAsync(replacement, "decision-a");
        Assert.Equal("COMMITTED", result.Status);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(StateLifecycle.Superseded, (await Read(project, "decision-a")).Lifecycle);
        Assert.Contains("DECISION-B", (await Read(project, "decision-a")).PayloadJson.ToUpperInvariant());
        Assert.Equal(StateLifecycle.Valid, (await Read(project, "decision-b")).Lifecycle);

        var context = await new ContextResolver(project.Store).ResolveAsync(new ContextRequest("project-1", "task-1", "goal-1"));
        Assert.Contains(context.Items, static item => item.EntityId == "decision-rejected" && item.PayloadJson.Contains("VALID_BUT_USER_REJECTED", StringComparison.Ordinal));
        Assert.Contains(context.Items, static item => item.EntityId == "decision-a" && item.Lifecycle == StateLifecycle.Superseded);
        Assert.Contains(context.Items, static item => item.EntityId == "decision-b" && item.PayloadJson.Contains("APPROVED_CURRENT", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.SupersedeAsync(
            replacement with { Decision = replacement.Decision with { ReasonCode = "DIFFERENT_REASON" } },
            "decision-a"));
    }

    [Fact]
    public async Task DecisionWithoutExistingTaskOrEvidenceIsRejectedBeforeJournalCommit()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedAsync(project);
        var memory = new DecisionMemoryCoordinator(project.Coordinator);
        var invalidTask = Request("invalid-task", Decision("decision-x", DecisionStatus.Rejected, "INVALID")) with
        {
            RelatedTaskIds = new[] { "missing-task" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.RecordAsync(invalidTask));
        Assert.Null(await project.Store.ReadOperationReceiptAsync("invalid-task"));

        var invalidEvidence = Request("invalid-evidence", Decision("decision-y", DecisionStatus.Rejected, "INVALID") with
        {
            EvidenceIds = new[] { "missing-evidence" }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.RecordAsync(invalidEvidence));
        Assert.Null(await project.Store.ReadOperationReceiptAsync("invalid-evidence"));
    }

    private static DecisionRecordRequest Request(string operationId, EngineeringDecision decision) => new(
        operationId,
        "project-1",
        1,
        $"source-{decision.DecisionId}",
        decision,
        new[] { "task-1" });

    private static EngineeringDecision Decision(string id, DecisionStatus status, string reason) => new(
        id,
        status,
        reason,
        "requirement:R-1",
        new[] { "service:orders" },
        new[] { "evidence-1" },
        status is DecisionStatus.Rejected or DecisionStatus.ValidButUserRejected ? new[] { "alternative:a" } : Array.Empty<string>(),
        null,
        DateTimeOffset.MinValue);

    private static async Task SeedAsync(TestProject project)
    {
        var mutations = new[]
        {
            State(EntityKinds.Goal, "goal-1", "{\"status\":\"CURRENT\"}"),
            State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
            State(EntityKinds.Evidence, "evidence-1", "{\"status\":\"PASS\"}")
        };
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "decision-seed", mutations, "FIXTURE_CREATED", "FIXTURE_CREATED", "{}"))).Committed);
    }

    private static async Task<StateSnapshot> Read(TestProject project, string id) =>
        await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Decision, id))
        ?? throw new InvalidOperationException($"Missing decision: {id}");

    private static StateMutation State(string kind, string id, string payload) => new(
        new StateKey("project-1", kind, id), 0, 1, StateLifecycle.Valid, "TEST", "3.0.0", $"source-{id}", $"deps-{id}", payload);
}

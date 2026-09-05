using Hiker.Pro.ChangeIntelligence;
using Hiker.Pro.Execution;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ChangeSchedulerTests
{
    [Fact]
    public async Task SystemicChangeUsesSingleWriterIndependentAssuranceAndExclusiveMutation()
    {
        await using var project = await TestProject.CreateAsync("change-scheduler-systemic");
        var systemic = new ChangeExecutionPolicy(ChangeRiskClasses.Systemic, "contract-1", true, true, true, true, true, false);
        var candidates = new[]
        {
            Candidate("systemic-task", "writer-systemic", "src/shared", systemic),
            Candidate("local-task", "writer-local", "src/local"),
            new SchedulingCandidate("assurance-task", "assurance-lane", AgentLaneRole.Assurance, EngineeringRisk.Medium, new[] { "review/systemic" }, "systemic-task")
        };

        var result = await new ConflictAwareScheduler(project.Coordinator).EvaluateAsync(project.ProjectId!, candidates, 3);

        Assert.Equal(1, result.WriterCapacity);
        Assert.Contains(result.Dispositions, static item => item.TaskId == "systemic-task" && item.Status == "ADMITTED" && item.ReasonCode == "SYSTEMIC_CHANGE_EXCLUSIVE_MUTATION");
        Assert.Contains(result.Dispositions, static item => item.TaskId == "local-task" && item.Status == "DEFERRED");
        Assert.Contains(result.Dispositions, static item => item.TaskId == "assurance-task" && item.Status == "ADMITTED");
    }

    [Fact]
    public async Task ConfirmedFixLoopCannotEnterWriterLane()
    {
        await using var project = await TestProject.CreateAsync("change-scheduler-loop");
        var disabled = new ChangeExecutionPolicy(ChangeRiskClasses.Systemic, "contract-loop", true, true, true, true, false, true);

        var result = await new ConflictAwareScheduler(project.Coordinator).EvaluateAsync(
            project.ProjectId!,
            new[] { Candidate("loop-task", "loop-writer", "src/shared", disabled) });

        var disposition = Assert.Single(result.Dispositions);
        Assert.Equal(ChangeControlStatuses.RootCauseRequired, disposition.Status);
        Assert.Equal("FIX_LOOP_PATCHING_DISABLED", disposition.ReasonCode);
    }

    [Fact]
    public async Task ConflictingGoalRevisionsAreDeferredInsteadOfRunningTwoWriters()
    {
        await using var project = await TestProject.CreateAsync("change-scheduler-goal-revision");
        var candidates = new[]
        {
            Candidate("goal-old", "writer-old", "src/shared", goalId: "GOAL-1", goalRevision: 4, goalFingerprint: "goal-4"),
            Candidate("goal-new", "writer-new", "src/other", goalId: "GOAL-1", goalRevision: 5, goalFingerprint: "goal-5")
        };

        var result = await new ConflictAwareScheduler(project.Coordinator).EvaluateAsync(project.ProjectId!, candidates, 3);

        Assert.All(result.Dispositions, item => Assert.Equal("DEFERRED", item.Status));
        Assert.All(result.Dispositions, item => Assert.Equal("GOAL_REVISION_CONFLICT", item.ReasonCode));
    }

    [Fact]
    public async Task DifferentSourceSnapshotsCannotMutateAnOverlappingScope()
    {
        await using var project = await TestProject.CreateAsync("change-scheduler-source-snapshot");
        var candidates = new[]
        {
            Candidate("source-a", "writer-a", "src/shared", sourceFingerprint: "source-a"),
            Candidate("source-b", "writer-b", "src/shared/api", sourceFingerprint: "source-b")
        };

        var result = await new ConflictAwareScheduler(project.Coordinator).EvaluateAsync(project.ProjectId!, candidates, 3);

        Assert.All(result.Dispositions, item => Assert.Equal("DEFERRED", item.Status));
        Assert.All(result.Dispositions, item => Assert.Equal("SOURCE_SNAPSHOT_CONFLICT", item.ReasonCode));
    }

    private static SchedulingCandidate Candidate(
        string taskId,
        string laneId,
        string scope,
        ChangeExecutionPolicy? policy = null,
        string? goalId = null,
        long? goalRevision = null,
        string? goalFingerprint = null,
        string? sourceFingerprint = null) => new(
        taskId,
        laneId,
        AgentLaneRole.Writer,
        EngineeringRisk.Medium,
        new[] { scope },
        ChangePolicy: policy,
        GoalId: goalId,
        GoalRevision: goalRevision,
        GoalFingerprint: goalFingerprint,
        SourceFingerprint: sourceFingerprint);
}

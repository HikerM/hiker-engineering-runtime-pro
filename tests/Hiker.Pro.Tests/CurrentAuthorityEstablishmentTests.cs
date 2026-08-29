using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class CurrentAuthorityEstablishmentTests
{
    private const string ProjectId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";

    [Fact]
    public async Task ExistingProjectWithoutGoalTaskCanEstablishCurrentAuthority()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var result = await fixture.EstablishAsync();
        Assert.Equal(CurrentAuthorityStatuses.Established, result.Status);
        Assert.NotNull(result.GoalId);
        Assert.NotNull(result.TaskId);
    }

    [Fact]
    public async Task EstablishCreatesExactlyOneGoalAndOneTask()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var result = await fixture.EstablishAsync();
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
        Assert.Equal(2, result.StateWrites);
    }

    [Fact]
    public async Task GoalAndTaskCreationIsAtomic()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync(CurrentAuthorityFailurePoint.BeforeCasCommit);
        await Assert.ThrowsAsync<InjectedAuthorityFailure>(() => fixture.EstablishAsync());
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
    }

    [Fact]
    public async Task ReplayReturnsAlreadyEstablishedWithZeroWrites()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var first = await fixture.EstablishAsync();
        var replay = await fixture.EstablishAsync();
        Assert.Equal(CurrentAuthorityStatuses.AlreadyEstablished, replay.Status);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(0, replay.StateWrites);
        Assert.Equal(first.GoalId, replay.GoalId);
        Assert.Equal(first.TaskId, replay.TaskId);
    }

    [Fact]
    public async Task EquivalentExistingAuthorityIsReused()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var first = await fixture.EstablishAsync();
        var second = await fixture.EstablishAsync(session: "second-provider-session");
        Assert.Equal(CurrentAuthorityStatuses.AdoptExistingAuthority, second.Status);
        Assert.Equal(0, second.StateWrites);
        Assert.Equal(first.GoalId, second.GoalId);
        Assert.Equal(first.TaskId, second.TaskId);
    }

    [Fact]
    public async Task MultipleGoalCandidatesFailClosed()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        await fixture.SeedAsync(EntityKinds.Goal, "competing-goal", "{\"goal_id\":\"competing-goal\",\"status\":\"ACTIVE\"}");
        var blocked = await fixture.EstablishAsync(session: "second-provider-session");
        Assert.Equal(CurrentAuthorityStatuses.AuthorityAmbiguous, blocked.Status);
        Assert.Equal(0, blocked.StateWrites);
    }

    [Fact]
    public async Task MultipleActiveTaskCandidatesFailClosed()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var established = await fixture.EstablishAsync();
        await fixture.SeedAsync(EntityKinds.Task, "competing-task", $$"""{"task_id":"competing-task","goal_id":"{{established.GoalId}}","status":"IN_PROGRESS"}""");
        var blocked = await fixture.EstablishAsync(session: "second-provider-session");
        Assert.Equal(CurrentAuthorityStatuses.AuthorityAmbiguous, blocked.Status);
        Assert.Equal(0, blocked.StateWrites);
    }

    [Fact]
    public async Task ProviderSessionMismatchBlocks()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        var session = "incompatible-provider-session";
        var fingerprint = LiveAdoptionStateCodec.Hash($"codex\0{session}");
        var bindingId = $"codex-{fingerprint[..24]}";
        await fixture.SeedAsync(EntityKinds.ProviderSessionBinding, bindingId, $$"""
            {"binding_id":"{{bindingId}}","provider":"codex","provider_session_fingerprint":"{{fingerprint}}","project_id":"{{ProjectId}}","goal_id":"wrong-goal","task_id":"wrong-task","checkpoint_id":"wrong-checkpoint","execution_id":"wrong-execution","status":"ACTIVE","attached_at":"2026-01-01T00:00:00Z"}
            """);
        var blocked = await fixture.EstablishAsync(session: session);
        Assert.Equal(CurrentAuthorityStatuses.ProviderSessionMismatch, blocked.Status);
        Assert.Equal(0, blocked.StateWrites);
    }

    [Fact]
    public async Task CompletedGoalWithSameProvenanceCannotBeReactivatedByClaimingActive()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var request = BrownfieldFixture.BuildRequest(BrownfieldFixture.DefaultSession, "NEW_TASK");
        var goalId = $"goal-{LiveAdoptionStateCodec.Hash($"{ProjectId}\0{request.Goal.AuthoritySource}\0{request.Goal.SourceFingerprint}\0{request.Goal.AuthorityGeneration}")[..24]}";
        await fixture.SeedAsync(EntityKinds.Goal, goalId, $$"""
            {"schema_version":"hiker-current-authority/v1","entity_type":"GOAL","goal_id":"{{goalId}}","task_id":null,"statement":"{{request.Goal.Statement}}","status":"COMPLETED","authority_source":"{{request.Goal.AuthoritySource}}","source_fingerprint":"{{request.Goal.SourceFingerprint}}","authority_generation":{{request.Goal.AuthorityGeneration}}}
            """);
        var blocked = await fixture.EstablishAsync();
        Assert.Equal(CurrentAuthorityStatuses.AuthorityConflict, blocked.Status);
        Assert.Equal(0, blocked.StateWrites);
    }

    [Fact]
    public async Task UntrustedAuthoritySourceIsRejectedByRuntime()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var request = BrownfieldFixture.BuildRequest(BrownfieldFixture.DefaultSession, "NEW_TASK") with
        {
            Task = BrownfieldFixture.BuildRequest(BrownfieldFixture.DefaultSession, "NEW_TASK").Task with
            {
                AuthoritySource = "MODEL_FREE_SUMMARY"
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.EstablishAsync(request));
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
    }

    [Fact]
    public async Task UnsafeBoundaryWritesZero()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var before = await fixture.Project.Store.GetMetricsAsync();
        var blocked = await fixture.EstablishAsync(boundary: "MID_TURN");
        var after = await fixture.Project.Store.GetMetricsAsync();
        Assert.Equal(CurrentAuthorityStatuses.WaitSafeBoundary, blocked.Status);
        Assert.Equal(0, blocked.StateWrites);
        Assert.Equal(before.Generation, after.Generation);
    }

    [Fact]
    public async Task UnrelatedHistoricalCheckpointIsNotAdopted()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        var adoption = await fixture.AttachAsync();
        Assert.Equal(LiveAdoptionStatuses.Adopted, adoption.Status);
        Assert.NotEqual(BrownfieldFixture.UnrelatedCheckpointId, adoption.CheckpointId);
    }

    [Fact]
    public async Task BusinessGoalRemainsActive()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var result = await fixture.EstablishAsync();
        var goal = await fixture.Project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Goal, result.GoalId!));
        Assert.Equal("ACTIVE", JsonDocument.Parse(goal!.PayloadJson).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BusinessTaskSemanticsPreserved()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var result = await fixture.EstablishAsync();
        var task = await fixture.Project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Task, result.TaskId!));
        var payload = JsonDocument.Parse(task!.PayloadJson).RootElement;
        Assert.Equal("IN_PROGRESS", payload.GetProperty("status").GetString());
        Assert.Equal(BrownfieldFixture.TaskStatement, payload.GetProperty("statement").GetString());
    }

    [Fact]
    public async Task ProjectIdUnchanged()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var result = await fixture.EstablishAsync();
        Assert.Equal(ProjectId, result.ProjectId);
        Assert.Single(result.Project.Identity.ActiveProjectIds);
    }

    [Fact]
    public async Task WorktreeUnchanged()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var before = await fixture.Project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Worktree, BrownfieldFixture.WorktreeId));
        _ = await fixture.EstablishAsync();
        var after = await fixture.Project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Worktree, BrownfieldFixture.WorktreeId));
        Assert.Equal(before, after);
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Worktree, lifecycle: null));
    }

    [Fact]
    public async Task NoOwnershipCreatedDuringAuthorityEstablishment()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Ownership, lifecycle: null));
    }

    [Fact]
    public async Task NoLeaseCreatedDuringAuthorityEstablishment()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.RuntimeLease, lifecycle: null));
    }

    [Fact]
    public async Task NoDuplicateProjectGoalTaskBindingWorktree()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        _ = await fixture.EstablishAsync();
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Project, lifecycle: null));
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.ProviderSessionBinding, lifecycle: null));
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Worktree, lifecycle: null));
    }

    [Fact]
    public async Task AttachAfterEstablishmentLiveAdopts()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var authority = await fixture.EstablishAsync();
        var adoption = await fixture.AttachAsync();
        Assert.Equal(LiveAdoptionStatuses.Adopted, adoption.Status);
        Assert.Equal(authority.GoalId, adoption.GoalId);
        Assert.Equal(authority.TaskId, adoption.TaskId);
    }

    [Fact]
    public async Task RepeatAttachIsIdempotent()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        _ = await fixture.EstablishAsync();
        _ = await fixture.AttachAsync();
        var replay = await fixture.AttachAsync();
        Assert.Equal(LiveAdoptionStatuses.AlreadyAdopted, replay.Status);
        Assert.Equal(0, replay.StateWrites);
    }

    [Fact]
    public async Task NextRealTurnPreservesSameProjectGoalTask()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync();
        var authority = await fixture.EstablishAsync();
        _ = await fixture.AttachAsync();
        var checkpoint = await new LiveTurnLifecycleService(fixture.Project.Store, fixture.Project.Coordinator, fixture.Project.Bootstrap)
            .CheckpointAsync(new LiveTurnCheckpointRequest("codex", BrownfieldFixture.DefaultSession));
        Assert.Equal(LiveTurnCheckpointStatuses.Checkpointed, checkpoint.Status);
        var next = await fixture.AttachAsync("next-real-turn", "RECOVERY_CHECKPOINT");
        Assert.Equal(ProjectId, next.ProjectId);
        Assert.Equal(authority.GoalId, next.GoalId);
        Assert.Equal(authority.TaskId, next.TaskId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailuresBeforeDurableCommitNeverLeaveHalfAuthority(int pointValue)
    {
        var point = (CurrentAuthorityFailurePoint)pointValue;
        await using var fixture = await BrownfieldFixture.CreateAsync(point);
        await Assert.ThrowsAsync<InjectedAuthorityFailure>(() => fixture.EstablishAsync());
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Empty(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
    }

    [Fact]
    public async Task LostResponseAfterDurableCommitRetriesAsAlreadyEstablished()
    {
        await using var fixture = await BrownfieldFixture.CreateAsync(CurrentAuthorityFailurePoint.AfterDurableCommitBeforeResponse);
        await Assert.ThrowsAsync<InjectedAuthorityFailure>(() => fixture.EstablishAsync());
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Goal, lifecycle: null));
        Assert.Single(await fixture.Project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null));
        fixture.DisableFailure();
        var replay = await fixture.EstablishAsync();
        Assert.Equal(CurrentAuthorityStatuses.AlreadyEstablished, replay.Status);
        Assert.Equal(0, replay.StateWrites);
    }

    private sealed class BrownfieldFixture : IAsyncDisposable
    {
        public const string DefaultSession = "verified-provider-session";
        public const string WorktreeId = "existing-worktree";
        public const string UnrelatedCheckpointId = "storyboard-quality-98-v2";
        public const string TaskStatement = "Freeze the current API contract";
        private readonly FailureInjector injector;

        private BrownfieldFixture(TestProject project, FailureInjector injector)
        {
            Project = project;
            this.injector = injector;
        }

        public TestProject Project { get; }

        public static async Task<BrownfieldFixture> CreateAsync(CurrentAuthorityFailurePoint? failurePoint = null)
        {
            var project = await TestProject.CreateAsync(ProjectId);
            var injector = new FailureInjector(failurePoint);
            var fixture = new BrownfieldFixture(project, injector);
            await fixture.SeedAsync(EntityKinds.Worktree, WorktreeId,
                $$"""{"worktree_id":"{{WorktreeId}}","project_id":"{{ProjectId}}","status":"ACTIVE"}""");
            await fixture.SeedAsync(EntityKinds.Checkpoint, UnrelatedCheckpointId,
                $$"""{"checkpoint_id":"{{UnrelatedCheckpointId}}","goal_id":"old-storyboard-goal","task_id":"old-storyboard-task","status":"ARCHIVED","legacy_finalized":true}""",
                StateLifecycle.Archived,
                "COMMUNITY_5_18_IMPORT",
                "2.0.0");
            return fixture;
        }

        public Task<CurrentAuthorityEstablishmentResult> EstablishAsync(
            string session = DefaultSession,
            string boundary = "NEW_TASK") => Service().EstablishAsync(Request(session, boundary));

        public Task<CurrentAuthorityEstablishmentResult> EstablishAsync(CurrentAuthorityEstablishmentRequest request) =>
            Service().EstablishAsync(request);

        public Task<LiveAdoptionResult> AttachAsync(
            string session = DefaultSession,
            string boundary = "NEW_TASK") => new LiveSessionAdoptionService(
                Project.Layout,
                Project.Store,
                Project.Coordinator,
                Project.Bootstrap,
                new ProcessIdentityProbe()).AdoptAsync(new LiveAdoptionRequest("codex", session, boundary));

        public async Task SeedAsync(
            string kind,
            string id,
            string payload,
            StateLifecycle lifecycle = StateLifecycle.Valid,
            string authority = "BROWNFIELD_FIXTURE",
            string schema = SqliteSchema.CurrentVersion)
        {
            var key = new StateKey(ProjectId, kind, id);
            var fingerprint = LiveAdoptionStateCodec.Hash(payload);
            var result = await Project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
                $"fixture-{kind.ToLowerInvariant()}-{id}",
                new[]
                {
                    new StateMutation(key, 0, 0, lifecycle, authority, schema, fingerprint, fingerprint, payload)
                },
                "FIXTURE_STATE_SEEDED",
                "BROWNFIELD_FIXTURE",
                "{}"));
            Assert.True(result.Committed);
        }

        public void DisableFailure() => injector.Point = null;

        public ValueTask DisposeAsync() => Project.DisposeAsync();

        private CurrentAuthorityEstablishmentService Service() => new(
            Project.Store,
            Project.Coordinator,
            Project.Bootstrap,
            injector);

        public static CurrentAuthorityEstablishmentRequest BuildRequest(string session, string boundary) => Request(session, boundary);

        private static CurrentAuthorityEstablishmentRequest Request(string session, string boundary)
        {
            const string goalStatement = "Deliver the existing brownfield business goal";
            var goalFingerprint = LiveAdoptionStateCodec.Hash($"CONTROLLER_CURRENT_GOAL\0{goalStatement}\0ACTIVE\01");
            var taskFingerprint = LiveAdoptionStateCodec.Hash($"CONTROLLER_CURRENT_ACTIVE_TASK\0{TaskStatement}\0IN_PROGRESS\01");
            return new CurrentAuthorityEstablishmentRequest(
                ProjectId,
                "codex",
                session,
                boundary,
                new CurrentAuthorityFact(goalStatement, "ACTIVE", "CONTROLLER_CURRENT_GOAL", goalFingerprint, 1),
                new CurrentAuthorityFact(TaskStatement, "IN_PROGRESS", "CONTROLLER_CURRENT_ACTIVE_TASK", taskFingerprint, 1));
        }
    }

    private sealed class FailureInjector(CurrentAuthorityFailurePoint? point) : ICurrentAuthorityFailureInjector
    {
        public CurrentAuthorityFailurePoint? Point { get; set; } = point;

        public void ThrowIfRequested(CurrentAuthorityFailurePoint point)
        {
            if (Point == point) throw new InjectedAuthorityFailure(point);
        }
    }

    private sealed class InjectedAuthorityFailure(CurrentAuthorityFailurePoint point)
        : Exception($"Injected authority failure at {point}");
}

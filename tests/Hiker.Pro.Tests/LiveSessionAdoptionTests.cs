using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class LiveSessionAdoptionTests
{
    private const string ProjectId = "live-adoption-project";

    [Fact]
    public async Task UnsafeBoundary_WaitsWithoutCreatingOrChangingState()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var before = await project.Store.GetMetricsAsync();

        var result = await Service(project).AdoptAsync(Request(boundary: "TURN_IN_PROGRESS"));
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.WaitSafeBoundary, result.Status);
        Assert.False(result.Committed);
        Assert.Equal(0, result.StateWrites);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SafeBoundary_AdoptsExistingGoalTaskCheckpointAndBuildsBoundedContext()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);

        var result = await Service(project).AdoptAsync(Request());

        Assert.Equal(LiveAdoptionStatuses.Adopted, result.Status);
        Assert.True(result.Committed);
        Assert.Equal(ProjectId, result.ProjectId);
        Assert.Equal("goal-current", result.GoalId);
        Assert.Equal("task-current", result.TaskId);
        Assert.StartsWith("adoption-", result.CheckpointId, StringComparison.Ordinal);
        Assert.NotNull(result.Context);
        Assert.InRange(result.ContextBytes, 1, 16_384);
        Assert.InRange(result.StateReads, 1, 24);
        Assert.InRange(result.StateWrites, 5, 7);
        Assert.DoesNotContain("provider-session-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);

        var bindings = await project.Store.ReadAsync(ProjectId, EntityKinds.ProviderSessionBinding, StateLifecycle.Valid, 4);
        var evidence = await project.Store.ReadAsync(ProjectId, EntityKinds.Evidence, StateLifecycle.Valid, 4);
        var checkpoints = await project.Store.ReadAsync(ProjectId, EntityKinds.Checkpoint, StateLifecycle.Valid, 8);
        var ownership = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Ownership, "task-current"));
        Assert.Single(bindings);
        Assert.Contains(evidence, static item => item.Key.EntityId.StartsWith("live-adoption-", StringComparison.Ordinal));
        Assert.Contains(checkpoints, static item => item.Key.EntityId.StartsWith("adoption-", StringComparison.Ordinal));
        Assert.Equal(LiveSessionAdoptionService.Authority, ownership?.Authority);
    }

    [Fact]
    public async Task SameProviderSessionRetry_IsReadOnlyAndGenerationStable()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var service = Service(project);
        Assert.Equal(LiveAdoptionStatuses.Adopted, (await service.AdoptAsync(Request())).Status);
        var before = await project.Store.GetMetricsAsync();

        var retry = await service.AdoptAsync(Request());
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.AlreadyAdopted, retry.Status);
        Assert.True(retry.Idempotent);
        Assert.Equal(0, retry.StateWrites);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ExistingAdoptionWithLegacyTaskRevision_RebindsOnceWithoutDuplicatingRuntimeState()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var service = Service(project);
        Assert.Equal(LiveAdoptionStatuses.Adopted, (await service.AdoptAsync(Request())).Status);
        var taskKey = new StateKey(ProjectId, EntityKinds.Task, "task-current");
        var task = Assert.IsType<StateSnapshot>(await project.Store.ReadAsync(taskKey));
        Assert.True((await project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
            "fixture-legacy-task-revision",
            new[]
            {
                new StateMutation(
                    task.Key,
                    task.Generation,
                    142,
                    task.Lifecycle,
                    task.Authority,
                    task.SchemaVersion,
                    task.SourceFingerprint,
                    task.DependencyFingerprint,
                    task.PayloadJson,
                    task.Retention)
            },
            "FIXTURE_REVISION_DRIFTED",
            "FIXTURE_ONLY",
            "{}"))).Committed);

        var rebound = await service.AdoptAsync(Request());
        var afterRebind = await project.Store.ReadAsync(taskKey);
        var metrics = await project.Store.GetMetricsAsync();
        var retry = await service.AdoptAsync(Request());
        var afterRetry = await project.Store.GetMetricsAsync();
        var tasks = await project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null, limit: 8);
        var bindings = await project.Store.ReadAsync(ProjectId, EntityKinds.ProviderSessionBinding, lifecycle: null, limit: 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, rebound.Status);
        Assert.True(rebound.Committed);
        Assert.Equal(1, rebound.StateWrites);
        Assert.NotNull(rebound.Context);
        Assert.Equal(3, afterRebind?.GoalRevision);
        Assert.Equal(LiveAdoptionStatuses.AlreadyAdopted, retry.Status);
        Assert.Equal(metrics, afterRetry);
        Assert.Single(tasks, static item => item.Key.EntityId == "task-current");
        Assert.Single(bindings, static item => item.Lifecycle == StateLifecycle.Valid);
    }

    [Fact]
    public async Task LiveRuntimeLease_WaitsAndDoesNotAdopt()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var process = await new ProcessIdentityProbe().CurrentAsync();
        await SeedAsync(project, EntityKinds.RuntimeLease, "task-current", RuntimeJson(new RuntimeLease(
            ProjectId,
            "task-current",
            "existing-runtime",
            "codex",
            process,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            "RUNNING",
            1)));
        var before = await project.Store.GetMetricsAsync();

        var result = await Service(project).AdoptAsync(Request());
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.WaitSafeBoundary, result.Status);
        Assert.Contains("ACTIVE_WRITER_IDENTITY_LIVE", result.Reasons);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DeadRuntimeLease_IsArchivedAndDomainWorkIsNotRecreated()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var deadIdentity = new RuntimeProcessIdentity(int.MaxValue, DateTimeOffset.UnixEpoch, "dead", "dead");
        await SeedAsync(project, EntityKinds.RuntimeLease, "task-current", RuntimeJson(new RuntimeLease(
            ProjectId,
            "task-current",
            "dead-runtime",
            "codex",
            deadIdentity,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "RUNNING",
            1)));

        var result = await Service(project).AdoptAsync(Request(boundary: "RECOVERY_CHECKPOINT"));
        var lease = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.RuntimeLease, "task-current"));
        var tasks = await project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null, limit: 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, result.Status);
        Assert.Equal(StateLifecycle.Archived, lease?.Lifecycle);
        Assert.Single(tasks, static item => item.Key.EntityId == "task-current");
    }

    [Fact]
    public async Task MultipleCurrentGoals_BlockWithoutGuessingOrWriting()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        await SeedAsync(project, EntityKinds.Goal, "goal-other", "{\"goal_id\":\"goal-other\",\"status\":\"CURRENT\",\"revision\":1}");
        var before = await project.Store.GetMetricsAsync();

        var result = await Service(project).AdoptAsync(Request());
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.AmbiguousGoal, result.Status);
        Assert.False(result.Committed);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task MissingProvableGoal_BlocksWithoutInventingProviderIdentityAsGoal()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        var before = await project.Store.GetMetricsAsync();

        var result = await Service(project).AdoptAsync(Request());
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.GoalRequired, result.Status);
        Assert.Null(result.GoalId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task EmptyProjectWithoutCommunityGoal_DoesNotCreateIdentityOrTask()
    {
        await using var project = await TestProject.CreateAsync();
        var before = await project.Store.GetMetricsAsync();

        var result = await Service(project).AdoptAsync(Request());
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.GoalRequired, result.Status);
        Assert.Equal("NO_PROVABLE_COMMUNITY_GOAL", Assert.Single(result.Reasons));
        Assert.Equal(0, result.StateWrites);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task CommunityLegacyFixture_AutoMigratesFinalizesAndAdoptsWithoutChangingSourceFiles()
    {
        await using var project = await TestProject.CreateAsync();
        var governance = Path.Combine(project.Layout.AiRoot, "governance");
        var runtime = Path.Combine(project.Layout.AiRoot, "runtime");
        Directory.CreateDirectory(governance);
        Directory.CreateDirectory(runtime);
        var goalPath = Path.Combine(governance, "goal-contract.json");
        var taskPath = Path.Combine(runtime, "task.json");
        const string goal = "{\"goal_id\":\"legacy-goal\",\"status\":\"CURRENT\",\"revision\":8,\"acceptance\":[\"AC-1\"]}";
        const string task = "{\"task_id\":\"legacy-task\",\"goal_id\":\"legacy-goal\",\"status\":\"RUNNING\",\"goal_revision\":8}";
        await File.WriteAllTextAsync(goalPath, goal);
        await File.WriteAllTextAsync(taskPath, task);

        var result = await Service(project).AdoptAsync(Request(boundary: "RECOVERY_CHECKPOINT"));
        var beforeRetry = await project.Store.GetMetricsAsync();
        var retry = await Service(project).AdoptAsync(Request(boundary: "TURN_TERMINAL"));
        var afterRetry = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveAdoptionStatuses.Adopted, result.Status);
        Assert.NotNull(result.ProjectId);
        Assert.Equal("legacy-goal", result.GoalId);
        Assert.Equal("legacy-task", result.TaskId);
        Assert.Equal(goal, await File.ReadAllTextAsync(goalPath));
        Assert.Equal(task, await File.ReadAllTextAsync(taskPath));
        var adoptedTask = await project.Store.ReadAsync(new StateKey(result.ProjectId!, EntityKinds.Task, "legacy-task"));
        Assert.Equal(StateLifecycle.Valid, adoptedTask?.Lifecycle);
        Assert.Equal(LiveSessionAdoptionService.Authority, adoptedTask?.Authority);
        Assert.Equal(8, adoptedTask?.GoalRevision);
        Assert.Equal(LiveAdoptionStatuses.AlreadyAdopted, retry.Status);
        Assert.Equal(beforeRetry, afterRetry);
        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(result.ProjectId!);
        Assert.Equal("HEALTHY", health.Status);
    }

    [Fact]
    public async Task MigrationIncomplete_AutoConvergesAndPreservesStableProjectIdentity()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        var identityKey = new StateKey(ProjectId, EntityKinds.Project, ProjectIdentityFacts.EntityId);
        var identity = Assert.IsType<StateSnapshot>(await project.Store.ReadAsync(identityKey));
        var identityPayload = System.Text.Json.Nodes.JsonNode.Parse(identity.PayloadJson)!.AsObject();
        identityPayload["origin"] = "LEGACY_MIGRATION";
        Assert.True((await project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
            "mark-identity-as-legacy-migration",
            new[]
            {
                new StateMutation(
                    identity.Key,
                    identity.Generation,
                    identity.GoalRevision,
                    identity.Lifecycle,
                    identity.Authority,
                    identity.SchemaVersion,
                    identity.SourceFingerprint,
                    identity.DependencyFingerprint,
                    identityPayload.ToJsonString(),
                    identity.Retention)
            },
            "PROJECT_IDENTITY_RECOVERED",
            "LEGACY_MIGRATION_FIXTURE",
            "{}"))).Committed);
        var governance = Path.Combine(project.Layout.AiRoot, "governance");
        var runtime = Path.Combine(project.Layout.AiRoot, "runtime");
        Directory.CreateDirectory(governance);
        Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(
            Path.Combine(governance, "goal-contract.json"),
            "{\"goal_id\":\"partial-goal\",\"status\":\"CURRENT\",\"revision\":5}");
        await File.WriteAllTextAsync(
            Path.Combine(runtime, "task.json"),
            "{\"task_id\":\"partial-task\",\"goal_id\":\"partial-goal\",\"status\":\"RUNNING\",\"goal_revision\":5}");
        var partial = new StateMutation(
            new StateKey(ProjectId, EntityKinds.Task, "partial-task"),
            0,
            5,
            StateLifecycle.Valid,
            LegacyRuntimeFinalizationFacts.Authority,
            LegacyRuntimeFinalizationFacts.SchemaVersion,
            "partial-source",
            "partial-dependency",
            "{\"task_id\":\"partial-task\",\"goal_id\":\"partial-goal\",\"status\":\"RUNNING\",\"goal_revision\":5}");
        Assert.True((await project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
            "seed-partial-community-import",
            new[] { partial },
            "COMMUNITY_STATE_IMPORTED",
            "PARTIAL_IMPORT_FIXTURE",
            "{}"))).Committed);
        Assert.Equal(ProjectBootstrapState.MigrationIncomplete, (await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read)).State);

        var adopted = await Service(project).AdoptAsync(Request(boundary: "RECOVERY_CHECKPOINT", session: "partial-session"));
        var after = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);
        var identities = await project.Store.ReadAsync(ProjectId, EntityKinds.Project, lifecycle: null, limit: 8);
        var activeTasks = await project.Store.ReadAsync(ProjectId, EntityKinds.Task, StateLifecycle.Valid, 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, adopted.Status);
        Assert.Equal(ProjectId, adopted.ProjectId);
        Assert.Equal("partial-goal", adopted.GoalId);
        Assert.Equal("partial-task", adopted.TaskId);
        Assert.Equal(ProjectBootstrapState.ActiveExistingProject, after.State);
        Assert.Equal(ProjectId, after.ProjectId);
        Assert.Single(identities, static identity => identity.Key.EntityId == ProjectIdentityFacts.EntityId);
        Assert.Single(activeTasks, static task => task.Key.EntityId == "partial-task");
    }

    [Fact]
    public async Task CommunityTaskAdvancedAfterInitialMigration_AdoptsLatestBoundedCurrentWork()
    {
        await using var project = await TestProject.CreateAsync();
        var governance = Path.Combine(project.Layout.AiRoot, "governance");
        var runtime = Path.Combine(project.Layout.AiRoot, "runtime");
        Directory.CreateDirectory(governance);
        Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(
            Path.Combine(governance, "goal-contract.json"),
            "{\"project_id\":\"stable-goal\",\"revision\":9}");
        var taskPath = Path.Combine(runtime, "task.json");
        await File.WriteAllTextAsync(taskPath, "{\"id\":\"community-task-old\",\"status\":\"IN_PROGRESS\",\"plan_version\":8}");
        var migration = await project.Bootstrap.MigrateCommunityAsync();
        Assert.Equal("IMPORTED", migration.Status);
        var finalized = await new LegacyRuntimeFinalizationService(
            project.Layout,
            project.Store,
            project.Coordinator,
            project.Bootstrap,
            new ProcessIdentityProbe()).FinalizeAsync(migration.ProjectId);
        Assert.Equal("FINALIZED", finalized.Status);
        await File.WriteAllTextAsync(taskPath, "{\"id\":\"community-task-current\",\"status\":\"IMPLEMENTATION_IN_PROGRESS\",\"plan_version\":9}");

        var adopted = await Service(project).AdoptAsync(Request());
        var current = await project.Store.ReadAsync(new StateKey(migration.ProjectId, EntityKinds.Task, "community-task-current"));
        var old = await project.Store.ReadAsync(migration.ProjectId, EntityKinds.Task, lifecycle: null, limit: 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, adopted.Status);
        Assert.Equal("stable-goal", adopted.GoalId);
        Assert.Equal("community-task-current", adopted.TaskId);
        Assert.Equal(LiveSessionAdoptionService.Authority, current?.Authority);
        Assert.Contains(old, static item => item.Lifecycle == StateLifecycle.Archived);
        Assert.Equal("{\"id\":\"community-task-current\",\"status\":\"IMPLEMENTATION_IN_PROGRESS\",\"plan_version\":9}", await File.ReadAllTextAsync(taskPath));
    }

    [Fact]
    public async Task TerminalCheckpoint_RecordsEvidenceReleasesWriterAndIsIdempotent()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var adoption = await Service(project).AdoptAsync(Request());
        Assert.Equal(LiveAdoptionStatuses.Adopted, adoption.Status);
        var lifecycle = new LiveTurnLifecycleService(project.Store, project.Coordinator, project.Bootstrap);

        var checkpoint = await lifecycle.CheckpointAsync(new LiveTurnCheckpointRequest(
            "codex",
            "provider-session-secret",
            ChangedSurfaces: new[] { "src/runtime" },
            EvidenceRefs: new[] { "test://live-adoption" },
            TestRefs: new[] { "LiveSessionAdoptionTests" }));
        var beforeRetry = await project.Store.GetMetricsAsync();
        var retry = await lifecycle.CheckpointAsync(new LiveTurnCheckpointRequest("codex", "provider-session-secret"));
        var afterRetry = await project.Store.GetMetricsAsync();

        Assert.Equal(LiveTurnCheckpointStatuses.Checkpointed, checkpoint.Status);
        Assert.True(checkpoint.Committed);
        Assert.True(checkpoint.OwnershipReleased);
        Assert.NotNull(checkpoint.ContextFingerprint);
        Assert.InRange(checkpoint.ContextBytes, 1, 16_384);
        Assert.Equal(LiveTurnCheckpointStatuses.AlreadyCheckpointed, retry.Status);
        Assert.True(retry.Idempotent);
        Assert.Equal(beforeRetry, afterRetry);
        var ownership = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Ownership, "task-current"));
        var bindings = await project.Store.ReadAsync(ProjectId, EntityKinds.ProviderSessionBinding, lifecycle: null, limit: 4);
        var turnEvidence = await project.Store.ReadAsync(ProjectId, EntityKinds.Evidence, StateLifecycle.Valid, 8);
        Assert.Equal(StateLifecycle.Archived, ownership?.Lifecycle);
        Assert.Contains(bindings, static item => item.Lifecycle == StateLifecycle.Archived);
        Assert.Contains(turnEvidence, static item => item.Key.EntityId.StartsWith("turn-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentProviderSession_WaitsUntilPriorSessionCheckpointThenReusesSameTask()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var service = Service(project);
        Assert.Equal(LiveAdoptionStatuses.Adopted, (await service.AdoptAsync(Request(session: "session-one"))).Status);

        var blocked = await service.AdoptAsync(Request(session: "session-two"));
        Assert.Equal(LiveAdoptionStatuses.WaitSafeBoundary, blocked.Status);
        Assert.Contains("ACTIVE_PROVIDER_SESSION_BOUND", blocked.Reasons);

        var lifecycle = new LiveTurnLifecycleService(project.Store, project.Coordinator, project.Bootstrap);
        Assert.Equal(
            LiveTurnCheckpointStatuses.Checkpointed,
            (await lifecycle.CheckpointAsync(new LiveTurnCheckpointRequest("codex", "session-one"))).Status);
        var adopted = await service.AdoptAsync(Request(session: "session-two"));
        var tasks = await project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null, limit: 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, adopted.Status);
        Assert.Equal("task-current", adopted.TaskId);
        Assert.Single(tasks, static item => item.Key.EntityId == "task-current");
    }

    [Fact]
    public async Task InterruptedProviderSession_RecoveryBoundaryArchivesOldBindingWithoutDuplicateTask()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var service = Service(project);
        Assert.Equal(LiveAdoptionStatuses.Adopted, (await service.AdoptAsync(Request(session: "crashed-session"))).Status);

        var recovered = await service.AdoptAsync(Request("RECOVERY_CHECKPOINT", "recovery-session"));
        var bindings = await project.Store.ReadAsync(ProjectId, EntityKinds.ProviderSessionBinding, lifecycle: null, limit: 8);
        var ownership = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Ownership, "task-current"));
        var tasks = await project.Store.ReadAsync(ProjectId, EntityKinds.Task, lifecycle: null, limit: 8);

        Assert.Equal(LiveAdoptionStatuses.Adopted, recovered.Status);
        Assert.Equal(2, bindings.Count);
        Assert.Single(bindings, static item => item.Lifecycle == StateLifecycle.Valid);
        Assert.Single(bindings, static item => item.Lifecycle == StateLifecycle.Archived);
        Assert.Contains("execution-", ownership?.PayloadJson, StringComparison.Ordinal);
        Assert.Single(tasks, static item => item.Key.EntityId == "task-current");
    }

    [Fact]
    public async Task ColdProjectHistory_DoesNotIncreaseAdoptionReadOrContextBudget()
    {
        await using var clean = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(clean);
        var cleanResult = await Service(clean).AdoptAsync(Request(session: "clean-session"));

        await using var aged = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(aged);
        for (var batch = 0; batch < 4; batch++)
        {
            var history = Enumerable.Range(batch * 250, 250).Select(index => new StateMutation(
                new StateKey(ProjectId, EntityKinds.Decision, $"cold-{index:D4}"),
                0,
                1,
                StateLifecycle.Archived,
                "TEST",
                "3.0.0",
                $"cold-source-{index}",
                $"cold-dependency-{index}",
                $"{{\"sequence\":{index}}}",
                RetentionTier.Cold)).ToArray();
            Assert.True((await aged.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
                $"seed-cold-{batch}",
                history,
                "COLD_HISTORY_IMPORTED",
                "COLD_HISTORY",
                "{}"))).Committed);
        }
        var agedResult = await Service(aged).AdoptAsync(Request(session: "aged-session"));

        Assert.Equal(LiveAdoptionStatuses.Adopted, cleanResult.Status);
        Assert.Equal(LiveAdoptionStatuses.Adopted, agedResult.Status);
        Assert.Equal(cleanResult.StateReads, agedResult.StateReads);
        Assert.InRange(Math.Abs(cleanResult.ContextBytes - agedResult.ContextBytes), 0, 256);
        Assert.InRange(agedResult.ContextBytes, 1, 16_384);
    }

    [Fact]
    public async Task RepeatedLiveTurns_ReplaceActiveContextEdgesAndKeepHotReadsBounded()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        await SeedCurrentWorkAsync(project);
        var service = Service(project);
        var lifecycle = new LiveTurnLifecycleService(project.Store, project.Coordinator, project.Bootstrap);
        LiveAdoptionResult? first = null;
        LiveAdoptionResult? last = null;

        for (var turn = 0; turn < 100; turn++)
        {
            var session = $"bounded-session-{turn:D3}";
            last = await service.AdoptAsync(Request(session: session));
            first ??= last;
            Assert.Equal(LiveAdoptionStatuses.Adopted, last.Status);
            Assert.Equal(
                LiveTurnCheckpointStatuses.Checkpointed,
                (await lifecycle.CheckpointAsync(new LiveTurnCheckpointRequest(
                    "codex",
                    session,
                    ChangedSurfaces: new[] { $"src/turn-{turn:D3}" },
                    EvidenceRefs: new[] { $"test://turn-{turn:D3}" }))).Status);
        }

        var activeEdges = await project.Store.QueryDependenciesBoundedAsync(
            ProjectId,
            EntityKinds.Task,
            "task-current",
            outgoing: true,
            limit: 16);
        Assert.NotNull(first);
        Assert.NotNull(last);
        Assert.Equal(first.StateReads, last.StateReads);
        Assert.InRange(last.ContextBytes, 1, 16_384);
        Assert.InRange(Math.Abs(last.ContextBytes - first.ContextBytes), 0, 2_048);
        Assert.InRange(activeEdges.Count, 1, 2);
        Assert.All(activeEdges, static edge => Assert.Contains(edge.Relation, new[] { "CHECKPOINTED_AT", "PROVEN_BY" }));
    }

    private static LiveSessionAdoptionService Service(TestProject project) => new(
        project.Layout,
        project.Store,
        project.Coordinator,
        project.Bootstrap,
        new ProcessIdentityProbe());

    private static LiveAdoptionRequest Request(string boundary = "TURN_TERMINAL", string session = "provider-session-secret") => new(
        "codex",
        session,
        boundary);

    private static Task SeedCurrentWorkAsync(TestProject project) => project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
        "seed-current-work",
        new[]
        {
            Mutation(EntityKinds.Goal, "goal-current", "{\"goal_id\":\"goal-current\",\"status\":\"CURRENT\",\"revision\":3}"),
            Mutation(EntityKinds.Task, "task-current", "{\"task_id\":\"task-current\",\"goal_id\":\"goal-current\",\"status\":\"RUNNING\",\"goal_revision\":3}"),
            Mutation(EntityKinds.Checkpoint, "checkpoint-current", "{\"checkpoint_id\":\"checkpoint-current\",\"task_id\":\"task-current\",\"goal_id\":\"goal-current\",\"status\":\"VALID\"}", RetentionTier.Warm)
        },
        "FIXTURE_CREATED",
        "LIVE_ADOPTION_FIXTURE",
        "{}"));

    private static async Task SeedAsync(TestProject project, string kind, string id, string payload)
    {
        var result = await project.Coordinator.CommitBatchAsync(new BatchTransactionRequest(
            $"seed-{kind}-{id}",
            new[] { Mutation(kind, id, payload) },
            "FIXTURE_CREATED",
            "LIVE_ADOPTION_FIXTURE",
            "{}"));
        Assert.True(result.Committed);
    }

    private static StateMutation Mutation(string kind, string id, string payload, RetentionTier retention = RetentionTier.Hot) => new(
        new StateKey(ProjectId, kind, id),
        0,
        3,
        StateLifecycle.Valid,
        "TEST",
        "3.0.0",
        $"source-{kind}-{id}",
        $"dependency-{kind}-{id}",
        payload,
        retention);

    private static string RuntimeJson<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    });
}

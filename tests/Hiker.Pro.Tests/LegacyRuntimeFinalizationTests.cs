using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class LegacyRuntimeFinalizationTests
{
    private const string ProjectId = "legacy-finalize-project";

    [Fact]
    public async Task CompletedLegacyMigration_FinalizesRuntimeFactsAtomicallyAndIdempotently()
    {
        await using var project = await CreateMigratedFixtureAsync();
        var doctor = new AdvancedDoctor(project.Store, new ProcessIdentityProbe());
        var beforeHealth = await doctor.CheckProjectAsync(ProjectId);
        var beforeMetrics = await project.Store.GetMetricsAsync();
        var ownershipBefore = await RequiredAsync(project, EntityKinds.Ownership, "governance:ownership.json");
        var worktreeBefore = await RequiredAsync(project, EntityKinds.Worktree, "workspace:task-map.json");
        var evidenceBefore = await RequiredAsync(project, EntityKinds.Evidence, "EVIDENCE-001");

        var first = await Finalizer(project).FinalizeAsync();
        var afterMetrics = await project.Store.GetMetricsAsync();
        var afterHealth = await doctor.CheckProjectAsync(ProjectId);
        var ownershipAfter = await RequiredAsync(project, ownershipBefore.Key.Kind, ownershipBefore.Key.EntityId);
        var worktreeAfter = await RequiredAsync(project, worktreeBefore.Key.Kind, worktreeBefore.Key.EntityId);
        var evidenceAfter = await RequiredAsync(project, evidenceBefore.Key.Kind, evidenceBefore.Key.EntityId);

        Assert.Equal("BLOCKED", beforeHealth.Status);
        Assert.Equal(
            LegacyRuntimeFinalizationFacts.RuntimeKinds.Count,
            beforeHealth.Findings.Count(static finding => finding.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW"));
        Assert.Equal("FINALIZED", first.Status);
        Assert.Equal("HEALTHY", first.Health.Status);
        Assert.Equal("HEALTHY", afterHealth.Status);
        Assert.NotNull(first.Backup);
        Assert.True(File.Exists(first.Backup.BackupPath));
        Assert.Equal(beforeMetrics.Generation + 1, afterMetrics.Generation);
        Assert.Equal(beforeMetrics.Entities, afterMetrics.Entities);
        Assert.Equal(beforeMetrics.Events + 1, afterMetrics.Events);
        Assert.Equal(LegacyRuntimeFinalizationFacts.RuntimeKinds.Count, first.Finalization.Finalized);
        Assert.Equal(first.Finalization.Generation, ownershipAfter.Generation);
        Assert.Equal(first.Finalization.Generation, worktreeAfter.Generation);
        AssertLegacyPreserved(ownershipBefore, ownershipAfter);
        AssertLegacyPreserved(worktreeBefore, worktreeAfter);
        Assert.Equal(StateLifecycle.Valid, evidenceAfter.Lifecycle);
        Assert.Equal(evidenceBefore.Generation, evidenceAfter.Generation);
        Assert.Equal(evidenceBefore.PayloadJson, evidenceAfter.PayloadJson);

        foreach (var kind in LegacyRuntimeFinalizationFacts.RuntimeKinds)
        {
            Assert.Empty(await project.Store.ReadAsync(ProjectId, kind, StateLifecycle.Valid, 1000));
        }

        var identity = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);
        Assert.Equal(ProjectId, identity.ProjectId);
        Assert.Equal(ProjectBootstrapState.ActiveExistingProject, identity.State);

        var beforeRetry = await project.Store.GetMetricsAsync();
        var retry = await Finalizer(project).FinalizeAsync();
        var afterRetry = await project.Store.GetMetricsAsync();
        Assert.Equal("ALREADY_FINALIZED", retry.Status);
        Assert.Null(retry.Backup);
        Assert.Equal(beforeRetry, afterRetry);
    }

    [Fact]
    public async Task UnknownLegacyRuntimeSchema_RequiresReviewWithoutBackupOrMutation()
    {
        await using var project = await CreateMigratedFixtureAsync();
        var key = new StateKey(ProjectId, EntityKinds.RuntimeLease, "legacy-lease");
        var result = await project.Store.CompareAndSwapAsync(new TransactionRequest(
            "seed-unknown-legacy-runtime",
            new StateMutation(
                key,
                0,
                0,
                StateLifecycle.Valid,
                LegacyRuntimeFinalizationFacts.Authority,
                "9.0.0",
                "unknown-source",
                "unknown-dependency",
                "{\"legacy\":true}"),
            "FIXTURE_CREATED",
            "UNKNOWN_LEGACY_RUNTIME_SCHEMA",
            "{}"));
        Assert.True(result.Committed);
        var before = await project.Store.GetMetricsAsync();

        var finalize = await Finalizer(project).FinalizeAsync();
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal("REQUIRES_REVIEW", finalize.Status);
        Assert.Equal("UNKNOWN_LEGACY_RUNTIME_STATE", finalize.ReasonCode);
        Assert.Null(finalize.Backup);
        Assert.Equal(before, after);
        Assert.Contains(finalize.Inspection.ReviewFacts, static item =>
            item.Kind == EntityKinds.RuntimeLease && item.EntityId == "legacy-lease");
        Assert.Equal(StateLifecycle.Valid, (await project.Store.ReadAsync(key))?.Lifecycle);
    }

    [Fact]
    public async Task FinalizeBackup_RestoresExactPreFinalizeBlockedState()
    {
        await using var project = await CreateMigratedFixtureAsync();
        var finalized = await Finalizer(project).FinalizeAsync();
        Assert.NotNull(finalized.Backup);
        Assert.Equal("HEALTHY", finalized.Health.Status);

        var restored = await new StateBackupService(project.Layout).RestoreAsync(finalized.Backup.BackupPath);
        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);
        var ownership = await RequiredAsync(project, EntityKinds.Ownership, "governance:ownership.json");

        Assert.Equal("RESTORED", restored.Status);
        Assert.Equal("BLOCKED", health.Status);
        Assert.Equal(StateLifecycle.Incompatible, ownership.Lifecycle);
        Assert.Equal(LegacyRuntimeFinalizationFacts.Authority, ownership.Authority);
    }

    private static LegacyRuntimeFinalizationService Finalizer(TestProject project) => new(
        project.Layout,
        project.Store,
        project.Coordinator,
        project.Bootstrap,
        new ProcessIdentityProbe());

    private static async Task<TestProject> CreateMigratedFixtureAsync()
    {
        var project = await TestProject.CreateAsync();
        try
        {
            await WriteLegacyAsync(project, "governance/ownership.json", "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
            await WriteLegacyAsync(project, "workspace/task-map.json", "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}");
            await WriteLegacyAsync(project, "runtime/task.json", "{\"task_id\":\"TASK-001\",\"status\":\"RUNNING\"}");
            await WriteLegacyAsync(project, "runtime/checkpoint-ledger.json", "{\"checkpoint_id\":\"CHECKPOINT-001\",\"status\":\"CURRENT\"}");
            await WriteLegacyAsync(project, "quality/evidence/index.json", "{\"evidence_id\":\"EVIDENCE-001\",\"status\":\"PASS\"}");
            await WriteLegacyAsync(project, "context/architecture.json", "{\"decision_id\":\"DECISION-001\",\"status\":\"APPROVED\"}");
            var migration = await project.Bootstrap.MigrateCommunityAsync(ProjectId);
            Assert.Equal("IMPORTED", migration.Status);
            Assert.Equal(ProjectId, migration.ProjectId);
            await SeedLegacyRuntimeAsync(project, EntityKinds.TaskDependency, "TASK-001:dependency", "{\"task_id\":\"TASK-001\",\"depends_on\":\"TASK-000\"}");
            await SeedLegacyRuntimeAsync(project, EntityKinds.RuntimeLease, "TASK-001", "{\"task_id\":\"TASK-001\",\"runtime_id\":\"legacy-runtime\"}");
            await SeedLegacyRuntimeAsync(project, EntityKinds.ResourceLease, "RESOURCE-001", "{\"resource_id\":\"RESOURCE-001\",\"runtime_id\":\"legacy-runtime\"}");
            await SeedLegacyRuntimeAsync(project, EntityKinds.Agent, "legacy-runtime", "{\"runtime_id\":\"legacy-runtime\",\"process_id\":1234}");
            return project;
        }
        catch
        {
            await project.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteLegacyAsync(TestProject project, string relative, string payload)
    {
        var path = project.Layout.ResolveAiRelativePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, payload);
    }

    private static async Task SeedLegacyRuntimeAsync(TestProject project, string kind, string entityId, string payload)
    {
        var result = await project.Store.CompareAndSwapAsync(new TransactionRequest(
            $"seed-legacy-{kind.ToLowerInvariant()}-{entityId}",
            new StateMutation(
                new StateKey(ProjectId, kind, entityId),
                0,
                0,
                StateLifecycle.Valid,
                LegacyRuntimeFinalizationFacts.Authority,
                LegacyRuntimeFinalizationFacts.SchemaVersion,
                $"source-{kind.ToLowerInvariant()}",
                $"dependency-{kind.ToLowerInvariant()}",
                payload),
            "FIELD_FIXTURE_CREATED",
            "LEGACY_RUNTIME_FIXTURE_CREATED",
            "{}"));
        Assert.True(result.Committed);
    }

    private static async Task<StateSnapshot> RequiredAsync(TestProject project, string kind, string entityId) =>
        await project.Store.ReadAsync(new StateKey(ProjectId, kind, entityId))
        ?? throw new Xunit.Sdk.XunitException($"Missing state {kind}/{entityId}");

    private static void AssertLegacyPreserved(StateSnapshot before, StateSnapshot after)
    {
        Assert.Equal(StateLifecycle.Archived, after.Lifecycle);
        Assert.Equal(RetentionTier.Archived, after.Retention);
        Assert.Equal(before.Authority, after.Authority);
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
        Assert.Equal(before.SourceFingerprint, after.SourceFingerprint);
        Assert.Equal(before.DependencyFingerprint, after.DependencyFingerprint);
        Assert.Equal(before.PayloadJson, after.PayloadJson);
        Assert.Equal(before.GoalRevision, after.GoalRevision);
    }
}

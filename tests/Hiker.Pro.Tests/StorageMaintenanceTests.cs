using Hiker.Pro.State;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StorageMaintenanceTests
{
    [Fact]
    public async Task CrashDuringArchive_ResumesFromVerifiedArchiveWithoutDuplicateEvents()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("crash-task", EntityKinds.Task, "task-1", "v1"))).Committed);
        for (var index = 0; index < 20; index++) await project.Coordinator.AppendEventAsync(Event(index));
        await ExecuteSqlAsync(project.Layout.DatabasePath, """
            CREATE TRIGGER fail_event_segment BEFORE INSERT ON event_segments
            BEGIN SELECT RAISE(ABORT, 'injected archive pointer failure'); END;
            """);
        var request = new EventRollupRequest("crash-rollup", "project-storage", 4, 100);
        await Assert.ThrowsAsync<SqliteException>(() => project.Coordinator.RollupEventsAsync(request));
        Assert.Equal("ARCHIVE_VERIFIED", await ScalarAsync(
            project.Layout.DatabasePath,
            "SELECT status FROM storage_maintenance WHERE operation_id='crash-rollup';"));

        await ExecuteSqlAsync(project.Layout.DatabasePath, "DROP TRIGGER fail_event_segment;");
        var recovered = await project.Coordinator.RollupEventsAsync(request);
        var replay = await project.Coordinator.RollupEventsAsync(request);
        var segmentCount = Convert.ToInt32(await ScalarAsync(
            project.Layout.DatabasePath,
            "SELECT COUNT(*) FROM event_segments WHERE project_id='project-storage';"), System.Globalization.CultureInfo.InvariantCulture);
        var dedupCount = Convert.ToInt32(await ScalarAsync(
            project.Layout.DatabasePath,
            "SELECT COUNT(*) FROM event_dedup WHERE project_id='project-storage';"), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("COMPLETE", recovered.Status);
        Assert.True(replay.Idempotent);
        Assert.Equal(1, segmentCount);
        Assert.Equal(recovered.EventsArchived, dedupCount);
    }

    [Fact]
    public async Task SnapshotPlusDelta_ReconstructsCurrentStateWithoutDayOneEventReplay()
    {
        await using var project = await TestProject.CreateAsync();
        var first = await project.Store.CompareAndSwapAsync(Request("snapshot-v1", EntityKinds.Task, "task-1", "v1"));
        var snapshot = await project.Coordinator.CreateRuntimeSnapshotAsync("snapshot-create", "project-storage");
        var secondRequest = Request("snapshot-v2", EntityKinds.Task, "task-1", "v2") with
        {
            Mutation = Request("snapshot-v2", EntityKinds.Task, "task-1", "v2").Mutation with
            {
                ExpectedGeneration = first.CurrentGeneration
            }
        };
        Assert.True((await project.Store.CompareAndSwapAsync(secondRequest)).Committed);

        var recovery = await project.Store.ReconstructSnapshotAsync("project-storage", snapshot.SnapshotId);
        Assert.Equal(1, recovery.DeltasApplied);
        Assert.Contains(recovery.Entities, static value => value.Key.EntityId == "task-1" && value.PayloadJson.Contains("v2", StringComparison.Ordinal));
        Assert.True(recovery.CurrentGeneration > recovery.SnapshotGeneration);
    }

    [Fact]
    public async Task ActiveWriterBlocksCompactionAndIdleCompactionReclaimsPhysicalSpace()
    {
        await using (var active = await TestProject.CreateAsync())
        {
            Assert.True((await active.Store.CompareAndSwapAsync(Request("active-owner", EntityKinds.Ownership, "writer-1", "active"))).Committed);
            var blocked = await active.Coordinator.CompactStorageAsync("compact-blocked", "project-storage");
            Assert.Equal("BLOCKED_ACTIVE_WRITER_OR_RECOVERY", blocked.Status);
            Assert.False(blocked.BackupCreated);
        }

        await using var idle = await TestProject.CreateAsync();
        await ExecuteSqlAsync(idle.Layout.DatabasePath, """
            CREATE TABLE compaction_fixture(payload BLOB NOT NULL);
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < 4096
            )
            INSERT INTO compaction_fixture(payload) SELECT randomblob(1024) FROM sequence;
            DELETE FROM compaction_fixture;
            """);
        var before = new FileInfo(idle.Layout.DatabasePath).Length;
        var compacted = await idle.Coordinator.CompactStorageAsync("compact-idle", "project-storage");
        var replay = await idle.Coordinator.CompactStorageAsync("compact-idle", "project-storage");
        Assert.Equal("COMPACTED", compacted.Status);
        Assert.True(compacted.BackupCreated);
        Assert.True(compacted.ReclaimedBytes > 0, $"before={compacted.BytesBefore},after={compacted.BytesAfter},ratio={compacted.FreelistRatioBefore}");
        Assert.True(compacted.BytesAfter < before);
        Assert.True(replay.Idempotent);
    }

    [Fact]
    public async Task HotStateAndCurrentDependencyQueriesUseBoundedIndexes()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "indexed-state",
            new[]
            {
                Mutation(EntityKinds.Task, "task-1", "task"),
                Mutation(EntityKinds.Decision, "decision-1", "decision")
            },
            "STATE_CREATED",
            "TEST",
            "{}",
            new[]
            {
                new DependencyEdge("project-storage", EntityKinds.Task, "task-1", EntityKinds.Decision, "decision-1", "DEPENDS_ON", "edge-1")
            }))).Committed);
        var entityPlan = await QueryPlanAsync(project.Layout.DatabasePath, """
            SELECT project_id,kind,entity_id FROM entities
            WHERE project_id='project-storage' AND kind='TASK' AND lifecycle='VALID'
            ORDER BY updated_at DESC LIMIT 10;
            """);
        var edgePlan = await QueryPlanAsync(project.Layout.DatabasePath, """
            SELECT * FROM dependency_edges
            WHERE project_id='project-storage' AND from_kind='TASK' AND from_id='task-1' AND lifecycle='VALID';
            """);
        Assert.Contains(entityPlan, static value => value.Contains("ix_entities_hot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(edgePlan, static value => value.Contains("ix_edges_outgoing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StorageBudget_IsPolicyOnlyAndDoesNotDeleteEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("budget-evidence", EntityKinds.Evidence, "evidence-1", "valid"))).Committed);
        var metrics = await project.Store.GetStorageMetricsAsync("project-storage");
        var report = new StorageBudgetEvaluator().Evaluate(metrics, new StorageBudget(1, 1, 1, 1, 1, 1));
        Assert.Equal("BUDGET_EXCEEDED", report.Status);
        Assert.Contains("ROLLUP_AND_COMPACT_WHEN_IDLE", report.RecommendedActions);
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("project-storage", EntityKinds.Evidence, "evidence-1")));
    }

    [Fact]
    public async Task ExplicitMaintenance_IsIdempotentAndSecretBearingEventsNeverEnterState()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("maintenance-task", EntityKinds.Task, "task-1", "valid"))).Committed);
        await Assert.ThrowsAsync<InvalidDataException>(() => project.Coordinator.AppendEventAsync(new EngineeringEvent(
            "secret-event", "project-storage", "TASK_PROGRESS", EntityKinds.Task, "task-1", 1,
            "SECRET_REJECTED", "{\"token\":\"must-not-persist\"}", DateTimeOffset.UtcNow)));
        var policy = new StorageMaintenancePolicy(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(-30),
            KeepRecentEvents: 100,
            KeepRecentOperations: 100,
            CompactWhenSafe: false);
        var coordinator = new StorageLifecycleMaintenanceCoordinator(project.Coordinator);
        var first = await coordinator.RunAsync("explicit-maintenance", "project-storage", policy);
        var replay = await coordinator.RunAsync("explicit-maintenance", "project-storage", policy);
        Assert.Equal("COMPLETE", first.Status);
        Assert.Equal("COMPLETE", replay.Status);
        Assert.True(replay.Events.Idempotent || replay.Events.Status == "NO_ROLLUP_REQUIRED");
        Assert.True(replay.Operations.Idempotent);
        Assert.True(replay.Dependencies.Idempotent);
        Assert.True(replay.Entities.Idempotent);
        Assert.True(replay.Artifacts.Idempotent);
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(
            project.Layout.DatabasePath,
            "SELECT COUNT(*) FROM engineering_events WHERE event_id='secret-event';"), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static EngineeringEvent Event(int index) => new(
        $"crash-event-{index:D4}", "project-storage", "TASK_PROGRESS", EntityKinds.Task, "task-1", 1,
        "BOUNDED_EVENT", "{}", DateTimeOffset.UnixEpoch.AddSeconds(index));

    private static TransactionRequest Request(string operationId, string kind, string id, string value) => new(
        operationId,
        Mutation(kind, id, value),
        "STATE_UPDATED",
        "TEST",
        "{}");

    private static StateMutation Mutation(string kind, string id, string value) => new(
        new StateKey("project-storage", kind, id), 0, 15, StateLifecycle.Valid, "STATE_COORDINATOR", "3.0.0",
        $"source-{id}", $"dependency-{id}", $"{{\"value\":\"{value}\"}}", RetentionTier.Hot);

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<IReadOnlyList<string>> QueryPlanAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(3));
        return values;
    }
}

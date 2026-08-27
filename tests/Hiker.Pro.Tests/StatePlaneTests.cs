using System.Text;
using Hiker.Pro.Artifacts;
using Hiker.Pro.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StatePlaneTests
{
    [Fact]
    public async Task Initialize_UsesRealSqliteWalAndSchema3()
    {
        await using var project = await TestProject.CreateAsync();
        await using var connection = new SqliteConnection($"Data Source={project.Layout.DatabasePath}");
        await connection.OpenAsync();
        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        Assert.Equal("3.0.0", await schema.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CompareAndSwap_AllowsExactlyOneConcurrentWriter()
    {
        await using var project = await TestProject.CreateAsync();
        var key = new StateKey("project-1", EntityKinds.Task, "task-1");
        var firstCoordinator = new StateCoordinator(project.Store);
        var secondStore = new SqliteStateStore(project.Layout);
        await secondStore.InitializeAsync();
        await using var secondStoreLease = secondStore;
        var secondCoordinator = new StateCoordinator(secondStore);
        var firstSnapshot = await firstCoordinator.ReadForReasoningAsync(key);
        var secondSnapshot = await secondCoordinator.ReadForReasoningAsync(key);
        var first = Commit(firstCoordinator, firstSnapshot, "op-a", "A");
        var second = Commit(secondCoordinator, secondSnapshot, "op-b", "B");
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, item => item.Committed);
        Assert.Single(results, item => item.Conflict && item.Status == "STALE_CONTEXT");
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Entities);
    }

    [Fact]
    public async Task OperationRetry_IsIdempotentAndPayloadConflictIsRejected()
    {
        await using var project = await TestProject.CreateAsync();
        var request = Request("op-1", "value-a");
        var first = await project.Store.CompareAndSwapAsync(request);
        var retry = await project.Store.CompareAndSwapAsync(request);
        var conflict = await project.Store.CompareAndSwapAsync(Request("op-1", "value-b"));
        Assert.True(first.Committed);
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
        Assert.Equal(first.CurrentGeneration, retry.CurrentGeneration);
        Assert.True(conflict.Conflict);
        Assert.Equal("OPERATION_ID_CONFLICT", conflict.Status);
        var metrics = await project.Store.GetMetricsAsync();
        Assert.Equal(1, metrics.Events);
        Assert.Equal(1, metrics.Entities);
    }

    [Fact]
    public async Task OperationRetry_RejectsChangedDependencySet()
    {
        await using var project = await TestProject.CreateAsync();
        var edgeA = new DependencyEdge("project-1", EntityKinds.Task, "task-1", EntityKinds.Evidence, "evidence-a", "PROVEN_BY", "hash-a");
        var edgeB = edgeA with { ToId = "evidence-b", Fingerprint = "hash-b" };
        var first = await project.Store.CompareAndSwapAsync(Request("op-deps", "value-a") with { DependencyChanges = new[] { edgeA } });
        var conflictingRetry = await project.Store.CompareAndSwapAsync(Request("op-deps", "value-a") with { DependencyChanges = new[] { edgeB } });
        Assert.True(first.Committed);
        Assert.True(conflictingRetry.Conflict);
        Assert.Equal("OPERATION_ID_CONFLICT", conflictingRetry.Status);
    }

    [Fact]
    public async Task HistoricalOperationReplay_DoesNotClaimLaterSnapshotAsItsResult()
    {
        await using var project = await TestProject.CreateAsync();
        var firstRequest = Request("op-first", "first");
        var first = await project.Store.CompareAndSwapAsync(firstRequest);
        var secondRequest = Request("op-second", "second") with
        {
            Mutation = Request("op-second", "second").Mutation with { ExpectedGeneration = first.CurrentGeneration }
        };
        Assert.True((await project.Store.CompareAndSwapAsync(secondRequest)).Committed);

        var replay = await project.Store.CompareAndSwapAsync(firstRequest);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(first.CurrentGeneration, replay.CurrentGeneration);
        Assert.Null(replay.Snapshot);
    }

    [Fact]
    public void ProjectLayout_RejectsRootedAndEscapingStateReferences()
    {
        var layout = new ProjectLayout(Path.Combine(Path.GetTempPath(), $"hiker-layout-{Guid.NewGuid():N}"));
        Assert.Throws<InvalidDataException>(() => layout.ResolveAiRelativePath(Path.GetFullPath(Path.GetTempPath())));
        Assert.Throws<InvalidDataException>(() => layout.ResolveAiRelativePath("../outside"));
    }

    [Fact]
    public async Task StateBackup_RestoresCommittedGenerationAfterLaterMutation()
    {
        await using var project = await TestProject.CreateAsync();
        var first = await project.Store.CompareAndSwapAsync(Request("backup-first", "first"));
        var backupService = new StateBackupService(project.Layout);
        var backup = await backupService.CreateAsync();
        var secondRequest = Request("backup-second", "second") with
        {
            Mutation = Request("backup-second", "second").Mutation with { ExpectedGeneration = first.CurrentGeneration }
        };
        Assert.True((await project.Store.CompareAndSwapAsync(secondRequest)).Committed);

        var restore = await backupService.RestoreAsync(backup.BackupPath);
        Assert.Equal("RESTORED", restore.Status);
        var restored = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Task, "task-1"));
        Assert.Equal(first.CurrentGeneration, restored?.Generation);
        Assert.Contains("first", restored?.PayloadJson);
        Assert.True(File.Exists(restore.SafetyBackupPath));
    }

    [Fact]
    public async Task StateExport_IsAtomicExplicitAndCannotTargetAuthoritativeStateDirectory()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("export-task", "exported"))).Committed);
        var result = await project.Store.ExportAsync("project-1");
        Assert.Equal("EXPORTED", result.Status);
        Assert.Equal(1, result.Entities);
        Assert.Equal(1, result.Operations);
        Assert.Equal(1, result.Events);
        Assert.True(File.Exists(result.OutputPath));
        Assert.False(File.Exists($"{result.OutputPath}.tmp"));
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.OutputPath));
        Assert.Equal(result.Generation, document.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(1, document.RootElement.GetProperty("entities").GetArrayLength());

        var forbidden = Path.Combine(project.Layout.StateDirectory, "export.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => project.Store.ExportAsync("project-1", forbidden));
        await Assert.ThrowsAsync<IOException>(() => project.Store.ExportAsync("project-1", result.OutputPath));
    }

    [Fact]
    public async Task Community518Import_IsAllowlistedResumableAndEstablishesSingleAuthorityMarker()
    {
        await using var project = await TestProject.CreateAsync();
        var governance = Path.Combine(project.Layout.AiRoot, "governance");
        var tasks = Path.Combine(project.Layout.AiRoot, "tasks");
        var rawEvidence = Path.Combine(project.Layout.AiRoot, "evidence", "raw");
        Directory.CreateDirectory(governance);
        Directory.CreateDirectory(tasks);
        Directory.CreateDirectory(rawEvidence);
        await File.WriteAllTextAsync(Path.Combine(governance, "goal-contract.json"), "{\"goal_id\":\"goal-1\",\"revision\":3}");
        await File.WriteAllTextAsync(Path.Combine(tasks, "TASK-001.json"), "{\"task_id\":\"TASK-001\",\"goal_revision\":3,\"status\":\"Development\"}");
        var preservedEvidence = Path.Combine(rawEvidence, "not-imported.json");
        await File.WriteAllTextAsync(preservedEvidence, "{\"prompt\":\"must stay outside state\"}");

        var importer = new Community518Importer(project.Layout, project.Coordinator);
        var first = await importer.ImportAsync("project-legacy");
        var retry = await importer.ImportAsync("project-legacy");
        Assert.Equal("IMPORTED", first.Status);
        Assert.Equal(2, first.Imported);
        Assert.Equal("ALREADY_IMPORTED", retry.Status);
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("project-legacy", EntityKinds.Task, "TASK-001")));
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("project-legacy", EntityKinds.Migration, "community-5.18")));
        Assert.Null(await project.Store.ReadAsync(new StateKey("project-legacy", EntityKinds.Evidence, "evidence:raw:not-imported.json")));
        Assert.True(File.Exists(preservedEvidence));
        Assert.Equal(3, (await project.Store.GetMetricsAsync()).Entities);
    }

    [Fact]
    public async Task Invalidation_IsAtomicJournaledAndIdempotent()
    {
        await using var project = await TestProject.CreateAsync();
        var committed = await project.Store.CompareAndSwapAsync(Request("seed-invalidation", "valid"));
        Assert.True(committed.Committed);
        var request = new InvalidationRequest(
            "invalidate-task-1",
            "project-1",
            new[] { new StateKey("project-1", EntityKinds.Task, "task-1") },
            2,
            "GOAL_CHANGED");

        var first = await project.Store.InvalidateAsync(request);
        var retry = await project.Store.InvalidateAsync(request);
        var conflictingRetry = await project.Store.InvalidateAsync(request with { GoalRevision = 3 });
        Assert.Equal("COMMITTED", first.Status);
        Assert.Equal(1, first.Affected);
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
        Assert.Equal(1, retry.Affected);
        Assert.True(conflictingRetry.Conflict);
        var state = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Task, "task-1"));
        Assert.Equal(StateLifecycle.Stale, state?.Lifecycle);
        Assert.Equal(2, state?.GoalRevision);
        Assert.Equal(2, (await project.Store.GetMetricsAsync()).Events);
    }

    [Fact]
    public async Task EventAppend_AllowsExactRetryButRejectsConflictingFacts()
    {
        await using var project = await TestProject.CreateAsync();
        var timestamp = DateTimeOffset.UtcNow;
        var engineeringEvent = new EngineeringEvent("event-1", "project-1", "TRACE", null, null, 0, "TRACE", "{}", timestamp);
        Assert.Equal(0, await project.Store.AppendEventAsync(engineeringEvent));
        Assert.Equal(0, await project.Store.AppendEventAsync(engineeringEvent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.Store.AppendEventAsync(engineeringEvent with { PayloadJson = "{\"different\":true}" }));
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Events);
    }

    [Fact]
    public async Task BatchCompareAndSwap_CommitsTaskLeaseAndOwnershipAtOneGeneration()
    {
        await using var project = await TestProject.CreateAsync();
        var mutations = new[]
        {
            Mutation(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
            Mutation(EntityKinds.RuntimeLease, "task-1", "{\"owner\":\"runtime-1\"}"),
            Mutation(EntityKinds.Ownership, "task-1", "{\"writer\":\"runtime-1\"}")
        };
        var request = new BatchTransactionRequest("batch-start", mutations, "TASK_STARTED", "TASK_RUNTIME_BOUND", "{}");
        var result = await project.Store.CompareAndSwapBatchAsync(request);
        var retry = await project.Store.CompareAndSwapBatchAsync(request);
        Assert.True(result.Committed);
        Assert.Equal(3, result.Snapshots.Count);
        Assert.Single(result.Snapshots.Select(static snapshot => snapshot.Generation).Distinct());
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
        Assert.Equal(3, (await project.Store.GetMetricsAsync()).Entities);
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Events);
    }

    [Fact]
    public async Task BatchCompareAndSwap_RejectsWholeBatchWhenOneMemberIsStale()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("batch-seed", "seed"))).Committed);
        var request = new BatchTransactionRequest(
            "batch-stale",
            new[]
            {
                Mutation(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
                Mutation(EntityKinds.RuntimeLease, "task-1", "{\"owner\":\"runtime-1\"}")
            },
            "TASK_STARTED",
            "TASK_RUNTIME_BOUND",
            "{}");
        var result = await project.Store.CompareAndSwapBatchAsync(request);
        Assert.True(result.Conflict);
        Assert.Equal("STALE_CONTEXT", result.Status);
        Assert.Null(await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1")));
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Entities);
    }

    [Fact]
    public async Task ReasoningSnapshot_HoldsNoTransactionOrWriterLock()
    {
        await using var project = await TestProject.CreateAsync();
        var coordinator = new StateCoordinator(project.Store);
        _ = await coordinator.ReadForReasoningAsync(new StateKey("project-1", EntityKinds.Task, "task-1"));
        var commit = project.Store.CompareAndSwapAsync(Request("op-during-reasoning", "committed"));
        var completed = await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(commit, completed);
        Assert.True((await commit).Committed);
    }

    [Fact]
    public async Task ArtifactStore_DeduplicatesAndDetectsCorruption()
    {
        await using var project = await TestProject.CreateAsync();
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var first = await artifacts.PutAsync(Put("same content"));
        var second = await artifacts.PutAsync(Put("same content"));
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.RelativeRef, second.RelativeRef);
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Artifacts);
        Assert.True((await artifacts.VerifyAsync(first)).Valid);
        var path = Path.Combine(project.Layout.AiRoot, first.RelativeRef.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, "tampered");
        Assert.Equal("SIZE_MISMATCH", (await artifacts.VerifyAsync(first)).Status);
    }

    [Fact]
    public async Task Doctor_BlocksBrokenArtifactReferenceWithoutDeletingUserState()
    {
        await using var project = await TestProject.CreateAsync();
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var record = await artifacts.PutAsync(Put("evidence"));
        File.Delete(Path.Combine(project.Layout.AiRoot, record.RelativeRef.Replace('/', Path.DirectorySeparatorChar)));
        var health = await project.Store.HealthCheckAsync();
        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, item => item.Code == "BROKEN_ARTIFACT_REF" && !item.Repairable);
    }

    [Fact]
    public async Task DoctorDetectsArtifactCorruptionAndBrokenDependency()
    {
        await using var project = await TestProject.CreateAsync();
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var record = await artifacts.PutAsync(Put("verified evidence"));
        var path = project.Layout.ResolveAiRelativePath(record.RelativeRef);
        await File.WriteAllTextAsync(path, "tampered evidence");
        var mutation = Mutation(EntityKinds.Task, "dependency-source", "{\"status\":\"RUNNING\"}");
        var edge = new DependencyEdge("project-1", EntityKinds.Task, "dependency-source", EntityKinds.Contract, "missing-contract", "CONSUMES", "missing-contract-edge");
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "broken-dependency", new[] { mutation }, "FIXTURE_CREATED", "FIXTURE_CREATED", "{}", new[] { edge }))).Committed);

        var health = await project.Store.HealthCheckAsync();

        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, static item => item.Code == "CORRUPT_ARTIFACT" && !item.Repairable);
        Assert.Contains(health.Findings, static item => item.Code == "BROKEN_DEPENDENCY" && !item.Repairable);
    }

    [Fact]
    public async Task DoctorRepairDropsOnlyInvalidProjectionAndGcRemovesAgedOrphanArtifact()
    {
        await using var project = await TestProject.CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={project.Layout.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO materialized_views(project_id,view_name,source_generation,payload_json,fingerprint,updated_at)
                VALUES('project-1','future-view',999,'{}','future','2026-01-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var projectionHealth = await project.Store.HealthCheckAsync();
        Assert.Equal("REPAIRABLE", projectionHealth.Status);
        Assert.Contains(projectionHealth.Findings, static item => item.Code == "CORRUPT_MATERIALIZED_VIEW" && item.Repairable);
        var repaired = await project.Coordinator.RepairAsync();
        Assert.Equal(1, repaired.Repaired);
        Assert.Equal("HEALTHY", repaired.Status);

        var orphanHash = new string('a', 64);
        var orphanDirectory = Path.Combine(project.Layout.ArtifactsRoot, "aa");
        Directory.CreateDirectory(orphanDirectory);
        var orphanPath = Path.Combine(orphanDirectory, orphanHash);
        await File.WriteAllTextAsync(orphanPath, "orphan");
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-2));
        var orphanHealth = await project.Store.HealthCheckAsync();
        Assert.Equal("REPAIRABLE", orphanHealth.Status);
        Assert.Contains(orphanHealth.Findings, static item => item.Code == "ORPHAN_ARTIFACT" && item.Repairable);
        var gc = await project.Coordinator.GarbageCollectAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(1, gc.OrphanArtifacts);
        Assert.False(File.Exists(orphanPath));
        Assert.Equal("HEALTHY", (await project.Store.HealthCheckAsync()).Status);
    }

    [Fact]
    public async Task MaterializedViewRejectsFutureGenerationBeforeWrite()
    {
        await using var project = await TestProject.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.Coordinator.MaterializeAsync(new MaterializationRequest(
            "project-1", "future-view", 1, "{}", "future")));
    }

    private static TransactionRequest Request(string operationId, string value) => new(
        operationId,
        new StateMutation(
            new StateKey("project-1", EntityKinds.Task, "task-1"),
            0,
            1,
            StateLifecycle.Valid,
            "STATE_COORDINATOR",
            "3.0.0",
            "source-1",
            "deps-1",
            $"{{\"value\":\"{value}\"}}"),
        "TASK_CREATED",
        "TASK_CREATED",
        "{}");

    private static StateMutation Mutation(string kind, string id, string payload) => new(
        new StateKey("project-1", kind, id),
        0,
        1,
        StateLifecycle.Valid,
        "STATE_COORDINATOR",
        "3.0.0",
        "source-1",
        "deps-1",
        payload);

    private static Task<TransactionResult> Commit(
        StateCoordinator coordinator,
        ImmutableReasoningSnapshot snapshot,
        string operationId,
        string value) => coordinator.CommitAsync(
            snapshot,
            operationId,
            new StateMutation(
                new StateKey("project-1", EntityKinds.Task, "task-1"),
                snapshot.State?.Generation ?? 0,
                1,
                StateLifecycle.Valid,
                "STATE_COORDINATOR",
                "3.0.0",
                "source-1",
                "deps-1",
                $"{{\"value\":\"{value}\"}}"),
            "TASK_CREATED",
            "TASK_CREATED",
            "{}");

    private static ArtifactPutRequest Put(string content) => new(
        "project-1",
        "TEST_EVIDENCE",
        new MemoryStream(Encoding.UTF8.GetBytes(content)),
        "task-1",
        "evidence-1",
        "source-1",
        RetentionTier.Warm);
}

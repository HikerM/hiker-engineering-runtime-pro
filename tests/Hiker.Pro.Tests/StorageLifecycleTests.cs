using System.Text;
using Hiker.Pro.Artifacts;
using Hiker.Pro.Context;
using Hiker.Pro.State;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StorageLifecycleTests
{
    [Fact]
    public async Task RetrievalIntegrity_RejectsStaleSupersededLegacyAndNewerSimilarFacts()
    {
        await using var project = await TestProject.CreateAsync();
        var mutations = new[]
        {
            State(EntityKinds.Goal, "goal-1", "{\"revision\":15}"),
            State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
            State(EntityKinds.Decision, "decision-current", "{\"text\":\"current contract\"}"),
            State(EntityKinds.Decision, "decision-similar-old", "{\"text\":\"EXACT QUERY current contract current contract\"}", StateLifecycle.Superseded),
            State(EntityKinds.Evidence, "evidence-stale", "{\"status\":\"PASS\"}", StateLifecycle.Stale),
            State(EntityKinds.Decision, "legacy-newer", "{\"text\":\"EXACT QUERY current contract\"}", authority: "COMMUNITY_5_18_IMPORT")
        };
        var edges = mutations.Skip(2).Select(value => new DependencyEdge(
            "project-storage", EntityKinds.Task, "task-1", value.Key.Kind, value.Key.EntityId, "DEPENDS_ON", $"edge-{value.Key.EntityId}")).ToArray();
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "retrieval-seed", mutations, "CONTEXT_CREATED", "TEST", "{}", edges))).Committed);

        var resolver = new ContextResolver(project.Store);
        var first = await resolver.ResolveAsync(new ContextRequest("project-storage", "task-1", "goal-1"));
        var second = await resolver.ResolveAsync(new ContextRequest("project-storage", "task-1", "goal-1"));

        Assert.Contains(first.Items, static value => value.EntityId == "decision-current" && value.AuthorityClass == RetrievalAuthority.AuthoritativeCurrent);
        Assert.DoesNotContain(first.Items, static value => value.EntityId is "decision-similar-old" or "evidence-stale" or "legacy-newer");
        Assert.Equal(0, first.RetrievalReceipt!.StaleRejected);
        Assert.Equal(0, first.RetrievalReceipt.SupersededRejected);
        Assert.Equal(1, first.RetrievalReceipt.AuthorityConflicts);
        Assert.Equal(0, first.RetrievalReceipt.ArchiveReads);
        Assert.Equal(0, first.RetrievalReceipt.ColdReads);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.RetrievalReceipt.SelectedItems, second.RetrievalReceipt!.SelectedItems);
        Assert.Equal(first.RetrievalReceipt.ReceiptFingerprint, second.RetrievalReceipt.ReceiptFingerprint);
        Assert.Equal(first.EncodedBytes, second.EncodedBytes);
    }

    [Fact]
    public async Task EntityRetention_PhysicallyMovesHistoricalStateAndGraphOutOfHotPath()
    {
        await using var project = await TestProject.CreateAsync();
        var mutations = new[]
        {
            State(EntityKinds.Goal, "goal-1", "{\"revision\":15}"),
            State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
            State(EntityKinds.Decision, "decision-old", "{\"status\":\"SUPERSEDED\"}", StateLifecycle.Superseded),
            State(EntityKinds.Evidence, "evidence-old", "{\"status\":\"STALE\"}", StateLifecycle.Stale)
        };
        var edges = new[]
        {
            new DependencyEdge("project-storage", EntityKinds.Task, "task-1", EntityKinds.Decision, "decision-old", "DEPENDS_ON", "edge-old"),
            new DependencyEdge("project-storage", EntityKinds.Task, "task-1", EntityKinds.Evidence, "evidence-old", "PROVEN_BY", "edge-evidence")
        };
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "entity-retention-seed", mutations, "STATE_CREATED", "TEST", "{}", edges))).Committed);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        var result = await project.Coordinator.RetainEntitiesAsync(
            "entity-retention-1", "project-storage", cutoff);
        var replay = await project.Coordinator.RetainEntitiesAsync(
            "entity-retention-1", "project-storage", cutoff);

        Assert.Equal(2, result.ArchivedEntities);
        Assert.Equal(2, result.ArchivedDependencies);
        Assert.True(replay.Idempotent);
        Assert.Null(await project.Store.ReadAsync(new StateKey("project-storage", EntityKinds.Decision, "decision-old")));
        var archived = await project.Store.ReadArchivedStateAsync(new StateKey("project-storage", EntityKinds.Decision, "decision-old"));
        Assert.NotNull(archived);
        Assert.Equal(RetentionTier.Archived, archived!.Retention);
        Assert.Empty(await project.Store.QueryDependenciesBoundedAsync("project-storage", EntityKinds.Task, "task-1", true, 32));
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("project-storage", EntityKinds.Task, "task-1")));
    }

    [Fact]
    public async Task ArtifactReachability_PreservesEvidenceCollectsUnreachableAndBacksUpIncrementally()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "artifact-roots", new[]
            {
                State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
                State(EntityKinds.Evidence, "evidence-1", "{\"status\":\"PASS\"}")
            }, "ROOTS_CREATED", "TEST", "{}"))).Committed);
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var retained = await artifacts.PutAsync(Put("release evidence", null, "evidence-1"));
        var orphan = await artifacts.PutAsync(Put("unreachable scratch", null, null));

        var reachability = await project.Coordinator.AnalyzeArtifactReachabilityAsync(
            "project-storage", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Contains(reachability, value => value.Artifact.Sha256 == retained.Sha256 && value.Reachability == StorageReachability.Retained);
        Assert.Contains(reachability, value => value.Artifact.Sha256 == orphan.Sha256 && value.Reachability == StorageReachability.Unreachable);

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        var collected = await project.Coordinator.CollectUnreachableArtifactsAsync(
            "artifact-gc-1", "project-storage", cutoff);
        var replay = await project.Coordinator.CollectUnreachableArtifactsAsync(
            "artifact-gc-1", "project-storage", cutoff);
        Assert.Equal(1, collected.DeletedFiles);
        Assert.True(replay.Idempotent);
        Assert.True(File.Exists(project.Layout.ResolveAiRelativePath(retained.RelativeRef)));
        Assert.False(File.Exists(project.Layout.ResolveAiRelativePath(orphan.RelativeRef)));

        var backup = new IncrementalArtifactBackupService(project.Layout, project.Coordinator);
        var first = await backup.CreateAsync("project-storage");
        var second = await backup.CreateAsync("project-storage");
        Assert.Equal(1, first.ArtifactsCopied);
        Assert.Equal(0, first.ArtifactsReused);
        Assert.Equal(0, second.ArtifactsCopied);
        Assert.Equal(1, second.ArtifactsReused);
        Assert.True(File.Exists(first.ManifestPath));
    }

    [Fact]
    public async Task FastDoctor_ReusesVerificationReceiptsWhileDeepDoctorHashesAllArtifacts()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("doctor-task", EntityKinds.Task, "task-1"))).Committed);
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        _ = await artifacts.PutAsync(Put("immutable evidence", "task-1", null));

        var fast = await project.Store.StorageHealthCheckAsync("project-storage", StorageHealthMode.Fast);
        var fastAgain = await project.Store.StorageHealthCheckAsync("project-storage", StorageHealthMode.Fast);
        var deep = await project.Store.StorageHealthCheckAsync("project-storage", StorageHealthMode.Deep);

        Assert.Equal("HEALTHY", fast.Status);
        Assert.Equal(0, fast.ArtifactFilesScanned);
        Assert.Equal(0, fastAgain.ArtifactFilesScanned);
        Assert.Equal(1, deep.ArtifactFilesScanned);
        Assert.True(deep.ArtifactBytesHashed > 0);
    }

    [Fact]
    public async Task EventRollup_PreservesSnapshotExplicitHistoryAndArchivedEventIdempotency()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapAsync(Request("event-task", EntityKinds.Task, "task-1"))).Committed);
        for (var index = 0; index < 24; index++)
        {
            await project.Coordinator.AppendEventAsync(Event(index));
        }
        var current = await project.Store.ReadAsync(new StateKey("project-storage", EntityKinds.Task, "task-1"));
        Assert.NotNull(current);
        var updated = Request("event-task-updated", EntityKinds.Task, "task-1") with
        {
            Mutation = Request("event-task-updated", EntityKinds.Task, "task-1").Mutation with
            {
                ExpectedGeneration = current.Generation,
                PayloadJson = "{\"status\":\"UPDATED\"}"
            }
        };
        Assert.True((await project.Store.CompareAndSwapAsync(updated)).Committed);

        var rollup = await project.Coordinator.RollupEventsAsync(new EventRollupRequest(
            "event-rollup-1", "project-storage", KeepRecentEvents: 5, MaximumEvents: 100));
        var history = await project.Store.ReadEventSegmentAsync("project-storage", rollup.SegmentId!);
        var recovery = await project.Store.ReconstructSnapshotAsync("project-storage", rollup.SnapshotId!);
        var replayGeneration = await project.Coordinator.AppendEventAsync(Event(0));
        var metrics = await project.Store.GetStorageMetricsAsync("project-storage");
        var remainingDeltas = await ScalarAsync(
            project.Layout.DatabasePath,
            "SELECT COUNT(*) FROM state_deltas delta JOIN event_segments segment ON segment.project_id=delta.project_id WHERE delta.project_id='project-storage' AND delta.generation > segment.to_generation;");

        Assert.Equal("COMPLETE", rollup.Status);
        Assert.Equal(21, rollup.EventsArchived);
        Assert.Equal(21, history.Count);
        Assert.Equal(1, replayGeneration);
        Assert.Equal(5, metrics.HotEvents);
        Assert.Equal(1, Convert.ToInt32(remainingDeltas, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(recovery.Entities, static value => value.Key.EntityId == "task-1");
    }

    [Fact]
    public async Task OperationRetention_KeepsHotWindowAndArchivedReceiptsRemainIdempotent()
    {
        await using var project = await TestProject.CreateAsync();
        var requests = Enumerable.Range(0, 8)
            .Select(index => Request($"operation-{index}", EntityKinds.Task, $"task-{index}"))
            .ToArray();
        foreach (var request in requests) Assert.True((await project.Store.CompareAndSwapAsync(request)).Committed);

        var retained = await project.Coordinator.RetainOperationsAsync(
            "operation-retention-1", "project-storage", DateTimeOffset.UtcNow.AddMinutes(1), keepRecent: 2);
        var receipt = await project.Store.ReadOperationReceiptAsync("operation-0");
        var replay = await project.Store.CompareAndSwapAsync(requests[0]);

        Assert.Equal(6, retained.Archived);
        Assert.NotNull(receipt);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(0, retained.Active);
        Assert.InRange(retained.RecentCompleted, 1, 3);
    }

    [Fact]
    public async Task MigrationInventory_ReusesUnchangedSegmentsAndDetectsNestedChanges()
    {
        await using var project = await TestProject.CreateAsync();
        var requirements = Path.Combine(project.Layout.AiRoot, "requirements", "nested");
        Directory.CreateDirectory(requirements);
        var source = Path.Combine(requirements, "requirement.json");
        await File.WriteAllTextAsync(source, "{\"requirement_id\":\"REQ-1\"}");

        var first = await project.Coordinator.InventoryLegacyStorageAsync("project-storage");
        var metrics = await project.Store.GetStorageMetricsAsync("project-storage");
        var second = await project.Coordinator.InventoryLegacyStorageAsync("project-storage");
        await File.WriteAllTextAsync(source, "{\"requirement_id\":\"REQ-2\"}");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        var changed = await project.Coordinator.InventoryLegacyStorageAsync("project-storage");

        Assert.Equal(1, first.ScannedSegments);
        Assert.Equal(new FileInfo(source).Length, metrics.PreservedBytes);
        Assert.True(metrics.AiTotalBytes >= metrics.StateDbBytes + metrics.PreservedBytes);
        Assert.Equal(1, second.ReusedSegments);
        Assert.Equal(0, second.ChangedSegments);
        Assert.Equal(1, changed.ChangedSegments);
        Assert.NotEqual(first.Segments[0].Fingerprint, changed.Segments[0].Fingerprint);
    }

    [Fact]
    public async Task LargeColdHistory_DoesNotExpandContextOrStateReads()
    {
        await using var project = await TestProject.CreateAsync();
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "bounded-context", new[]
            {
                State(EntityKinds.Goal, "goal-1", "{\"revision\":15}"),
                State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
                State(EntityKinds.Decision, "decision-current", "{\"status\":\"CURRENT\"}")
            }, "CONTEXT_CREATED", "TEST", "{}", new[]
            {
                new DependencyEdge("project-storage", EntityKinds.Task, "task-1", EntityKinds.Decision, "decision-current", "DEPENDS_ON", "edge-current")
            }))).Committed);
        var resolver = new ContextResolver(project.Store);
        var before = await resolver.ResolveAsync(new ContextRequest("project-storage", "task-1", "goal-1"));
        for (var batch = 0; batch < 10; batch++)
        {
            var history = Enumerable.Range(batch * 100, 100)
                .Select(index => State(EntityKinds.Decision, $"history-{index:D4}", $"{{\"value\":{index}}}", StateLifecycle.Archived, RetentionTier.Cold))
                .ToArray();
            Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"cold-history-{batch}", history, "HISTORY_IMPORTED", "TEST", "{}"))).Committed);
        }
        var after = await resolver.ResolveAsync(new ContextRequest("project-storage", "task-1", "goal-1"));
        Assert.Equal(before.StateReads, after.StateReads);
        Assert.Equal(before.EncodedBytes, after.EncodedBytes);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(0, after.RetrievalReceipt!.ArchiveReads);
    }

    private static async Task<object?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static ArtifactPutRequest Put(string content, string? taskId, string? evidenceId) => new(
        "project-storage", "TEST_EVIDENCE", new MemoryStream(Encoding.UTF8.GetBytes(content)), taskId, evidenceId, "source-artifact", RetentionTier.Warm);

    private static EngineeringEvent Event(int index) => new(
        $"stream-event-{index:D4}", "project-storage", "TASK_PROGRESS", EntityKinds.Task, "task-1", 1,
        "BOUNDED_EVENT", "{}", DateTimeOffset.UnixEpoch.AddSeconds(index));

    private static TransactionRequest Request(string operationId, string kind, string id) => new(
        operationId,
        State(kind, id, "{\"status\":\"VALID\"}"),
        "STATE_UPDATED",
        "TEST",
        "{}");

    private static StateMutation State(
        string kind,
        string id,
        string payload,
        StateLifecycle lifecycle = StateLifecycle.Valid,
        RetentionTier retention = RetentionTier.Hot,
        string authority = "STATE_COORDINATOR") => new(
        new StateKey("project-storage", kind, id),
        0,
        15,
        lifecycle,
        authority,
        "3.0.0",
        $"source-{id}",
        $"dependency-{id}",
        payload,
        retention);
}

using System.Diagnostics;
using Hiker.Pro.Context;
using Hiker.Pro.State;
using Hiker.Pro.Storage;

namespace Hiker.Pro.Verification;

internal sealed class StorageLifecycleScenario
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-storage-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var checks = new List<VerificationCheck>();
        try
        {
            var layout = new ProjectLayout(root);
            await using var store = new SqliteStateStore(layout);
            await store.InitializeAsync().ConfigureAwait(false);
            var state = new StateCoordinator(store);
            const string projectId = "storage-performance";
            _ = await new ProjectBootstrapService(layout, store, state).InitializeAsync(projectId).ConfigureAwait(false);
            await SeedCurrentAsync(store, projectId).ConfigureAwait(false);
            var resolver = new ContextResolver(store);
            var baselineContext = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            var baselineLatency = await MeasureAsync(80, () => resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1"))).ConfigureAwait(false);
            var baselineDoctor = await MeasureAsync(40, () => store.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast)).ConfigureAwait(false);

            const int historicalFacts = 20_000;
            await SeedHistoricalAsync(store, projectId, historicalFacts).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
            var retained = await state.RetainEntitiesAsync("storage-verification-entities", projectId, cutoff).ConfigureAwait(false);
            var agedContext = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            var agedLatency = await MeasureAsync(80, () => resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1"))).ConfigureAwait(false);
            var agedDoctor = await MeasureAsync(40, () => store.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast)).ConfigureAwait(false);
            var doctor = await store.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast).ConfigureAwait(false);
            var rollup = await state.RollupEventsAsync(new EventRollupRequest(
                "storage-verification-events", projectId, KeepRecentEvents: 10, MaximumEvents: 100_000)).ConfigureAwait(false);
            var recovery = await store.ReconstructSnapshotAsync(projectId, rollup.SnapshotId!).ConfigureAwait(false);
            var metrics = await store.GetStorageMetricsAsync(projectId).ConfigureAwait(false);

            var hotBounded = baselineContext.StateReads == agedContext.StateReads &&
                             baselineContext.EncodedBytes == agedContext.EncodedBytes &&
                             baselineContext.Fingerprint == agedContext.Fingerprint &&
                             agedContext.RetrievalReceipt?.ArchiveReads == 0 &&
                             agedContext.RetrievalReceipt?.ColdReads == 0 &&
                             Stable(baselineLatency.P95Ms, agedLatency.P95Ms);
            checks.Add(new VerificationCheck(
                "STORAGE_SYNTHETIC_HISTORY_HOT_PATH",
                hotBounded ? "PASS" : "BLOCKED",
                "SIMULATION_PROVEN",
                "SYNTHETIC metadata represents 100MB/1GB/5GB/20GB logical history; the largest tier uses 20,000 physically archived facts and is not a 20GB real-file claim.",
                new Dictionary<string, object?>
                {
                    ["scale_labels"] = new[] { "100MB_SYNTHETIC", "1GB_SYNTHETIC", "5GB_SYNTHETIC", "20GB_SYNTHETIC" },
                    ["historical_facts"] = historicalFacts,
                    ["physically_archived_facts"] = retained.ArchivedEntities,
                    ["context_bytes_before"] = baselineContext.EncodedBytes,
                    ["context_bytes_after"] = agedContext.EncodedBytes,
                    ["state_reads_before"] = baselineContext.StateReads,
                    ["state_reads_after"] = agedContext.StateReads,
                    ["archive_reads"] = agedContext.RetrievalReceipt?.ArchiveReads,
                    ["context_p50_before_ms"] = baselineLatency.P50Ms,
                    ["context_p95_before_ms"] = baselineLatency.P95Ms,
                    ["context_p50_after_ms"] = agedLatency.P50Ms,
                    ["context_p95_after_ms"] = agedLatency.P95Ms
                }));
            var fastDoctor = doctor.ArtifactFilesScanned == 0 && doctor.ArchiveFilesScanned == 0 &&
                             Stable(baselineDoctor.P95Ms, agedDoctor.P95Ms);
            checks.Add(new VerificationCheck(
                "STORAGE_FAST_DOCTOR_BOUNDED",
                fastDoctor ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Fast Doctor uses SQLite/index receipts and does not open cold archives or re-hash unchanged artifacts.",
                new Dictionary<string, object?>
                {
                    ["artifact_files_scanned"] = doctor.ArtifactFilesScanned,
                    ["archive_files_scanned"] = doctor.ArchiveFilesScanned,
                    ["doctor_p95_before_ms"] = baselineDoctor.P95Ms,
                    ["doctor_p95_after_ms"] = agedDoctor.P95Ms,
                    ["indicators"] = doctor.HealthIndicators
                }));
            var recoverable = rollup.Status == "COMPLETE" && rollup.EventsArchived > 0 &&
                              recovery.Entities.Any(static value => value.Key.Kind == EntityKinds.Task && value.Key.EntityId == "task-1") &&
                              metrics.HotEvents <= 10;
            checks.Add(new VerificationCheck(
                "STORAGE_ROLLUP_SNAPSHOT_RECOVERY",
                recoverable ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Verified Snapshot + post-snapshot delta projection recovers current state without replaying archived events.",
                new Dictionary<string, object?>
                {
                    ["events_archived"] = rollup.EventsArchived,
                    ["hot_events"] = metrics.HotEvents,
                    ["snapshot_generation"] = recovery.SnapshotGeneration,
                    ["current_generation"] = recovery.CurrentGeneration,
                    ["deltas_applied"] = recovery.DeltasApplied,
                    ["state_db_bytes"] = metrics.StateDbBytes,
                    ["archive_bytes"] = metrics.ArchiveBytes
                }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
        return checks;
    }

    private static async Task SeedCurrentAsync(SqliteStateStore store, string projectId)
    {
        _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "storage-current",
            new[]
            {
                Mutation(projectId, EntityKinds.Goal, "goal-1", StateLifecycle.Valid, RetentionTier.Hot, "{\"revision\":1}"),
                Mutation(projectId, EntityKinds.Task, "task-1", StateLifecycle.Valid, RetentionTier.Hot, "{\"status\":\"RUNNING\"}"),
                Mutation(projectId, EntityKinds.Decision, "decision-current", StateLifecycle.Valid, RetentionTier.Warm, "{\"status\":\"CURRENT\"}")
            },
            "CURRENT_STATE_CREATED",
            "VERIFICATION",
            "{}",
            new[] { new DependencyEdge(projectId, EntityKinds.Task, "task-1", EntityKinds.Decision, "decision-current", "DEPENDS_ON", "edge-current") })).ConfigureAwait(false);
    }

    private static async Task SeedHistoricalAsync(SqliteStateStore store, string projectId, int count)
    {
        for (var offset = 0; offset < count; offset += 256)
        {
            var mutations = Enumerable.Range(offset, Math.Min(256, count - offset))
                .Select(index => Mutation(projectId, EntityKinds.Decision, $"history-{index:D6}", StateLifecycle.Superseded, RetentionTier.Cold,
                    $"{{\"status\":\"SUPERSEDED\",\"logical_history_bytes\":{1_073_741}}}"))
                .ToArray();
            _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"storage-history-{offset:D6}", mutations, "HISTORY_IMPORTED", "VERIFICATION", "{}")).ConfigureAwait(false);
        }
    }

    private static StateMutation Mutation(
        string projectId,
        string kind,
        string id,
        StateLifecycle lifecycle,
        RetentionTier retention,
        string payload) => new(
        new StateKey(projectId, kind, id), 0, 1, lifecycle, "VERIFICATION", Hiker.Pro.Release.BuildVersionFacts.Current.StateSchema,
        $"source-{id}", $"dependency-{id}", payload, retention);

    private static async Task<Latency> MeasureAsync<T>(int iterations, Func<Task<T>> action)
    {
        var values = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var timer = Stopwatch.StartNew();
            _ = await action().ConfigureAwait(false);
            timer.Stop();
            values[index] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(values);
        return new Latency(values[(int)Math.Floor((values.Length - 1) * 0.50)], values[(int)Math.Floor((values.Length - 1) * 0.95)]);
    }

    private static bool Stable(double baseline, double current) => current <= baseline * 4.0 || current - baseline <= 8.0;
    private sealed record Latency(double P50Ms, double P95Ms);
}

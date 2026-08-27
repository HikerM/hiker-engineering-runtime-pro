using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Hiker.Pro.Storage;

namespace Hiker.Pro.Verification;

internal sealed class StorageFieldScenario(VerificationOptions options)
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var root = options.FieldRoot ?? throw new ArgumentException("storage-field requires --field-root <isolated project copy>");
        var layout = new ProjectLayout(root);
        if (!Directory.Exists(layout.AiRoot)) throw new DirectoryNotFoundException("Field root has no .ai directory");
        var physical = InspectPhysicalAi(layout.AiRoot);
        if (physical.Bytes < 1L * 1024 * 1024 * 1024) throw new InvalidDataException("Storage field requires a real .ai copy of at least 1GB");

        await using var store = new SqliteStateStore(layout);
        await store.InitializeAsync().ConfigureAwait(false);
        var coordinator = new StateCoordinator(store);
        var bootstrap = new ProjectBootstrapService(layout, store, coordinator);
        var identity = await store.InspectProjectIdentityAsync().ConfigureAwait(false);
        var projectId = AssertSingle(identity.ActiveProjectIds, "project identity");
        var goal = AssertSingle((await store.ReadAsync(projectId, EntityKinds.Goal, StateLifecycle.Valid, 4).ConfigureAwait(false))
            .Where(static state => state.Authority == LiveSessionAdoptionService.Authority).ToArray(), "current goal");
        var task = AssertSingle((await store.ReadAsync(projectId, EntityKinds.Task, StateLifecycle.Valid, 8).ConfigureAwait(false))
            .Where(state => state.Authority == LiveSessionAdoptionService.Authority && BelongsToGoal(state, goal.Key.EntityId)).ToArray(), "current task");
        var decisionsBefore = await CountKindAsync(store, projectId, EntityKinds.Decision).ConfigureAwait(false);
        var evidenceBefore = await CountKindAsync(store, projectId, EntityKinds.Evidence).ConfigureAwait(false);
        var inventoryTimer = Stopwatch.StartNew();
        var inventory = await coordinator.InventoryLegacyStorageAsync(projectId).ConfigureAwait(false);
        inventoryTimer.Stop();
        var preservedBytes = inventory.Segments.Sum(static segment => segment.Bytes);
        var inventoryReplay = await coordinator.InventoryLegacyStorageAsync(projectId).ConfigureAwait(false);

        var resolver = new ContextResolver(store);
        var contextLatency = await MeasureAsync(40, () => resolver.ResolveAsync(new ContextRequest(projectId, task.Key.EntityId, goal.Key.EntityId))).ConfigureAwait(false);
        var doctorLatency = await MeasureAsync(20, () => store.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast)).ConfigureAwait(false);
        var context = await resolver.ResolveAsync(new ContextRequest(projectId, task.Key.EntityId, goal.Key.EntityId)).ConfigureAwait(false);
        var doctor = await store.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast).ConfigureAwait(false);
        var deep = await store.StorageHealthCheckAsync(projectId, StorageHealthMode.Deep).ConfigureAwait(false);
        var adoptionService = new LiveSessionAdoptionService(
            layout,
            store,
            coordinator,
            bootstrap,
            new ProcessIdentityProbe());
        var adoptionRequest = new LiveAdoptionRequest(
                "codex",
                "storage-field-verification",
                "RECOVERY_CHECKPOINT");
        var adoption = await adoptionService.AdoptAsync(adoptionRequest).ConfigureAwait(false);
        var retryRequest = new LiveAdoptionRequest(
                "codex",
                "storage-field-verification",
                "TURN_TERMINAL");
        var retry = await adoptionService.AdoptAsync(retryRequest).ConfigureAwait(false);
        var attachLatency = await MeasureAsync(20, () => adoptionService.AdoptAsync(retryRequest)).ConfigureAwait(false);
        var lifecycleService = new LiveTurnLifecycleService(store, coordinator, bootstrap);
        var checkpointRequest = new LiveTurnCheckpointRequest(
                "codex",
                "storage-field-verification",
                ChangedSurfaces: new[] { "storage-field-copy" },
                EvidenceRefs: new[] { "verification://storage-field" },
                TestRefs: new[] { "GA-5.19-STORAGE-LIFECYCLE-001" });
        var checkpoint = await lifecycleService.CheckpointAsync(checkpointRequest).ConfigureAwait(false);
        var checkpointLatency = await MeasureAsync(20, () => lifecycleService.CheckpointAsync(checkpointRequest)).ConfigureAwait(false);
        var goalAfter = await store.ReadAsync(goal.Key).ConfigureAwait(false);
        var taskAfter = await store.ReadAsync(task.Key).ConfigureAwait(false);
        var decisionsAfter = await CountKindAsync(store, projectId, EntityKinds.Decision).ConfigureAwait(false);
        var evidenceAfter = await CountKindAsync(store, projectId, EntityKinds.Evidence).ConfigureAwait(false);
        var evidence = AssertSingle(
            (await store.ReadAsync(projectId, EntityKinds.Evidence, StateLifecycle.Valid, 1).ConfigureAwait(false)).ToArray(),
            "recent evidence lookup");
        var statusLatency = await MeasureAsync(20, async () =>
        {
            var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read).ConfigureAwait(false);
            var storage = await coordinator.StorageHealthCheckAsync(projectId, StorageHealthMode.Fast).ConfigureAwait(false);
            var health = await new Hiker.Pro.Diagnostics.AdvancedDoctor(store, new ProcessIdentityProbe())
                .CheckProjectAsync(projectId, storage).ConfigureAwait(false);
            var stateMetrics = await store.GetMetricsAsync().ConfigureAwait(false);
            return (active, health, stateMetrics);
        }).ConfigureAwait(false);
        var evidenceLatency = await MeasureAsync(40, () => store.ReadAsync(evidence.Key)).ConfigureAwait(false);
        var graphLatency = await MeasureAsync(40, () => store.QueryDependenciesBoundedAsync(
            projectId,
            EntityKinds.Task,
            task.Key.EntityId,
            outgoing: true,
            limit: 32)).ConfigureAwait(false);
        var goalLatency = await MeasureAsync(40, () => store.ReadAsync(goal.Key)).ConfigureAwait(false);
        var metrics = await store.GetStorageMetricsAsync(projectId).ConfigureAwait(false);

        var identityStable = goalAfter is not null && taskAfter is not null &&
                             goalAfter.Key == goal.Key && taskAfter.Key == task.Key &&
                             goalAfter.GoalRevision == taskAfter.GoalRevision;
        var hotPath = context.RetrievalReceipt is { ArchiveReads: 0, ColdReads: 0 } &&
                      context.EncodedBytes <= 16_384 &&
                      doctor.ArtifactFilesScanned <= 256 && doctor.ArchiveFilesScanned == 0;
        var lifecycle = adoption.Status is LiveAdoptionStatuses.Adopted or LiveAdoptionStatuses.AlreadyAdopted &&
                        retry.Status == LiveAdoptionStatuses.AlreadyAdopted && retry.StateWrites == 0 &&
                        checkpoint.Status is LiveTurnCheckpointStatuses.Checkpointed or LiveTurnCheckpointStatuses.AlreadyCheckpointed;
        var preserved = decisionsAfter == decisionsBefore && evidenceAfter >= evidenceBefore &&
                        physical.Files > 0 && preservedBytes > 0 && metrics.PreservedBytes == preservedBytes;
        var inventoryIncremental = inventoryReplay.ChangedSegments == 0 && inventoryReplay.ReusedSegments == inventory.Segments.Count;
        var pass = identityStable && hotPath && lifecycle && preserved && inventoryIncremental &&
                   doctor.Status != "BLOCKED" && deep.Status != "BLOCKED";
        return new[]
        {
            new VerificationCheck(
                "STORAGE_REAL_PROJECT_FIELD",
                pass ? "PASS" : "BLOCKED",
                "REAL_PROJECT_COPY",
                "A read-write isolated copy of a real >=1GB .ai project was inventoried, resumed, resolved, checked, checkpointed, and rechecked without changing its engineering identity.",
                new Dictionary<string, object?>
                {
                    ["field_bytes"] = physical.Bytes,
                    ["field_files"] = physical.Files,
                    ["inventory_files"] = inventory.Segments.Sum(static segment => segment.Files),
                    ["inventory_preserved_bytes"] = preservedBytes,
                    ["inventory_first_ms"] = inventoryTimer.Elapsed.TotalMilliseconds,
                    ["inventory_reused_segments"] = inventoryReplay.ReusedSegments,
                    ["project_id_stable"] = identityStable,
                    ["goal_id_stable"] = goalAfter?.Key.EntityId == goal.Key.EntityId,
                    ["task_id_stable"] = taskAfter?.Key.EntityId == task.Key.EntityId,
                    ["decisions_before"] = decisionsBefore,
                    ["decisions_after"] = decisionsAfter,
                    ["evidence_before"] = evidenceBefore,
                    ["evidence_after"] = evidenceAfter,
                    ["context_bytes"] = context.EncodedBytes,
                    ["context_state_reads"] = context.StateReads,
                    ["context_archive_reads"] = context.RetrievalReceipt?.ArchiveReads,
                    ["context_p50_ms"] = contextLatency.P50Ms,
                    ["context_p95_ms"] = contextLatency.P95Ms,
                    ["context_p99_ms"] = contextLatency.P99Ms,
                    ["status_p50_ms"] = statusLatency.P50Ms,
                    ["status_p95_ms"] = statusLatency.P95Ms,
                    ["status_p99_ms"] = statusLatency.P99Ms,
                    ["doctor_p50_ms"] = doctorLatency.P50Ms,
                    ["doctor_p95_ms"] = doctorLatency.P95Ms,
                    ["doctor_p99_ms"] = doctorLatency.P99Ms,
                    ["attach_resume_p50_ms"] = attachLatency.P50Ms,
                    ["attach_resume_p95_ms"] = attachLatency.P95Ms,
                    ["attach_resume_p99_ms"] = attachLatency.P99Ms,
                    ["checkpoint_p50_ms"] = checkpointLatency.P50Ms,
                    ["checkpoint_p95_ms"] = checkpointLatency.P95Ms,
                    ["checkpoint_p99_ms"] = checkpointLatency.P99Ms,
                    ["evidence_lookup_p50_ms"] = evidenceLatency.P50Ms,
                    ["evidence_lookup_p95_ms"] = evidenceLatency.P95Ms,
                    ["evidence_lookup_p99_ms"] = evidenceLatency.P99Ms,
                    ["blast_graph_p50_ms"] = graphLatency.P50Ms,
                    ["blast_graph_p95_ms"] = graphLatency.P95Ms,
                    ["blast_graph_p99_ms"] = graphLatency.P99Ms,
                    ["current_goal_p50_ms"] = goalLatency.P50Ms,
                    ["current_goal_p95_ms"] = goalLatency.P95Ms,
                    ["current_goal_p99_ms"] = goalLatency.P99Ms,
                    ["fast_doctor_artifact_scans"] = doctor.ArtifactFilesScanned,
                    ["fast_doctor_archive_scans"] = doctor.ArchiveFilesScanned,
                    ["deep_doctor_artifact_scans"] = deep.ArtifactFilesScanned,
                    ["deep_doctor_archive_scans"] = deep.ArchiveFilesScanned,
                    ["adoption_status"] = adoption.Status,
                    ["retry_status"] = retry.Status,
                    ["retry_writes"] = retry.StateWrites,
                    ["checkpoint_status"] = checkpoint.Status,
                    ["ai_total_bytes"] = metrics.AiTotalBytes,
                    ["state_db_bytes"] = metrics.StateDbBytes,
                    ["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64
                })
        };
    }

    private static (long Files, long Bytes) InspectPhysicalAi(string aiRoot)
    {
        long files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(aiRoot);
        while (pending.Count > 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Field .ai contains a reparse point");
                if (Directory.Exists(path)) pending.Push(path);
                else
                {
                    files++;
                    bytes += new FileInfo(path).Length;
                    if (files > 200_000 || bytes > 100L * 1024 * 1024 * 1024) throw new InvalidDataException("Field .ai exceeds verification budget");
                }
            }
        }
        return (files, bytes);
    }

    private static bool BelongsToGoal(StateSnapshot task, string goalId)
    {
        try
        {
            using var document = JsonDocument.Parse(task.PayloadJson);
            return document.RootElement.TryGetProperty("goal_id", out var goal) &&
                   goal.ValueKind == JsonValueKind.String &&
                   string.Equals(goal.GetString(), goalId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static T AssertSingle<T>(IReadOnlyCollection<T> values, string name) =>
        values.Count == 1 ? values.Single() : throw new InvalidDataException($"Field has no unique {name}");

    private static async Task<int> CountKindAsync(IStateStore store, string projectId, string kind) =>
        (await store.ReadAsync(projectId, kind, lifecycle: null, limit: 1_000).ConfigureAwait(false)).Count;

    private static async Task<Latency> MeasureAsync<T>(int iterations, Func<Task<T>> action)
    {
        var values = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var timer = Stopwatch.StartNew();
            _ = await action().ConfigureAwait(false);
            values[index] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(values);
        return new Latency(Percentile(values, 0.50), Percentile(values, 0.95), Percentile(values, 0.99));
    }

    private static double Percentile(double[] values, double percentile) =>
        values[(int)Math.Floor((values.Length - 1) * percentile)];

    private sealed record Latency(double P50Ms, double P95Ms, double P99Ms);
}

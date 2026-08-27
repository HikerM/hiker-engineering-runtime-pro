using System.Diagnostics;
using Hiker.Pro.Context;
using Hiker.Pro.Environment;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Verification;

internal sealed class EnvironmentControlPlaneScenario
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-environment-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var checks = new List<VerificationCheck>();
        try
        {
            var layout = new ProjectLayout(root);
            await using var store = new SqliteStateStore(layout);
            await store.InitializeAsync().ConfigureAwait(false);
            var state = new StateCoordinator(store);
            var bootstrap = new ProjectBootstrapService(layout, store, state);
            const string projectId = "environment-performance";
            _ = await bootstrap.InitializeAsync(projectId).ConfigureAwait(false);
            await SeedGoalTaskAsync(store, projectId).ConfigureAwait(false);
            var registry = new EnvironmentProviderRegistry();
            registry.Register(new LocalProjectEnvironmentProvider(root));
            var profiles = new EnvironmentProfileCoordinator(state, registry);
            var selected = await profiles.ResolveAndSelectAsync(
                "environment-perf-select", projectId, 1, new EnvironmentDetectionRequest(root, "verification")).ConfigureAwait(false);
            var descriptor = selected.Profile!.Graph.Nodes.Single().Resources.Single();
            var resources = new EnvironmentResourceLeaseCoordinator(state, new ProcessIdentityProbe());
            var processes = new EnvironmentProcessRegistry(state, new ProcessIdentityProbe());
            var instances = new EnvironmentInstanceCoordinator(state, registry, resources);
            var activated = await instances.ActivateAsync(new EnvironmentInstanceRequest(
                "environment-perf-instance", root, projectId, "goal-1", 1, "task-1", "worktree-1", "runtime-1", selected.Profile.ProfileId,
                new[] { new ResourceRequirement(descriptor.ResourceType, descriptor.ResourceId, descriptor.ProviderId, descriptor.Scope, descriptor.SharingMode, 1, descriptor.Capacity) },
                new HashSet<string>(StringComparer.Ordinal), null, TimeSpan.FromMinutes(5), "source")).ConfigureAwait(false);
            var resolver = new ContextResolver(store);
            var doctor = new EnvironmentDoctor(state, registry, new ProcessIdentityProbe());
            _ = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            _ = await doctor.CheckAsync(root, projectId).ConfigureAwait(false);

            var contextBefore = await MeasureAsync(100, () => resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1"))).ConfigureAwait(false);
            var doctorBefore = await MeasureAsync(50, () => doctor.CheckAsync(root, projectId)).ConfigureAwait(false);
            var baselinePackage = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            await SeedColdHistoryAsync(store, projectId, 1024).ConfigureAwait(false);
            var contextAfter = await MeasureAsync(100, () => resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1"))).ConfigureAwait(false);
            var doctorAfter = await MeasureAsync(50, () => doctor.CheckAsync(root, projectId)).ConfigureAwait(false);
            var agedPackage = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);

            var generationBeforeReuse = (await store.GetMetricsAsync().ConfigureAwait(false)).Generation;
            var reuse = await profiles.ResolveAndSelectAsync(
                "environment-perf-reuse", projectId, 1, new EnvironmentDetectionRequest(root, "verification")).ConfigureAwait(false);
            var existing = await instances.ActivateAsync(new EnvironmentInstanceRequest(
                "environment-perf-existing", root, projectId, "goal-1", 1, "task-1", "worktree-1", "runtime-1", selected.Profile.ProfileId,
                new[] { new ResourceRequirement(descriptor.ResourceType, descriptor.ResourceId, descriptor.ProviderId, descriptor.Scope, descriptor.SharingMode, 1, descriptor.Capacity) },
                new HashSet<string>(StringComparer.Ordinal), null, TimeSpan.FromMinutes(5), "source")).ConfigureAwait(false);
            var generationAfterReuse = (await store.GetMetricsAsync().ConfigureAwait(false)).Generation;

            var bounded = baselinePackage.StateReads == agedPackage.StateReads && baselinePackage.EncodedBytes == agedPackage.EncodedBytes &&
                          Stable(contextBefore.P95Ms, contextAfter.P95Ms) && Stable(doctorBefore.P95Ms, doctorAfter.P95Ms);
            checks.Add(new VerificationCheck(
                "ENVIRONMENT_HOT_INDEX_BOUNDED",
                bounded ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Hot context and Doctor use active indexes after 1,024 cold environment facts.",
                new Dictionary<string, object?>
                {
                    ["cold_history"] = 1024,
                    ["context_before_p50_ms"] = contextBefore.P50Ms,
                    ["context_before_p95_ms"] = contextBefore.P95Ms,
                    ["context_after_p50_ms"] = contextAfter.P50Ms,
                    ["context_after_p95_ms"] = contextAfter.P95Ms,
                    ["doctor_before_p95_ms"] = doctorBefore.P95Ms,
                    ["doctor_after_p95_ms"] = doctorAfter.P95Ms,
                    ["state_reads_before"] = baselinePackage.StateReads,
                    ["state_reads_after"] = agedPackage.StateReads,
                    ["context_bytes_before"] = baselinePackage.EncodedBytes,
                    ["context_bytes_after"] = agedPackage.EncodedBytes
                }));
            checks.Add(new VerificationCheck(
                "ENVIRONMENT_REUSE_ZERO_WRITE",
                reuse.Reused && existing.Status == "ACTIVE_EXISTING_INSTANCE" && generationBeforeReuse == generationAfterReuse ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Verified profile and active Task environment are reused without another state write.",
                new Dictionary<string, object?>
                {
                    ["profile_reused"] = reuse.Reused,
                    ["instance_status"] = existing.Status,
                    ["generation_before"] = generationBeforeReuse,
                    ["generation_after"] = generationAfterReuse,
                    ["activated_status"] = activated.Status
                }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return checks;
    }

    private static async Task SeedGoalTaskAsync(SqliteStateStore store, string projectId)
    {
        _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "environment-perf-goal-task",
            new[]
            {
                Mutation(projectId, EntityKinds.Goal, "goal-1", StateLifecycle.Valid, RetentionTier.Hot, "{\"status\":\"ACTIVE\"}"),
                Mutation(projectId, EntityKinds.Task, "task-1", StateLifecycle.Valid, RetentionTier.Hot, "{\"status\":\"RUNNING\"}")
            },
            "PERFORMANCE_SEEDED", "GOAL_TASK", "{}")).ConfigureAwait(false);
    }

    private static async Task SeedColdHistoryAsync(SqliteStateStore store, string projectId, int count)
    {
        for (var offset = 0; offset < count; offset += 64)
        {
            var mutations = Enumerable.Range(offset, Math.Min(64, count - offset))
                .Select(index => Mutation(projectId, EntityKinds.EnvironmentInstance, $"cold-{index:D5}", StateLifecycle.Archived, RetentionTier.Cold, "{\"status\":\"RELEASED\"}"))
                .ToArray();
            _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"environment-perf-cold-{offset}", mutations, "HISTORY_ARCHIVED", "COLD_ENVIRONMENT_HISTORY", "{}")).ConfigureAwait(false);
        }
    }

    private static StateMutation Mutation(string projectId, string kind, string id, StateLifecycle lifecycle, RetentionTier retention, string payload) => new(
        new StateKey(projectId, kind, id), 0, 1, lifecycle, "VERIFICATION", Hiker.Pro.Release.BuildVersionFacts.Current.StateSchema, "source", "dependency", payload, retention);

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

    private static bool Stable(double baseline, double current) => current <= baseline * 3.0 || current - baseline <= 5.0;
    private sealed record Latency(double P50Ms, double P95Ms);
}

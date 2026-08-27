using System.Diagnostics;
using System.Security.Cryptography;
using Hiker.Pro.ChangeIntelligence;
using Hiker.Pro.Context;
using Hiker.Pro.Graph;
using Hiker.Pro.State;

namespace Hiker.Pro.Verification;

internal sealed class ChangeIntelligenceScenario(VerificationOptions options)
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-change-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var checks = new List<VerificationCheck>();
        try
        {
            var layout = new ProjectLayout(root);
            await using var store = new SqliteStateStore(layout);
            await store.InitializeAsync().ConfigureAwait(false);
            var state = new StateCoordinator(store);
            const string projectId = "change-intelligence-verification";
            _ = await new ProjectBootstrapService(layout, store, state).InitializeAsync(projectId).ConfigureAwait(false);
            var field = await SeedRealSourceGraphAsync(store, projectId).ConfigureAwait(false);
            var probe = new ChangeRiskProbe(state);
            var localRequest = Probe(projectId, "local-change", "local-change-outcome");
            var localValues = await MeasureAsync(250, () => probe.ProbeAsync(localRequest)).ConfigureAwait(false);
            var localP50 = Percentile(localValues, 0.50);
            var localP95 = Percentile(localValues, 0.95);
            var local = await probe.ProbeAsync(localRequest).ConfigureAwait(false);
            var generationBeforeLocal = (await store.GetMetricsAsync().ConfigureAwait(false)).Generation;
            var localPreflight = await new ChangeIntelligencePlane(state).PreflightAsync(new ChangePreflightRequest("local-field-preflight", localRequest)).ConfigureAwait(false);
            var generationAfterLocal = (await store.GetMetricsAsync().ConfigureAwait(false)).Generation;
            checks.Add(new VerificationCheck(
                "CHANGE_LOCAL_FAST_PATH",
                local.Classification == ChangeRiskClasses.Local && !local.RequiresFullAnalysis && !localPreflight.StateWritten &&
                generationBeforeLocal == generationAfterLocal && localP95 <= 5 ? "PASS" : "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "An explicit real source file with no indexed consumers stays on the zero-write fast path.",
                new Dictionary<string, object?>
                {
                    ["source_file"] = field.LocalRelativePath,
                    ["source_fingerprint"] = field.LocalFingerprint,
                    ["probe_p50_ms"] = localP50,
                    ["probe_p95_ms"] = localP95,
                    ["probe_p95_budget_ms"] = 5,
                    ["graph_queries_per_probe"] = local.GraphQueries,
                    ["state_generation_before"] = generationBeforeLocal,
                    ["state_generation_after"] = generationAfterLocal
                }));

            var sharedRequest = Probe(projectId, "shared-change", "state-coordinator") with
            {
                Signals = new ChangeRiskSignals(CrossServiceBoundary: true, CriticalPath: true)
            };
            var graph = new ImpactGraphEngine(state);
            var blastValues = await MeasureAsync(100, () => graph.AnalyzeAsync(new ImpactAnalysisRequest(projectId, sharedRequest.Seeds, 256, 8))).ConfigureAwait(false);
            var blastP50 = Percentile(blastValues, 0.50);
            var blastP95 = Percentile(blastValues, 0.95);
            var preflight = await new ChangeIntelligencePlane(state).PreflightAsync(new ChangePreflightRequest(
                "shared-field-preflight",
                sharedRequest,
                Contract("shared-field-contract", "shared-change", "src/Hiker.Pro.Runtime/State"),
                256,
                8)).ConfigureAwait(false);
            checks.Add(new VerificationCheck(
                "CHANGE_SHARED_BLAST_AND_POLICY",
                preflight.Blast?.Classification == ChangeRiskClasses.CrossCutting && preflight.Policy.RequiresSingleWriter &&
                preflight.Policy.RequiresIndependentAssurance && blastP95 <= 10 &&
                preflight.Blast.Relations.All(static relation => relation.ObservedAt != default && relation.RelationFingerprint is not null)
                    ? "PASS"
                    : "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "The actual StateCoordinator source fingerprint and its indexed consumers drive cross-cutting policy through the existing ImpactGraphEngine.",
                new Dictionary<string, object?>
                {
                    ["source_file"] = field.SharedRelativePath,
                    ["source_fingerprint"] = field.SharedFingerprint,
                    ["consumers"] = field.DependentRelativePaths,
                    ["blast_p50_ms"] = blastP50,
                    ["blast_p95_ms"] = blastP95,
                    ["blast_p95_budget_ms"] = 10,
                    ["nodes_visited"] = preflight.Blast?.NodesVisited,
                    ["graph_queries"] = preflight.Blast?.GraphQueries,
                    ["context_delta_bytes"] = preflight.ContextDeltaBytes
                }));

            var resolver = new ContextResolver(store);
            var before = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            await SeedColdHistoryAsync(store, projectId, 1024).ConfigureAwait(false);
            var after = await resolver.ResolveAsync(new ContextRequest(projectId, "task-1", "goal-1")).ConfigureAwait(false);
            checks.Add(new VerificationCheck(
                "CHANGE_CONTEXT_BOUNDED",
                before.StateReads == after.StateReads && before.EncodedBytes == after.EncodedBytes && before.Fingerprint == after.Fingerprint &&
                after.EncodedBytes <= 32_768 ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Current Task Change Contract and predicted impact remain bounded after 1,024 cold Change facts.",
                new Dictionary<string, object?>
                {
                    ["cold_history"] = 1024,
                    ["state_reads_before"] = before.StateReads,
                    ["state_reads_after"] = after.StateReads,
                    ["context_bytes_before"] = before.EncodedBytes,
                    ["context_bytes_after"] = after.EncodedBytes
                }));

            var history = await ReplayRealMigrationHistoryAsync(state, projectId).ConfigureAwait(false);
            checks.Add(new VerificationCheck(
                "CHANGE_REAL_HISTORY_SHADOW_REPLAY",
                history.Commits == 3 && history.Result.Status == ChangeControlStatuses.FixLoopConfirmed && !history.Result.PatchingAllowed ? "PASS" : "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "Three real Legacy Migration repair commits were replayed as bounded historical evidence; the production loop guard disabled a fourth ordinary patch. This is shadow evidence, not a claim that field rework has already improved.",
                new Dictionary<string, object?>
                {
                    ["historical_commits"] = history.Commits,
                    ["scope_expansions"] = history.Result.State.ScopeExpansionCount,
                    ["ordinary_patch_allowed_after_replay"] = history.Result.PatchingAllowed,
                    ["field_outcome_improvement_proven"] = false,
                    ["time_to_accepted_change_improvement_proven"] = false
                }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return checks;
    }

    private async Task<RealSourceGraph> SeedRealSourceGraphAsync(SqliteStateStore store, string projectId)
    {
        const string shared = "src/Hiker.Pro.Runtime/State/StateCoordinator.cs";
        const string local = "src/Hiker.Pro.Runtime/ChangeIntelligence/ChangeOutcomeCoordinator.cs";
        var dependents = new[]
        {
            "src/Hiker.Pro.Runtime/Graph/ImpactGraphEngine.cs",
            "src/Hiker.Pro.Runtime/Graph/GoalChangeCoordinator.cs",
            "src/Hiker.Pro.Runtime/Environment/EnvironmentRuntimeControl.cs"
        };
        var explicitFiles = dependents.Append(shared).Append(local).ToArray();
        foreach (var relative in explicitFiles)
        {
            var path = Path.Combine(options.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException("Explicit Change field source is missing", relative);
        }
        foreach (var relative in dependents)
        {
            var text = await File.ReadAllTextAsync(Path.Combine(options.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar))).ConfigureAwait(false);
            if (!text.Contains("StateCoordinator", StringComparison.Ordinal)) throw new InvalidDataException($"Explicit dependent no longer references StateCoordinator: {relative}");
        }
        var sharedFingerprint = Fingerprint(Path.Combine(options.RepositoryRoot, shared.Replace('/', Path.DirectorySeparatorChar)));
        var localFingerprint = Fingerprint(Path.Combine(options.RepositoryRoot, local.Replace('/', Path.DirectorySeparatorChar)));
        var mutations = new List<StateMutation>
        {
            Mutation(projectId, EntityKinds.Goal, "goal-1", "goal"),
            Mutation(projectId, EntityKinds.Task, "task-1", "task"),
            Mutation(projectId, EntityKinds.Code, "state-coordinator", sharedFingerprint),
            Mutation(projectId, EntityKinds.Code, "local-change-outcome", localFingerprint)
        };
        mutations.AddRange(dependents.Select(relative => Mutation(projectId, EntityKinds.Code, Id(relative), Fingerprint(Path.Combine(options.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar))))));
        var edges = dependents.Select(relative => new DependencyEdge(
            projectId, EntityKinds.Code, Id(relative), EntityKinds.Code, "state-coordinator", "CALLS", Fingerprint(Path.Combine(options.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar))))).ToArray();
        _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "change-field-source-seed", mutations, "CHANGE_FIELD_GRAPH_CREATED", "EXPLICIT_REAL_SOURCE_GRAPH", "{}", edges)).ConfigureAwait(false);
        return new RealSourceGraph(shared, sharedFingerprint, local, localFingerprint, dependents);
    }

    private async Task<HistoryReplay> ReplayRealMigrationHistoryAsync(StateCoordinator state, string projectId)
    {
        var paths = new[]
        {
            "src/Hiker.Pro.Runtime/State/ProjectBootstrapService.cs",
            "src/Hiker.Pro.Runtime/State/Community518Importer.cs",
            "src/Hiker.Pro.Runtime/State/LegacyUntrustedContentInspector.cs"
        };
        var log = await ProcessTools.RequireAsync("git", options.RepositoryRoot,
            new[] { "log", "-3", "--format=%H%x09%s", "--" }.Concat(paths), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var commits = log.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2)).Where(static parts => parts.Length == 2 && parts[1].StartsWith("fix(migration):", StringComparison.Ordinal)).Reverse().ToArray();
        if (commits.Length != 3) throw new InvalidDataException("Expected the bounded three-commit Legacy Migration repair history");
        var coordinator = new ChangeLoopCoordinator(state);
        ChangeAttemptResult? result = null;
        for (var index = 0; index < commits.Length; index++)
        {
            var files = await ProcessTools.RequireAsync("git", options.RepositoryRoot,
                new[] { "show", "--format=", "--name-only", commits[index][0], "--" }.Concat(paths), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var scopes = files.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
            result = await coordinator.RecordAttemptAsync(
                $"migration-history-attempt-{index + 1}", projectId, "legacy-migration-history", 1,
                new ChangeAttempt($"history-{index + 1}", "LEGACY_PROJECT_NOT_ACTIVE", scopes, false, 0, 0, 0,
                    new[] { commits[index][0][..12] }, default),
                "migration-history").ConfigureAwait(false);
        }
        return new HistoryReplay(commits.Length, result!);
    }

    private static async Task SeedColdHistoryAsync(SqliteStateStore store, string projectId, int count)
    {
        for (var offset = 0; offset < count; offset += 64)
        {
            var mutations = Enumerable.Range(offset, Math.Min(64, count - offset)).Select(index => new StateMutation(
                new StateKey(projectId, EntityKinds.ChangeImpact, $"cold-change-{index:D5}"), 0, 1, StateLifecycle.Archived,
                "VERIFICATION", "3.0.0", "cold-source", "cold-dependency", "{\"status\":\"HISTORICAL\"}", RetentionTier.Cold)).ToArray();
            _ = await store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"change-cold-{offset}", mutations, "CHANGE_HISTORY_ARCHIVED", "COLD_CHANGE_HISTORY", "{}")).ConfigureAwait(false);
        }
    }

    private static ChangeProbeRequest Probe(string projectId, string changeId, string entityId) => new(
        projectId, "task-1", changeId,
        new[] { new ImpactSeed(new StateKey(projectId, EntityKinds.Code, entityId), "MODIFIED", "source") },
        new ChangeRiskSignals(), Array.Empty<Hiker.Pro.Environment.ResourceRequirement>(), "source");

    private static ChangeContractDefinition Contract(string id, string changeId, params string[] scopes) => new(
        id, changeId, "task-1", 1, "SHARED_STATE_CHANGE", "single-state-coordinator-authority", scopes,
        new[] { "change-behavior" }, new[] { "existing-state-transitions" }, new[] { "no-second-writer" },
        new[] { "state-regression" }, "rollback-to-previous-state-coordinator", "source");

    private static StateMutation Mutation(string projectId, string kind, string id, string fingerprint) => new(
        new StateKey(projectId, kind, id), 0, 1, StateLifecycle.Valid, "VERIFICATION", "3.0.0", fingerprint, fingerprint, "{\"status\":\"ACTIVE\"}");

    private static string Id(string relative) => Path.GetFileNameWithoutExtension(relative).ToLowerInvariant();
    private static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task<double[]> MeasureAsync<T>(int iterations, Func<Task<T>> action)
    {
        var values = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var timer = Stopwatch.StartNew();
            _ = await action().ConfigureAwait(false);
            timer.Stop();
            values[index] = timer.Elapsed.TotalMilliseconds;
        }
        return values;
    }

    private static double Percentile(IEnumerable<double> values, double percentile) => ProcessTools.Percentile(values, percentile);

    private sealed record RealSourceGraph(
        string SharedRelativePath,
        string SharedFingerprint,
        string LocalRelativePath,
        string LocalFingerprint,
        IReadOnlyList<string> DependentRelativePaths);
    private sealed record HistoryReplay(int Commits, ChangeAttemptResult Result);
}

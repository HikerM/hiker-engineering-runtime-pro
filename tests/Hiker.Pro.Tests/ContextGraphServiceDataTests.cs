using Hiker.Pro.Context;
using Hiker.Pro.Data;
using Hiker.Pro.Execution;
using Hiker.Pro.Graph;
using Hiker.Pro.Services;
using Hiker.Pro.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ContextGraphServiceDataTests
{
    [Fact]
    public async Task ContextResolver_CostDependsOnTaskEdgesNotColdHistorySize()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedContextAsync(project.Store);
        var resolver = new ContextResolver(project.Store);
        var before = await resolver.ResolveAsync(new ContextRequest("project-1", "task-1", "goal-1"));
        for (var batch = 0; batch < 4; batch++)
        {
            var mutations = Enumerable.Range(batch * 125, 125).Select(index => State(
                EntityKinds.Decision,
                $"cold-{index:D4}",
                $"{{\"status\":\"SUPERSEDED\",\"sequence\":{index}}}",
                lifecycle: StateLifecycle.Archived,
                retention: RetentionTier.Cold)).ToArray();
            Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"cold-batch-{batch}", mutations, "HISTORY_IMPORTED", "COLD_HISTORY", "{}"))).Committed);
        }

        var after = await resolver.ResolveAsync(new ContextRequest("project-1", "task-1", "goal-1"));
        Assert.Equal(before.StateReads, after.StateReads);
        Assert.Equal(before.EncodedBytes, after.EncodedBytes);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.True(after.EncodedBytes <= 32_768);
        Assert.DoesNotContain(after.Items, static item => item.EntityId.StartsWith("cold-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImpactGraph_PropagatesThroughStructuredEdgesAndInvalidatesOnlyAffectedEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        var mutations = new[]
        {
            State(EntityKinds.Contract, "api-v1", "{\"version\":1}"),
            State(EntityKinds.Service, "frontend", "{\"runtime\":\"node\"}"),
            State(EntityKinds.Evidence, "api-test", "{\"status\":\"PASS\"}"),
            State(EntityKinds.Evidence, "db-test", "{\"status\":\"PASS\"}")
        };
        var edges = new[]
        {
            new DependencyEdge("project-1", EntityKinds.Service, "frontend", EntityKinds.Contract, "api-v1", "CONSUMES", "edge-1"),
            new DependencyEdge("project-1", EntityKinds.Evidence, "api-test", EntityKinds.Contract, "api-v1", "VALIDATES", "edge-2")
        };
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "impact-seed", mutations, "GRAPH_CREATED", "GRAPH_CREATED", "{}", edges))).Committed);
        var engine = new ImpactGraphEngine(project.Coordinator);
        var request = new ImpactAnalysisRequest("project-1", new[]
        {
            new ImpactSeed(new StateKey("project-1", EntityKinds.Contract, "api-v1"), "MODIFIED", "contract-v2")
        });
        var analysis = await engine.AnalyzeAsync(request);
        Assert.Contains(analysis.Nodes, static node => node.Key.Kind == EntityKinds.Service && node.Key.EntityId == "frontend" && node.Disposition == ImpactDisposition.Affected);
        Assert.Contains(analysis.EvidenceToInvalidate, static key => key.EntityId == "api-test");
        Assert.DoesNotContain(analysis.EvidenceToInvalidate, static key => key.EntityId == "db-test");
        Assert.True((await engine.InvalidateAffectedEvidenceAsync("impact-invalidate", request, 2)).Committed);
        Assert.Equal(StateLifecycle.Stale, (await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Evidence, "api-test")))?.Lifecycle);
        Assert.Equal(StateLifecycle.Valid, (await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Evidence, "db-test")))?.Lifecycle);
    }

    [Fact]
    public async Task ServiceTopology_RegistersPolyglotContractEdgesIdempotently()
    {
        await using var project = await TestProject.CreateAsync();
        var engine = new ServiceTopologyEngine(project.Coordinator);
        var descriptor = new ServiceDescriptor(
            "orders-api",
            "dotnet",
            "9.0",
            "MSBuild",
            new[] { "http:orders" },
            new[] { "orders-db" },
            new[] { "orders-events" },
            new[] { new ServiceContractDescriptor("orders-http-v1", "OPENAPI", "1.0", "contract-hash", new[] { "web-ui", "desktop-client" }) },
            new[] { "identity-api" },
            "service-hash");
        var first = await engine.RegisterAsync("service-register", "project-1", 1, descriptor);
        var retry = await engine.RegisterAsync("service-register", "project-1", 1, descriptor);
        Assert.Equal("COMMITTED", first.Status);
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
        Assert.Single(await engine.ActiveServicesAsync("project-1"));
        var dependencies = await project.Store.QueryDependenciesAsync("project-1", EntityKinds.Service, "orders-api", outgoing: true);
        Assert.Contains(dependencies, static edge => edge.Relation == "DEPENDS_ON" && edge.ToId == "identity-api");
        Assert.Contains(dependencies, static edge => edge.Relation == "OWNS_CONTRACT" && edge.ToId == "orders-http-v1");
    }

    [Fact]
    public async Task ResourceLease_CasAllocatesDistinctPortsAcrossConcurrentWorkers()
    {
        await using var project = await TestProject.CreateAsync();
        var coordinator = new ResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var tasks = Enumerable.Range(0, 8).Select(index => coordinator.AcquireAsync(new ResourceLeaseRequest(
            $"port-op-{index}", "project-1", $"task-{index}", $"runtime-{index}", "PORT", null,
            52000, 52100, null, TimeSpan.FromMinutes(5), $"source-{index}"))).ToArray();
        var leases = await Task.WhenAll(tasks);
        Assert.Equal(8, leases.Select(static item => item.Lease.Token).Distinct(StringComparer.Ordinal).Count());
        Assert.All(leases, static item => Assert.Equal("COMMITTED", item.Status));
        var retry = await coordinator.AcquireAsync(new ResourceLeaseRequest(
            "port-op-0", "project-1", "task-0", "runtime-0", "PORT", null, 52000, 52100, null, TimeSpan.FromMinutes(5), "source-0"));
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
    }

    [Fact]
    public async Task EngineeringAssets_AndMinimumRuntimeSetUseObservedDependenciesOnly()
    {
        await using var project = await TestProject.CreateAsync();
        var topology = new ServiceTopologyEngine(project.Coordinator);
        await topology.RegisterAsync("service-identity", "project-1", 1, Service("identity", Array.Empty<string>()));
        await topology.RegisterAsync("service-orders", "project-1", 1, Service("orders", new[] { "identity" }));
        await topology.RegisterAsync("service-web", "project-1", 1, Service("web", new[] { "orders" }));
        var runtime = await topology.ResolveMinimumRuntimeSetAsync("project-1", new[] { "web" });
        Assert.Equal("RESOLVED", runtime.Status);
        Assert.Equal(new[] { "identity", "orders", "web" }, runtime.Services);

        var assets = new EngineeringAssetRegistry(project.Coordinator);
        Assert.Equal("COMMITTED", (await assets.RegisterRuntimeProfileAsync(
            "profile-web", "project-1", 1,
            new RuntimeProfileDescriptor("web-node", "web", "typescript", "22", "node", "npm", "npm run build", "npm test", "npm start", "npm run debug", "/health", new[] { "test", "production" }, new[] { "orders" }, "profile-source"))).Status);
        Assert.Equal("COMMITTED", (await assets.RegisterScriptAsync(
            "script-backup", "project-1", 1,
            new ScriptDescriptor("backup-script", "backup database", "pwsh", new[] { "database" }, new[] { "archive" }, new[] { "writes archive" },
                new[] { new EngineeringDependencyRef(EntityKinds.Database, "orders-db") }, "HIGH", "restore verified archive", "script-source"))).Status);
        Assert.Equal("COMMITTED", (await assets.RegisterMiddlewareAsync(
            "middleware-cache", "project-1", 1,
            new MiddlewareDescriptor("redis", "CACHE", "7", new[] { "orders" }, "HIGH", "environment", "PING", "fail closed for writes", "middleware-source"))).Status);
        var middlewareEdges = await project.Store.QueryDependenciesAsync("project-1", EntityKinds.Service, "orders", outgoing: true);
        Assert.Contains(middlewareEdges, static edge => edge.ToKind == EntityKinds.Middleware && edge.ToId == "redis");
    }

    [Fact]
    public async Task SqlitePerformance_UsesRealQueryPlansConcurrencyAndNPlusOneEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        var database = Path.Combine(project.Root, "performance.db");
        var connectionString = $"Data Source={database};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE orders(id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, total REAL NOT NULL); CREATE INDEX ix_orders_user ON orders(user_id);";
            await command.ExecuteNonQueryAsync();
            await using var transaction = connection.BeginTransaction();
            for (var index = 0; index < 1000; index++)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO orders(user_id,total) VALUES($user,$total);";
                insert.Parameters.AddWithValue("$user", index % 100);
                insert.Parameters.AddWithValue("$total", index);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var adapter = new SqlitePerformanceAdapter(connectionString);
        var profile = await adapter.ProfileAsync();
        var indexed = await adapter.AnalyzeReadQueryAsync("SELECT * FROM orders WHERE user_id=$user", new Dictionary<string, object?> { ["$user"] = 42 });
        var scan = await adapter.AnalyzeReadQueryAsync("SELECT * FROM orders WHERE total > $total", new Dictionary<string, object?> { ["$total"] = 900 });
        var concurrency = await adapter.ProbeConcurrencyAsync(4, 25, allowFixtureWrites: true);
        Assert.Equal("wal", profile.JournalMode, ignoreCase: true);
        Assert.True(indexed.UsesIndex);
        Assert.False(indexed.FullTableScan);
        Assert.True(scan.FullTableScan);
        Assert.Equal(100, concurrency.Operations);
        Assert.Equal(100, concurrency.Succeeded + concurrency.BusyFailures);
        var observations = Enumerable.Range(0, 12).Select(index => new QueryObservation("request-1", "orders-by-user", $"user-{index}", TimeSpan.FromMilliseconds(2))).ToArray();
        var nPlusOne = Assert.Single(DataPerformanceEngine.DetectNPlusOne(observations));
        Assert.Equal(12, nPlusOne.Executions);

        var before = Evidence("orders", p95: 80, p99: 100, qps: 500, poolWait: 2, lockWait: 3);
        var after = Evidence("orders", p95: 130, p99: 170, qps: 300, poolWait: 20, lockWait: 30);
        var regression = DataPerformanceEngine.Evaluate(new PerformanceContract(100, 150, 0.01, 10, 15, 0, 400), before, after);
        Assert.Equal("PERFORMANCE_FAIL", regression.Status);
        Assert.Contains("P95_REGRESSION", regression.Violations);
        Assert.Equal("POOL_EXHAUSTED", DataPerformanceEngine.AnalyzePool(new[]
        {
            new ConnectionPoolObservation(2, false),
            new ConnectionPoolObservation(100, true)
        }).Status);
        Assert.Equal("DEADLOCK_DETECTED", DataPerformanceEngine.AnalyzeLocks(new[]
        {
            new LockObservation(3, false, false),
            new LockObservation(50, true, false)
        }).Status);
        Assert.Equal(16, DataPerformanceEngine.FindSaturation(new[]
        {
            new SaturationSample(4, 400, 10, 0),
            new SaturationSample(8, 700, 12, 0),
            new SaturationSample(16, 720, 30, 0)
        }).SaturationPoint);
    }

    [Fact]
    public async Task ProjectResume_IsBoundedAndMigrationRiskRequiresRealEvidence()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedContextAsync(project.Store);
        var resume = await new ProjectResumeService(project.Store).ResumeAsync("project-1", "goal-1", maxBytes: 48_000);
        Assert.NotNull(resume.CurrentGoal);
        Assert.Single(resume.OpenTasks);
        Assert.True(resume.EncodedBytes <= 48_000);
        var risk = MigrationRiskAnalyzer.Assess(new[]
        {
            new MigrationChange("orders", MigrationOperationKind.CreateIndex, 5_000_000, Transactional: false, Reversible: true, OnlineCapable: false),
            new MigrationChange("customers", MigrationOperationKind.DropColumn, 2_000_000, Transactional: true, Reversible: false, OnlineCapable: false)
        });
        Assert.Equal("HIGH", risk.Risk);
        Assert.True(risk.RequiresRehearsal);
        Assert.True(risk.RequiresRollbackPlan);
        Assert.Contains(risk.Findings, static finding => finding.Code == "LARGE_BLOCKING_INDEX_BUILD");
        Assert.Contains(risk.Findings, static finding => finding.Code == "IRREVERSIBLE_CHANGE");
    }

    private static async Task SeedContextAsync(IStateStore store)
    {
        var mutations = new[]
        {
            State(EntityKinds.Goal, "goal-1", "{\"revision\":1,\"acceptance\":[\"AC-1\"]}"),
            State(EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}"),
            State(EntityKinds.Decision, "decision-1", "{\"status\":\"APPROVED_CURRENT\",\"reason\":\"compatibility\"}"),
            State(EntityKinds.Service, "service-1", "{\"runtime\":\"node\"}"),
            State(EntityKinds.Evidence, "evidence-1", "{\"status\":\"PASS\"}")
        };
        var edges = new[]
        {
            new DependencyEdge("project-1", EntityKinds.Task, "task-1", EntityKinds.Decision, "decision-1", "DEPENDS_ON", "edge-d"),
            new DependencyEdge("project-1", EntityKinds.Task, "task-1", EntityKinds.Service, "service-1", "DEPENDS_ON", "edge-s"),
            new DependencyEdge("project-1", EntityKinds.Task, "task-1", EntityKinds.Evidence, "evidence-1", "PROVEN_BY", "edge-e")
        };
        Assert.True((await store.CompareAndSwapBatchAsync(new BatchTransactionRequest("context-seed", mutations, "CONTEXT_CREATED", "CONTEXT_CREATED", "{}", edges))).Committed);
    }

    private static StateMutation State(
        string kind,
        string id,
        string payload,
        StateLifecycle lifecycle = StateLifecycle.Valid,
        RetentionTier retention = RetentionTier.Hot) => new(
        new StateKey("project-1", kind, id), 0, 1, lifecycle, "TEST", "3.0.0", $"source-{id}", $"deps-{id}", payload, retention);

    private static ServiceDescriptor Service(string id, IReadOnlyList<string> dependencies) => new(
        id,
        "dotnet",
        "9.0",
        "MSBuild",
        new[] { $"http:{id}" },
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<ServiceContractDescriptor>(),
        dependencies,
        $"source-{id}");

    private static WorkloadEvidence Evidence(string workload, double p95, double p99, double qps, double poolWait, double lockWait) => new(
        workload,
        true,
        p95 / 2,
        p95,
        p99,
        qps,
        qps / 2,
        0,
        poolWait,
        lockWait,
        0,
        8);
}

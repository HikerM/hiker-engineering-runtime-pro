using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Release;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ProjectRoutingFactsTests
{
    [Fact]
    public async Task ProjectFacts_AreBoundedAuthorityAwareAndExcludeSensitivePayloadFields()
    {
        await using var project = await TestProject.CreateAsync("routing-facts-project");
        await CommitAsync(project, EntityKinds.Goal, "goal-current", "LIVE_SESSION_ADOPTION", BuildVersionFacts.Current.StateSchema,
            "{\"goal_id\":\"goal-current\",\"status\":\"ACTIVE\",\"prompt\":\"do not expose this prompt\"}", 4);
        await CommitAsync(project, EntityKinds.Task, "task-current", "LIVE_SESSION_ADOPTION", BuildVersionFacts.Current.StateSchema,
            "{\"task_id\":\"task-current\",\"goal_id\":\"goal-current\",\"status\":\"RUNNING\",\"stage\":\"IMPLEMENTATION\",\"changed_scope\":[\"web/admin\",\"api/contracts\"],\"secret\":\"token-value\"}", 4);
        await CommitAsync(project, EntityKinds.Decision, "architecture-current", "ARCHITECTURE_AUTHORITY", BuildVersionFacts.Current.StateSchema,
            "{\"architecture\":\"bs\",\"verified_topology_roots\":[\"web\",\"server\"],\"source_code\":\"not returned\"}");
        await CommitAsync(project, EntityKinds.Decision, "architecture-legacy", "COMMUNITY_5_18_IMPORT", "2.0.0",
            "{\"architecture\":\"backend\"}");
        await CommitAsync(project, EntityKinds.EnvironmentProfile, "environment-current", "ENVIRONMENT_CONTROL_PLANE", BuildVersionFacts.Current.StateSchema,
            "{\"profile\":\"LOCAL_PROJECT\",\"absolute_path\":\"C:\\\\Users\\\\Private\"}");
        var cold = Path.Combine(project.Root, ".ai", "archive", "large-history");
        Directory.CreateDirectory(cold);
        for (var index = 0; index < 500; index++) await File.WriteAllTextAsync(Path.Combine(cold, $"event-{index:D4}.json"), "{}");

        var preflight = await project.Bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read);
        var watch = Stopwatch.StartNew();
        var facts = await new ProjectRoutingFactsService(project.Store).ReadAsync(preflight);
        watch.Stop();
        var json = JsonSerializer.Serialize(facts);

        Assert.Equal("bs", facts.ProjectArchitecture?.Value);
        Assert.Equal("AUTHORITATIVE_CURRENT", facts.ProjectArchitecture?.Authority);
        Assert.Equal("goal-current", facts.CurrentGoal?.Value);
        Assert.Equal("task-current", facts.CurrentTask?.Value);
        Assert.Equal(4, facts.CurrentGoalRevision);
        Assert.Equal(new[] { "web", "server" }, facts.VerifiedTopologyRoots);
        Assert.Equal(new[] { "web/admin", "api/contracts" }, facts.CurrentChangedScope);
        Assert.Equal("LOCAL_PROJECT", facts.EnvironmentProfile?.Value);
        Assert.InRange(facts.StateReads, 1, 8);
        Assert.InRange(facts.ContextBytes, 1, 16_384);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"bounded read took {watch.Elapsed}");
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\\\Users", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_code", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CommitAsync(
        TestProject project,
        string kind,
        string id,
        string authority,
        string schema,
        string payload,
        long revision = 0)
    {
        var key = new StateKey(project.ProjectId!, kind, id);
        var snapshot = await project.Coordinator.ReadForReasoningAsync(key);
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var result = await project.Coordinator.CommitAsync(
            snapshot,
            $"routing-fact-{kind.ToLowerInvariant()}-{id}",
            new StateMutation(key, 0, revision, StateLifecycle.Valid, authority, schema, fingerprint, fingerprint, payload),
            "ROUTING_FACT_SEEDED",
            "ROUTING_FACT_TEST",
            "{}");
        Assert.True(result.Committed, result.Status);
    }
}

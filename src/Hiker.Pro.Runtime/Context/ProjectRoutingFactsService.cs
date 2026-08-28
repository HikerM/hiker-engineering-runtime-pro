using System.Text;
using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public sealed record AuthorityAwareFact<T>(
    T? Value,
    string Authority,
    long Generation,
    string? SourceFingerprint,
    string Freshness,
    string Lifecycle);

public sealed record ProjectRoutingFacts(
    string Status,
    string ProjectId,
    long ProjectGeneration,
    AuthorityAwareFact<string>? CurrentGoal,
    AuthorityAwareFact<string>? CurrentTask,
    long CurrentGoalRevision,
    AuthorityAwareFact<string>? ProjectArchitecture,
    IReadOnlyList<string> VerifiedTopologyRoots,
    string? CurrentStage,
    IReadOnlyList<string> CurrentChangedScope,
    AuthorityAwareFact<string>? EnvironmentProfile,
    IReadOnlyList<string> RelevantSourceFingerprints,
    string FactAuthority,
    string FactFreshness,
    long FactGeneration,
    int StateReads,
    int ContextBytes);

public sealed class ProjectRoutingFactsService(IStateStore store)
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const int MaxListItems = 16;

    public async Task<ProjectRoutingFacts> ReadAsync(
        ProjectPreflightResult project,
        CancellationToken cancellationToken = default)
    {
        if (!project.CanProceed || project.ProjectId is null)
            throw new ProjectBootstrapException(project);
        var projectId = project.ProjectId;
        var metrics = await store.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new LiveAdoptionStateResolver(store);
        var goal = await resolver.ResolveGoalAsync(projectId, null, cancellationToken).ConfigureAwait(false);
        var task = goal.State is null
            ? new LiveStateResolution(LiveAdoptionStatuses.TaskRequired, null, 0, Array.Empty<string>())
            : await resolver.ResolveTaskAsync(projectId, goal.State, null, cancellationToken).ConfigureAwait(false);
        var decisions = await store.ReadAsync(projectId, EntityKinds.Decision, lifecycle: null, limit: 8, cancellationToken).ConfigureAwait(false);
        var environments = await store.ReadAsync(projectId, EntityKinds.EnvironmentProfile, StateLifecycle.Valid, 4, cancellationToken).ConfigureAwait(false);
        var reads = 4 + goal.Reads + task.Reads;

        var architecture = decisions
            .Where(IsCurrentAuthority)
            .Select(snapshot => (Snapshot: snapshot, Value: Token(snapshot, "project_architecture", "architecture", "architecture_type")))
            .Where(static value => value.Value is not null)
            .OrderByDescending(static value => value.Snapshot.Generation)
            .FirstOrDefault();
        var environment = environments
            .Select(snapshot => (Snapshot: snapshot, Value: Token(snapshot, "profile", "environment", "provider")))
            .Where(static value => value.Value is not null)
            .OrderByDescending(static value => value.Snapshot.Generation)
            .FirstOrDefault();
        var topology = decisions
            .Where(IsCurrentAuthority)
            .SelectMany(snapshot => Tokens(snapshot, "verified_topology_roots", "topology_roots"))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListItems)
            .ToArray();
        var changedScope = task.State is null
            ? Array.Empty<string>()
            : Tokens(task.State, "changed_scope", "affected_scope", "changed_surfaces").Take(MaxListItems).ToArray();
        var sourceFingerprints = new[] { goal.State, task.State, architecture.Snapshot, environment.Snapshot }
            .Where(static value => value is not null && value.SourceFingerprint.Length == 64)
            .Select(static value => value!.SourceFingerprint)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var currentGeneration = new[]
        {
            goal.State?.Generation ?? 0,
            task.State?.Generation ?? 0,
            architecture.Snapshot?.Generation ?? 0,
            environment.Snapshot?.Generation ?? 0
        }.Max();
        var result = new ProjectRoutingFacts(
            "READY",
            projectId,
            metrics.Generation,
            Fact(goal.State, goal.State?.Key.EntityId),
            Fact(task.State, task.State?.Key.EntityId),
            goal.State?.GoalRevision ?? 0,
            Fact(architecture.Snapshot, architecture.Value),
            topology,
            task.State is null ? null : Token(task.State, "stage", "status"),
            changedScope,
            Fact(environment.Snapshot, environment.Value),
            sourceFingerprints,
            "PRO_STATE_PLANE",
            currentGeneration == metrics.Generation ? "CURRENT" : "BOUNDED_SNAPSHOT",
            currentGeneration,
            reads,
            0);
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result));
        return result with { ContextBytes = bytes };
    }

    private static AuthorityAwareFact<string>? Fact(StateSnapshot? snapshot, string? value)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(value)) return null;
        return new AuthorityAwareFact<string>(
            value,
            Authority(snapshot),
            snapshot.Generation,
            snapshot.SourceFingerprint,
            Freshness(snapshot),
            snapshot.Lifecycle.ToString().ToUpperInvariant());
    }

    private static bool IsCurrentAuthority(StateSnapshot snapshot) =>
        snapshot.Lifecycle == StateLifecycle.Valid &&
        !string.Equals(snapshot.Authority, LegacyRuntimeFinalizationFacts.Authority, StringComparison.Ordinal) &&
        string.Equals(snapshot.SchemaVersion, SqliteSchema.CurrentVersion, StringComparison.Ordinal);

    private static string Authority(StateSnapshot snapshot) => IsCurrentAuthority(snapshot)
        ? "AUTHORITATIVE_CURRENT"
        : string.Equals(snapshot.Authority, LegacyRuntimeFinalizationFacts.Authority, StringComparison.Ordinal)
            ? "IMPORTED_LEGACY"
            : "NON_AUTHORITATIVE";

    private static string Freshness(StateSnapshot snapshot) => snapshot.Lifecycle switch
    {
        StateLifecycle.Valid when IsCurrentAuthority(snapshot) => "CURRENT",
        StateLifecycle.Valid => "LEGACY_OR_UNVERIFIED",
        StateLifecycle.Archived => "HISTORICAL",
        _ => "STALE"
    };

    private static string? Token(StateSnapshot snapshot, params string[] names)
    {
        if (!TryPayload(snapshot, out var payload)) return null;
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        }
        return null;
    }

    private static IEnumerable<string> Tokens(StateSnapshot snapshot, params string[] names)
    {
        if (!TryPayload(snapshot, out var payload)) yield break;
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in value.EnumerateArray().Take(MaxListItems))
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    yield return item.GetString()!.Trim();
            }
            yield break;
        }
    }

    private static bool TryPayload(StateSnapshot snapshot, out JsonElement payload)
    {
        payload = default;
        if (Encoding.UTF8.GetByteCount(snapshot.PayloadJson) > MaxPayloadBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

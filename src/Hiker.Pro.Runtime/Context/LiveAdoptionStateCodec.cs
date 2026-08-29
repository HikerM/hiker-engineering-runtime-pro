using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

internal static class LiveAdoptionStateCodec
{
    private static readonly IReadOnlySet<string> LiveContextRelations = new HashSet<string>(
        new[] { "RESUMES_FROM", "CHECKPOINTED_AT", "PROVEN_BY", "BOUND_TO" },
        StringComparer.Ordinal);

    public static bool TryBinding(StateSnapshot snapshot, out ProviderSessionBindingState binding)
    {
        try
        {
            binding = JsonSerializer.Deserialize<ProviderSessionBindingState>(snapshot.PayloadJson, JsonOptions())!;
            return binding is not null;
        }
        catch (JsonException)
        {
            binding = default!;
            return false;
        }
    }

    public static bool TryAdoptedTask(StateSnapshot snapshot, out AdoptedTaskState task)
    {
        try
        {
            task = JsonSerializer.Deserialize<AdoptedTaskState>(snapshot.PayloadJson, JsonOptions())!;
            return task is not null;
        }
        catch (JsonException)
        {
            task = default!;
            return false;
        }
    }

    public static string ExecutionId(string projectId, string taskId, string bindingId) =>
        $"execution-{Hash($"{projectId}\0{taskId}\0{bindingId}")[..24]}";

    public static bool IsOwnedByBinding(StateSnapshot ownership, string projectId, string bindingId)
    {
        if (ownership.Lifecycle != StateLifecycle.Valid ||
            !string.Equals(ownership.Authority, LiveSessionAdoptionService.Authority, StringComparison.Ordinal) ||
            !TryPayload(ownership.PayloadJson, out var payload) ||
            !payload.TryGetProperty("task_id", out var taskIdValue) ||
            taskIdValue.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("writer_runtime_id", out var writerValue) ||
            writerValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var taskId = taskIdValue.GetString();
        return !string.IsNullOrWhiteSpace(taskId) &&
               string.Equals(taskId, ownership.Key.EntityId, StringComparison.Ordinal) &&
               string.Equals(writerValue.GetString(), ExecutionId(projectId, taskId, bindingId), StringComparison.Ordinal);
    }

    public static bool TryPayload(string json, out JsonElement payload)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    public static object SourcePayload(string json) =>
        TryPayload(json, out var payload) ? payload : new { preserved_source_fingerprint_only = true };

    public static string GoalSourceRef(StateSnapshot goal) =>
        Provenance(goal, "source_ref") ??
        (RuntimeStateCodec.IsLegacyImportedFact(goal) || goal.Authority == CommunityCurrentWorkInspector.Authority
            ? ".ai/governance/goal-contract.json"
            : $"state://goal/{goal.Key.EntityId}");

    public static string TaskSourceRef(StateSnapshot task) =>
        Provenance(task, "source_ref") ??
        (task.Authority == CommunityCurrentWorkInspector.Authority
            ? ".ai/runtime/task.json"
            : RuntimeStateCodec.IsLegacyImportedFact(task)
                ? $".ai/tasks/{task.Key.EntityId}.json"
                : $"state://task/{task.Key.EntityId}");

    public static string SourceAuthority(StateSnapshot state) =>
        Provenance(state, "source_authority") ?? state.Authority;

    public static string SourceFingerprint(StateSnapshot state) =>
        Provenance(state, "source_fingerprint") ?? state.SourceFingerprint;

    public static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        string sourceFingerprint,
        object payload,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        StateLifecycle.Valid,
        LiveSessionAdoptionService.Authority,
        SqliteSchema.CurrentVersion,
        sourceFingerprint,
        sourceFingerprint,
        JsonSerializer.Serialize(payload, JsonOptions()),
        retention);

    public static IReadOnlyList<DependencyEdge> ReplaceLiveContextEdges(
        IReadOnlyList<DependencyEdge> existing,
        params DependencyEdge[] current)
    {
        var changes = existing
            .Where(edge => LiveContextRelations.Contains(edge.Relation))
            .Select(edge => edge with { Lifecycle = StateLifecycle.Archived })
            .ToList();
        changes.AddRange(current);
        return changes;
    }

    public static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Provenance(StateSnapshot state, string property)
    {
        if (!string.Equals(state.Authority, LiveSessionAdoptionService.Authority, StringComparison.Ordinal) ||
            !TryPayload(state.PayloadJson, out var payload) ||
            !payload.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        return value.GetString();
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

internal sealed class CommunityCurrentWorkInspector(ProjectLayout layout, IStateStore store)
{
    private const long MaxSourceBytes = 2 * 1024 * 1024;
    public const string Authority = "COMMUNITY_5_18_LIVE_SOURCE";

    public async Task<CommunityCurrentWork?> InspectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var goalSource = await ReadSourceAsync("governance/goal-contract.json", EntityKinds.Goal, cancellationToken).ConfigureAwait(false);
        var taskSource = await ReadSourceAsync("runtime/task.json", EntityKinds.Task, cancellationToken).ConfigureAwait(false);
        if (goalSource is null || taskSource is null) return null;
        var goalId = Token(goalSource.Payload, "goal_id", "id", "project_id");
        var taskId = Token(taskSource.Payload, "task_id", "id");
        if (goalId is null || taskId is null) return null;
        var goalKey = new StateKey(projectId, EntityKinds.Goal, goalId).Normalize();
        var taskKey = new StateKey(projectId, EntityKinds.Task, taskId).Normalize();
        var goalCurrent = await store.ReadAsync(goalKey, cancellationToken).ConfigureAwait(false);
        var taskCurrent = await store.ReadAsync(taskKey, cancellationToken).ConfigureAwait(false);
        var revision = GoalRevision(goalSource.Payload);
        return new CommunityCurrentWork(
            SnapshotOrCurrent(goalKey, goalCurrent, revision, goalSource),
            SnapshotOrCurrent(taskKey, taskCurrent, TaskGoalRevision(taskSource.Payload, revision), taskSource),
            4);
    }

    private async Task<CommunitySource?> ReadSourceAsync(
        string relative,
        string kind,
        CancellationToken cancellationToken)
    {
        var path = layout.ResolveAiRelativePath(relative);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaxSourceBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var payload = document.RootElement.Clone();
        return new CommunitySource(
            kind,
            relative,
            JsonSerializer.Serialize(payload),
            payload,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            info.LastWriteTimeUtc);
    }

    private static StateSnapshot Snapshot(
        StateKey key,
        StateSnapshot? current,
        long revision,
        CommunitySource source) => new(
        key,
        current?.Generation ?? 0,
        revision,
        StateLifecycle.Valid,
        Authority,
        "2.0.0",
        source.Fingerprint,
        source.Fingerprint,
        source.Json,
        source.LastWriteAt,
        RetentionTier.Hot);

    private static StateSnapshot SnapshotOrCurrent(
        StateKey key,
        StateSnapshot? current,
        long revision,
        CommunitySource source)
    {
        if (current is { Lifecycle: StateLifecycle.Valid } &&
            string.Equals(current.Authority, LiveSessionAdoptionService.Authority, StringComparison.Ordinal) &&
            string.Equals(current.SchemaVersion, SqliteSchema.CurrentVersion, StringComparison.Ordinal) &&
            TrySourceFingerprint(current.PayloadJson, out var adoptedSource) &&
            string.Equals(adoptedSource, source.Fingerprint, StringComparison.Ordinal))
        {
            return current;
        }

        return Snapshot(key, current, revision, source);
    }

    private static bool TrySourceFingerprint(string payloadJson, out string sourceFingerprint)
    {
        sourceFingerprint = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("source_fingerprint", out var fingerprint) ||
                fingerprint.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(fingerprint.GetString()))
            {
                return false;
            }

            sourceFingerprint = fingerprint.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Token(JsonElement payload, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (payload.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                var token = value.GetString()!.Trim();
                try
                {
                    return new StateKey("project", EntityKinds.Task, token).Normalize().EntityId;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static long GoalRevision(JsonElement payload)
    {
        foreach (var property in new[] { "goal_revision", "revision" })
        {
            if (payload.TryGetProperty(property, out var value) && value.TryGetInt64(out var revision))
            {
                return Math.Max(0, revision);
            }
        }

        return 0;
    }

    private static long TaskGoalRevision(JsonElement payload, long currentGoalRevision)
    {
        if (payload.TryGetProperty("goal_revision", out var value) && value.TryGetInt64(out var revision))
        {
            return Math.Max(0, revision);
        }

        return currentGoalRevision;
    }

    private sealed record CommunitySource(
        string Kind,
        string Relative,
        string Json,
        JsonElement Payload,
        string Fingerprint,
        DateTimeOffset LastWriteAt);
}

internal sealed record CommunityCurrentWork(
    StateSnapshot Goal,
    StateSnapshot Task,
    int Reads);

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
        var revision = Revision(goalSource.Payload);
        return new CommunityCurrentWork(
            Snapshot(goalKey, goalCurrent, revision, goalSource),
            Snapshot(taskKey, taskCurrent, Math.Max(revision, Revision(taskSource.Payload)), taskSource),
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

    private static long Revision(JsonElement payload)
    {
        foreach (var property in new[] { "goal_revision", "revision", "plan_version" })
        {
            if (payload.TryGetProperty(property, out var value) && value.TryGetInt64(out var revision))
            {
                return Math.Max(0, revision);
            }
        }

        return 0;
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

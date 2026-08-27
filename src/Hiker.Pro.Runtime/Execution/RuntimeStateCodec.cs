using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

namespace Hiker.Pro.Execution;

internal static class RuntimeStateCodec
{
    private const string LegacyImportAuthority = "COMMUNITY_5_18_IMPORT";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static bool IsLegacyImportedFact(StateSnapshot snapshot) =>
        string.Equals(snapshot.Authority, LegacyImportAuthority, StringComparison.Ordinal);

    public static T Parse<T>(StateSnapshot snapshot)
    {
        if (IsLegacyImportedFact(snapshot))
        {
            throw new InvalidDataException($"Legacy imported {snapshot.Key.Kind} fact is not an active 5.19 runtime state");
        }

        var value = JsonSerializer.Deserialize<T>(snapshot.PayloadJson, Options)
            ?? throw new InvalidDataException($"Invalid {snapshot.Key.Kind} state payload");
        ValidateRequiredFields(value);
        return value;
    }

    public static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        StateLifecycle lifecycle,
        string sourceFingerprint,
        object payload,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        lifecycle,
        "RUNTIME_COORDINATOR",
        "3.0.0",
        sourceFingerprint,
        sourceFingerprint,
        Json(payload),
        retention);

    private static void ValidateRequiredFields<T>(T value)
    {
        switch (value)
        {
            case RuntimeOwnershipState ownership:
                Required(ownership.TaskId, "task_id", EntityKinds.Ownership);
                Required(ownership.WriterRuntimeId, "writer_runtime_id", EntityKinds.Ownership);
                Required(ownership.WorkspaceRoot, "workspace_root", EntityKinds.Ownership);
                Required(ownership.WorkspaceFingerprint, "workspace_fingerprint", EntityKinds.Ownership);
                RequiredTimestamp(ownership.UpdatedAt, "updated_at", EntityKinds.Ownership);
                break;
            case RuntimeWorktreeState worktree:
                Required(worktree.WorktreeId, "worktree_id", EntityKinds.Worktree);
                Required(worktree.TaskId, "task_id", EntityKinds.Worktree);
                Required(worktree.WriterRuntimeId, "writer_runtime_id", EntityKinds.Worktree);
                Required(worktree.Root, "root", EntityKinds.Worktree);
                Required(worktree.GitCommonDirectory, "git_common_directory", EntityKinds.Worktree);
                Required(worktree.Head, "head", EntityKinds.Worktree);
                Required(worktree.Branch, "branch", EntityKinds.Worktree);
                if (worktree.PredictedAffectedScopes is null ||
                    worktree.PredictedAffectedScopes.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidDataException($"{EntityKinds.Worktree} requires valid predicted_affected_scopes");
                }
                Required(worktree.Status, "status", EntityKinds.Worktree);
                RequiredTimestamp(worktree.UpdatedAt, "updated_at", EntityKinds.Worktree);
                break;
        }
    }

    private static void Required(string? value, string field, string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{kind} requires {field}");
        }
    }

    private static void RequiredTimestamp(DateTimeOffset value, string field, string kind)
    {
        if (value == default)
        {
            throw new InvalidDataException($"{kind} requires {field}");
        }
    }
}

internal sealed record RuntimeTaskState(
    string TaskId,
    string RuntimeId,
    string AdapterId,
    RuntimeLifecycle Status,
    long GoalRevision,
    string WorkspaceFingerprint,
    string? CheckpointId,
    DateTimeOffset UpdatedAt);

internal sealed record RuntimeAgentState(
    string RuntimeId,
    string AdapterId,
    RuntimeLifecycle Status,
    RuntimeProcessIdentity? ProcessIdentity,
    string RecoveryTokenFingerprint,
    DateTimeOffset UpdatedAt);

internal sealed record RuntimeOwnershipState(
    string TaskId,
    string WriterRuntimeId,
    string WorkspaceRoot,
    string WorkspaceFingerprint,
    DateTimeOffset UpdatedAt);

internal sealed record RuntimeWorktreeState(
    string WorktreeId,
    string TaskId,
    string WriterRuntimeId,
    string Root,
    string GitCommonDirectory,
    string Head,
    string Branch,
    IReadOnlyList<string> PredictedAffectedScopes,
    string Status,
    DateTimeOffset UpdatedAt);

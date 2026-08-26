using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

namespace Hiker.Pro.Execution;

internal static class RuntimeStateCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(StateSnapshot snapshot) =>
        JsonSerializer.Deserialize<T>(snapshot.PayloadJson, Options)
        ?? throw new InvalidDataException($"Invalid {snapshot.Key.Kind} state payload");

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

using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public static class LiveAdoptionProtocol
{
    public const int CurrentVersion = 2;
}

public static class LiveAdoptionStatuses
{
    public const string Adopted = "LIVE_SESSION_ADOPTED";
    public const string AlreadyAdopted = "ALREADY_ADOPTED";
    public const string WaitSafeBoundary = "WAIT_SAFE_BOUNDARY";
    public const string AmbiguousGoal = "AMBIGUOUS_CURRENT_GOAL";
    public const string GoalRequired = "GOAL_ADOPTION_REQUIRED";
    public const string AmbiguousTask = "AMBIGUOUS_CURRENT_TASK";
    public const string TaskRequired = "TASK_ADOPTION_REQUIRED";
    public const string Blocked = "BLOCKED";
}

public sealed record LiveAdoptionRequest(
    string Provider,
    string ProviderSessionId,
    string BoundaryProof,
    string? GoalId = null,
    string? TaskId = null,
    int MaxContextBytes = 16_384);

public sealed record LiveAdoptionResult(
    string Status,
    string? ProjectId,
    string? GoalId,
    string? TaskId,
    string? CheckpointId,
    string Provider,
    string ProviderSessionFingerprint,
    bool Committed,
    bool Idempotent,
    string NextAction,
    int StateReads,
    int StateWrites,
    int ContextBytes,
    VerifiedContextPackage? Context,
    ProjectPreflightResult Project,
    IReadOnlyList<string> Reasons);

public static class LiveTurnCheckpointStatuses
{
    public const string Checkpointed = "TURN_CHECKPOINTED";
    public const string AlreadyCheckpointed = "ALREADY_CHECKPOINTED";
    public const string NotAdopted = "SESSION_NOT_ADOPTED";
    public const string Blocked = "BLOCKED";
}

public sealed record LiveTurnCheckpointRequest(
    string Provider,
    string ProviderSessionId,
    string Outcome = "TURN_TERMINAL",
    IReadOnlyList<string>? ChangedSurfaces = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    IReadOnlyList<string>? TestRefs = null,
    int MaxContextBytes = 16_384);

public sealed class LiveTurnCheckpointValidationException(
    string reasonCode,
    string field,
    string message,
    IReadOnlyList<string>? acceptedValues = null) : ArgumentException(message, field)
{
    public string ReasonCode { get; } = reasonCode;

    public string Field { get; } = field;

    public IReadOnlyList<string> AcceptedValues { get; } = acceptedValues ?? Array.Empty<string>();
}

public sealed record LiveTurnCheckpointResult(
    string Status,
    string? ProjectId,
    string? GoalId,
    string? TaskId,
    string? CheckpointId,
    string ProviderSessionFingerprint,
    bool Committed,
    bool Idempotent,
    bool OwnershipReleased,
    int StateReads,
    int StateWrites,
    int ContextBytes,
    string? ContextFingerprint,
    IReadOnlyList<string> Reasons);

internal sealed record ProviderSessionBindingState(
    string BindingId,
    string Provider,
    string ProviderSessionFingerprint,
    string ProjectId,
    string GoalId,
    string TaskId,
    string CheckpointId,
    string ExecutionId,
    string Status,
    DateTimeOffset AttachedAt);

internal sealed record AdoptedGoalState(
    string SchemaVersion,
    string GoalId,
    string Status,
    long Revision,
    string SourceAuthority,
    string SourceFingerprint,
    string SourceRef,
    object SourcePayload);

internal sealed record AdoptedTaskState(
    string SchemaVersion,
    string TaskId,
    string GoalId,
    string Status,
    string ProviderBindingId,
    string ExecutionId,
    string CheckpointId,
    string SourceAuthority,
    string SourceFingerprint,
    string SourceRef,
    DateTimeOffset UpdatedAt);

internal sealed record AdoptionCheckpointState(
    string SchemaVersion,
    string CheckpointId,
    string TaskId,
    string GoalId,
    string ProviderBindingId,
    string? SourceCheckpointId,
    string? SourceCheckpointFingerprint,
    DateTimeOffset CreatedAt,
    string Status);

internal sealed record LiveTurnCheckpointState(
    string SchemaVersion,
    string CheckpointId,
    string TaskId,
    string GoalId,
    string ProviderBindingId,
    string SourceCheckpointId,
    string Outcome,
    IReadOnlyList<string> ChangedSurfaces,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> TestRefs,
    string ContextFingerprint,
    DateTimeOffset CreatedAt,
    string Status);

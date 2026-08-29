using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public static class CurrentAuthorityStatuses
{
    public const string Established = "ESTABLISHED";
    public const string AlreadyEstablished = "ALREADY_ESTABLISHED";
    public const string AdoptExistingAuthority = "ADOPT_EXISTING_AUTHORITY";
    public const string WaitSafeBoundary = "WAIT_SAFE_BOUNDARY";
    public const string AuthorityAmbiguous = "AUTHORITY_AMBIGUOUS";
    public const string AuthorityConflict = "AUTHORITY_CONFLICT";
    public const string ProviderSessionMismatch = "PROVIDER_SESSION_MISMATCH";
    public const string ProjectMismatch = "PROJECT_MISMATCH";
    public const string ExecutionFailure = "EXECUTION_FAILURE";
}

public static class CurrentAuthoritySources
{
    public static IReadOnlySet<string> Goal { get; } = new HashSet<string>(
        new[] { "CONTROLLER_CURRENT_GOAL", "PROVIDER_CURRENT_GOAL", "RUNTIME_CURRENT_GOAL_CONTRACT" },
        StringComparer.Ordinal);

    public static IReadOnlySet<string> Task { get; } = new HashSet<string>(
        new[] { "CONTROLLER_CURRENT_ACTIVE_TASK", "PROVIDER_CURRENT_ACTIVE_TASK", "RUNTIME_CURRENT_TASK_CONTRACT" },
        StringComparer.Ordinal);
}

public sealed record CurrentAuthorityFact(
    string Statement,
    string State,
    string AuthoritySource,
    string SourceFingerprint,
    long AuthorityGeneration);

public sealed record CurrentAuthorityEstablishmentRequest(
    string ProjectId,
    string Provider,
    string ProviderSessionId,
    string BoundaryProof,
    CurrentAuthorityFact Goal,
    CurrentAuthorityFact Task,
    string? OperationId = null);

public sealed record CurrentAuthorityEstablishmentResult(
    string Status,
    string? ProjectId,
    string? GoalId,
    string? TaskId,
    string GoalAuthoritySource,
    string TaskAuthoritySource,
    string Provider,
    string ProviderSessionFingerprint,
    string? OperationId,
    bool SafeBoundary,
    long StateGenerationBefore,
    long StateGenerationAfter,
    int StateReads,
    int StateWrites,
    bool IdempotentReplay,
    ProjectPreflightResult Project,
    IReadOnlyList<string> Reasons);

internal static class LiveSafeBoundary
{
    private static readonly IReadOnlySet<string> Accepted = new HashSet<string>(
        new[] { "TURN_TERMINAL", "NATURAL_CHECKPOINT", "NEW_TASK", "RECOVERY_CHECKPOINT" },
        StringComparer.Ordinal);

    public static bool IsAccepted(string value) =>
        !string.IsNullOrWhiteSpace(value) && Accepted.Contains(value.Trim().ToUpperInvariant());
}

internal enum CurrentAuthorityFailurePoint
{
    BeforePreparation,
    AfterGoalPreparation,
    BeforeTaskPreparation,
    BeforeCasCommit,
    AfterDurableCommitBeforeResponse
}

internal interface ICurrentAuthorityFailureInjector
{
    void ThrowIfRequested(CurrentAuthorityFailurePoint point);
}

internal sealed class NoCurrentAuthorityFailureInjector : ICurrentAuthorityFailureInjector
{
    public static NoCurrentAuthorityFailureInjector Instance { get; } = new();

    public void ThrowIfRequested(CurrentAuthorityFailurePoint point)
    {
    }
}

internal sealed record CurrentAuthorityEntityPayload(
    string SchemaVersion,
    string EntityType,
    string? GoalId,
    string? TaskId,
    string Statement,
    string Status,
    string AuthoritySource,
    string SourceFingerprint,
    long AuthorityGeneration);

internal sealed record CurrentAuthorityOperationReceipt(
    string SchemaVersion,
    string ProjectId,
    string GoalId,
    string TaskId,
    string GoalAuthoritySource,
    string TaskAuthoritySource,
    string ProviderSessionFingerprint,
    string OperationId);

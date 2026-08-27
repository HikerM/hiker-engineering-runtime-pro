using Hiker.Pro.State;
using Hiker.Pro.Storage;

namespace Hiker.Pro.Context;

public enum ContextTemperature
{
    Hot,
    Warm,
    Cold
}

public sealed record ContextRequest(
    string ProjectId,
    string TaskId,
    string GoalId,
    int MaxBytes = 32_768,
    int MaxItems = 64,
    bool IncludeWarm = true);

public sealed record ContextItem(
    string Ref,
    string Kind,
    string EntityId,
    long Generation,
    long GoalRevision,
    StateLifecycle Lifecycle,
    ContextTemperature Temperature,
    string Authority,
    RetrievalAuthority AuthorityClass,
    string SourceFingerprint,
    string PayloadJson);

public sealed record VerifiedContextPackage(
    string Format,
    string ProjectId,
    string TaskId,
    string GoalId,
    long StoreGeneration,
    IReadOnlyList<ContextItem> Items,
    int EncodedBytes,
    int StateReads,
    bool Truncated,
    string Fingerprint,
    DateTimeOffset ResolvedAt,
    RetrievalReceipt? RetrievalReceipt = null);

public sealed record ProjectResumeSummary(
    string ProjectId,
    ContextItem? CurrentGoal,
    IReadOnlyList<ContextItem> OpenTasks,
    IReadOnlyList<ContextItem> BlockedTasks,
    IReadOnlyList<ContextItem> LatestDecisions,
    IReadOnlyList<ContextItem> ValidEvidence,
    IReadOnlyList<ContextItem> StaleEvidence,
    IReadOnlyList<ContextItem> ActiveWorktrees,
    IReadOnlyList<ContextItem> RuntimeLeases,
    string RecommendedNextAction,
    int EncodedBytes,
    bool Truncated);

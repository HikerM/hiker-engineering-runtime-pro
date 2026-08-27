using System.Collections.ObjectModel;

namespace Hiker.Pro.State;

public enum StateLifecycle
{
    Valid,
    Stale,
    Superseded,
    Orphaned,
    Corrupt,
    Incompatible,
    Archived
}

public enum RetentionTier
{
    Hot,
    Warm,
    Cold,
    Archived,
    Cache,
    Temp
}

public enum EvidenceLevel
{
    Implemented,
    DeterministicVerified,
    SimulationProven,
    RealIntegrationProven,
    FieldProven
}

public static class EntityKinds
{
    public const string Project = "PROJECT";
    public const string Goal = "GOAL";
    public const string GoalRevision = "GOAL_REVISION";
    public const string Task = "TASK";
    public const string TaskDependency = "TASK_DEPENDENCY";
    public const string Decision = "DECISION";
    public const string Ownership = "OWNERSHIP";
    public const string Worktree = "WORKTREE";
    public const string RuntimeLease = "RUNTIME_LEASE";
    public const string Agent = "AGENT";
    public const string Checkpoint = "CHECKPOINT";
    public const string Evidence = "EVIDENCE";
    public const string Candidate = "CANDIDATE";
    public const string Release = "RELEASE";
    public const string RuntimeProfile = "RUNTIME_PROFILE";
    public const string Requirement = "REQUIREMENT";
    public const string Code = "CODE";
    public const string Database = "DATABASE";
    public const string Ui = "UI";
    public const string Test = "TEST";
    public const string Script = "SCRIPT";
    public const string Middleware = "MIDDLEWARE";
    public const string Service = "SERVICE";
    public const string Contract = "CONTRACT";
    public const string ResourceLease = "RESOURCE_LEASE";
    public const string Migration = "MIGRATION";
}

public sealed record StateKey(string ProjectId, string Kind, string EntityId)
{
    public StateKey Normalize() => new(
        RequiredToken(ProjectId, nameof(ProjectId)),
        RequiredToken(Kind, nameof(Kind)).ToUpperInvariant(),
        RequiredToken(EntityId, nameof(EntityId)));

    private static string RequiredToken(string value, string name)
    {
        var token = value.Trim();
        if (token.Length is 0 or > 160)
        {
            throw new ArgumentException($"{name} must contain 1-160 characters", name);
        }

        return token;
    }
}

public sealed record StateSnapshot(
    StateKey Key,
    long Generation,
    long GoalRevision,
    StateLifecycle Lifecycle,
    string Authority,
    string SchemaVersion,
    string SourceFingerprint,
    string DependencyFingerprint,
    string PayloadJson,
    DateTimeOffset UpdatedAt,
    RetentionTier Retention);

public sealed record StateMutation(
    StateKey Key,
    long ExpectedGeneration,
    long GoalRevision,
    StateLifecycle Lifecycle,
    string Authority,
    string SchemaVersion,
    string SourceFingerprint,
    string DependencyFingerprint,
    string PayloadJson,
    RetentionTier Retention = RetentionTier.Hot);

public sealed record EngineeringEvent(
    string EventId,
    string ProjectId,
    string EventType,
    string? EntityKind,
    string? EntityId,
    long Generation,
    string SummaryCode,
    string PayloadJson,
    DateTimeOffset OccurredAt);

public sealed record TransactionRequest(
    string OperationId,
    StateMutation Mutation,
    string EventType,
    string SummaryCode,
    string EventPayloadJson,
    IReadOnlyList<DependencyEdge>? DependencyChanges = null);

public sealed record BatchTransactionRequest(
    string OperationId,
    IReadOnlyList<StateMutation> Mutations,
    string EventType,
    string SummaryCode,
    string EventPayloadJson,
    IReadOnlyList<DependencyEdge>? DependencyChanges = null,
    string? ResultJson = null);

public sealed record TransactionResult(
    bool Committed,
    bool Conflict,
    long CurrentGeneration,
    StateSnapshot? Snapshot,
    string Status);

public sealed record BatchTransactionResult(
    bool Committed,
    bool Conflict,
    long CurrentGeneration,
    IReadOnlyList<StateSnapshot> Snapshots,
    string Status);

public sealed record OperationReceipt(
    string OperationId,
    string ProjectId,
    string OperationType,
    string RequestFingerprint,
    string Status,
    long? Generation,
    string EntityKind,
    string EntityId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ResultJson);

public sealed record InvalidationRequest(
    string OperationId,
    string ProjectId,
    IReadOnlyCollection<StateKey> Keys,
    long GoalRevision,
    string ReasonCode);

public sealed record InvalidationResult(bool Committed, bool Conflict, int Affected, long Generation, string Status);

public sealed record DependencyEdge(
    string ProjectId,
    string FromKind,
    string FromId,
    string ToKind,
    string ToId,
    string Relation,
    string Fingerprint,
    StateLifecycle Lifecycle = StateLifecycle.Valid);

public sealed record MaterializationRequest(
    string ProjectId,
    string ViewName,
    long SourceGeneration,
    string PayloadJson,
    string Fingerprint);

public sealed record MaterializationResult(bool Updated, long SourceGeneration, string Fingerprint);

public sealed record MigrationResult(string FromVersion, string ToVersion, bool Applied, string Status);

public sealed record HealthFinding(string Code, string Severity, string Entity, string Detail, bool Repairable);

public sealed record HealthReport(string Status, IReadOnlyList<HealthFinding> Findings)
{
    public static HealthReport Healthy { get; } = new("HEALTHY", Array.Empty<HealthFinding>());
}

public sealed record RepairReport(string Status, int Repaired, IReadOnlyList<HealthFinding> Remaining);

public sealed record GcReport(int Archived, int DeletedCache, int DeletedTemp, int OrphanArtifacts);

public sealed record StoreMetrics(long Generation, long Entities, long Events, long Dependencies, long Artifacts);

public sealed record StateBackupResult(
    string Status,
    string BackupPath,
    string ManifestPath,
    string Sha256,
    long Bytes,
    DateTimeOffset CreatedAt);

public sealed record StateRestoreResult(string Status, string SourceSha256, string SafetyBackupPath, HealthReport Health);

public sealed record StateExportResult(
    string Status,
    string OutputPath,
    string Sha256,
    long Bytes,
    long Generation,
    long Entities,
    long Operations,
    long Events,
    long Dependencies,
    long Artifacts,
    DateTimeOffset CreatedAt);

public sealed record CommunityImportReport(
    string Status,
    string ProjectId,
    int Discovered,
    int Imported,
    int Replayed,
    string SourceFingerprint,
    IReadOnlyList<string> Rejected,
    LegacyInventorySummary? Inventory = null);

public sealed record ArtifactRecord(
    string ArtifactId,
    string ProjectId,
    string Type,
    string Sha256,
    long Bytes,
    string RelativeRef,
    string? TaskId,
    string? EvidenceId,
    string SourceFingerprint,
    RetentionTier Retention,
    DateTimeOffset CreatedAt);

public static class ContractCollections
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

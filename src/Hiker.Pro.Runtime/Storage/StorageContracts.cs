using Hiker.Pro.State;

namespace Hiker.Pro.Storage;

public enum StorageHealthMode
{
    Fast,
    Deep
}

public enum StorageReachability
{
    Live,
    Retained,
    Cold,
    Archivable,
    Unreachable
}

public enum RetrievalAuthority
{
    AuthoritativeCurrent,
    VerifiedDerived,
    RuntimeObservation,
    Historical,
    LegacyPreserved,
    Cache
}

public sealed record ArtifactLink(
    string ProjectId,
    string Sha256,
    string RefKind,
    string RefId,
    string Purpose,
    DateTimeOffset CreatedAt);

public sealed record ArtifactReachabilityRecord(
    ArtifactRecord Artifact,
    StorageReachability Reachability,
    IReadOnlyList<ArtifactLink> Links,
    string ReasonCode);

public sealed record ArtifactGcResult(
    string Status,
    int Marked,
    int DeletedMetadata,
    int DeletedFiles,
    long ReclaimedBytes,
    long Generation,
    bool Idempotent);

public sealed record RuntimeStateSnapshot(
    string SnapshotId,
    string ProjectId,
    long Generation,
    long EventSequence,
    string SchemaVersion,
    string Fingerprint,
    IReadOnlyList<StateSnapshot> Entities,
    IReadOnlyList<DependencyEdge> Dependencies,
    DateTimeOffset CreatedAt);

public sealed record SnapshotRecoveryProjection(
    string SnapshotId,
    long SnapshotGeneration,
    long CurrentGeneration,
    IReadOnlyList<StateSnapshot> Entities,
    int DeltasApplied,
    string Fingerprint);

public sealed record EventRollupRequest(
    string OperationId,
    string ProjectId,
    int KeepRecentEvents = 1_000,
    int MaximumEvents = 100_000);

public sealed record EventRollupResult(
    string Status,
    string? SegmentId,
    string? SnapshotId,
    int EventsArchived,
    int DeltasArchived,
    long FirstSequence,
    long LastSequence,
    long Bytes,
    string? Sha256,
    bool Idempotent);

public sealed record ArchivedEventSegment(
    string SegmentId,
    string ProjectId,
    long FromGeneration,
    long ToGeneration,
    long FirstSequence,
    long LastSequence,
    int EventCount,
    int DeltaCount,
    string RelativeRef,
    string Sha256,
    long Bytes,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record OperationRetentionResult(
    string Status,
    int Archived,
    int Active,
    int RecentCompleted,
    int RecoveryRequired,
    bool Idempotent);

public sealed record DependencyRetentionResult(string Status, int Archived, int Current, bool Idempotent);

public sealed record EntityRetentionResult(
    string Status,
    int ArchivedEntities,
    int ArchivedDependencies,
    int CurrentEntities,
    bool Idempotent);

public sealed record StorageCompactionResult(
    string Status,
    long BytesBefore,
    long BytesAfter,
    long ReclaimedBytes,
    double FreelistRatioBefore,
    bool BackupCreated,
    string? BackupSha256,
    bool Idempotent);

public sealed record StorageBudget(
    long HotStateBytes,
    long WarmStateBytes,
    long CacheBytes,
    long TempBytes,
    long ArtifactBytes,
    long ArchiveBytes);

public sealed record StorageBudgetReport(
    string Status,
    IReadOnlyList<string> Exceeded,
    IReadOnlyList<string> RecommendedActions,
    StorageMetrics Metrics);

public sealed record StorageMetrics(
    long AiTotalBytes,
    long StateDbBytes,
    long ArtifactBytes,
    long ArchiveBytes,
    long CacheBytes,
    long TempBytes,
    long PreservedBytes,
    long HotStateBytes,
    long WarmStateBytes,
    long HotEntities,
    long WarmEntities,
    long ColdEntities,
    long ArchivedEntities,
    long HotEvents,
    long HotOperations,
    long ArchivedOperations,
    long CurrentDependencies,
    long HistoricalDependencies,
    long ArtifactCount,
    long VerifiedArtifactCount,
    long Generation);

public sealed record StorageDoctorReport(
    string Status,
    StorageHealthMode Mode,
    IReadOnlyList<HealthFinding> Findings,
    IReadOnlyList<string> HealthIndicators,
    StorageMetrics Metrics,
    long ArtifactBytesHashed,
    int ArtifactFilesScanned,
    int ArchiveFilesScanned,
    double LatencyMs);

public sealed record RetrievalReceipt(
    string ProjectId,
    string GoalId,
    string TaskId,
    long StoreGeneration,
    IReadOnlyList<string> SelectedItems,
    int SelectedBytes,
    int StateReads,
    int GraphQueries,
    int ArchiveReads,
    int ColdReads,
    int StaleRejected,
    int SupersededRejected,
    int AuthorityConflicts,
    string ContextFingerprint,
    string ReceiptFingerprint,
    double LatencyMs,
    DateTimeOffset ResolvedAt);

public sealed record RetrievalIntegrityResult(
    Hiker.Pro.Context.VerifiedContextPackage Context,
    RetrievalReceipt Receipt);

public sealed record MigrationInventorySegment(
    string ProjectId,
    string RelativeRoot,
    string Fingerprint,
    long Files,
    long Bytes,
    string Status,
    DateTimeOffset VerifiedAt);

public sealed record IncrementalMigrationInventoryResult(
    string Status,
    int ScannedSegments,
    int ReusedSegments,
    int ChangedSegments,
    IReadOnlyList<MigrationInventorySegment> Segments);

public sealed record IncrementalArtifactBackupResult(
    string Status,
    string BackupSetId,
    string ManifestPath,
    string ManifestSha256,
    StateBackupResult StateBackup,
    int ArtifactsConsidered,
    int ArtifactsCopied,
    int ArtifactsReused,
    long BytesCopied,
    DateTimeOffset CreatedAt);

public sealed record StorageMaintenancePolicy(
    DateTimeOffset HistoricalBefore,
    DateTimeOffset ArtifactRetentionBefore,
    int KeepRecentEvents = 1_000,
    int KeepRecentOperations = 10_000,
    bool CompactWhenSafe = false);

public sealed record StorageLifecycleMaintenanceResult(
    string Status,
    EventRollupResult Events,
    OperationRetentionResult Operations,
    DependencyRetentionResult Dependencies,
    EntityRetentionResult Entities,
    ArtifactGcResult Artifacts,
    StorageCompactionResult? Compaction,
    StorageMetrics Before,
    StorageMetrics After);

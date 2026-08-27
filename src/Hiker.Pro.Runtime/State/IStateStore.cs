namespace Hiker.Pro.State;

using Hiker.Pro.Storage;

public interface IStateStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<StateSnapshot?> ReadAsync(StateKey key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StateSnapshot>> ReadAsync(
        string projectId,
        string kind,
        StateLifecycle? lifecycle = StateLifecycle.Valid,
        int limit = 100,
        CancellationToken cancellationToken = default);
    Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken cancellationToken = default);
    Task<TransactionResult> CompareAndSwapAsync(TransactionRequest request, CancellationToken cancellationToken = default);
    Task<BatchTransactionResult> CompareAndSwapBatchAsync(BatchTransactionRequest request, CancellationToken cancellationToken = default);
    Task<OperationReceipt?> ReadOperationReceiptAsync(string operationId, CancellationToken cancellationToken = default);
    Task<long> AppendEventAsync(EngineeringEvent engineeringEvent, CancellationToken cancellationToken = default);
    Task<InvalidationResult> InvalidateAsync(InvalidationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DependencyEdge>> QueryDependenciesAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DependencyEdge>> QueryDependenciesBoundedAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        int limit,
        CancellationToken cancellationToken = default);
    Task<MaterializationResult> MaterializeAsync(MaterializationRequest request, CancellationToken cancellationToken = default);
    Task<MigrationResult> MigrateAsync(string targetVersion, CancellationToken cancellationToken = default);
    Task<HealthReport> HealthCheckAsync(CancellationToken cancellationToken = default);
    Task<RepairReport> RepairAsync(CancellationToken cancellationToken = default);
    Task<GcReport> GarbageCollectAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
    Task<StoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
    Task<ProjectIdentityInventory> InspectProjectIdentityAsync(CancellationToken cancellationToken = default);
    Task<ProjectForkResult> ForkProjectAsync(ProjectForkRequest request, CancellationToken cancellationToken = default);
    Task<LegacyRuntimeInspection> InspectLegacyRuntimeAsync(
        string projectId,
        CancellationToken cancellationToken = default);
    Task<LegacyRuntimeFinalizeResult> FinalizeLegacyRuntimeAsync(
        LegacyRuntimeFinalizeRequest request,
        CancellationToken cancellationToken = default);
    Task<StateExportResult> ExportAsync(
        string projectId,
        string? outputPath = null,
        CancellationToken cancellationToken = default);
    Task RegisterArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken = default);
    Task LinkArtifactAsync(ArtifactLink link, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactReachabilityRecord>> AnalyzeArtifactReachabilityAsync(
        string projectId,
        DateTimeOffset retentionCutoff,
        int limit = 100_000,
        CancellationToken cancellationToken = default);
    Task<ArtifactGcResult> CollectUnreachableArtifactsAsync(
        string operationId,
        string projectId,
        DateTimeOffset retentionCutoff,
        CancellationToken cancellationToken = default);
    Task<RuntimeStateSnapshot> CreateRuntimeSnapshotAsync(
        string operationId,
        string projectId,
        CancellationToken cancellationToken = default);
    Task<RuntimeStateSnapshot?> ReadRuntimeSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default);
    Task<SnapshotRecoveryProjection> ReconstructSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default);
    Task<EventRollupResult> RollupEventsAsync(EventRollupRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EngineeringEvent>> ReadEventSegmentAsync(
        string projectId,
        string segmentId,
        CancellationToken cancellationToken = default);
    Task<OperationRetentionResult> RetainOperationsAsync(
        string operationId,
        string projectId,
        DateTimeOffset completedBefore,
        int keepRecent,
        CancellationToken cancellationToken = default);
    Task<DependencyRetentionResult> RetainDependenciesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);
    Task<EntityRetentionResult> RetainEntitiesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);
    Task<StateSnapshot?> ReadArchivedStateAsync(StateKey key, CancellationToken cancellationToken = default);
    Task<StorageMetrics> GetStorageMetricsAsync(string projectId, CancellationToken cancellationToken = default);
    Task<StorageDoctorReport> StorageHealthCheckAsync(
        string projectId,
        StorageHealthMode mode,
        CancellationToken cancellationToken = default);
    Task<StorageCompactionResult> CompactStorageAsync(
        string operationId,
        string projectId,
        CancellationToken cancellationToken = default);
    Task<IncrementalMigrationInventoryResult> InventoryLegacyStorageAsync(
        string projectId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> ReferencedArtifactHashesAsync(CancellationToken cancellationToken = default);
}

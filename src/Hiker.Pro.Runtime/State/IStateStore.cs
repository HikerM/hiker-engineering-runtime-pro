namespace Hiker.Pro.State;

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
    Task<MaterializationResult> MaterializeAsync(MaterializationRequest request, CancellationToken cancellationToken = default);
    Task<MigrationResult> MigrateAsync(string targetVersion, CancellationToken cancellationToken = default);
    Task<HealthReport> HealthCheckAsync(CancellationToken cancellationToken = default);
    Task<RepairReport> RepairAsync(CancellationToken cancellationToken = default);
    Task<GcReport> GarbageCollectAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
    Task<StoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
    Task<ProjectIdentityInventory> InspectProjectIdentityAsync(CancellationToken cancellationToken = default);
    Task<ProjectForkResult> ForkProjectAsync(ProjectForkRequest request, CancellationToken cancellationToken = default);
    Task<StateExportResult> ExportAsync(
        string projectId,
        string? outputPath = null,
        CancellationToken cancellationToken = default);
    Task RegisterArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> ReferencedArtifactHashesAsync(CancellationToken cancellationToken = default);
}

namespace Hiker.Pro.State;

using Hiker.Pro.Storage;

public sealed record ImmutableReasoningSnapshot(
    StateSnapshot? State,
    long StoreGeneration,
    DateTimeOffset ReadAt,
    string SnapshotToken);

public sealed class StateCoordinator
{
    private readonly IStateStore _store;
    private readonly SemaphoreSlim _writer = new(1, 1);

    public StateCoordinator(IStateStore store)
    {
        _store = store;
    }

    public async Task<ImmutableReasoningSnapshot> ReadForReasoningAsync(
        StateKey key,
        CancellationToken cancellationToken = default)
    {
        var state = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var metrics = await _store.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        var readAt = DateTimeOffset.UtcNow;
        var token = $"{key.ProjectId}:{key.Kind}:{key.EntityId}:{state?.Generation ?? 0}:{metrics.Generation}";
        return new ImmutableReasoningSnapshot(state, metrics.Generation, readAt, token);
    }

    public Task<StateSnapshot?> ReadAsync(StateKey key, CancellationToken cancellationToken = default) =>
        _store.ReadAsync(key, cancellationToken);

    public Task<IReadOnlyList<StateSnapshot>> ReadAsync(
        string projectId,
        string kind,
        StateLifecycle? lifecycle = StateLifecycle.Valid,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        _store.ReadAsync(projectId, kind, lifecycle, limit, cancellationToken);

    public Task<OperationReceipt?> ReadOperationReceiptAsync(string operationId, CancellationToken cancellationToken = default) =>
        _store.ReadOperationReceiptAsync(operationId, cancellationToken);

    public Task<IReadOnlyList<DependencyEdge>> QueryDependenciesAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        CancellationToken cancellationToken = default) =>
        _store.QueryDependenciesAsync(projectId, kind, entityId, outgoing, cancellationToken);

    public Task<IReadOnlyList<DependencyEdge>> QueryDependenciesBoundedAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        int limit,
        CancellationToken cancellationToken = default) =>
        _store.QueryDependenciesBoundedAsync(projectId, kind, entityId, outgoing, limit, cancellationToken);

    public Task<HealthReport> HealthCheckAsync(CancellationToken cancellationToken = default) =>
        _store.HealthCheckAsync(cancellationToken);

    public Task<StoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default) =>
        _store.GetMetricsAsync(cancellationToken);

    public Task<IReadOnlySet<string>> ReferencedArtifactHashesAsync(CancellationToken cancellationToken = default) =>
        _store.ReferencedArtifactHashesAsync(cancellationToken);

    public async Task<TransactionResult> CommitAsync(
        ImmutableReasoningSnapshot snapshot,
        string operationId,
        StateMutation intendedState,
        string eventType,
        string summaryCode,
        string eventPayloadJson,
        IReadOnlyList<DependencyEdge>? dependencyChanges = null,
        CancellationToken cancellationToken = default)
    {
        var expected = snapshot.State?.Generation ?? 0;
        if (intendedState.ExpectedGeneration != expected)
        {
            return new TransactionResult(false, true, expected, snapshot.State, "INVALID_SNAPSHOT_BINDING");
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.CompareAndSwapAsync(
                new TransactionRequest(
                    operationId,
                    intendedState,
                    eventType,
                    summaryCode,
                    eventPayloadJson,
                    dependencyChanges),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<BatchTransactionResult> CommitBatchAsync(
        BatchTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _store.CompareAndSwapBatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public Task<long> AppendEventAsync(EngineeringEvent engineeringEvent, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.AppendEventAsync(engineeringEvent, token), cancellationToken);

    public Task<InvalidationResult> InvalidateAsync(InvalidationRequest request, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.InvalidateAsync(request, token), cancellationToken);

    public Task<MaterializationResult> MaterializeAsync(MaterializationRequest request, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.MaterializeAsync(request, token), cancellationToken);

    public Task<MigrationResult> MigrateAsync(string targetVersion, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.MigrateAsync(targetVersion, token), cancellationToken);

    public Task<ProjectForkResult> ForkProjectAsync(ProjectForkRequest request, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.ForkProjectAsync(request, token), cancellationToken);

    public Task<LegacyRuntimeFinalizeResult> FinalizeLegacyRuntimeStateAsync(
        LegacyRuntimeFinalizeRequest request,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.FinalizeLegacyRuntimeAsync(request, token), cancellationToken);

    public Task<RepairReport> RepairAsync(CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.RepairAsync(token), cancellationToken);

    public Task<GcReport> GarbageCollectAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.GarbageCollectAsync(olderThan, token), cancellationToken);

    public Task RegisterArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken = default) =>
        WithWriterAsync(async token =>
        {
            await _store.RegisterArtifactAsync(artifact, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task LinkArtifactAsync(ArtifactLink link, CancellationToken cancellationToken = default) =>
        WithWriterAsync(async token =>
        {
            await _store.LinkArtifactAsync(link, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<ArtifactReachabilityRecord>> AnalyzeArtifactReachabilityAsync(
        string projectId,
        DateTimeOffset retentionCutoff,
        int limit = 100_000,
        CancellationToken cancellationToken = default) =>
        _store.AnalyzeArtifactReachabilityAsync(projectId, retentionCutoff, limit, cancellationToken);

    public Task<ArtifactGcResult> CollectUnreachableArtifactsAsync(
        string operationId,
        string projectId,
        DateTimeOffset retentionCutoff,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.CollectUnreachableArtifactsAsync(operationId, projectId, retentionCutoff, token), cancellationToken);

    public Task<RuntimeStateSnapshot> CreateRuntimeSnapshotAsync(
        string operationId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.CreateRuntimeSnapshotAsync(operationId, projectId, token), cancellationToken);

    public Task<RuntimeStateSnapshot?> ReadRuntimeSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        _store.ReadRuntimeSnapshotAsync(projectId, snapshotId, cancellationToken);

    public Task<SnapshotRecoveryProjection> ReconstructSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default) =>
        _store.ReconstructSnapshotAsync(projectId, snapshotId, cancellationToken);

    public Task<EventRollupResult> RollupEventsAsync(EventRollupRequest request, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.RollupEventsAsync(request, token), cancellationToken);

    public Task<OperationRetentionResult> RetainOperationsAsync(
        string operationId,
        string projectId,
        DateTimeOffset completedBefore,
        int keepRecent,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.RetainOperationsAsync(operationId, projectId, completedBefore, keepRecent, token), cancellationToken);

    public Task<DependencyRetentionResult> RetainDependenciesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.RetainDependenciesAsync(operationId, projectId, olderThan, token), cancellationToken);

    public Task<EntityRetentionResult> RetainEntitiesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.RetainEntitiesAsync(operationId, projectId, olderThan, token), cancellationToken);

    public Task<StateSnapshot?> ReadArchivedStateAsync(StateKey key, CancellationToken cancellationToken = default) =>
        _store.ReadArchivedStateAsync(key, cancellationToken);

    public Task<StorageMetrics> GetStorageMetricsAsync(string projectId, CancellationToken cancellationToken = default) =>
        _store.GetStorageMetricsAsync(projectId, cancellationToken);

    public Task<StorageDoctorReport> StorageHealthCheckAsync(
        string projectId,
        StorageHealthMode mode,
        CancellationToken cancellationToken = default) =>
        _store.StorageHealthCheckAsync(projectId, mode, cancellationToken);

    public Task<StorageCompactionResult> CompactStorageAsync(string operationId, string projectId, CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.CompactStorageAsync(operationId, projectId, token), cancellationToken);

    public Task<IncrementalMigrationInventoryResult> InventoryLegacyStorageAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        WithWriterAsync(token => _store.InventoryLegacyStorageAsync(projectId, token), cancellationToken);

    private async Task<T> WithWriterAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }
}

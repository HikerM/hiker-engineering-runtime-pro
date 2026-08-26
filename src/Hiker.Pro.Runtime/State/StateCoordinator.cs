namespace Hiker.Pro.State;

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
}

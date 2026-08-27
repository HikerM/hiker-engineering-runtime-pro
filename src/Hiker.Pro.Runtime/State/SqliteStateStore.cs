using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore : IStateStore
{
    private const string SnapshotColumns = "project_id, kind, entity_id, generation, goal_revision, lifecycle, retention, authority, schema_version, source_fingerprint, dependency_fingerprint, payload_json, updated_at";
    private readonly ProjectLayout _layout;
    private readonly string _connectionString;

    public SqliteStateStore(ProjectLayout layout)
    {
        _layout = layout;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _layout.EnsureDirectories();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StateSnapshot?> ReadAsync(StateKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadWithinAsync(connection, transaction: null, key.Normalize(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StateSnapshot>> ReadAsync(
        string projectId,
        string kind,
        StateLifecycle? lifecycle = StateLifecycle.Valid,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 1000");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SnapshotColumns}
            FROM entities
            WHERE project_id = $project AND kind = $kind
              AND ($lifecycle IS NULL OR lifecycle = $lifecycle)
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", new StateKey(projectId, kind, "query").Normalize().ProjectId);
        command.Parameters.AddWithValue("$kind", kind.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$lifecycle", lifecycle is null ? DBNull.Value : SqliteMapping.EnumToken(lifecycle.Value));
        command.Parameters.AddWithValue("$limit", limit);
        var values = new List<StateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteMapping.ReadSnapshot(reader));
        }

        return ContractCollections.Freeze(values);
    }

    public Task<TransactionResult> TransactionAsync(TransactionRequest request, CancellationToken cancellationToken = default) =>
        CompareAndSwapAsync(request, cancellationToken);

    public async Task<TransactionResult> CompareAndSwapAsync(TransactionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var normalized = request.Mutation.Key.Normalize();
        var requestFingerprint = RequestFingerprint(request with { Mutation = request.Mutation with { Key = normalized } });
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var prior = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(prior.Value.Fingerprint),
                    Encoding.ASCII.GetBytes(requestFingerprint)))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new TransactionResult(false, true, prior.Value.Generation, null, "OPERATION_ID_CONFLICT");
            }

            var committed = await ReadWithinAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var exactCommittedSnapshot = committed?.Generation == prior.Value.Generation ? committed : null;
            return new TransactionResult(true, false, prior.Value.Generation, exactCommittedSnapshot, "IDEMPOTENT_REPLAY");
        }

        var current = await ReadWithinAsync(connection, transaction, normalized, cancellationToken).ConfigureAwait(false);
        var actualGeneration = current?.Generation ?? 0;
        if (actualGeneration != request.Mutation.ExpectedGeneration)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new TransactionResult(false, true, actualGeneration, current, "STALE_CONTEXT");
        }

        await InsertPreparedOperationAsync(connection, transaction, request, normalized, requestFingerprint, cancellationToken).ConfigureAwait(false);
        var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var updatedAt = DateTimeOffset.UtcNow;
        await UpsertEntityAsync(connection, transaction, request.Mutation with { Key = normalized }, generation, updatedAt, cancellationToken).ConfigureAwait(false);
        if (request.DependencyChanges is not null)
        {
            foreach (var edge in request.DependencyChanges)
            {
                await UpsertEdgeAsync(connection, transaction, edge, updatedAt, cancellationToken).ConfigureAwait(false);
            }
        }

        await InsertEventAsync(
            connection,
            transaction,
            new EngineeringEvent(
                request.OperationId,
                normalized.ProjectId,
                request.EventType,
                normalized.Kind,
                normalized.EntityId,
                generation,
                request.SummaryCode,
                request.EventPayloadJson,
                updatedAt),
            cancellationToken).ConfigureAwait(false);
        await CompleteOperationAsync(connection, transaction, request.OperationId, generation, updatedAt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new StateSnapshot(
            normalized,
            generation,
            request.Mutation.GoalRevision,
            request.Mutation.Lifecycle,
            request.Mutation.Authority,
            request.Mutation.SchemaVersion,
            request.Mutation.SourceFingerprint,
            request.Mutation.DependencyFingerprint,
            request.Mutation.PayloadJson,
            updatedAt,
            request.Mutation.Retention);
        return new TransactionResult(true, false, generation, snapshot, "COMMITTED");
    }

    public async Task<BatchTransactionResult> CompareAndSwapBatchAsync(
        BatchTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchRequest(request);
        var mutations = request.Mutations
            .Select(static mutation => mutation with { Key = mutation.Key.Normalize() })
            .OrderBy(static mutation => mutation.Key.Kind, StringComparer.Ordinal)
            .ThenBy(static mutation => mutation.Key.EntityId, StringComparer.Ordinal)
            .ToArray();
        var normalizedRequest = request with { Mutations = mutations };
        var requestFingerprint = BatchRequestFingerprint(normalizedRequest);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!string.Equals(prior.Value.Fingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new BatchTransactionResult(false, true, prior.Value.Generation, Array.Empty<StateSnapshot>(), "OPERATION_ID_CONFLICT");
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new BatchTransactionResult(true, false, prior.Value.Generation, Array.Empty<StateSnapshot>(), "IDEMPOTENT_REPLAY");
        }

        foreach (var mutation in mutations)
        {
            var current = await ReadWithinAsync(connection, transaction, mutation.Key, cancellationToken).ConfigureAwait(false);
            if ((current?.Generation ?? 0) != mutation.ExpectedGeneration)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new BatchTransactionResult(
                    false,
                    true,
                    current?.Generation ?? 0,
                    current is null ? Array.Empty<StateSnapshot>() : new[] { current },
                    "STALE_CONTEXT");
            }
        }

        var representative = mutations[0];
        await InsertPreparedOperationAsync(
            connection,
            transaction,
            new TransactionRequest(request.OperationId, representative, request.EventType, request.SummaryCode, request.EventPayloadJson, request.DependencyChanges),
            representative.Key,
            requestFingerprint,
            cancellationToken).ConfigureAwait(false);
        var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var updatedAt = DateTimeOffset.UtcNow;
        var committed = new List<StateSnapshot>(mutations.Length);
        foreach (var mutation in mutations)
        {
            await UpsertEntityAsync(connection, transaction, mutation, generation, updatedAt, cancellationToken).ConfigureAwait(false);
            committed.Add(new StateSnapshot(
                mutation.Key,
                generation,
                mutation.GoalRevision,
                mutation.Lifecycle,
                mutation.Authority,
                mutation.SchemaVersion,
                mutation.SourceFingerprint,
                mutation.DependencyFingerprint,
                mutation.PayloadJson,
                updatedAt,
                mutation.Retention));
        }

        if (request.DependencyChanges is not null)
        {
            foreach (var edge in request.DependencyChanges)
            {
                await UpsertEdgeAsync(connection, transaction, edge, updatedAt, cancellationToken).ConfigureAwait(false);
            }
        }

        await InsertEventAsync(connection, transaction, new EngineeringEvent(
            request.OperationId,
            representative.Key.ProjectId,
            request.EventType,
            null,
            null,
            generation,
            request.SummaryCode,
            request.EventPayloadJson,
            updatedAt), cancellationToken).ConfigureAwait(false);
        await CompleteOperationAsync(connection, transaction, request.OperationId, generation, updatedAt, cancellationToken, request.ResultJson).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatchTransactionResult(true, false, generation, ContractCollections.Freeze(committed), "COMMITTED");
    }

    public async Task<OperationReceipt?> ReadOperationReceiptAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 160)
        {
            throw new ArgumentException("OperationId must contain 1-160 characters", nameof(operationId));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id,operation_type,request_fingerprint,status,generation,entity_kind,entity_id,created_at,completed_at,result_json FROM operations WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new OperationReceipt(
            operationId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public async Task<long> AppendEventAsync(EngineeringEvent engineeringEvent, CancellationToken cancellationToken = default)
    {
        SqliteMapping.ValidateJson(engineeringEvent.PayloadJson, nameof(engineeringEvent.PayloadJson));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadEventAsync(connection, transaction, engineeringEvent.EventId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(EventFingerprint(existing), EventFingerprint(engineeringEvent), StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Event ID is already bound to different facts");
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing.Generation;
        }

        await InsertEventAsync(connection, transaction, engineeringEvent, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return engineeringEvent.Generation;
    }

    private static async Task<EngineeringEvent?> ReadEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id,event_type,entity_kind,entity_id,generation,summary_code,payload_json,occurred_at FROM engineering_events WHERE event_id=$id;";
        command.Parameters.AddWithValue("$id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new EngineeringEvent(
            eventId,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string EventFingerprint(EngineeringEvent value) => RequestHash(string.Join('\n',
        value.ProjectId,
        value.EventType,
        value.EntityKind,
        value.EntityId,
        value.Generation,
        value.SummaryCode,
        value.PayloadJson,
        value.OccurredAt.ToUniversalTime().ToString("O")));

    public Task<IReadOnlyList<DependencyEdge>> QueryDependenciesAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        CancellationToken cancellationToken = default) =>
        QueryDependenciesBoundedAsync(projectId, kind, entityId, outgoing, 4096, cancellationToken);

    public async Task<IReadOnlyList<DependencyEdge>> QueryDependenciesBoundedAsync(
        string projectId,
        string kind,
        string entityId,
        bool outgoing,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var key = new StateKey(projectId, kind, entityId).Normalize();
        if (limit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var direction = outgoing ? "from_kind = $kind AND from_id = $entity" : "to_kind = $kind AND to_id = $entity";
        command.CommandText = $"""
            SELECT project_id, from_kind, from_id, to_kind, to_id, relation, fingerprint, lifecycle
            FROM dependency_edges
            WHERE project_id = $project AND {direction} AND lifecycle = 'VALID'
            ORDER BY relation, from_kind, from_id, to_kind, to_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", key.ProjectId);
        command.Parameters.AddWithValue("$kind", key.Kind);
        command.Parameters.AddWithValue("$entity", key.EntityId);
        command.Parameters.AddWithValue("$limit", limit);
        var edges = new List<DependencyEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(SqliteMapping.ReadEdge(reader));
        }

        return ContractCollections.Freeze(edges);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<StateSnapshot?> ReadWithinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StateKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SnapshotColumns} FROM entities WHERE project_id=$project AND kind=$kind AND entity_id=$entity;";
        SqliteMapping.AddStateKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? SqliteMapping.ReadSnapshot(reader) : null;
    }

    private static void ValidateRequest(TransactionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 160)
        {
            throw new ArgumentException("OperationId must contain 1-160 characters", nameof(request));
        }

        _ = request.Mutation.Key.Normalize();
        SqliteMapping.ValidateJson(request.Mutation.PayloadJson, nameof(request.Mutation.PayloadJson));
        SqliteMapping.ValidateJson(request.EventPayloadJson, nameof(request.EventPayloadJson));
        if (request.DependencyChanges is not null)
        {
            foreach (var edge in request.DependencyChanges)
            {
                ValidateDependencyEdge(edge, request.Mutation.Key.ProjectId);
            }
        }
    }

    private static void ValidateBatchRequest(BatchTransactionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 160)
        {
            throw new ArgumentException("OperationId must contain 1-160 characters", nameof(request));
        }

        if (request.Mutations.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A batch must contain 1-256 mutations");
        }

        var normalized = request.Mutations.Select(static mutation => mutation.Key.Normalize()).ToArray();
        if (normalized.Select(static key => $"{key.ProjectId}|{key.Kind}|{key.EntityId}").Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
            normalized.Select(static key => key.ProjectId).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new ArgumentException("Batch mutation keys must be unique and belong to one project", nameof(request));
        }

        foreach (var mutation in request.Mutations)
        {
            SqliteMapping.ValidateJson(mutation.PayloadJson, nameof(request.Mutations));
        }

        SqliteMapping.ValidateJson(request.EventPayloadJson, nameof(request.EventPayloadJson));
        if (request.DependencyChanges is not null)
        {
            foreach (var edge in request.DependencyChanges)
            {
                ValidateDependencyEdge(edge, normalized[0].ProjectId);
            }
        }
    }

    private static string RequestFingerprint(TransactionRequest request)
    {
        var dependencyValue = request.DependencyChanges is null
            ? string.Empty
            : string.Join('\n', request.DependencyChanges
                .Select(NormalizeDependencyEdge)
                .OrderBy(static value => value, StringComparer.Ordinal));
        var value = string.Join('\n',
            request.Mutation.Key.ProjectId,
            request.Mutation.Key.Kind,
            request.Mutation.Key.EntityId,
            request.Mutation.ExpectedGeneration,
            request.Mutation.GoalRevision,
            request.Mutation.Lifecycle,
            request.Mutation.Authority,
            request.Mutation.SchemaVersion,
            request.Mutation.SourceFingerprint,
            request.Mutation.DependencyFingerprint,
            request.Mutation.PayloadJson,
            request.Mutation.Retention,
            request.EventType,
            request.SummaryCode,
            request.EventPayloadJson,
            dependencyValue);
        return RequestHash(value);
    }

    private static string BatchRequestFingerprint(BatchTransactionRequest request)
    {
        var mutationValues = request.Mutations.Select(static mutation => string.Join('|',
            mutation.Key.ProjectId,
            mutation.Key.Kind,
            mutation.Key.EntityId,
            mutation.ExpectedGeneration,
            mutation.GoalRevision,
            mutation.Lifecycle,
            mutation.Authority,
            mutation.SchemaVersion,
            mutation.SourceFingerprint,
            mutation.DependencyFingerprint,
            mutation.PayloadJson,
            mutation.Retention));
        var dependencyValues = request.DependencyChanges is null
            ? Array.Empty<string>()
            : request.DependencyChanges.Select(NormalizeDependencyEdge).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        return RequestHash(string.Join('\n', mutationValues
            .Concat(new[] { request.EventType, request.SummaryCode, request.EventPayloadJson, request.ResultJson ?? string.Empty })
            .Concat(dependencyValues)));
    }

    private static string RequestHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateDependencyEdge(DependencyEdge edge, string projectId)
    {
        var normalizedProject = new StateKey(edge.ProjectId, "EDGE", "EDGE").Normalize().ProjectId;
        var expectedProject = new StateKey(projectId, "EDGE", "EDGE").Normalize().ProjectId;
        if (!string.Equals(normalizedProject, expectedProject, StringComparison.Ordinal))
        {
            throw new ArgumentException("Dependency changes must belong to the mutated project", nameof(edge));
        }

        _ = new StateKey(edge.ProjectId, edge.FromKind, edge.FromId).Normalize();
        _ = new StateKey(edge.ProjectId, edge.ToKind, edge.ToId).Normalize();
        if (string.IsNullOrWhiteSpace(edge.Relation) || edge.Relation.Length > 160 ||
            string.IsNullOrWhiteSpace(edge.Fingerprint) || edge.Fingerprint.Length > 256)
        {
            throw new ArgumentException("Dependency relation and fingerprint must be bounded non-empty values", nameof(edge));
        }
    }

    private static string NormalizeDependencyEdge(DependencyEdge edge) => string.Join('|',
        edge.ProjectId.Trim(),
        edge.FromKind.Trim().ToUpperInvariant(),
        edge.FromId.Trim(),
        edge.ToKind.Trim().ToUpperInvariant(),
        edge.ToId.Trim(),
        edge.Relation.Trim().ToUpperInvariant(),
        edge.Fingerprint.Trim(),
        edge.Lifecycle);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<RuntimeStateSnapshot> CreateRuntimeSnapshotAsync(
        string operationId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        var fingerprint = RequestHash($"runtime-snapshot|{projectId}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "RUNTIME_SNAPSHOT", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
            {
                var receipt = JsonSerializer.Deserialize<SnapshotReceipt>(prior.Value.ResultJson, StorageJson.Options)
                    ?? throw new InvalidDataException("Runtime Snapshot receipt is invalid");
                return await ReadRuntimeSnapshotAsync(projectId, receipt.SnapshotId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Runtime Snapshot receipt points to missing state");
            }
        }
        else
        {
            await BeginStorageMaintenanceAsync(operationId, projectId, "RUNTIME_SNAPSHOT", fingerprint, "PREPARING", null, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var eventSequence = await ScalarAsync(connection, transaction, "SELECT COALESCE(MAX(sequence),0) FROM engineering_events WHERE project_id=$project;", projectId, cancellationToken).ConfigureAwait(false);
        var entities = await ReadSnapshotEntitiesAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        var dependencies = await ReadSnapshotDependenciesAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        var payload = new RuntimeSnapshotPayload(entities, dependencies);
        var payloadJson = JsonSerializer.Serialize(payload, StorageJson.Options);
        StoragePayloadPolicy.EnsureArchiveSafe(payloadJson);
        var snapshotFingerprint = Hash(payloadJson);
        var snapshotId = $"snapshot-{generation:D12}-{snapshotFingerprint[..16]}";

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_snapshots(snapshot_id,project_id,generation,event_sequence,schema_version,fingerprint,payload_json,created_at)
                VALUES($id,$project,$generation,$sequence,$schema,$fingerprint,$payload,$created)
                ON CONFLICT(snapshot_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", snapshotId);
            command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$sequence", eventSequence);
            command.Parameters.AddWithValue("$schema", SqliteSchema.CurrentVersion);
            command.Parameters.AddWithValue("$fingerprint", snapshotFingerprint);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var receiptJson = JsonSerializer.Serialize(new SnapshotReceipt(snapshotId), StorageJson.Options);
        await UpdateStorageMaintenanceAsync(connection, transaction, operationId, "COMPLETE", receiptJson, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeStateSnapshot(snapshotId, projectId, generation, eventSequence, SqliteSchema.CurrentVersion, snapshotFingerprint, entities, dependencies, createdAt);
    }

    public async Task<RuntimeStateSnapshot?> ReadRuntimeSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(projectId, EntityKinds.Project, snapshotId).Normalize();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation,event_sequence,schema_version,fingerprint,payload_json,created_at
            FROM runtime_snapshots WHERE project_id=$project AND snapshot_id=$id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$id", snapshotId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var payloadJson = reader.GetString(4);
        var expected = reader.GetString(3);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(Hash(payloadJson))))
            throw new InvalidDataException("Runtime Snapshot fingerprint does not match its payload");
        var payload = JsonSerializer.Deserialize<RuntimeSnapshotPayload>(payloadJson, StorageJson.Options)
            ?? throw new InvalidDataException("Runtime Snapshot payload is invalid");
        return new RuntimeStateSnapshot(
            snapshotId,
            projectId,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            expected,
            ContractCollections.Freeze(payload.Entities),
            ContractCollections.Freeze(payload.Dependencies),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<SnapshotRecoveryProjection> ReconstructSnapshotAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadRuntimeSnapshotAsync(projectId, snapshotId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime Snapshot does not exist");
        var current = snapshot.Entities.ToDictionary(static value => $"{value.Key.Kind}|{value.Key.EntityId}", StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation,kind,entity_id,goal_revision,lifecycle,retention,authority,schema_version,
                   source_fingerprint,dependency_fingerprint,payload_json,updated_at
            FROM state_deltas WHERE project_id=$project AND generation>$generation
            ORDER BY generation,kind,entity_id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$generation", snapshot.Generation);
        var applied = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = DeltaSnapshot(projectId, reader);
            current[$"{state.Key.Kind}|{state.Key.EntityId}"] = state;
            applied++;
        }
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var entities = current.Values.OrderBy(static value => value.Key.Kind, StringComparer.Ordinal).ThenBy(static value => value.Key.EntityId, StringComparer.Ordinal).ToArray();
        return new SnapshotRecoveryProjection(snapshotId, snapshot.Generation, generation, entities, applied, SnapshotFingerprint(entities));
    }

    private static async Task InsertStateDeltaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StateMutation mutation,
        long generation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO state_deltas(project_id,generation,kind,entity_id,goal_revision,lifecycle,retention,authority,schema_version,
                source_fingerprint,dependency_fingerprint,payload_json,updated_at)
            VALUES($project,$generation,$kind,$entity,$goal,$lifecycle,$retention,$authority,$schema,$source,$dependency,$payload,$updated)
            ON CONFLICT(project_id,generation,kind,entity_id) DO UPDATE SET
                goal_revision=excluded.goal_revision,lifecycle=excluded.lifecycle,retention=excluded.retention,authority=excluded.authority,
                schema_version=excluded.schema_version,source_fingerprint=excluded.source_fingerprint,
                dependency_fingerprint=excluded.dependency_fingerprint,payload_json=excluded.payload_json,updated_at=excluded.updated_at;
            """;
        SqliteMapping.AddStateKey(command, mutation.Key);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$goal", mutation.GoalRevision);
        command.Parameters.AddWithValue("$lifecycle", SqliteMapping.EnumToken(mutation.Lifecycle));
        command.Parameters.AddWithValue("$retention", SqliteMapping.EnumToken(mutation.Retention));
        command.Parameters.AddWithValue("$authority", mutation.Authority);
        command.Parameters.AddWithValue("$schema", mutation.SchemaVersion);
        command.Parameters.AddWithValue("$source", mutation.SourceFingerprint);
        command.Parameters.AddWithValue("$dependency", mutation.DependencyFingerprint);
        command.Parameters.AddWithValue("$payload", mutation.PayloadJson);
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StateSnapshot>> ReadSnapshotEntitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind,entity_id,generation,goal_revision,lifecycle,authority,schema_version,source_fingerprint,
                   dependency_fingerprint,payload_json,updated_at,retention
            FROM entities WHERE project_id=$project AND retention IN ('HOT','WARM')
              AND lifecycle NOT IN ('ARCHIVED','CORRUPT','INCOMPATIBLE')
            ORDER BY kind,entity_id LIMIT 10001;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<StateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new StateSnapshot(
                new StateKey(projectId, reader.GetString(0), reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3),
                Enum.Parse<StateLifecycle>(reader.GetString(4), true), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture), Enum.Parse<RetentionTier>(reader.GetString(11), true)));
        }
        if (values.Count > 10_000) throw new InvalidOperationException("Active Runtime Snapshot entity budget is exhausted");
        return ContractCollections.Freeze(values);
    }

    private static async Task<IReadOnlyList<DependencyEdge>> ReadSnapshotDependenciesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT from_kind,from_id,to_kind,to_id,relation,fingerprint,lifecycle
            FROM dependency_edges WHERE project_id=$project AND lifecycle='VALID'
            ORDER BY from_kind,from_id,to_kind,to_id,relation LIMIT 20001;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<DependencyEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new DependencyEdge(projectId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), Enum.Parse<StateLifecycle>(reader.GetString(6), true)));
        if (values.Count > 20_000) throw new InvalidOperationException("Active Runtime Snapshot dependency budget is exhausted");
        return ContractCollections.Freeze(values);
    }

    private static StateSnapshot DeltaSnapshot(string projectId, SqliteDataReader reader) => new(
        new StateKey(projectId, reader.GetString(1), reader.GetString(2)), reader.GetInt64(0), reader.GetInt64(3),
        Enum.Parse<StateLifecycle>(reader.GetString(4), true), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
        reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture), Enum.Parse<RetentionTier>(reader.GetString(5), true));

    private static async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SnapshotFingerprint(IEnumerable<StateSnapshot> states) => Hash(string.Join('\n', states.Select(static state =>
        $"{state.Key.Kind}|{state.Key.EntityId}|{state.Generation}|{state.GoalRevision}|{state.Lifecycle}|{state.SourceFingerprint}|{state.DependencyFingerprint}")));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record RuntimeSnapshotPayload(IReadOnlyList<StateSnapshot> Entities, IReadOnlyList<DependencyEdge> Dependencies);
    private sealed record SnapshotReceipt(string SnapshotId);
}

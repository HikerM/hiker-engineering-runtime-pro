using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    private static async Task<(string Fingerprint, long Generation, string? ResultJson)?> ReadOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_fingerprint, generation, result_json FROM operations WHERE operation_id=$id AND status='COMPLETE';";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task InsertPreparedOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransactionRequest request,
        StateKey key,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations(
                operation_id, project_id, operation_type, request_fingerprint, status,
                generation, entity_kind, entity_id, created_at, completed_at)
            VALUES($id, $project, $type, $fingerprint, 'PREPARED', NULL, $kind, $entity, $at, NULL);
            """;
        command.Parameters.AddWithValue("$id", request.OperationId);
        command.Parameters.AddWithValue("$project", key.ProjectId);
        command.Parameters.AddWithValue("$type", request.EventType);
        command.Parameters.AddWithValue("$fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("$kind", key.Kind);
        command.Parameters.AddWithValue("$entity", key.EntityId);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> NextGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE metadata
            SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            WHERE key = 'global_generation'
            RETURNING CAST(value AS INTEGER);
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task UpsertEntityAsync(
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
            INSERT INTO entities(
                project_id, kind, entity_id, generation, goal_revision, lifecycle, retention,
                authority, schema_version, source_fingerprint, dependency_fingerprint,
                payload_json, updated_at)
            VALUES(
                $project, $kind, $entity, $generation, $goal, $lifecycle, $retention,
                $authority, $schema, $source, $dependency, $payload, $updated)
            ON CONFLICT(project_id, kind, entity_id) DO UPDATE SET
                generation=excluded.generation,
                goal_revision=excluded.goal_revision,
                lifecycle=excluded.lifecycle,
                retention=excluded.retention,
                authority=excluded.authority,
                schema_version=excluded.schema_version,
                source_fingerprint=excluded.source_fingerprint,
                dependency_fingerprint=excluded.dependency_fingerprint,
                payload_json=excluded.payload_json,
                updated_at=excluded.updated_at;
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

    private static async Task UpsertEdgeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DependencyEdge edge,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dependency_edges(
                project_id, from_kind, from_id, to_kind, to_id, relation,
                fingerprint, lifecycle, updated_at)
            VALUES($project, $fromKind, $fromId, $toKind, $toId, $relation,
                $fingerprint, $lifecycle, $updated)
            ON CONFLICT(project_id, from_kind, from_id, to_kind, to_id, relation) DO UPDATE SET
                fingerprint=excluded.fingerprint,
                lifecycle=excluded.lifecycle,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$project", edge.ProjectId);
        command.Parameters.AddWithValue("$fromKind", edge.FromKind.ToUpperInvariant());
        command.Parameters.AddWithValue("$fromId", edge.FromId);
        command.Parameters.AddWithValue("$toKind", edge.ToKind.ToUpperInvariant());
        command.Parameters.AddWithValue("$toId", edge.ToId);
        command.Parameters.AddWithValue("$relation", edge.Relation.ToUpperInvariant());
        command.Parameters.AddWithValue("$fingerprint", edge.Fingerprint);
        command.Parameters.AddWithValue("$lifecycle", SqliteMapping.EnumToken(edge.Lifecycle));
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EngineeringEvent engineeringEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO engineering_events(
                event_id, project_id, event_type, entity_kind, entity_id,
                generation, summary_code, payload_json, occurred_at)
            VALUES($id, $project, $type, $kind, $entity, $generation, $summary, $payload, $at)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", engineeringEvent.EventId);
        command.Parameters.AddWithValue("$project", engineeringEvent.ProjectId);
        command.Parameters.AddWithValue("$type", engineeringEvent.EventType.ToUpperInvariant());
        command.Parameters.AddWithValue("$kind", (object?)engineeringEvent.EntityKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$entity", (object?)engineeringEvent.EntityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$generation", engineeringEvent.Generation);
        command.Parameters.AddWithValue("$summary", engineeringEvent.SummaryCode);
        command.Parameters.AddWithValue("$payload", engineeringEvent.PayloadJson);
        command.Parameters.AddWithValue("$at", engineeringEvent.OccurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        long generation,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken,
        string? resultJson = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE operations
            SET status='COMPLETE', generation=$generation, completed_at=$completed, result_json=$result
            WHERE operation_id=$id AND status='PREPARED';
            """;
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Operation journal finalization did not update exactly one row");
        }
    }
}

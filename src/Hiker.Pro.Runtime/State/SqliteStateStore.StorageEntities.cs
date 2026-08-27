using System.Text.Json;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<EntityRetentionResult> RetainEntitiesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        var fingerprint = RequestHash($"entity-retention|{projectId}|{olderThan.ToUniversalTime():O}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "ENTITY_RETENTION", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
            {
                return JsonSerializer.Deserialize<EntityRetentionResult>(prior.Value.ResultJson, StorageJson.Options)!
                    with
                { Idempotent = true };
            }
        }
        else
        {
            await BeginStorageMaintenanceAsync(
                operationId,
                projectId,
                "ENTITY_RETENTION",
                fingerprint,
                "PREPARED",
                null,
                cancellationToken).ConfigureAwait(false);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureEntityArchivePayloadsSafeAsync(connection, projectId, olderThan, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var archivedAt = DateTimeOffset.UtcNow.ToString("O");

        await using (var archiveDependencies = connection.CreateCommand())
        {
            archiveDependencies.Transaction = transaction;
            archiveDependencies.CommandText = """
                INSERT INTO archived_dependency_edges(
                    project_id,from_kind,from_id,to_kind,to_id,relation,fingerprint,lifecycle,updated_at,archived_at)
                SELECT edge.project_id,edge.from_kind,edge.from_id,edge.to_kind,edge.to_id,edge.relation,
                       edge.fingerprint,edge.lifecycle,edge.updated_at,$archived
                FROM dependency_edges edge
                WHERE edge.project_id=$project AND EXISTS(
                    SELECT 1 FROM entities entity
                    WHERE entity.project_id=edge.project_id AND entity.updated_at < $older
                      AND entity.lifecycle IN ('STALE','SUPERSEDED','ORPHANED','ARCHIVED')
                      AND ((entity.kind=edge.from_kind AND entity.entity_id=edge.from_id)
                        OR (entity.kind=edge.to_kind AND entity.entity_id=edge.to_id)))
                ON CONFLICT(project_id,from_kind,from_id,to_kind,to_id,relation) DO UPDATE SET
                    fingerprint=excluded.fingerprint,lifecycle=excluded.lifecycle,
                    updated_at=excluded.updated_at,archived_at=excluded.archived_at;
                """;
            archiveDependencies.Parameters.AddWithValue("$project", projectId);
            archiveDependencies.Parameters.AddWithValue("$older", olderThan.ToUniversalTime().ToString("O"));
            archiveDependencies.Parameters.AddWithValue("$archived", archivedAt);
            await archiveDependencies.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int archivedDependencies;
        await using (var deleteDependencies = connection.CreateCommand())
        {
            deleteDependencies.Transaction = transaction;
            deleteDependencies.CommandText = """
                DELETE FROM dependency_edges
                WHERE project_id=$project AND EXISTS(
                    SELECT 1 FROM archived_dependency_edges history
                    WHERE history.project_id=dependency_edges.project_id
                      AND history.from_kind=dependency_edges.from_kind AND history.from_id=dependency_edges.from_id
                      AND history.to_kind=dependency_edges.to_kind AND history.to_id=dependency_edges.to_id
                      AND history.relation=dependency_edges.relation);
                """;
            deleteDependencies.Parameters.AddWithValue("$project", projectId);
            archivedDependencies = await deleteDependencies.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var archiveEntities = connection.CreateCommand())
        {
            archiveEntities.Transaction = transaction;
            archiveEntities.CommandText = """
                INSERT INTO archived_entities(
                    project_id,kind,entity_id,generation,goal_revision,lifecycle,retention,authority,schema_version,
                    source_fingerprint,dependency_fingerprint,payload_json,updated_at,archived_at)
                SELECT project_id,kind,entity_id,generation,goal_revision,lifecycle,'ARCHIVED',authority,schema_version,
                       source_fingerprint,dependency_fingerprint,payload_json,updated_at,$archived
                FROM entities
                WHERE project_id=$project AND updated_at < $older
                  AND lifecycle IN ('STALE','SUPERSEDED','ORPHANED','ARCHIVED')
                ON CONFLICT(project_id,kind,entity_id) DO UPDATE SET
                    generation=excluded.generation,goal_revision=excluded.goal_revision,lifecycle=excluded.lifecycle,
                    retention=excluded.retention,authority=excluded.authority,schema_version=excluded.schema_version,
                    source_fingerprint=excluded.source_fingerprint,dependency_fingerprint=excluded.dependency_fingerprint,
                    payload_json=excluded.payload_json,updated_at=excluded.updated_at,archived_at=excluded.archived_at
                WHERE excluded.generation >= archived_entities.generation;
                """;
            archiveEntities.Parameters.AddWithValue("$project", projectId);
            archiveEntities.Parameters.AddWithValue("$older", olderThan.ToUniversalTime().ToString("O"));
            archiveEntities.Parameters.AddWithValue("$archived", archivedAt);
            await archiveEntities.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int archivedEntities;
        await using (var deleteEntities = connection.CreateCommand())
        {
            deleteEntities.Transaction = transaction;
            deleteEntities.CommandText = """
                DELETE FROM entities
                WHERE project_id=$project AND updated_at < $older
                  AND lifecycle IN ('STALE','SUPERSEDED','ORPHANED','ARCHIVED')
                  AND EXISTS(
                      SELECT 1 FROM archived_entities history
                      WHERE history.project_id=entities.project_id AND history.kind=entities.kind
                        AND history.entity_id=entities.entity_id AND history.generation >= entities.generation);
                """;
            deleteEntities.Parameters.AddWithValue("$project", projectId);
            deleteEntities.Parameters.AddWithValue("$older", olderThan.ToUniversalTime().ToString("O"));
            archivedEntities = await deleteEntities.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetStorageCounterFromQueryAsync(
            connection,
            transaction,
            projectId,
            "archived_entities",
            "SELECT COUNT(*) FROM archived_entities WHERE project_id=$project",
            cancellationToken).ConfigureAwait(false);
        await SetStorageCounterFromQueryAsync(
            connection,
            transaction,
            projectId,
            "archived_dependencies",
            "SELECT COUNT(*) FROM archived_dependency_edges WHERE project_id=$project",
            cancellationToken).ConfigureAwait(false);

        var current = (int)await CountWithStatusAsync(connection, transaction, "entities", projectId, "1=1", cancellationToken).ConfigureAwait(false);
        var result = new EntityRetentionResult("COMPLETE", archivedEntities, archivedDependencies, current, false);
        await UpdateStorageMaintenanceAsync(
            connection,
            transaction,
            operationId,
            "COMPLETE",
            JsonSerializer.Serialize(result, StorageJson.Options),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<StateSnapshot?> ReadArchivedStateAsync(StateKey key, CancellationToken cancellationToken = default)
    {
        var normalized = key.Normalize();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SnapshotColumns}
            FROM archived_entities
            WHERE project_id=$project AND kind=$kind AND entity_id=$entity;
            """;
        SqliteMapping.AddStateKey(command, normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? SqliteMapping.ReadSnapshot(reader) : null;
    }

    private static async Task EnsureEntityArchivePayloadsSafeAsync(
        SqliteConnection connection,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM entities
            WHERE project_id=$project AND updated_at < $older
              AND lifecycle IN ('STALE','SUPERSEDED','ORPHANED','ARCHIVED')
            ORDER BY updated_at LIMIT 100001;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$older", olderThan.ToUniversalTime().ToString("O"));
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++count > 100_000) throw new InvalidOperationException("Entity retention scan budget is exhausted");
            StoragePayloadPolicy.EnsureArchiveSafe(reader.GetString(0));
        }
    }
}

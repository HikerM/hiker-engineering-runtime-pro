using System.Text.Json;
using Hiker.Pro.Storage;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<OperationRetentionResult> RetainOperationsAsync(
        string operationId,
        string projectId,
        DateTimeOffset completedBefore,
        int keepRecent,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        if (keepRecent is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(keepRecent));
        var fingerprint = RequestHash($"operation-retention|{projectId}|{completedBefore.ToUniversalTime():O}|{keepRecent}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "OPERATION_RETENTION", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
                return JsonSerializer.Deserialize<OperationRetentionResult>(prior.Value.ResultJson, StorageJson.Options)! with { Idempotent = true };
        }
        else await BeginStorageMaintenanceAsync(operationId, projectId, "OPERATION_RETENTION", fingerprint, "PREPARED", null, cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                INSERT INTO archived_operations(operation_id,project_id,operation_type,request_fingerprint,status,generation,entity_kind,entity_id,created_at,completed_at,result_json,archived_at)
                SELECT operation_id,project_id,operation_type,request_fingerprint,status,generation,entity_kind,entity_id,created_at,completed_at,result_json,$archived
                FROM operations
                WHERE project_id=$project AND status='COMPLETE' AND completed_at < $before
                  AND entity_kind NOT IN ('RELEASE','CHECKPOINT')
                  AND operation_type NOT LIKE '%RELEASE%'
                  AND NOT EXISTS(
                      SELECT 1 FROM dependency_edges edge
                      WHERE edge.project_id=operations.project_id AND edge.lifecycle='VALID'
                        AND ((edge.from_kind=operations.entity_kind AND edge.from_id=operations.entity_id AND edge.to_kind='RELEASE')
                          OR (edge.to_kind=operations.entity_kind AND edge.to_id=operations.entity_id AND edge.from_kind='RELEASE')))
                  AND operation_id NOT IN (SELECT operation_id FROM operations WHERE project_id=$project AND status='COMPLETE' ORDER BY completed_at DESC LIMIT $keep)
                ON CONFLICT(operation_id) DO NOTHING;
                """;
            archive.Parameters.AddWithValue("$project", projectId);
            archive.Parameters.AddWithValue("$before", completedBefore.ToUniversalTime().ToString("O"));
            archive.Parameters.AddWithValue("$keep", keepRecent);
            archive.Parameters.AddWithValue("$archived", DateTimeOffset.UtcNow.ToString("O"));
            await archive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        int archived;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM operations WHERE operation_id IN (SELECT operation_id FROM archived_operations WHERE project_id=$project);";
            delete.Parameters.AddWithValue("$project", projectId);
            archived = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await SetStorageCounterFromQueryAsync(
            connection, transaction, projectId, "archived_operations",
            "SELECT COUNT(*) FROM archived_operations WHERE project_id=$project", cancellationToken).ConfigureAwait(false);
        var active = (int)await CountWithStatusAsync(connection, transaction, "operations", projectId, "status <> 'COMPLETE'", cancellationToken).ConfigureAwait(false);
        var recent = (int)await CountWithStatusAsync(connection, transaction, "operations", projectId, "status = 'COMPLETE'", cancellationToken).ConfigureAwait(false);
        var result = new OperationRetentionResult("COMPLETE", archived, active, recent, active, false);
        await UpdateStorageMaintenanceAsync(connection, transaction, operationId, "COMPLETE", JsonSerializer.Serialize(result, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<DependencyRetentionResult> RetainDependenciesAsync(
        string operationId,
        string projectId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        var fingerprint = RequestHash($"dependency-retention|{projectId}|{olderThan.ToUniversalTime():O}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "DEPENDENCY_RETENTION", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
                return JsonSerializer.Deserialize<DependencyRetentionResult>(prior.Value.ResultJson, StorageJson.Options)! with { Idempotent = true };
        }
        else await BeginStorageMaintenanceAsync(operationId, projectId, "DEPENDENCY_RETENTION", fingerprint, "PREPARED", null, cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                INSERT INTO archived_dependency_edges(project_id,from_kind,from_id,to_kind,to_id,relation,fingerprint,lifecycle,updated_at,archived_at)
                SELECT edge.project_id,edge.from_kind,edge.from_id,edge.to_kind,edge.to_id,edge.relation,edge.fingerprint,edge.lifecycle,edge.updated_at,$archived
                FROM dependency_edges edge
                LEFT JOIN entities source ON source.project_id=edge.project_id AND source.kind=edge.from_kind AND source.entity_id=edge.from_id
                LEFT JOIN entities target ON target.project_id=edge.project_id AND target.kind=edge.to_kind AND target.entity_id=edge.to_id
                WHERE edge.project_id=$project AND edge.updated_at < $older
                  AND (edge.lifecycle <> 'VALID'
                    OR (source.entity_id IS NOT NULL AND source.lifecycle <> 'VALID')
                    OR (target.entity_id IS NOT NULL AND target.lifecycle <> 'VALID'))
                ON CONFLICT(project_id,from_kind,from_id,to_kind,to_id,relation) DO UPDATE SET
                    fingerprint=excluded.fingerprint,lifecycle=excluded.lifecycle,updated_at=excluded.updated_at,archived_at=excluded.archived_at;
                """;
            archive.Parameters.AddWithValue("$project", projectId);
            archive.Parameters.AddWithValue("$older", olderThan.ToUniversalTime().ToString("O"));
            archive.Parameters.AddWithValue("$archived", DateTimeOffset.UtcNow.ToString("O"));
            await archive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        int archived;
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM dependency_edges
                WHERE project_id=$project AND EXISTS(
                    SELECT 1 FROM archived_dependency_edges history
                    WHERE history.project_id=dependency_edges.project_id AND history.from_kind=dependency_edges.from_kind AND history.from_id=dependency_edges.from_id
                      AND history.to_kind=dependency_edges.to_kind AND history.to_id=dependency_edges.to_id AND history.relation=dependency_edges.relation);
                """;
            delete.Parameters.AddWithValue("$project", projectId);
            archived = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await SetStorageCounterFromQueryAsync(
            connection, transaction, projectId, "archived_dependencies",
            "SELECT COUNT(*) FROM archived_dependency_edges WHERE project_id=$project", cancellationToken).ConfigureAwait(false);
        var current = (int)await CountWithStatusAsync(connection, transaction, "dependency_edges", projectId, "1=1", cancellationToken).ConfigureAwait(false);
        var result = new DependencyRetentionResult("COMPLETE", archived, current, false);
        await UpdateStorageMaintenanceAsync(connection, transaction, operationId, "COMPLETE", JsonSerializer.Serialize(result, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<long> CountWithStatusAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string table,
        string projectId,
        string predicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE project_id=$project AND {predicate};";
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}

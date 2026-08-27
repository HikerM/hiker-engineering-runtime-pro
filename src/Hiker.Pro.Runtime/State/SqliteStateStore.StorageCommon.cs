using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    private static void ValidateStorageOperation(string operationId, string projectId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 160)
            throw new ArgumentException("Storage operation ID must contain 1-160 characters", nameof(operationId));
        _ = new StateKey(projectId, EntityKinds.Project, "storage").Normalize();
    }

    private async Task<StorageMaintenanceRow?> ReadStorageMaintenanceAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id,operation_type,request_fingerprint,status,result_json FROM storage_maintenance WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new StorageMaintenanceRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async Task BeginStorageMaintenanceAsync(
        string operationId,
        string projectId,
        string operationType,
        string requestFingerprint,
        string status,
        string? resultJson,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO storage_maintenance(operation_id,project_id,operation_type,request_fingerprint,status,result_json,created_at,updated_at)
            VALUES($id,$project,$type,$fingerprint,$status,$result,$at,$at);
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$type", operationType);
        command.Parameters.AddWithValue("$fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateStorageMaintenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string status,
        string? resultJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE storage_maintenance
            SET status=$status,result_json=COALESCE($result,result_json),updated_at=$at
            WHERE operation_id=$id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", operationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Storage maintenance receipt is missing");
    }

    private async Task CompleteStorageMaintenanceAsync(
        string operationId,
        string resultJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await UpdateStorageMaintenanceAsync(connection, transaction, operationId, "COMPLETE", resultJson, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureStorageReplay(StorageMaintenanceRow row, string operationType, string requestFingerprint)
    {
        if (row.OperationType != operationType || row.RequestFingerprint != requestFingerprint)
            throw new InvalidOperationException("Storage operation ID is bound to different maintenance facts");
    }

    private static async Task<IReadOnlyList<ArtifactRecord>> ReadArtifactsAsync(
        SqliteConnection connection,
        string projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id,project_id,type,sha256,bytes,relative_ref,task_id,evidence_id,source_fingerprint,retention,created_at
            FROM artifacts WHERE project_id=$project ORDER BY created_at,artifact_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$limit", limit);
        var values = new List<ArtifactRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new ArtifactRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                Enum.Parse<RetentionTier>(reader.GetString(9), ignoreCase: true), DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return values;
    }

    private static async Task<Dictionary<string, IReadOnlyList<ArtifactLink>>> ReadArtifactLinksAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256,ref_kind,ref_id,purpose,created_at FROM artifact_links WHERE project_id=$project ORDER BY sha256,ref_kind,ref_id;";
        command.Parameters.AddWithValue("$project", projectId);
        var mutable = new Dictionary<string, List<ArtifactLink>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = reader.GetString(0);
            if (!mutable.TryGetValue(hash, out var links)) mutable[hash] = links = new List<ArtifactLink>();
            links.Add(new ArtifactLink(projectId, hash, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return mutable.ToDictionary(static item => item.Key, static item => (IReadOnlyList<ArtifactLink>)ContractCollections.Freeze(item.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, (StateLifecycle Lifecycle, RetentionTier Retention, string Kind)?>> ReadArtifactLinkStateAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT link.ref_kind,link.ref_id,
                   COALESCE(entity.lifecycle,history.lifecycle),
                   COALESCE(entity.retention,history.retention)
            FROM artifact_links link
            LEFT JOIN entities entity ON entity.project_id=link.project_id AND entity.kind=link.ref_kind AND entity.entity_id=link.ref_id
            LEFT JOIN archived_entities history ON history.project_id=link.project_id AND history.kind=link.ref_kind AND history.entity_id=link.ref_id
            WHERE link.project_id=$project;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var values = new Dictionary<string, (StateLifecycle, RetentionTier, string)?>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = $"{reader.GetString(0)}|{reader.GetString(1)}";
            values[key] = reader.IsDBNull(2) ? null : (Enum.Parse<StateLifecycle>(reader.GetString(2), true), Enum.Parse<RetentionTier>(reader.GetString(3), true), reader.GetString(0));
        }
        return values;
    }

    private static async Task DeleteVerificationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM artifact_verifications WHERE project_id=$project AND sha256=$hash;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteArtifactLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM artifact_links WHERE project_id=$project AND sha256=$hash;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ArtifactMetadataExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM artifacts WHERE project_id=$project AND sha256=$hash);";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task SetStorageCounterFromQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        string counterName,
        string scalarSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO storage_counters(project_id,counter_name,counter_value,updated_at)
            SELECT $project,$name,({scalarSql}),$updated
            ON CONFLICT(project_id,counter_name) DO UPDATE SET
                counter_value=excluded.counter_value,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$name", counterName);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadStorageCounterAsync(
        SqliteConnection connection,
        string projectId,
        string counterName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT counter_value FROM storage_counters WHERE project_id=$project AND counter_name=$name;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$name", counterName);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private readonly record struct StorageMaintenanceRow(string ProjectId, string OperationType, string RequestFingerprint, string Status, string? ResultJson);
}

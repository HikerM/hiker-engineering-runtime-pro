using System.Text.Json;
using Hiker.Pro.Storage;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<StorageCompactionResult> CompactStorageAsync(
        string operationId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        var fingerprint = RequestHash($"storage-compaction|{projectId}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "STORAGE_COMPACTION", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
                return JsonSerializer.Deserialize<StorageCompactionResult>(prior.Value.ResultJson, StorageJson.Options)! with { Idempotent = true };
        }

        await using (var safety = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            var active = await ActiveCompactionBlockersAsync(safety, projectId, cancellationToken).ConfigureAwait(false);
            if (active > 0) return new StorageCompactionResult("BLOCKED_ACTIVE_WRITER_OR_RECOVERY", 0, 0, 0, 0, false, null, false);
            var ratio = await FreelistRatioAsync(safety, cancellationToken).ConfigureAwait(false);
            var bytes = File.Exists(_layout.DatabasePath) ? new FileInfo(_layout.DatabasePath).Length : 0;
            if (ratio < 0.10 || bytes < 1_048_576)
            {
                var skipped = new StorageCompactionResult("NOT_REQUIRED", bytes, bytes, 0, ratio, false, null, false);
                if (prior is null) await BeginStorageMaintenanceAsync(operationId, projectId, "STORAGE_COMPACTION", fingerprint, "COMPLETE", JsonSerializer.Serialize(skipped, StorageJson.Options), cancellationToken).ConfigureAwait(false);
                else await CompleteStorageMaintenanceAsync(operationId, JsonSerializer.Serialize(skipped, StorageJson.Options), cancellationToken).ConfigureAwait(false);
                return skipped;
            }
        }

        if (prior is null)
        {
            await BeginStorageMaintenanceAsync(operationId, projectId, "STORAGE_COMPACTION", fingerprint, "PREPARED", null, cancellationToken).ConfigureAwait(false);
            prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        }

        CompactionRecoveryPlan recovery;
        if (prior?.Status == "BACKUP_VERIFIED" && prior.Value.ResultJson is not null)
        {
            recovery = JsonSerializer.Deserialize<CompactionRecoveryPlan>(prior.Value.ResultJson, StorageJson.Options)
                ?? throw new InvalidDataException("Compaction recovery receipt is invalid");
        }
        else
        {
            var backup = await new StateBackupService(_layout).CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            recovery = new CompactionRecoveryPlan(backup.Sha256, Path.GetRelativePath(_layout.AiRoot, backup.BackupPath).Replace(Path.DirectorySeparatorChar, '/'));
            await using var receiptConnection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var receiptTransaction = receiptConnection.BeginTransaction(deferred: false);
            await UpdateStorageMaintenanceAsync(receiptConnection, receiptTransaction, operationId, "BACKUP_VERIFIED", JsonSerializer.Serialize(recovery, StorageJson.Options), cancellationToken).ConfigureAwait(false);
            await receiptTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        long before;
        double ratioBefore;
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            before = new FileInfo(_layout.DatabasePath).Length;
            ratioBefore = await FreelistRatioAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var finalize = connection.CreateCommand();
            finalize.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await finalize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var after = new FileInfo(_layout.DatabasePath).Length;
        var result = new StorageCompactionResult("COMPACTED", before, after, Math.Max(0, before - after), ratioBefore, true, recovery.BackupSha256, false);
        await CompleteStorageMaintenanceAsync(operationId, JsonSerializer.Serialize(result, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<long> ActiveCompactionBlockersAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM entities WHERE project_id=$project AND lifecycle='VALID' AND kind IN ('RUNTIME_LEASE','OWNERSHIP','WORKTREE')) +
              (SELECT COUNT(*) FROM operations WHERE project_id=$project AND status <> 'COMPLETE') +
              (SELECT COUNT(*) FROM storage_maintenance WHERE project_id=$project AND status NOT IN ('COMPLETE','PREPARED','BACKUP_VERIFIED'));
            """;
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CompactionRecoveryPlan(string BackupSha256, string BackupRelativeRef);
}

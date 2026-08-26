using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed class StateBackupService(ProjectLayout layout)
{
    public async Task<StateBackupResult> CreateAsync(string? destinationDirectory = null, CancellationToken cancellationToken = default)
    {
        layout.EnsureDirectories();
        if (!File.Exists(layout.DatabasePath))
        {
            throw new InvalidOperationException("State database does not exist");
        }

        var directory = Path.GetFullPath(destinationDirectory ?? Path.Combine(layout.ArchiveRoot, "state-backups"));
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(directory, $"hiker-state-{stamp}.db");
        await BackupDatabaseAsync(layout.DatabasePath, backupPath, allowExistingDestination: false, cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(backupPath, cancellationToken).ConfigureAwait(false);

        var hash = await HashFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        var manifestPath = $"{backupPath}.manifest.json";
        var manifest = JsonSerializer.Serialize(new
        {
            format = "hiker-state-backup/v1",
            schema_version = SqliteSchema.CurrentVersion,
            sha256 = hash,
            bytes = new FileInfo(backupPath).Length,
            created_at = createdAt
        }, new JsonSerializerOptions { WriteIndented = true });
        await WriteDurableAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new StateBackupResult("CREATED", backupPath, manifestPath, hash, new FileInfo(backupPath).Length, createdAt);
    }

    public async Task<StateRestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(backupPath);
        await ValidateDatabaseAsync(source, cancellationToken).ConfigureAwait(false);
        var sourceHash = await HashFileAsync(source, cancellationToken).ConfigureAwait(false);
        var safety = await CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        SqliteConnection.ClearAllPools();
        try
        {
            await BackupDatabaseAsync(source, layout.DatabasePath, allowExistingDestination: true, cancellationToken).ConfigureAwait(false);
            await using var store = new SqliteStateStore(layout);
            var health = await store.HealthCheckAsync(cancellationToken).ConfigureAwait(false);
            if (health.Status == "BLOCKED")
            {
                throw new InvalidDataException("Restored state did not pass Doctor");
            }

            return new StateRestoreResult("RESTORED", sourceHash, safety.BackupPath, health);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            await BackupDatabaseAsync(safety.BackupPath, layout.DatabasePath, allowExistingDestination: true, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task BackupDatabaseAsync(
        string sourcePath,
        string destinationPath,
        bool allowExistingDestination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!allowExistingDestination && File.Exists(destinationPath))
        {
            throw new IOException("Backup destination already exists");
        }

        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using var source = new SqliteConnection(sourceBuilder.ToString());
        await using var destination = new SqliteConnection(destinationBuilder.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        await using var flush = destination.CreateCommand();
        flush.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await flush.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("State backup was not found", path);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT value FROM metadata WHERE key='schema_version'), (SELECT * FROM pragma_quick_check LIMIT 1);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), SqliteSchema.CurrentVersion, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("State backup schema or integrity is invalid");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task WriteDurableAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: false);
    }
}

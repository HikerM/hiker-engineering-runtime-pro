using Microsoft.Data.Sqlite;
using Hiker.Pro.Release;
using Hiker.Pro.Storage;

namespace Hiker.Pro.State;

internal static class SqliteSchema
{
    public const string CurrentVersion = "3.0.0";

    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS entities (
            project_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            generation INTEGER NOT NULL,
            goal_revision INTEGER NOT NULL,
            lifecycle TEXT NOT NULL,
            retention TEXT NOT NULL,
            authority TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL,
            dependency_fingerprint TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (project_id, kind, entity_id)
        );

        CREATE INDEX IF NOT EXISTS ix_entities_hot
            ON entities(project_id, kind, lifecycle, updated_at DESC);
        CREATE INDEX IF NOT EXISTS ix_entities_generation
            ON entities(project_id, generation);

        CREATE TABLE IF NOT EXISTS operations (
            operation_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            operation_type TEXT NOT NULL,
            request_fingerprint TEXT NOT NULL,
            status TEXT NOT NULL,
            generation INTEGER,
            entity_kind TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT,
            result_json TEXT
        );

        CREATE TABLE IF NOT EXISTS engineering_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id TEXT NOT NULL UNIQUE,
            project_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            entity_kind TEXT,
            entity_id TEXT,
            generation INTEGER NOT NULL,
            summary_code TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            occurred_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_events_project_generation
            ON engineering_events(project_id, generation DESC);

        CREATE TABLE IF NOT EXISTS dependency_edges (
            project_id TEXT NOT NULL,
            from_kind TEXT NOT NULL,
            from_id TEXT NOT NULL,
            to_kind TEXT NOT NULL,
            to_id TEXT NOT NULL,
            relation TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            lifecycle TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(project_id, from_kind, from_id, to_kind, to_id, relation)
        );

        CREATE INDEX IF NOT EXISTS ix_edges_outgoing
            ON dependency_edges(project_id, from_kind, from_id, lifecycle);
        CREATE INDEX IF NOT EXISTS ix_edges_incoming
            ON dependency_edges(project_id, to_kind, to_id, lifecycle);

        CREATE TABLE IF NOT EXISTS materialized_views (
            project_id TEXT NOT NULL,
            view_name TEXT NOT NULL,
            source_generation INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(project_id, view_name)
        );

        CREATE TABLE IF NOT EXISTS artifacts (
            artifact_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            type TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            bytes INTEGER NOT NULL,
            relative_ref TEXT NOT NULL,
            task_id TEXT,
            evidence_id TEXT,
            source_fingerprint TEXT NOT NULL,
            retention TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_artifacts_project_hash
            ON artifacts(project_id, sha256);

        CREATE TABLE IF NOT EXISTS schema_migrations (
            version TEXT PRIMARY KEY,
            applied_at TEXT NOT NULL,
            checksum TEXT NOT NULL
        );
        """;

    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!string.Equals(CurrentVersion, BuildVersionFacts.Current.StateSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Compiled state schema does not match release version facts");
        }

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, CreateSql, cancellationToken).ConfigureAwait(false);

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            INSERT INTO metadata(key, value) VALUES('schema_version', $version)
            ON CONFLICT(key) DO NOTHING;
            INSERT INTO metadata(key, value) VALUES('global_generation', '0')
            ON CONFLICT(key) DO NOTHING;
            """;
        metadata.Parameters.AddWithValue("$version", CurrentVersion);
        await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            INSERT INTO schema_migrations(version, applied_at, checksum)
            VALUES($version, $at, $checksum)
            ON CONFLICT(version) DO NOTHING;
            """;
        migration.Parameters.AddWithValue("$version", CurrentVersion);
        migration.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        migration.Parameters.AddWithValue("$checksum", SchemaChecksum);
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await StorageSchema.InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> IsCurrentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('metadata','entities','operations','engineering_events','dependency_edges','materialized_views','artifacts','schema_migrations','artifact_verifications','artifact_links','runtime_snapshots','state_deltas','event_segments','event_dedup','archived_operations','archived_dependency_edges','archived_entities','storage_maintenance','storage_counters','migration_inventory_segments');";
        var tableCount = Convert.ToInt32(await tables.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (tableCount != 20)
        {
            return false;
        }

        await using var migrations = connection.CreateCommand();
        migrations.CommandText = "SELECT version, checksum FROM schema_migrations WHERE version IN ($core, $storage);";
        migrations.Parameters.AddWithValue("$core", CurrentVersion);
        migrations.Parameters.AddWithValue("$storage", StorageSchema.ExtensionVersion);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await migrations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values.TryGetValue(CurrentVersion, out var coreChecksum) &&
               IsCompatibleChecksum(coreChecksum) &&
               values.TryGetValue(StorageSchema.ExtensionVersion, out var storageChecksum) &&
               StorageSchema.IsCompatibleChecksum(storageChecksum);
    }

    public static string SchemaChecksum => SchemaSqlChecksum.Compute(CreateSql);

    public static bool IsCompatibleChecksum(string? checksum) => SchemaSqlChecksum.IsCompatible(checksum, CreateSql);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

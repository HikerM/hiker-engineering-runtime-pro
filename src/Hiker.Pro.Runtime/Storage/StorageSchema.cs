using Hiker.Pro.State;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.Storage;

internal static class StorageSchema
{
    public const string ExtensionVersion = "storage-lifecycle-v1";

    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS artifact_verifications (
            project_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            bytes INTEGER NOT NULL,
            last_write_utc TEXT NOT NULL,
            verified_at TEXT NOT NULL,
            source_generation INTEGER NOT NULL,
            status TEXT NOT NULL,
            PRIMARY KEY(project_id, sha256)
        );

        CREATE TABLE IF NOT EXISTS artifact_links (
            project_id TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            ref_kind TEXT NOT NULL,
            ref_id TEXT NOT NULL,
            purpose TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY(project_id, sha256, ref_kind, ref_id, purpose)
        );
        CREATE INDEX IF NOT EXISTS ix_artifact_links_ref
            ON artifact_links(project_id, ref_kind, ref_id);

        CREATE TABLE IF NOT EXISTS runtime_snapshots (
            snapshot_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            generation INTEGER NOT NULL,
            event_sequence INTEGER NOT NULL,
            schema_version TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_runtime_snapshots_project_generation
            ON runtime_snapshots(project_id, generation DESC);

        CREATE TABLE IF NOT EXISTS state_deltas (
            project_id TEXT NOT NULL,
            generation INTEGER NOT NULL,
            kind TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            goal_revision INTEGER NOT NULL,
            lifecycle TEXT NOT NULL,
            retention TEXT NOT NULL,
            authority TEXT NOT NULL,
            schema_version TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL,
            dependency_fingerprint TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(project_id, generation, kind, entity_id)
        );
        CREATE INDEX IF NOT EXISTS ix_state_deltas_project_generation
            ON state_deltas(project_id, generation);

        CREATE TABLE IF NOT EXISTS event_segments (
            segment_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            from_generation INTEGER NOT NULL,
            to_generation INTEGER NOT NULL,
            first_sequence INTEGER NOT NULL,
            last_sequence INTEGER NOT NULL,
            event_count INTEGER NOT NULL,
            delta_count INTEGER NOT NULL,
            relative_ref TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            bytes INTEGER NOT NULL,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_event_segments_project_generation
            ON event_segments(project_id, to_generation DESC);

        CREATE TABLE IF NOT EXISTS event_dedup (
            event_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            generation INTEGER NOT NULL,
            fingerprint TEXT NOT NULL,
            segment_id TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS archived_operations (
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
            result_json TEXT,
            archived_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_archived_operations_project_generation
            ON archived_operations(project_id, generation DESC);

        CREATE TABLE IF NOT EXISTS archived_dependency_edges (
            project_id TEXT NOT NULL,
            from_kind TEXT NOT NULL,
            from_id TEXT NOT NULL,
            to_kind TEXT NOT NULL,
            to_id TEXT NOT NULL,
            relation TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            lifecycle TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            archived_at TEXT NOT NULL,
            PRIMARY KEY(project_id, from_kind, from_id, to_kind, to_id, relation)
        );

        CREATE TABLE IF NOT EXISTS archived_entities (
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
            archived_at TEXT NOT NULL,
            PRIMARY KEY(project_id, kind, entity_id)
        );
        CREATE INDEX IF NOT EXISTS ix_archived_entities_project_generation
            ON archived_entities(project_id, generation DESC);

        CREATE TABLE IF NOT EXISTS storage_maintenance (
            operation_id TEXT PRIMARY KEY,
            project_id TEXT NOT NULL,
            operation_type TEXT NOT NULL,
            request_fingerprint TEXT NOT NULL,
            status TEXT NOT NULL,
            result_json TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS storage_counters (
            project_id TEXT NOT NULL,
            counter_name TEXT NOT NULL,
            counter_value INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(project_id, counter_name)
        );

        CREATE TABLE IF NOT EXISTS migration_inventory_segments (
            project_id TEXT NOT NULL,
            relative_root TEXT NOT NULL,
            root_identity TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            files INTEGER NOT NULL,
            bytes INTEGER NOT NULL,
            status TEXT NOT NULL,
            verified_at TEXT NOT NULL,
            PRIMARY KEY(project_id, relative_root)
        );
        """;

    public static string Checksum => SchemaSqlChecksum.Compute(CreateSql);

    public static bool IsCompatibleChecksum(string? checksum) => SchemaSqlChecksum.IsCompatible(checksum, CreateSql);

    public static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = CreateSql;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version;";
        existing.Parameters.AddWithValue("$version", ExtensionVersion);
        var scalar = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var checksum = scalar is null or DBNull
            ? null
            : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);
        if (checksum is not null && !IsCompatibleChecksum(checksum))
            throw new InvalidDataException("Storage lifecycle schema extension checksum does not match");

        await using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            INSERT INTO schema_migrations(version, applied_at, checksum)
            VALUES($version, $at, $checksum)
            ON CONFLICT(version) DO NOTHING;
            """;
        migration.Parameters.AddWithValue("$version", ExtensionVersion);
        migration.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        migration.Parameters.AddWithValue("$checksum", Checksum);
        await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

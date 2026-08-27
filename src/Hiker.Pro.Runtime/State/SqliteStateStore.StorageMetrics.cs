using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<StorageMetrics> GetStorageMetricsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = new StateKey(projectId, EntityKinds.Project, "storage").Normalize().ProjectId;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var stateBytes = File.Exists(_layout.DatabasePath) ? new FileInfo(_layout.DatabasePath).Length : 0;
        var artifactBytes = await SumAsync(connection, "SELECT COALESCE(SUM(bytes),0) FROM artifacts WHERE project_id=$project;", project, cancellationToken).ConfigureAwait(false);
        var archiveBytes = await SumAsync(connection, "SELECT COALESCE(SUM(bytes),0) FROM event_segments WHERE project_id=$project;", project, cancellationToken).ConfigureAwait(false);
        var cacheBytes = await SumAsync(connection, "SELECT COALESCE(SUM(length(payload_json)),0) FROM entities WHERE project_id=$project AND retention='CACHE';", project, cancellationToken).ConfigureAwait(false);
        var tempBytes = await SumAsync(connection, "SELECT COALESCE(SUM(length(payload_json)),0) FROM entities WHERE project_id=$project AND retention='TEMP';", project, cancellationToken).ConfigureAwait(false);
        var preservedBytes = await SumAsync(connection, "SELECT COALESCE(SUM(bytes),0) FROM migration_inventory_segments WHERE project_id=$project AND status='VERIFIED';", project, cancellationToken).ConfigureAwait(false);
        var hotBytes = await SumAsync(connection, "SELECT COALESCE(SUM(length(payload_json)+512),0) FROM entities WHERE project_id=$project AND retention='HOT';", project, cancellationToken).ConfigureAwait(false);
        var warmBytes = await SumAsync(connection, "SELECT COALESCE(SUM(length(payload_json)+512),0) FROM entities WHERE project_id=$project AND retention='WARM';", project, cancellationToken).ConfigureAwait(false);
        var hot = await CountAsync(connection, "entities", project, "retention='HOT'", cancellationToken).ConfigureAwait(false);
        var warm = await CountAsync(connection, "entities", project, "retention='WARM'", cancellationToken).ConfigureAwait(false);
        var cold = await CountAsync(connection, "entities", project, "retention='COLD'", cancellationToken).ConfigureAwait(false);
        var archived = await CountAsync(connection, "entities", project, "retention='ARCHIVED'", cancellationToken).ConfigureAwait(false)
            + await ReadStorageCounterAsync(connection, project, "archived_entities", cancellationToken).ConfigureAwait(false);
        var events = await CountAsync(connection, "engineering_events", project, "1=1", cancellationToken).ConfigureAwait(false);
        var operations = await CountAsync(connection, "operations", project, "1=1", cancellationToken).ConfigureAwait(false);
        var archivedOperations = await ReadStorageCounterAsync(connection, project, "archived_operations", cancellationToken).ConfigureAwait(false);
        var dependencies = await CountAsync(connection, "dependency_edges", project, "lifecycle='VALID'", cancellationToken).ConfigureAwait(false);
        var historicalDependencies = await ReadStorageCounterAsync(connection, project, "archived_dependencies", cancellationToken).ConfigureAwait(false);
        var artifacts = await CountAsync(connection, "artifacts", project, "1=1", cancellationToken).ConfigureAwait(false);
        var verified = await CountAsync(connection, "artifact_verifications", project, "status='VERIFIED'", cancellationToken).ConfigureAwait(false);
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        return new StorageMetrics(
            stateBytes + artifactBytes + archiveBytes + preservedBytes,
            stateBytes,
            artifactBytes,
            archiveBytes,
            cacheBytes,
            tempBytes,
            preservedBytes,
            hotBytes,
            warmBytes,
            hot,
            warm,
            cold,
            archived,
            events,
            operations,
            archivedOperations,
            dependencies,
            historicalDependencies,
            artifacts,
            verified,
            generation);
    }

    private static async Task<long> SumAsync(SqliteConnection connection, string sql, string projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string projectId, string predicate, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE project_id=$project AND {predicate};";
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}

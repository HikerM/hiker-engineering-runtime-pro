using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<StateExportResult> ExportAsync(
        string projectId,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedProject = new StateKey(projectId, EntityKinds.Project, "export").Normalize().ProjectId;
        _layout.EnsureDirectories();
        var createdAt = DateTimeOffset.UtcNow;
        var destination = ResolveExportPath(normalizedProject, outputPath, createdAt);
        if (File.Exists(destination))
        {
            throw new IOException("State export destination already exists");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        long generation;
        long entities;
        long operations;
        long events;
        long dependencies;
        long artifacts;
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using (var generationCommand = connection.CreateCommand())
            {
                generationCommand.Transaction = transaction;
                generationCommand.CommandText = "SELECT value FROM metadata WHERE key='global_generation';";
                generation = long.Parse(
                    Convert.ToString(await generationCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? "0",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                131072,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("schema_version", SqliteSchema.CurrentVersion);
                writer.WriteString("format", "hiker-state-export/v1");
                writer.WriteString("project_id", normalizedProject);
                writer.WriteNumber("generation", generation);
                writer.WriteString("created_at", createdAt);
                entities = await WriteEntitiesAsync(writer, connection, transaction, normalizedProject, cancellationToken).ConfigureAwait(false);
                operations = await WriteOperationsAsync(writer, connection, transaction, normalizedProject, cancellationToken).ConfigureAwait(false);
                events = await WriteEventsAsync(writer, connection, transaction, normalizedProject, cancellationToken).ConfigureAwait(false);
                dependencies = await WriteDependenciesAsync(writer, connection, transaction, normalizedProject, cancellationToken).ConfigureAwait(false);
                artifacts = await WriteArtifactsAsync(writer, connection, transaction, normalizedProject, cancellationToken).ConfigureAwait(false);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            File.Move(temporary, destination, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }

        var info = new FileInfo(destination);
        await using var content = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        return new StateExportResult(
            "EXPORTED",
            destination,
            sha256,
            info.Length,
            generation,
            entities,
            operations,
            events,
            dependencies,
            artifacts,
            createdAt);
    }

    private string ResolveExportPath(string projectId, string? outputPath, DateTimeOffset createdAt)
    {
        var destination = outputPath is null
            ? Path.Combine(
                _layout.ExportsRoot,
                $"state-{ShortHash(projectId)}-{createdAt:yyyyMMddTHHmmssfffZ}.json")
            : Path.GetFullPath(outputPath, _layout.ProjectRoot);
        var stateRelative = Path.GetRelativePath(_layout.StateDirectory, destination);
        if (stateRelative != ".." && !stateRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("State exports cannot overwrite or reside inside the authoritative state directory");
        }

        return destination;
    }

    private static async Task<long> WriteEntitiesAsync(
        Utf8JsonWriter writer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("entities");
        writer.WriteStartArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SnapshotColumns} FROM entities WHERE project_id=$project ORDER BY kind, entity_id;";
        command.Parameters.AddWithValue("$project", projectId);
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, SqliteMapping.ReadSnapshot(reader), ExportJson);
            count++;
        }

        writer.WriteEndArray();
        return count;
    }

    private static async Task<long> WriteOperationsAsync(
        Utf8JsonWriter writer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("operations");
        writer.WriteStartArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, project_id, operation_type, request_fingerprint, status,
                   generation, entity_kind, entity_id, created_at, completed_at, result_json
            FROM operations WHERE project_id=$project ORDER BY created_at, operation_id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var receipt = new OperationReceipt(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : reader.GetString(10));
            JsonSerializer.Serialize(writer, receipt, ExportJson);
            count++;
        }

        writer.WriteEndArray();
        return count;
    }

    private static async Task<long> WriteEventsAsync(
        Utf8JsonWriter writer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("engineering_events");
        writer.WriteStartArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, event_id, event_type, entity_kind, entity_id, generation,
                   summary_code, payload_json, occurred_at
            FROM engineering_events WHERE project_id=$project ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", reader.GetInt64(0));
            writer.WriteString("event_id", reader.GetString(1));
            writer.WriteString("event_type", reader.GetString(2));
            if (!reader.IsDBNull(3)) writer.WriteString("entity_kind", reader.GetString(3));
            if (!reader.IsDBNull(4)) writer.WriteString("entity_id", reader.GetString(4));
            writer.WriteNumber("generation", reader.GetInt64(5));
            writer.WriteString("summary_code", reader.GetString(6));
            writer.WriteString("payload_json", reader.GetString(7));
            writer.WriteString("occurred_at", DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            count++;
        }

        writer.WriteEndArray();
        return count;
    }

    private static async Task<long> WriteDependenciesAsync(
        Utf8JsonWriter writer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("dependency_edges");
        writer.WriteStartArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, from_kind, from_id, to_kind, to_id, relation, fingerprint, lifecycle
            FROM dependency_edges WHERE project_id=$project
            ORDER BY from_kind, from_id, to_kind, to_id, relation;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            JsonSerializer.Serialize(writer, SqliteMapping.ReadEdge(reader), ExportJson);
            count++;
        }

        writer.WriteEndArray();
        return count;
    }

    private static async Task<long> WriteArtifactsAsync(
        Utf8JsonWriter writer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("artifact_refs");
        writer.WriteStartArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT artifact_id, project_id, type, sha256, bytes, relative_ref, task_id,
                   evidence_id, source_fingerprint, retention, created_at
            FROM artifacts WHERE project_id=$project ORDER BY artifact_id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        long count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var artifact = new ArtifactRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                Enum.Parse<RetentionTier>(reader.GetString(9), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture));
            JsonSerializer.Serialize(writer, artifact, ExportJson);
            count++;
        }

        writer.WriteEndArray();
        return count;
    }

    private static string ShortHash(string value) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

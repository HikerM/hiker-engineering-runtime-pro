using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<InvalidationResult> InvalidateAsync(
        InvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 160 ||
            string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Length > 160)
        {
            throw new ArgumentException("Invalidation operation ID and reason code must be bounded non-empty values", nameof(request));
        }

        if (request.Keys.Count == 0)
        {
            throw new ArgumentException("Invalidation requires at least one state key", nameof(request));
        }

        var normalizedProject = new StateKey(request.ProjectId, "PROJECT", "PROJECT").Normalize().ProjectId;
        var normalized = request.Keys.Select(key => key.Normalize()).Distinct().OrderBy(static key => key.Kind, StringComparer.Ordinal).ThenBy(static key => key.EntityId, StringComparer.Ordinal).ToArray();
        if (normalized.Any(key => !string.Equals(key.ProjectId, normalizedProject, StringComparison.Ordinal)))
        {
            throw new ArgumentException("All invalidation keys must belong to the requested project", nameof(request));
        }

        var requestFingerprint = RequestHash(string.Join('\n', normalizedProject, request.GoalRevision, request.ReasonCode,
            string.Join('\n', normalized.Select(static key => $"{key.Kind}|{key.EntityId}"))));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!string.Equals(prior.Value.Fingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new InvalidationResult(false, true, 0, prior.Value.Generation, "OPERATION_ID_CONFLICT");
            }

            var priorAffected = 0;
            if (prior.Value.ResultJson is not null)
            {
                using var resultDocument = System.Text.Json.JsonDocument.Parse(prior.Value.ResultJson);
                priorAffected = resultDocument.RootElement.GetProperty("affected").GetInt32();
            }
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new InvalidationResult(true, false, priorAffected, prior.Value.Generation, "IDEMPOTENT_REPLAY");
        }

        await InsertPreparedOperationAsync(
            connection,
            transaction,
            new TransactionRequest(request.OperationId, new StateMutation(normalized[0], 0, request.GoalRevision, StateLifecycle.Stale, "STATE_COORDINATOR", SqliteSchema.CurrentVersion, "invalidation", "invalidation", "{}"), "EVIDENCE_INVALIDATED", request.ReasonCode, "{}"),
            normalized[0],
            requestFingerprint,
            cancellationToken).ConfigureAwait(false);
        var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var updated = 0;
        foreach (var key in normalized)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE entities
                SET lifecycle='STALE', goal_revision=$goal, generation=$generation, updated_at=$updated
                WHERE project_id=$project AND kind=$kind AND entity_id=$entity
                  AND lifecycle NOT IN ('ARCHIVED', 'CORRUPT', 'INCOMPATIBLE');
                """;
            SqliteMapping.AddStateKey(command, key);
            command.Parameters.AddWithValue("$goal", request.GoalRevision);
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            updated += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEventAsync(
            connection,
            transaction,
            new EngineeringEvent(
                $"invalidate-{Guid.NewGuid():N}",
                normalizedProject,
                "EVIDENCE_INVALIDATED",
                null,
                null,
                generation,
                request.ReasonCode,
                "{}",
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await CompleteOperationAsync(connection, transaction, request.OperationId, generation, DateTimeOffset.UtcNow, cancellationToken, System.Text.Json.JsonSerializer.Serialize(new { affected = updated })).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InvalidationResult(true, false, updated, generation, "COMMITTED");
    }

    public async Task<MaterializationResult> MaterializeAsync(
        MaterializationRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(request.ProjectId, "MATERIALIZED_VIEW", request.ViewName).Normalize();
        if (request.SourceGeneration < 0 || string.IsNullOrWhiteSpace(request.Fingerprint) || request.Fingerprint.Length > 256)
        {
            throw new ArgumentException("Materialized view generation and fingerprint are invalid", nameof(request));
        }
        SqliteMapping.ValidateJson(request.PayloadJson, nameof(request.PayloadJson));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var currentGeneration = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (request.SourceGeneration > currentGeneration)
        {
            throw new InvalidOperationException("Materialized view cannot reference a future state generation");
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO materialized_views(project_id, view_name, source_generation, payload_json, fingerprint, updated_at)
            VALUES($project, $view, $generation, $payload, $fingerprint, $updated)
            ON CONFLICT(project_id, view_name) DO UPDATE SET
                source_generation=excluded.source_generation,
                payload_json=excluded.payload_json,
                fingerprint=excluded.fingerprint,
                updated_at=excluded.updated_at
            WHERE excluded.source_generation >= materialized_views.source_generation;
            """;
        command.Parameters.AddWithValue("$project", request.ProjectId);
        command.Parameters.AddWithValue("$view", request.ViewName);
        command.Parameters.AddWithValue("$generation", request.SourceGeneration);
        command.Parameters.AddWithValue("$payload", request.PayloadJson);
        command.Parameters.AddWithValue("$fingerprint", request.Fingerprint);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        return new MaterializationResult(changed, request.SourceGeneration, request.Fingerprint);
    }

    public async Task<MigrationResult> MigrateAsync(string targetVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await MetadataAsync(connection, "schema_version", cancellationToken).ConfigureAwait(false) ?? "unknown";
        if (string.Equals(current, targetVersion, StringComparison.Ordinal))
        {
            return new MigrationResult(current, targetVersion, false, "CURRENT");
        }

        if (!string.Equals(targetVersion, SqliteSchema.CurrentVersion, StringComparison.Ordinal))
        {
            return new MigrationResult(current, targetVersion, false, "INCOMPATIBLE_TARGET");
        }

        return new MigrationResult(current, targetVersion, false, "MIGRATION_PATH_REQUIRED");
    }

    public async Task<HealthReport> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<HealthFinding>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var quick = connection.CreateCommand())
        {
            quick.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await quick.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding("STATE_DB_CORRUPT", "BLOCKED", "state", result ?? "quick_check failed", false));
            }
        }

        var schema = await MetadataAsync(connection, "schema_version", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(schema, SqliteSchema.CurrentVersion, StringComparison.Ordinal))
        {
            findings.Add(new HealthFinding("SCHEMA_MISMATCH", "BLOCKED", "state", schema ?? "missing", false));
        }

        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version;";
            migration.Parameters.AddWithValue("$version", SqliteSchema.CurrentVersion);
            var checksum = Convert.ToString(await migration.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!SqliteSchema.IsCompatibleChecksum(checksum))
            {
                findings.Add(new HealthFinding("SCHEMA_CHECKSUM_MISMATCH", "BLOCKED", "state", checksum ?? "missing", false));
            }
        }

        await using (var lifecycle = connection.CreateCommand())
        {
            lifecycle.CommandText = "SELECT COUNT(*) FROM entities WHERE lifecycle NOT IN ('VALID','STALE','SUPERSEDED','ORPHANED','CORRUPT','INCOMPATIBLE','ARCHIVED') OR retention NOT IN ('HOT','WARM','COLD','ARCHIVED','CACHE','TEMP');";
            var count = Convert.ToInt32(await lifecycle.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new HealthFinding("INVALID_STATE_ENUM", "BLOCKED", "entities", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
            }
        }

        var globalGeneration = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = """
                SELECT COUNT(*) FROM entities
                WHERE generation <= 0 OR generation > $generation OR goal_revision < 0
                   OR length(trim(authority)) = 0 OR length(trim(schema_version)) = 0
                   OR length(trim(source_fingerprint)) = 0 OR length(trim(dependency_fingerprint)) = 0
                   OR json_valid(payload_json) = 0;
                """;
            integrity.Parameters.AddWithValue("$generation", globalGeneration);
            var count = Convert.ToInt32(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new HealthFinding("INVALID_STATE_FACT", "BLOCKED", "entities", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
            }
        }

        await using (var dependencies = connection.CreateCommand())
        {
            dependencies.CommandText = """
                SELECT COUNT(*)
                FROM dependency_edges edge
                LEFT JOIN entities source
                  ON source.project_id=edge.project_id AND source.kind=edge.from_kind AND source.entity_id=edge.from_id
                LEFT JOIN entities target
                  ON target.project_id=edge.project_id AND target.kind=edge.to_kind AND target.entity_id=edge.to_id
                WHERE edge.lifecycle='VALID' AND (source.entity_id IS NULL OR target.entity_id IS NULL);
                """;
            var count = Convert.ToInt32(await dependencies.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new HealthFinding("BROKEN_DEPENDENCY", "BLOCKED", "dependency_edges", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
            }
        }

        await using (var projections = connection.CreateCommand())
        {
            projections.CommandText = """
                SELECT COUNT(*) FROM materialized_views
                WHERE source_generation < 0 OR source_generation > $generation
                   OR length(trim(fingerprint)) = 0 OR json_valid(payload_json) = 0;
                """;
            projections.Parameters.AddWithValue("$generation", globalGeneration);
            var count = Convert.ToInt32(await projections.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new HealthFinding("CORRUPT_MATERIALIZED_VIEW", "REPAIRABLE", "materialized_views", count.ToString(System.Globalization.CultureInfo.InvariantCulture), true));
            }
        }

        await using (var prepared = connection.CreateCommand())
        {
            prepared.CommandText = "SELECT COUNT(*) FROM operations WHERE status <> 'COMPLETE';";
            var count = Convert.ToInt32(await prepared.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new HealthFinding("INCOMPLETE_OPERATION", "BLOCKED", "operations", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
            }
        }

        var artifactFacts = new List<(string Id, string Hash, long Bytes, string RelativeRef)>();
        await using (var artifacts = connection.CreateCommand())
        {
            artifacts.CommandText = "SELECT artifact_id, sha256, bytes, relative_ref FROM artifacts;";
            await using var reader = await artifacts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                artifactFacts.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
            }
        }

        foreach (var artifact in artifactFacts)
        {
            var relative = artifact.RelativeRef.Replace('/', Path.DirectorySeparatorChar);
            string? path = null;
            try
            {
                path = _layout.ResolveAiRelativePath(relative);
            }
            catch (InvalidDataException)
            {
                // Report the invalid reference below without resolving outside the project.
            }

            if (path is null || !File.Exists(path))
            {
                findings.Add(new HealthFinding("BROKEN_ARTIFACT_REF", "BLOCKED", artifact.Id, relative, false));
                continue;
            }

            var info = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (info.Length != artifact.Bytes || !string.Equals(actual, artifact.Hash, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding("CORRUPT_ARTIFACT", "BLOCKED", artifact.Id, "size or SHA-256 mismatch", false));
            }
        }

        var referencedHashes = artifactFacts.Select(static value => value.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_layout.ArtifactsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(_layout.ArtifactsRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (IsSha256Name(name) && !referencedHashes.Contains(name))
                {
                    findings.Add(new HealthFinding("ORPHAN_ARTIFACT", "REPAIRABLE", name, "content-addressed file has no metadata reference", true));
                }
            }
        }

        if (findings.Count == 0)
        {
            return HealthReport.Healthy;
        }

        return new HealthReport(findings.All(item => item.Repairable) ? "REPAIRABLE" : "BLOCKED", ContractCollections.Freeze(findings));
    }

    public async Task<RepairReport> RepairAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM materialized_views
            WHERE source_generation < 0 OR source_generation > $generation
               OR length(trim(fingerprint)) = 0 OR json_valid(payload_json) = 0;
            """;
        command.Parameters.AddWithValue("$generation", generation);
        var repaired = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var optimize = connection.CreateCommand();
        optimize.CommandText = "REINDEX; PRAGMA optimize;";
        await optimize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var health = await HealthCheckAsync(cancellationToken).ConfigureAwait(false);
        return new RepairReport(health.Status, repaired, health.Findings);
    }

    public async Task<GcReport> GarbageCollectAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var archived = await ExecuteCountAsync(
            connection,
            transaction,
            "UPDATE entities SET lifecycle='ARCHIVED', retention='ARCHIVED' WHERE lifecycle IN ('STALE','SUPERSEDED') AND updated_at < $older;",
            olderThan,
            cancellationToken).ConfigureAwait(false);
        var deletedCache = await ExecuteCountAsync(
            connection,
            transaction,
            "DELETE FROM entities WHERE retention='CACHE' AND updated_at < $older;",
            olderThan,
            cancellationToken).ConfigureAwait(false);
        var deletedTemp = await ExecuteCountAsync(
            connection,
            transaction,
            "DELETE FROM entities WHERE retention='TEMP' AND updated_at < $older;",
            olderThan,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var referenced = await ReferencedArtifactHashesAsync(cancellationToken).ConfigureAwait(false);
        var orphanArtifacts = 0;
        if (Directory.Exists(_layout.ArtifactsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(_layout.ArtifactsRoot, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < olderThan.UtcDateTime && IsSha256Name(info.Name) && !referenced.Contains(info.Name))
                {
                    File.Delete(path);
                    orphanArtifacts++;
                }
            }
        }
        return new GcReport(archived, deletedCache, deletedTemp, orphanArtifacts);
    }

    public async Task<StoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        return new StoreMetrics(
            generation,
            await ScalarCountAsync(connection, "entities", cancellationToken).ConfigureAwait(false),
            await ScalarCountAsync(connection, "engineering_events", cancellationToken).ConfigureAwait(false),
            await ScalarCountAsync(connection, "dependency_edges", cancellationToken).ConfigureAwait(false),
            await ScalarCountAsync(connection, "artifacts", cancellationToken).ConfigureAwait(false));
    }

    public async Task RegisterArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts(
                artifact_id, project_id, type, sha256, bytes, relative_ref,
                task_id, evidence_id, source_fingerprint, retention, created_at)
            VALUES($id, $project, $type, $hash, $bytes, $ref, $task, $evidence, $source, $retention, $created)
            ON CONFLICT(project_id, sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", artifact.ArtifactId);
        command.Parameters.AddWithValue("$project", artifact.ProjectId);
        command.Parameters.AddWithValue("$type", artifact.Type);
        command.Parameters.AddWithValue("$hash", artifact.Sha256);
        command.Parameters.AddWithValue("$bytes", artifact.Bytes);
        command.Parameters.AddWithValue("$ref", artifact.RelativeRef);
        command.Parameters.AddWithValue("$task", (object?)artifact.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", (object?)artifact.EvidenceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", artifact.SourceFingerprint);
        command.Parameters.AddWithValue("$retention", SqliteMapping.EnumToken(artifact.Retention));
        command.Parameters.AddWithValue("$created", artifact.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await UpsertArtifactVerificationAsync(connection, artifact, cancellationToken).ConfigureAwait(false);
        await InsertArtifactLinkIfPresentAsync(connection, artifact, artifact.TaskId is null ? null : EntityKinds.Task, artifact.TaskId, "TASK_OUTPUT", cancellationToken).ConfigureAwait(false);
        await InsertArtifactLinkIfPresentAsync(connection, artifact, artifact.EvidenceId is null ? null : EntityKinds.Evidence, artifact.EvidenceId, "EVIDENCE_CONTENT", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> ReferencedArtifactHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM artifacts;";
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    private static async Task<string?> MetadataAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecuteCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$older", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarCountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsSha256Name(string value) => value.Length == 64 && value.All(static character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

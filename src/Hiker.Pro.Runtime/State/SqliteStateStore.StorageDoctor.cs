using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Storage;
using Hiker.Pro.Safety;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<StorageDoctorReport> StorageHealthCheckAsync(
        string projectId,
        StorageHealthMode mode,
        CancellationToken cancellationToken = default)
    {
        var project = new StateKey(projectId, EntityKinds.Project, "storage-doctor").Normalize().ProjectId;
        var timer = Stopwatch.StartNew();
        var findings = new List<HealthFinding>();
        long hashedBytes = 0;
        var artifactScans = 0;
        var archiveScans = 0;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await CheckCoreAsync(connection, project, mode, findings, cancellationToken).ConfigureAwait(false);
        var artifacts = await ReadDoctorArtifactsAsync(connection, project, mode, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            var path = ResolveArtifactPath(artifact.RelativeRef);
            if (!File.Exists(path))
            {
                findings.Add(new HealthFinding("BROKEN_ARTIFACT_REF", "BLOCKED", artifact.ArtifactId, artifact.RelativeRef, false));
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length != artifact.Bytes)
            {
                findings.Add(new HealthFinding("CORRUPT_ARTIFACT", "BLOCKED", artifact.ArtifactId, "size mismatch", false));
                continue;
            }
            var verification = await ReadArtifactVerificationAsync(connection, project, artifact.Sha256, cancellationToken).ConfigureAwait(false);
            var unchanged = verification is not null && verification.Value.Bytes == info.Length && verification.Value.LastWriteUtc == info.LastWriteTimeUtc;
            if (mode == StorageHealthMode.Fast && unchanged) continue;
            var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            hashedBytes += info.Length;
            artifactScans++;
            if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new HealthFinding("CORRUPT_ARTIFACT", "BLOCKED", artifact.ArtifactId, "SHA-256 mismatch", false));
                continue;
            }
        }

        var segments = await ReadDoctorSegmentsAsync(connection, project, mode, cancellationToken).ConfigureAwait(false);
        foreach (var segment in segments)
        {
            var path = _layout.ResolveAiRelativePath(segment.RelativeRef);
            if (!File.Exists(path))
            {
                findings.Add(new HealthFinding("ARCHIVE_SEGMENT_MISSING", "BLOCKED", segment.SegmentId, segment.RelativeRef, false));
                continue;
            }
            if (mode == StorageHealthMode.Deep)
            {
                archiveScans++;
                if (!string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), segment.Sha256, StringComparison.OrdinalIgnoreCase))
                    findings.Add(new HealthFinding("ARCHIVE_SEGMENT_CORRUPT", "BLOCKED", segment.SegmentId, "SHA-256 mismatch", false));
            }
        }
        if (mode == StorageHealthMode.Deep) CheckOrphanFiles(project, artifacts, findings);
        var ratio = await FreelistRatioAsync(connection, cancellationToken).ConfigureAwait(false);
        if (ratio >= 0.25 && new FileInfo(_layout.DatabasePath).Length >= 1_048_576)
            findings.Add(new HealthFinding("COMPACTION_RECOMMENDED", "REPAIRABLE", "state", ratio.ToString("F4", System.Globalization.CultureInfo.InvariantCulture), true));
        var metrics = await GetStorageMetricsAsync(project, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        var status = findings.Any(static value => value.Severity == "BLOCKED") ? "BLOCKED" :
            findings.Count > 0 ? "REPAIRABLE" : "HEALTHY";
        var indicators = new List<string>();
        if (!findings.Any(static value => value.Code is "STATE_DB_CORRUPT" or "INVALID_STATE_FACT" or "SCHEMA_MISMATCH" or "SCHEMA_CHECKSUM_MISMATCH")) indicators.Add("HOT_STATE_HEALTHY");
        if (ratio < 0.25) indicators.Add("STORAGE_GROWTH_HEALTHY");
        if (!findings.Any(static value => value.Code.StartsWith("ARCHIVE_", StringComparison.Ordinal))) indicators.Add("ARCHIVE_HEALTHY");
        if (!findings.Any(static value => value.Code is "BROKEN_ARTIFACT_REF" or "CORRUPT_ARTIFACT" or "ORPHAN_ARTIFACTS_FOUND")) indicators.Add("ARTIFACT_REACHABILITY_HEALTHY");
        return new StorageDoctorReport(status, mode, ContractCollections.Freeze(findings), ContractCollections.Freeze(indicators), metrics, hashedBytes, artifactScans, archiveScans, timer.Elapsed.TotalMilliseconds);
    }

    private async Task CheckCoreAsync(
        SqliteConnection connection,
        string projectId,
        StorageHealthMode mode,
        List<HealthFinding> findings,
        CancellationToken cancellationToken)
    {
        await CheckDatabaseIntegrityAsync(connection, projectId, mode, findings, cancellationToken).ConfigureAwait(false);
        await using (var extension = connection.CreateCommand())
        {
            extension.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version;";
            extension.Parameters.AddWithValue("$version", StorageSchema.ExtensionVersion);
            var value = Convert.ToString(await extension.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!StorageSchema.IsCompatibleChecksum(value)) findings.Add(new HealthFinding("STORAGE_SCHEMA_MISMATCH", "BLOCKED", "state", value ?? "missing", false));
        }
        await using (var core = connection.CreateCommand())
        {
            core.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
            var value = Convert.ToString(await core.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(value, SqliteSchema.CurrentVersion, StringComparison.Ordinal))
                findings.Add(new HealthFinding("SCHEMA_MISMATCH", "BLOCKED", "state", value ?? "missing", false));
        }
        await using (var coreMigration = connection.CreateCommand())
        {
            coreMigration.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version;";
            coreMigration.Parameters.AddWithValue("$version", SqliteSchema.CurrentVersion);
            var value = Convert.ToString(await coreMigration.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (!SqliteSchema.IsCompatibleChecksum(value))
                findings.Add(new HealthFinding("SCHEMA_CHECKSUM_MISMATCH", "BLOCKED", "state", value ?? "missing", false));
        }
        await using (var facts = connection.CreateCommand())
        {
            facts.CommandText = """
                SELECT COUNT(*) FROM entities
                WHERE project_id=$project AND (
                    generation <= 0 OR goal_revision < 0 OR json_valid(payload_json)=0
                    OR length(trim(authority))=0 OR length(trim(schema_version))=0
                    OR length(trim(source_fingerprint))=0 OR length(trim(dependency_fingerprint))=0);
                """;
            facts.Parameters.AddWithValue("$project", projectId);
            var count = Convert.ToInt32(await facts.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0) findings.Add(new HealthFinding("INVALID_STATE_FACT", "BLOCKED", "entities", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
        }
        await using (var active = connection.CreateCommand())
        {
            active.CommandText = "SELECT COUNT(*) FROM operations WHERE project_id=$project AND status <> 'COMPLETE';";
            active.Parameters.AddWithValue("$project", projectId);
            var count = Convert.ToInt32(await active.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0) findings.Add(new HealthFinding("INCOMPLETE_OPERATION", "BLOCKED", "operations", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
        }
        await using (var maintenance = connection.CreateCommand())
        {
            maintenance.CommandText = "SELECT COUNT(*) FROM storage_maintenance WHERE project_id=$project AND status <> 'COMPLETE';";
            maintenance.Parameters.AddWithValue("$project", projectId);
            var count = Convert.ToInt32(await maintenance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0) findings.Add(new HealthFinding("STORAGE_MAINTENANCE_RECOVERY_REQUIRED", "BLOCKED", "storage", count.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
        }
        await using (var dependencies = connection.CreateCommand())
        {
            dependencies.CommandText = """
                SELECT COUNT(*) FROM dependency_edges edge
                LEFT JOIN entities source ON source.project_id=edge.project_id AND source.kind=edge.from_kind AND source.entity_id=edge.from_id
                LEFT JOIN entities target ON target.project_id=edge.project_id AND target.kind=edge.to_kind AND target.entity_id=edge.to_id
                WHERE edge.project_id=$project AND edge.lifecycle='VALID'
                  AND ((source.entity_id IS NOT NULL AND source.lifecycle <> 'VALID')
                    OR (target.entity_id IS NOT NULL AND target.lifecycle <> 'VALID'));
                """;
            dependencies.Parameters.AddWithValue("$project", projectId);
            var count = Convert.ToInt32(await dependencies.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0) findings.Add(new HealthFinding("HISTORICAL_DEPENDENCY_IN_CURRENT_GRAPH", "REPAIRABLE", "dependency_edges", count.ToString(System.Globalization.CultureInfo.InvariantCulture), true));
        }
    }

    private async Task CheckDatabaseIntegrityAsync(
        SqliteConnection connection,
        string projectId,
        StorageHealthMode mode,
        List<HealthFinding> findings,
        CancellationToken cancellationToken)
    {
        var identity = DatabaseStorageIdentity();
        if (mode == StorageHealthMode.Fast)
        {
            var receipt = await ReadFastDoctorReceiptAsync(cancellationToken).ConfigureAwait(false);
            if (receipt is not null && receipt.ProjectId == projectId && receipt.SchemaVersion == SqliteSchema.CurrentVersion &&
                receipt.StorageIdentity == identity && receipt.Status == "OK")
                return;
        }

        await using var quick = connection.CreateCommand();
        quick.CommandText = "PRAGMA quick_check;";
        var value = Convert.ToString(await quick.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new HealthFinding("STATE_DB_CORRUPT", "BLOCKED", "state", value ?? "quick_check failed", false));
            return;
        }
    }

    private string DatabaseStorageIdentity()
    {
        static string FileIdentity(string path)
        {
            if (!File.Exists(path)) return "missing";
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        return $"db={FileIdentity(_layout.DatabasePath)}|wal={FileIdentity($"{_layout.DatabasePath}-wal")}";
    }

    private async Task<FastDoctorReceipt?> ReadFastDoctorReceiptAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.FastDoctorReceiptPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_layout.FastDoctorReceiptPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FastDoctorReceipt>(json, StorageJson.Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteFastDoctorReceiptAsync(FastDoctorReceipt receipt, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_layout.CacheRoot);
        var json = JsonSerializer.Serialize(receipt, StorageJson.Options);
        var temporary = $"{_layout.FastDoctorReceiptPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _layout.FastDoctorReceiptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<IReadOnlyList<ArtifactRecord>> ReadDoctorArtifactsAsync(SqliteConnection connection, string projectId, StorageHealthMode mode, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = mode == StorageHealthMode.Deep
            ? """
              SELECT artifact_id,project_id,type,sha256,bytes,relative_ref,task_id,evidence_id,source_fingerprint,retention,created_at
              FROM artifacts WHERE project_id=$project ORDER BY artifact_id LIMIT 100001;
              """
            : """
              WITH candidates AS (
                SELECT DISTINCT artifact.sha256 FROM artifacts artifact
                JOIN artifact_links link ON link.project_id=artifact.project_id AND link.sha256=artifact.sha256
                JOIN entities entity ON entity.project_id=link.project_id AND entity.kind=link.ref_kind AND entity.entity_id=link.ref_id
                WHERE artifact.project_id=$project AND entity.lifecycle='VALID'
                UNION
                SELECT sha256 FROM (
                    SELECT sha256 FROM artifacts WHERE project_id=$project ORDER BY created_at DESC LIMIT 64
                )
              )
              SELECT artifact.artifact_id,artifact.project_id,artifact.type,artifact.sha256,artifact.bytes,artifact.relative_ref,
                     artifact.task_id,artifact.evidence_id,artifact.source_fingerprint,artifact.retention,artifact.created_at
              FROM artifacts artifact JOIN candidates ON candidates.sha256=artifact.sha256
              WHERE artifact.project_id=$project ORDER BY artifact.artifact_id LIMIT 257;
              """;
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<ArtifactRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new ArtifactRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                Enum.Parse<RetentionTier>(reader.GetString(9), true), DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture)));
        }
        var budget = mode == StorageHealthMode.Deep ? 100_000 : 256;
        if (values.Count > budget) throw new InvalidOperationException("Storage Doctor artifact budget is exhausted");
        return values;
    }

    private static async Task<IReadOnlyList<ArchivedEventSegment>> ReadDoctorSegmentsAsync(SqliteConnection connection, string projectId, StorageHealthMode mode, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT segment_id,from_generation,to_generation,first_sequence,last_sequence,event_count,delta_count,relative_ref,sha256,bytes,status,created_at
            FROM event_segments WHERE project_id=$project ORDER BY created_at DESC LIMIT {(mode == StorageHealthMode.Deep ? 100_001 : 16)};
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<ArchivedEventSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new ArchivedEventSegment(reader.GetString(0), projectId, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7), reader.GetString(8), reader.GetInt64(9), reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture)));
        if (mode == StorageHealthMode.Deep && values.Count > 100_000) throw new InvalidOperationException("Deep Doctor archive budget is exhausted");
        return values;
    }

    private void CheckOrphanFiles(string projectId, IReadOnlyList<ArtifactRecord> artifacts, List<HealthFinding> findings)
    {
        var known = artifacts.Select(static value => value.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_layout.ArtifactsRoot)) return;
        var (paths, _) = TraversalGuard.EnumerateFiles(_layout.ArtifactsRoot, new TraversalBudget(MaxDepth: 4, MaxDirectories: 512, MaxEntries: 100_000, MaxFiles: 100_000, MaxObservedBytes: 100L * 1024 * 1024 * 1024, MaxElapsedMilliseconds: 30_000));
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (IsSha256Name(name) && !known.Contains(name)) findings.Add(new HealthFinding("ORPHAN_ARTIFACTS_FOUND", "REPAIRABLE", name, projectId, true));
        }
    }

    private static async Task<(long Bytes, DateTime LastWriteUtc)?> ReadArtifactVerificationAsync(SqliteConnection connection, string projectId, string hash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bytes,last_write_utc FROM artifact_verifications WHERE project_id=$project AND sha256=$hash AND status='VERIFIED';";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), DateTime.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind))
            : null;
    }

    private static async Task RefreshArtifactVerificationAsync(SqliteConnection connection, string projectId, ArtifactRecord artifact, FileInfo info, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifact_verifications(project_id,sha256,bytes,last_write_utc,verified_at,source_generation,status)
            VALUES($project,$hash,$bytes,$write,$verified,0,'VERIFIED')
            ON CONFLICT(project_id,sha256) DO UPDATE SET bytes=excluded.bytes,last_write_utc=excluded.last_write_utc,verified_at=excluded.verified_at,status='VERIFIED';
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", artifact.Sha256);
        command.Parameters.AddWithValue("$bytes", info.Length);
        command.Parameters.AddWithValue("$write", info.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$verified", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<double> FreelistRatioAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var free = connection.CreateCommand();
        free.CommandText = "PRAGMA freelist_count;";
        var freelist = Convert.ToInt64(await free.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        await using var pages = connection.CreateCommand();
        pages.CommandText = "PRAGMA page_count;";
        var pageCount = Convert.ToInt64(await pages.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return pageCount == 0 ? 0 : (double)freelist / pageCount;
    }

    private sealed record FastDoctorReceipt(
        string ProjectId,
        string SchemaVersion,
        string StorageIdentity,
        string Status,
        DateTimeOffset VerifiedAt);
}

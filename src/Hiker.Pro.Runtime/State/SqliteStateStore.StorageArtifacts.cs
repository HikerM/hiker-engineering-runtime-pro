using System.Text.Json;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task LinkArtifactAsync(ArtifactLink link, CancellationToken cancellationToken = default)
    {
        var reference = new StateKey(link.ProjectId, link.RefKind, link.RefId).Normalize();
        if (link.Sha256.Length != 64 || !link.Sha256.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            string.IsNullOrWhiteSpace(link.Purpose) || link.Purpose.Length > 160)
            throw new ArgumentException("Artifact link is invalid", nameof(link));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM artifacts WHERE project_id=$project AND sha256=$hash);",
                reference.ProjectId, link.Sha256, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Artifact link requires registered content");
        if (!await ExistsAsync(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM entities WHERE project_id=$project AND kind=$kind AND entity_id=$entity);",
                reference, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Artifact link requires an existing state fact");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO artifact_links(project_id,sha256,ref_kind,ref_id,purpose,created_at)
            VALUES($project,$hash,$kind,$id,$purpose,$created)
            ON CONFLICT(project_id,sha256,ref_kind,ref_id,purpose) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$project", reference.ProjectId);
        command.Parameters.AddWithValue("$hash", link.Sha256);
        command.Parameters.AddWithValue("$kind", reference.Kind);
        command.Parameters.AddWithValue("$id", reference.EntityId);
        command.Parameters.AddWithValue("$purpose", link.Purpose.Trim());
        command.Parameters.AddWithValue("$created", link.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactReachabilityRecord>> AnalyzeArtifactReachabilityAsync(
        string projectId,
        DateTimeOffset retentionCutoff,
        int limit = 100_000,
        CancellationToken cancellationToken = default)
    {
        var project = new StateKey(projectId, EntityKinds.Project, "storage").Normalize().ProjectId;
        if (limit is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = await ReadArtifactsAsync(connection, project, limit + 1, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count > limit) throw new InvalidOperationException("Artifact reachability scan budget is exhausted");
        var links = await ReadArtifactLinksAsync(connection, project, cancellationToken).ConfigureAwait(false);
        var state = await ReadArtifactLinkStateAsync(connection, project, cancellationToken).ConfigureAwait(false);
        var results = new List<ArtifactReachabilityRecord>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            var artifactLinks = links.TryGetValue(artifact.Sha256, out var values) ? values : Array.Empty<ArtifactLink>();
            var linkedStates = artifactLinks.Select(link => state.GetValueOrDefault($"{link.RefKind}|{link.RefId}"))
                .Where(static value => value is not null).Select(static value => value!.Value).ToArray();
            var classification = ClassifyArtifact(artifact, linkedStates, retentionCutoff);
            results.Add(new ArtifactReachabilityRecord(artifact, classification.Reachability, artifactLinks, classification.Reason));
        }
        return ContractCollections.Freeze(results);
    }

    public async Task<ArtifactGcResult> CollectUnreachableArtifactsAsync(
        string operationId,
        string projectId,
        DateTimeOffset retentionCutoff,
        CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(operationId, projectId);
        var fingerprint = RequestHash($"artifact-gc|{projectId}|{retentionCutoff.ToUniversalTime():O}");
        var prior = await ReadStorageMaintenanceAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "ARTIFACT_GC", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
                return JsonSerializer.Deserialize<ArtifactGcResult>(prior.Value.ResultJson, StorageJson.Options)! with { Idempotent = true };
        }

        ArtifactGcPlan plan;
        if (prior is null)
        {
            var candidates = (await AnalyzeArtifactReachabilityAsync(projectId, retentionCutoff, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(static value => value.Reachability == StorageReachability.Unreachable)
                .Select(static value => new ArtifactGcCandidate(value.Artifact.Sha256, value.Artifact.RelativeRef, value.Artifact.Bytes)).ToArray();
            plan = new ArtifactGcPlan(candidates);
            await BeginStorageMaintenanceAsync(operationId, projectId, "ARTIFACT_GC", fingerprint, "MARKED_FOR_SWEEP",
                JsonSerializer.Serialize(plan, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            plan = prior.Value.ResultJson is null
                ? throw new InvalidDataException("Artifact GC receipt has no recovery plan")
                : JsonSerializer.Deserialize<ArtifactGcPlan>(prior.Value.ResultJson, StorageJson.Options)
                  ?? throw new InvalidDataException("Artifact GC recovery plan is invalid");
        }

        var deletedMetadata = 0;
        var eligibleHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            foreach (var candidate in plan.Candidates)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM artifacts
                    WHERE project_id=$project AND sha256=$hash
                      AND retention <> 'ARCHIVED'
                      AND NOT EXISTS(
                          SELECT 1 FROM artifact_links link
                          JOIN entities entity ON entity.project_id=link.project_id AND entity.kind=link.ref_kind AND entity.entity_id=link.ref_id
                          WHERE link.project_id=$project AND link.sha256=$hash AND entity.lifecycle='VALID');
                    """;
                delete.Parameters.AddWithValue("$project", projectId);
                delete.Parameters.AddWithValue("$hash", candidate.Sha256);
                var deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                deletedMetadata += deleted;
                if (deleted == 0)
                {
                    if (prior?.Status == "SOURCE_ELIGIBLE_FOR_DELETE" &&
                        !await ArtifactMetadataExistsAsync(connection, transaction, projectId, candidate.Sha256, cancellationToken).ConfigureAwait(false))
                        eligibleHashes.Add(candidate.Sha256);
                    continue;
                }
                eligibleHashes.Add(candidate.Sha256);
                await DeleteArtifactLinksAsync(connection, transaction, projectId, candidate.Sha256, cancellationToken).ConfigureAwait(false);
                await DeleteVerificationAsync(connection, transaction, projectId, candidate.Sha256, cancellationToken).ConfigureAwait(false);
            }
            await UpdateStorageMaintenanceAsync(connection, transaction, operationId, "SOURCE_ELIGIBLE_FOR_DELETE", null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var deletedFiles = 0;
        long reclaimed = 0;
        foreach (var candidate in plan.Candidates)
        {
            if (!eligibleHashes.Contains(candidate.Sha256)) continue;
            var path = ResolveArtifactPath(candidate.RelativeRef);
            if (!File.Exists(path)) continue;
            reclaimed += new FileInfo(path).Length;
            File.Delete(path);
            deletedFiles++;
        }
        var generation = (await GetMetricsAsync(cancellationToken).ConfigureAwait(false)).Generation;
        var result = new ArtifactGcResult("COMPLETE", plan.Candidates.Count, deletedMetadata, deletedFiles, reclaimed, generation, false);
        await CompleteStorageMaintenanceAsync(operationId, JsonSerializer.Serialize(result, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task UpsertArtifactVerificationAsync(
        SqliteConnection connection,
        ArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        var path = ResolveArtifactPath(artifact.RelativeRef);
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        var generation = long.Parse(await MetadataAsync(connection, "global_generation", cancellationToken).ConfigureAwait(false) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifact_verifications(project_id,sha256,bytes,last_write_utc,verified_at,source_generation,status)
            VALUES($project,$hash,$bytes,$write,$verified,$generation,'VERIFIED')
            ON CONFLICT(project_id,sha256) DO UPDATE SET bytes=excluded.bytes,last_write_utc=excluded.last_write_utc,
                verified_at=excluded.verified_at,source_generation=excluded.source_generation,status='VERIFIED';
            """;
        command.Parameters.AddWithValue("$project", artifact.ProjectId);
        command.Parameters.AddWithValue("$hash", artifact.Sha256);
        command.Parameters.AddWithValue("$bytes", info.Length);
        command.Parameters.AddWithValue("$write", info.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$verified", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$generation", generation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertArtifactLinkIfPresentAsync(
        SqliteConnection connection,
        ArtifactRecord artifact,
        string? kind,
        string? id,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (id is null || kind is null) return;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifact_links(project_id,sha256,ref_kind,ref_id,purpose,created_at)
            VALUES($project,$hash,$kind,$id,$purpose,$created)
            ON CONFLICT(project_id,sha256,ref_kind,ref_id,purpose) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$project", artifact.ProjectId);
        command.Parameters.AddWithValue("$hash", artifact.Sha256);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$purpose", purpose);
        command.Parameters.AddWithValue("$created", artifact.CreatedAt.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ResolveArtifactPath(string relativeRef)
    {
        var path = _layout.ResolveAiRelativePath(relativeRef);
        var root = Path.GetFullPath(_layout.ArtifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact reference escapes content-addressed storage");
        return path;
    }

    private static (StorageReachability Reachability, string Reason) ClassifyArtifact(
        ArtifactRecord artifact,
        IReadOnlyList<(StateLifecycle Lifecycle, RetentionTier Retention, string Kind)> states,
        DateTimeOffset cutoff)
    {
        if (states.Any(static value => value.Lifecycle == StateLifecycle.Valid && value.Kind == EntityKinds.Task)) return (StorageReachability.Live, "ACTIVE_TASK_ROOT");
        if (states.Any(static value => value.Kind is EntityKinds.Release or EntityKinds.Evidence or EntityKinds.Checkpoint)) return (StorageReachability.Retained, "AUDIT_OR_RECOVERY_ROOT");
        if (states.Any(static value => value.Lifecycle == StateLifecycle.Valid)) return (StorageReachability.Live, "CURRENT_STATE_ROOT");
        if (artifact.Retention == RetentionTier.Archived) return (StorageReachability.Retained, "EXPLICIT_ARCHIVE_RETENTION");
        if (states.Count > 0 && artifact.CreatedAt >= cutoff) return (StorageReachability.Cold, "HISTORICAL_WITHIN_RETENTION");
        if (states.Count > 0) return (StorageReachability.Archivable, "HISTORICAL_REFERENCE");
        if (artifact.CreatedAt >= cutoff) return (StorageReachability.Cold, "UNLINKED_WITHIN_RETENTION");
        return (StorageReachability.Unreachable, "NO_CURRENT_AUDIT_OR_RECOVERY_ROOT");
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        StateKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        SqliteMapping.AddStateKey(command, key);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string projectId,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed record ArtifactGcPlan(IReadOnlyList<ArtifactGcCandidate> Candidates);
    private sealed record ArtifactGcCandidate(string Sha256, string RelativeRef, long Bytes);
}

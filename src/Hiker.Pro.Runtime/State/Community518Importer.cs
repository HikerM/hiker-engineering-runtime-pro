using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.State;

public sealed class Community518Importer(ProjectLayout layout, StateCoordinator state)
{
    public async Task<CommunityImportReport> ImportAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var normalizedProject = new StateKey(projectId, EntityKinds.Project, "project").Normalize().ProjectId;
        var markerKey = new StateKey(normalizedProject, EntityKinds.Migration, "community-5.18");
        var marker = await state.ReadAsync(markerKey, cancellationToken).ConfigureAwait(false);
        if (marker is not null)
        {
            using var markerDocument = JsonDocument.Parse(marker.PayloadJson);
            return new CommunityImportReport(
                "ALREADY_IMPORTED",
                normalizedProject,
                markerDocument.RootElement.GetProperty("discovered").GetInt32(),
                0,
                markerDocument.RootElement.GetProperty("imported").GetInt32(),
                marker.SourceFingerprint,
                Array.Empty<string>());
        }

        var sources = Community518SourceCatalog.Discover(layout);
        var rejected = new List<string>();
        var imported = 0;
        var replayed = 0;
        using var aggregateHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(layout.AiRoot, source.Path).Replace('\\', '/');
            string payload;
            try
            {
                payload = await File.ReadAllTextAsync(source.Path, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 128 });
                payload = JsonSerializer.Serialize(document.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                rejected.Add($"{relative}:{exception.GetType().Name}");
                continue;
            }

            var sourceHash = Sha256(payload);
            aggregateHash.AppendData(Encoding.UTF8.GetBytes($"{relative}\0{sourceHash}\n"));
            var entityId = EntityId(relative, payload);
            var key = new StateKey(normalizedProject, source.Kind, entityId);
            var request = new TransactionRequest(
                $"community-5.18-{Sha256(relative)[..24]}",
                new StateMutation(
                    key,
                    0,
                    GoalRevision(payload),
                    LegacyLifecycle(source.Kind),
                    "COMMUNITY_5_18_IMPORT",
                    "2.0.0",
                    sourceHash,
                    Sha256(relative),
                    payload,
                    source.Kind is EntityKinds.Checkpoint or EntityKinds.Evidence ? RetentionTier.Warm : RetentionTier.Hot),
                "COMMUNITY_STATE_IMPORTED",
                "COMMUNITY_5_18_IMPORT",
                JsonSerializer.Serialize(new { source_ref = $".ai/{relative}" }));
            var snapshot = await state.ReadForReasoningAsync(key, cancellationToken).ConfigureAwait(false);
            var result = await state.CommitAsync(
                snapshot,
                request.OperationId,
                request.Mutation,
                request.EventType,
                request.SummaryCode,
                request.EventPayloadJson,
                request.DependencyChanges,
                cancellationToken).ConfigureAwait(false);
            if (!result.Committed)
            {
                rejected.Add($"{relative}:{result.Status}");
            }
            else if (result.Status == "IDEMPOTENT_REPLAY")
            {
                replayed++;
            }
            else
            {
                imported++;
            }
        }

        var aggregate = Convert.ToHexString(aggregateHash.GetHashAndReset()).ToLowerInvariant();
        if (rejected.Count > 0)
        {
            return new CommunityImportReport("REQUIRES_REVIEW", normalizedProject, sources.Count, imported, replayed, aggregate, ContractCollections.Freeze(rejected));
        }

        var markerPayload = JsonSerializer.Serialize(new
        {
            source_version = "5.18",
            discovered = sources.Count,
            imported = imported + replayed,
            source_fingerprint = aggregate,
            completed_at = DateTimeOffset.UtcNow,
            authority = "SQLITE_STATE_PLANE",
            legacy_files = "READ_ONLY_SOURCE"
        });
        var markerSnapshot = await state.ReadForReasoningAsync(markerKey, cancellationToken).ConfigureAwait(false);
        var markerResult = await state.CommitAsync(
            markerSnapshot,
            $"community-5.18-complete-{aggregate[..24]}",
            new StateMutation(markerKey, 0, 0, StateLifecycle.Valid, "STATE_COORDINATOR", SqliteSchema.CurrentVersion, aggregate, aggregate, markerPayload),
            "COMMUNITY_IMPORT_COMPLETED",
            "COMMUNITY_5_18_IMPORT_COMPLETE",
            "{}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!markerResult.Committed)
        {
            return new CommunityImportReport("REQUIRES_REVIEW", normalizedProject, sources.Count, imported, replayed, aggregate, new[] { $"marker:{markerResult.Status}" });
        }

        return new CommunityImportReport("IMPORTED", normalizedProject, sources.Count, imported, replayed, aggregate, Array.Empty<string>());
    }

    private static string EntityId(string relative, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        foreach (var property in new[] { "task_id", "checkpoint_id", "goal_id", "project_id", "decision_id", "evidence_id" })
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }
        }

        var candidate = relative.Replace('/', ':');
        return candidate.Length <= 160 ? candidate : $"legacy-{Sha256(relative)[..32]}";
    }

    private static long GoalRevision(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "goal_revision", "revision" })
            {
                if (document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt64(out var revision))
                {
                    return Math.Max(0, revision);
                }
            }
        }

        return 0;
    }

    private static StateLifecycle LegacyLifecycle(string kind) =>
        kind is EntityKinds.Ownership or EntityKinds.Worktree
            ? StateLifecycle.Incompatible
            : StateLifecycle.Valid;

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

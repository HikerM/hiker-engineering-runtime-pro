using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.State;

public sealed class Community518Importer(ProjectLayout layout, IStateStore store)
{
    private const int MaxFiles = 100_000;
    private static readonly IReadOnlyDictionary<string, string> ExactFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["schema.json"] = EntityKinds.Project,
        ["context/project.json"] = EntityKinds.Project,
        ["context/tech-stack.json"] = EntityKinds.RuntimeProfile,
        ["context/architecture.json"] = EntityKinds.Decision,
        ["context/standards.json"] = EntityKinds.Decision,
        ["runtime/task.json"] = EntityKinds.Task,
        ["runtime/control.json"] = EntityKinds.RuntimeProfile,
        ["runtime/checkpoint-ledger.json"] = EntityKinds.Checkpoint,
        ["governance/project-state.json"] = EntityKinds.Project,
        ["governance/context-retention.json"] = EntityKinds.RuntimeProfile,
        ["governance/task-index.json"] = EntityKinds.Task,
        ["governance/locked-decisions.json"] = EntityKinds.Decision,
        ["governance/ownership.json"] = EntityKinds.Ownership,
        ["governance/goal-contract.json"] = EntityKinds.Goal,
        ["workspace/task-map.json"] = EntityKinds.Worktree,
        ["quality/policy.json"] = EntityKinds.Evidence,
        ["quality/evidence/index.json"] = EntityKinds.Evidence
    };

    public async Task<CommunityImportReport> ImportAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var normalizedProject = new StateKey(projectId, EntityKinds.Project, "project").Normalize().ProjectId;
        var markerKey = new StateKey(normalizedProject, EntityKinds.Migration, "community-5.18");
        var marker = await store.ReadAsync(markerKey, cancellationToken).ConfigureAwait(false);
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

        var sources = DiscoverSources();
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
                    StateLifecycle.Valid,
                    "COMMUNITY_5_18_IMPORT",
                    "2.0.0",
                    sourceHash,
                    Sha256(relative),
                    payload,
                    source.Kind is EntityKinds.Checkpoint or EntityKinds.Evidence ? RetentionTier.Warm : RetentionTier.Hot),
                "COMMUNITY_STATE_IMPORTED",
                "COMMUNITY_5_18_IMPORT",
                JsonSerializer.Serialize(new { source_ref = $".ai/{relative}" }));
            var result = await store.CompareAndSwapAsync(request, cancellationToken).ConfigureAwait(false);
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
        var markerResult = await store.CompareAndSwapAsync(new TransactionRequest(
            $"community-5.18-complete-{aggregate[..24]}",
            new StateMutation(markerKey, 0, 0, StateLifecycle.Valid, "STATE_COORDINATOR", SqliteSchema.CurrentVersion, aggregate, aggregate, markerPayload),
            "COMMUNITY_IMPORT_COMPLETED",
            "COMMUNITY_5_18_IMPORT_COMPLETE",
            "{}"), cancellationToken).ConfigureAwait(false);
        if (!markerResult.Committed)
        {
            return new CommunityImportReport("REQUIRES_REVIEW", normalizedProject, sources.Count, imported, replayed, aggregate, new[] { $"marker:{markerResult.Status}" });
        }

        return new CommunityImportReport("IMPORTED", normalizedProject, sources.Count, imported, replayed, aggregate, Array.Empty<string>());
    }

    private IReadOnlyList<(string Path, string Kind)> DiscoverSources()
    {
        var values = new List<(string Path, string Kind)>();
        foreach (var item in ExactFiles)
        {
            var path = layout.ResolveAiRelativePath(item.Key);
            if (File.Exists(path))
            {
                values.Add((path, item.Value));
            }
        }

        AddPattern(values, "tasks", "*.json", EntityKinds.Task);
        AddPattern(values, Path.Combine("runtime", "checkpoints"), "*.json", EntityKinds.Checkpoint);
        if (values.Count > MaxFiles)
        {
            throw new InvalidOperationException($"Community import exceeds the {MaxFiles} file safety budget");
        }

        return values.OrderBy(static value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void AddPattern(List<(string Path, string Kind)> values, string relativeDirectory, string pattern, string kind)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        values.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(path => (path, kind)));
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

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

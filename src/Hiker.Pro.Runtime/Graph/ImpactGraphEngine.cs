using Hiker.Pro.State;

namespace Hiker.Pro.Graph;

public enum ImpactDisposition
{
    Affected,
    Unaffected,
    Superseded,
    RequiresReview
}

public sealed record ImpactSeed(StateKey Key, string ChangeType, string SourceFingerprint);

public sealed record ImpactedNode(
    StateKey Key,
    int Distance,
    string ViaRelation,
    ImpactDisposition Disposition,
    string ReasonCode);

public sealed record ImpactAnalysisRequest(
    string ProjectId,
    IReadOnlyList<ImpactSeed> Seeds,
    int MaxNodes = 4096,
    int MaxDepth = 16);

public sealed record ImpactAnalysisResult(
    IReadOnlyList<ImpactedNode> Nodes,
    IReadOnlyList<StateKey> EvidenceToInvalidate,
    int DependencyQueries,
    bool Truncated,
    string Status);

public sealed class ImpactGraphEngine(IStateStore stateStore)
{
    private static readonly HashSet<string> StrongRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEPENDS_ON", "CONSUMES", "IMPLEMENTS", "CALLS", "READS", "WRITES", "PROVEN_BY", "VALIDATES", "OWNS_CONTRACT"
    };

    private static readonly HashSet<string> ReviewRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "RELATED_TO", "DOCUMENTS", "OBSERVES"
    };

    public async Task<ImpactAnalysisResult> AnalyzeAsync(ImpactAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var queue = new Queue<(StateKey Key, int Distance, string Via)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ImpactedNode>();
        foreach (var seed in request.Seeds)
        {
            var key = seed.Key.Normalize();
            queue.Enqueue((key, 0, "SEED"));
        }

        var queries = 0;
        var truncated = false;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            var identity = $"{current.Key.Kind}|{current.Key.EntityId}";
            if (!visited.Add(identity))
            {
                continue;
            }

            if (results.Count >= request.MaxNodes)
            {
                truncated = true;
                break;
            }

            var disposition = current.Distance == 0 || StrongRelations.Contains(current.Via)
                ? ImpactDisposition.Affected
                : ReviewRelations.Contains(current.Via)
                    ? ImpactDisposition.RequiresReview
                    : ImpactDisposition.RequiresReview;
            results.Add(new ImpactedNode(
                current.Key,
                current.Distance,
                current.Via,
                disposition,
                current.Distance == 0 ? "DECLARED_CHANGE" : disposition == ImpactDisposition.Affected ? "TRANSITIVE_DEPENDENCY" : "AMBIGUOUS_RELATION"));
            if (current.Distance >= request.MaxDepth)
            {
                truncated = truncated || queue.Count > 0;
                continue;
            }

            var incoming = await stateStore.QueryDependenciesAsync(
                request.ProjectId,
                current.Key.Kind,
                current.Key.EntityId,
                outgoing: false,
                cancellationToken).ConfigureAwait(false);
            queries++;
            foreach (var edge in incoming)
            {
                queue.Enqueue((new StateKey(request.ProjectId, edge.FromKind, edge.FromId), current.Distance + 1, edge.Relation));
            }
        }

        var evidence = results
            .Where(static item => item.Key.Kind == EntityKinds.Evidence && item.Disposition == ImpactDisposition.Affected)
            .Select(static item => item.Key)
            .ToArray();
        return new ImpactAnalysisResult(
            ContractCollections.Freeze(results),
            ContractCollections.Freeze(evidence),
            queries,
            truncated,
            truncated ? "REQUIRES_REVIEW" : "ANALYZED");
    }

    public async Task<InvalidationResult> InvalidateAffectedEvidenceAsync(
        string operationId,
        ImpactAnalysisRequest request,
        long goalRevision,
        CancellationToken cancellationToken = default)
    {
        var analysis = await AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        if (analysis.Truncated)
        {
            throw new InvalidOperationException("Truncated impact analysis cannot invalidate evidence automatically");
        }

        if (analysis.EvidenceToInvalidate.Count == 0)
        {
            throw new InvalidOperationException("Impact analysis found no affected evidence");
        }

        return await stateStore.InvalidateAsync(new InvalidationRequest(
            operationId,
            request.ProjectId,
            analysis.EvidenceToInvalidate,
            goalRevision,
            "DEPENDENCY_CHANGED"), cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(ImpactAnalysisRequest request)
    {
        if (request.Seeds.Count is < 1 or > 128 || request.MaxNodes is < 1 or > 100_000 || request.MaxDepth is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Impact graph budgets are invalid");
        }

        foreach (var seed in request.Seeds)
        {
            var key = seed.Key.Normalize();
            if (!string.Equals(key.ProjectId, request.ProjectId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Impact seed belongs to another project", nameof(request));
            }
        }
    }
}

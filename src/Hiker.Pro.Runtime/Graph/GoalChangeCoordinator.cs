using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

namespace Hiker.Pro.Graph;

public enum GoalChangeKind
{
    Add,
    Remove,
    Modify,
    Architecture,
    StackMigration,
    Undo
}

public sealed record TaskImpactClassification(
    string TaskId,
    ImpactDisposition Disposition,
    string ReasonCode,
    IReadOnlyList<string> EvidenceIds);

public sealed record GoalChangeRequest(
    string OperationId,
    string ProjectId,
    string GoalId,
    long FromRevision,
    long ToRevision,
    GoalChangeKind ChangeKind,
    string ChangeCode,
    string SourceFingerprint,
    IReadOnlyList<TaskImpactClassification> Classifications);

public sealed record GoalChangeResult(
    string Status,
    long Generation,
    long GoalRevision,
    int Affected,
    int Unaffected,
    int Superseded,
    int RequiresReview,
    int EvidenceInvalidated,
    bool IdempotentReplay);

public sealed class GoalChangeCoordinator(StateCoordinator state)
{
    private const string Authority = "GOAL_CHANGE_COORDINATOR";
    private const string SchemaVersion = "3.0.0";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public async Task<GoalChangeResult> ApplyAsync(
        GoalChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidate(request);
        var requestFingerprint = RequestFingerprint(normalized);
        var prior = await state.ReadOperationReceiptAsync(normalized.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            return Replay(prior, requestFingerprint);
        }

        var goalKey = new StateKey(normalized.ProjectId, EntityKinds.Goal, normalized.GoalId);
        var currentGoal = await state.ReadAsync(goalKey, cancellationToken).ConfigureAwait(false);
        ValidateCurrentRevision(normalized, currentGoal);

        var mutations = new List<StateMutation>();
        var edges = new List<DependencyEdge>();
        var invalidatedEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var classification in normalized.Classifications)
        {
            var taskKey = new StateKey(normalized.ProjectId, EntityKinds.Task, classification.TaskId);
            var task = await state.ReadAsync(taskKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Classified task does not exist: {classification.TaskId}");
            var evidence = await ValidateEvidenceAsync(normalized, classification, cancellationToken).ConfigureAwait(false);
            if (classification.Disposition != ImpactDisposition.RequiresReview)
            {
                mutations.Add(Preserve(
                    task,
                    normalized.ToRevision,
                    classification.Disposition == ImpactDisposition.Affected ? StateLifecycle.Stale :
                    classification.Disposition == ImpactDisposition.Superseded ? StateLifecycle.Superseded : task.Lifecycle,
                    normalized.SourceFingerprint));
            }

            foreach (var snapshot in evidence)
            {
                if (invalidatedEvidence.Add(snapshot.Key.EntityId))
                {
                    mutations.Add(Preserve(snapshot, normalized.ToRevision, StateLifecycle.Stale, normalized.SourceFingerprint));
                }
            }

            var bindingKey = new StateKey(
                normalized.ProjectId,
                EntityKinds.TaskDependency,
                $"{normalized.GoalId}:{classification.TaskId}");
            var binding = await state.ReadAsync(bindingKey, cancellationToken).ConfigureAwait(false);
            var boundRevision = classification.Disposition == ImpactDisposition.RequiresReview
                ? normalized.FromRevision
                : normalized.ToRevision;
            mutations.Add(NewMutation(
                bindingKey,
                binding,
                normalized.ToRevision,
                StateLifecycle.Valid,
                normalized.SourceFingerprint,
                new GoalTaskBinding(
                    normalized.GoalId,
                    classification.TaskId,
                    normalized.FromRevision,
                    normalized.ToRevision,
                    boundRevision,
                    classification.Disposition,
                    classification.ReasonCode,
                    normalized.SourceFingerprint)));
            edges.Add(new DependencyEdge(
                normalized.ProjectId,
                EntityKinds.TaskDependency,
                bindingKey.EntityId,
                EntityKinds.GoalRevision,
                RevisionId(normalized.GoalId, normalized.ToRevision),
                "CLASSIFIES",
                normalized.SourceFingerprint));
        }

        var now = DateTimeOffset.UtcNow;
        mutations.Add(NewMutation(
            goalKey,
            currentGoal,
            normalized.ToRevision,
            StateLifecycle.Valid,
            normalized.SourceFingerprint,
            new CurrentGoal(
                normalized.GoalId,
                normalized.ToRevision,
                normalized.ChangeKind,
                normalized.ChangeCode,
                normalized.SourceFingerprint,
                now)));
        var revisionKey = new StateKey(
            normalized.ProjectId,
            EntityKinds.GoalRevision,
            RevisionId(normalized.GoalId, normalized.ToRevision));
        var revision = await state.ReadAsync(revisionKey, cancellationToken).ConfigureAwait(false);
        if (revision is not null)
        {
            throw new InvalidOperationException("Goal revision already exists without a matching operation receipt");
        }
        mutations.Add(NewMutation(
            revisionKey,
            null,
            normalized.ToRevision,
            StateLifecycle.Valid,
            normalized.SourceFingerprint,
            new GoalRevisionFact(
                normalized.GoalId,
                normalized.FromRevision,
                normalized.ToRevision,
                normalized.ChangeKind,
                normalized.ChangeCode,
                normalized.SourceFingerprint,
                now),
            RetentionTier.Warm));

        var counts = Counts(normalized.Classifications);
        var result = new GoalChangeReceipt(
            requestFingerprint,
            normalized.ToRevision,
            counts.Affected,
            counts.Unaffected,
            counts.Superseded,
            counts.RequiresReview,
            invalidatedEvidence.Count);
        var transaction = await state.CommitBatchAsync(new BatchTransactionRequest(
            normalized.OperationId,
            mutations,
            "GOAL_CHANGED",
            "GOAL_REVISION_ATOMICALLY_APPLIED",
            Json(new
            {
                change_kind = normalized.ChangeKind,
                revision = normalized.ToRevision,
                counts.Affected,
                counts.Unaffected,
                counts.Superseded,
                counts.RequiresReview,
                evidence_invalidated = invalidatedEvidence.Count
            }),
            edges,
            Json(result)), cancellationToken).ConfigureAwait(false);
        if (!transaction.Committed)
        {
            return new GoalChangeResult(
                transaction.Status,
                transaction.CurrentGeneration,
                normalized.FromRevision,
                0,
                0,
                0,
                0,
                0,
                false);
        }

        return ToResult("COMMITTED", transaction.CurrentGeneration, result, false);
    }

    private async Task<IReadOnlyList<StateSnapshot>> ValidateEvidenceAsync(
        GoalChangeRequest request,
        TaskImpactClassification classification,
        CancellationToken cancellationToken)
    {
        if (classification.EvidenceIds.Count == 0)
        {
            return Array.Empty<StateSnapshot>();
        }

        if (classification.Disposition != ImpactDisposition.Affected)
        {
            throw new InvalidOperationException("Only AFFECTED tasks may invalidate evidence");
        }

        var dependencies = await state.QueryDependenciesAsync(
            request.ProjectId,
            EntityKinds.Task,
            classification.TaskId,
            outgoing: true,
            cancellationToken).ConfigureAwait(false);
        var provenEvidence = dependencies
            .Where(static edge => edge.ToKind == EntityKinds.Evidence && edge.Relation.Equals("PROVEN_BY", StringComparison.OrdinalIgnoreCase))
            .Select(static edge => edge.ToId)
            .ToHashSet(StringComparer.Ordinal);
        var snapshots = new List<StateSnapshot>();
        foreach (var evidenceId in classification.EvidenceIds)
        {
            if (!provenEvidence.Contains(evidenceId))
            {
                throw new InvalidOperationException($"Evidence is not bound to classified task: {classification.TaskId}/{evidenceId}");
            }

            var snapshot = await state.ReadAsync(
                new StateKey(request.ProjectId, EntityKinds.Evidence, evidenceId),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Classified evidence does not exist: {evidenceId}");
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    private static GoalChangeRequest NormalizeAndValidate(GoalChangeRequest request)
    {
        var operation = Token(request.OperationId, nameof(request.OperationId));
        var project = Token(request.ProjectId, nameof(request.ProjectId));
        var goal = Token(request.GoalId, nameof(request.GoalId));
        var changeCode = Token(request.ChangeCode, nameof(request.ChangeCode));
        var source = Token(request.SourceFingerprint, nameof(request.SourceFingerprint));
        if (request.FromRevision < 0 || request.ToRevision != request.FromRevision + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Goal revisions must advance exactly once");
        }
        if (request.Classifications.Count is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A goal change must classify 1-512 tasks");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var classifications = request.Classifications.Select(item =>
        {
            var task = Token(item.TaskId, nameof(item.TaskId));
            if (!seen.Add(task))
            {
                throw new ArgumentException($"Task is classified more than once: {task}", nameof(request));
            }
            var reason = Token(item.ReasonCode, nameof(item.ReasonCode));
            var evidence = item.EvidenceIds.Select(value => Token(value, nameof(item.EvidenceIds)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            return new TaskImpactClassification(task, item.Disposition, reason, evidence);
        }).OrderBy(static item => item.TaskId, StringComparer.Ordinal).ToArray();
        return request with
        {
            OperationId = operation,
            ProjectId = project,
            GoalId = goal,
            ChangeCode = changeCode,
            SourceFingerprint = source,
            Classifications = classifications
        };
    }

    private static void ValidateCurrentRevision(GoalChangeRequest request, StateSnapshot? currentGoal)
    {
        if (request.FromRevision == 0 && request.ChangeKind == GoalChangeKind.Add)
        {
            if (currentGoal is not null)
            {
                throw new InvalidOperationException("ADD cannot replace an existing goal");
            }
            return;
        }
        if (currentGoal is null || currentGoal.GoalRevision != request.FromRevision)
        {
            throw new InvalidOperationException("Goal revision no longer matches the classified change");
        }
    }

    private static GoalChangeResult Replay(OperationReceipt receipt, string requestFingerprint)
    {
        if (receipt.Status != "COMPLETE" || receipt.OperationType != "GOAL_CHANGED" || receipt.ResultJson is null)
        {
            throw new InvalidOperationException("Operation ID is bound to an incomplete or incompatible operation");
        }
        var result = JsonSerializer.Deserialize<GoalChangeReceipt>(receipt.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Goal change receipt is invalid");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(result.RequestFingerprint),
                Encoding.UTF8.GetBytes(requestFingerprint)))
        {
            throw new InvalidOperationException("Operation ID is already bound to a different goal change");
        }
        return ToResult("IDEMPOTENT_REPLAY", receipt.Generation ?? 0, result, true);
    }

    private static GoalChangeResult ToResult(string status, long generation, GoalChangeReceipt receipt, bool replay) => new(
        status,
        generation,
        receipt.GoalRevision,
        receipt.Affected,
        receipt.Unaffected,
        receipt.Superseded,
        receipt.RequiresReview,
        receipt.EvidenceInvalidated,
        replay);

    private static StateMutation Preserve(
        StateSnapshot snapshot,
        long goalRevision,
        StateLifecycle lifecycle,
        string sourceFingerprint) => new(
        snapshot.Key,
        snapshot.Generation,
        goalRevision,
        lifecycle,
        Authority,
        snapshot.SchemaVersion,
        sourceFingerprint,
        snapshot.DependencyFingerprint,
        snapshot.PayloadJson,
        snapshot.Retention);

    private static StateMutation NewMutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        StateLifecycle lifecycle,
        string sourceFingerprint,
        object payload,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        lifecycle,
        Authority,
        SchemaVersion,
        sourceFingerprint,
        sourceFingerprint,
        Json(payload),
        retention);

    private static string RequestFingerprint(GoalChangeRequest request) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Json(new
        {
            request.ProjectId,
            request.GoalId,
            request.FromRevision,
            request.ToRevision,
            request.ChangeKind,
            request.ChangeCode,
            request.SourceFingerprint,
            request.Classifications
        })))).ToLowerInvariant();

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Token(string value, string name)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 160)
        {
            throw new ArgumentException($"{name} must contain 1-160 characters", name);
        }
        return normalized;
    }

    private static string RevisionId(string goalId, long revision) => $"{goalId}:r{revision:D8}";

    private static GoalChangeCounts Counts(IReadOnlyList<TaskImpactClassification> classifications) => new(
        classifications.Count(static item => item.Disposition == ImpactDisposition.Affected),
        classifications.Count(static item => item.Disposition == ImpactDisposition.Unaffected),
        classifications.Count(static item => item.Disposition == ImpactDisposition.Superseded),
        classifications.Count(static item => item.Disposition == ImpactDisposition.RequiresReview));

    private sealed record GoalChangeCounts(int Affected, int Unaffected, int Superseded, int RequiresReview);
    private sealed record GoalChangeReceipt(
        string RequestFingerprint,
        long GoalRevision,
        int Affected,
        int Unaffected,
        int Superseded,
        int RequiresReview,
        int EvidenceInvalidated);
    private sealed record CurrentGoal(
        string GoalId,
        long Revision,
        GoalChangeKind LastChangeKind,
        string ChangeCode,
        string SourceFingerprint,
        DateTimeOffset UpdatedAt);
    private sealed record GoalRevisionFact(
        string GoalId,
        long FromRevision,
        long ToRevision,
        GoalChangeKind ChangeKind,
        string ChangeCode,
        string SourceFingerprint,
        DateTimeOffset CreatedAt);
    private sealed record GoalTaskBinding(
        string GoalId,
        string TaskId,
        long FromRevision,
        long ProposedRevision,
        long BoundRevision,
        ImpactDisposition Disposition,
        string ReasonCode,
        string SourceFingerprint);
}

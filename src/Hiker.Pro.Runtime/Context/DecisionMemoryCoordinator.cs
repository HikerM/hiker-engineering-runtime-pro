using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public enum DecisionStatus
{
    Proposed,
    ApprovedCurrent,
    Rejected,
    ValidButUserRejected,
    Superseded
}

public sealed record EngineeringDecision(
    string DecisionId,
    DecisionStatus Status,
    string ReasonCode,
    string SourceRef,
    IReadOnlyList<string> ScopeRefs,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> RejectedAlternativeRefs,
    string? SupersededBy,
    DateTimeOffset RecordedAt);

public sealed record DecisionRecordRequest(
    string OperationId,
    string ProjectId,
    long GoalRevision,
    string SourceFingerprint,
    EngineeringDecision Decision,
    IReadOnlyList<string> RelatedTaskIds);

public sealed record DecisionCommitResult(string Status, long Generation, string DecisionId, bool IdempotentReplay);

public sealed class DecisionMemoryCoordinator(StateCoordinator state, TimeProvider? timeProvider = null)
{
    private const string Authority = "DECISION_MEMORY_COORDINATOR";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<DecisionCommitResult> RecordAsync(
        DecisionRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var requestFingerprint = Fingerprint(request, null);
        var replay = await ReplayAsync(request.OperationId, request.Decision.DecisionId, requestFingerprint, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var key = new StateKey(request.ProjectId, EntityKinds.Decision, request.Decision.DecisionId);
        if (await state.ReadAsync(key, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("Decision already exists without a matching operation receipt");
        }

        var edges = await ValidateAndBuildEdgesAsync(request, cancellationToken).ConfigureAwait(false);
        var decision = request.Decision with
        {
            ScopeRefs = Normalize(request.Decision.ScopeRefs),
            EvidenceIds = Normalize(request.Decision.EvidenceIds),
            RejectedAlternativeRefs = Normalize(request.Decision.RejectedAlternativeRefs),
            RecordedAt = _clock.GetUtcNow()
        };
        var transaction = await state.CommitBatchAsync(new BatchTransactionRequest(
            request.OperationId,
            new[] { Mutation(key, null, request.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, decision) },
            "DECISION_RECORDED",
            decision.Status.ToString().ToUpperInvariant(),
            Json(new { decision_id = decision.DecisionId, status = decision.Status }),
            edges,
            Json(new DecisionReceipt(decision.DecisionId, requestFingerprint))), cancellationToken).ConfigureAwait(false);
        return new DecisionCommitResult(transaction.Status, transaction.CurrentGeneration, decision.DecisionId, false);
    }

    public async Task<DecisionCommitResult> SupersedeAsync(
        DecisionRecordRequest replacement,
        string currentDecisionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(replacement);
        var currentId = Token(currentDecisionId, nameof(currentDecisionId));
        if (replacement.Decision.Status != DecisionStatus.ApprovedCurrent)
        {
            throw new InvalidOperationException("A replacement decision must be APPROVED_CURRENT");
        }

        var requestFingerprint = Fingerprint(replacement, currentId);
        var replay = await ReplayAsync(replacement.OperationId, replacement.Decision.DecisionId, requestFingerprint, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var currentKey = new StateKey(replacement.ProjectId, EntityKinds.Decision, currentId);
        var current = await state.ReadAsync(currentKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Current decision does not exist");
        var currentFact = Parse(current);
        if (currentFact.Status == DecisionStatus.Superseded || current.Lifecycle == StateLifecycle.Superseded)
        {
            throw new InvalidOperationException("Decision has already been superseded");
        }

        var replacementKey = new StateKey(replacement.ProjectId, EntityKinds.Decision, replacement.Decision.DecisionId);
        if (await state.ReadAsync(replacementKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("Replacement decision already exists without a matching operation receipt");
        }
        var edges = (await ValidateAndBuildEdgesAsync(replacement, cancellationToken).ConfigureAwait(false)).ToList();
        edges.Add(new DependencyEdge(
            replacement.ProjectId,
            EntityKinds.Decision,
            currentId,
            EntityKinds.Decision,
            replacement.Decision.DecisionId,
            "SUPERSEDED_BY",
            replacement.SourceFingerprint));
        var at = _clock.GetUtcNow();
        var next = replacement.Decision with
        {
            ScopeRefs = Normalize(replacement.Decision.ScopeRefs),
            EvidenceIds = Normalize(replacement.Decision.EvidenceIds),
            RejectedAlternativeRefs = Normalize(replacement.Decision.RejectedAlternativeRefs),
            RecordedAt = at
        };
        var superseded = currentFact with { Status = DecisionStatus.Superseded, SupersededBy = next.DecisionId };
        var transaction = await state.CommitBatchAsync(new BatchTransactionRequest(
            replacement.OperationId,
            new[]
            {
                Mutation(currentKey, current, replacement.GoalRevision, StateLifecycle.Superseded, replacement.SourceFingerprint, superseded),
                Mutation(replacementKey, null, replacement.GoalRevision, StateLifecycle.Valid, replacement.SourceFingerprint, next)
            },
            "DECISION_SUPERSEDED",
            "DECISION_REPLACED_ATOMICALLY",
            Json(new { prior_decision_id = currentId, replacement_decision_id = next.DecisionId }),
            edges,
            Json(new DecisionReceipt(next.DecisionId, requestFingerprint))), cancellationToken).ConfigureAwait(false);
        return new DecisionCommitResult(transaction.Status, transaction.CurrentGeneration, next.DecisionId, false);
    }

    private async Task<IReadOnlyList<DependencyEdge>> ValidateAndBuildEdgesAsync(
        DecisionRecordRequest request,
        CancellationToken cancellationToken)
    {
        var edges = new List<DependencyEdge>();
        foreach (var taskId in Normalize(request.RelatedTaskIds))
        {
            if (await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Task, taskId), cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException($"Related task does not exist: {taskId}");
            }
            edges.Add(new DependencyEdge(request.ProjectId, EntityKinds.Task, taskId, EntityKinds.Decision, request.Decision.DecisionId, "DEPENDS_ON", request.SourceFingerprint));
        }
        foreach (var evidenceId in Normalize(request.Decision.EvidenceIds))
        {
            if (await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Evidence, evidenceId), cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException($"Decision evidence does not exist: {evidenceId}");
            }
            edges.Add(new DependencyEdge(request.ProjectId, EntityKinds.Decision, request.Decision.DecisionId, EntityKinds.Evidence, evidenceId, "PROVEN_BY", request.SourceFingerprint));
        }
        return edges;
    }

    private async Task<DecisionCommitResult?> ReplayAsync(
        string operationId,
        string decisionId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }
        if (receipt.Status != "COMPLETE" || receipt.ResultJson is null)
        {
            throw new InvalidOperationException("Decision operation is incomplete");
        }
        var recorded = JsonSerializer.Deserialize<DecisionReceipt>(receipt.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Decision operation receipt is invalid");
        if (!string.Equals(recorded.DecisionId, decisionId, StringComparison.Ordinal) ||
            !string.Equals(recorded.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Operation ID is bound to another decision");
        }
        return new DecisionCommitResult("IDEMPOTENT_REPLAY", receipt.Generation ?? 0, decisionId, true);
    }

    private static void ValidateRequest(DecisionRecordRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Decision, request.Decision.DecisionId).Normalize();
        _ = Token(request.OperationId, nameof(request.OperationId));
        _ = Token(request.SourceFingerprint, nameof(request.SourceFingerprint));
        _ = Token(request.Decision.ReasonCode, nameof(request.Decision.ReasonCode));
        _ = Token(request.Decision.SourceRef, nameof(request.Decision.SourceRef));
        if (request.GoalRevision < 0 || request.RelatedTaskIds.Count is < 1 or > 128 || request.Decision.EvidenceIds.Count > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        if (request.Decision.Status == DecisionStatus.Superseded || request.Decision.SupersededBy is not null)
        {
            throw new InvalidOperationException("Only SupersedeAsync may create supersession facts");
        }
    }

    private static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        StateLifecycle lifecycle,
        string sourceFingerprint,
        EngineeringDecision value) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        lifecycle,
        Authority,
        "3.0.0",
        sourceFingerprint,
        sourceFingerprint,
        Json(value),
        RetentionTier.Warm);

    private static EngineeringDecision Parse(StateSnapshot snapshot) =>
        JsonSerializer.Deserialize<EngineeringDecision>(snapshot.PayloadJson, JsonOptions)
        ?? throw new InvalidDataException("Decision state is invalid");

    private static string[] Normalize(IEnumerable<string> values) => values
        .Select(value => Token(value, "value"))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static value => value, StringComparer.Ordinal)
        .ToArray();

    private static string Token(string value, string name)
    {
        var token = value.Trim();
        if (token.Length is 0 or > 160)
        {
            throw new ArgumentException($"{name} must contain 1-160 characters", name);
        }
        return token;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Fingerprint(DecisionRecordRequest request, string? currentDecisionId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Json(new
        {
            request.ProjectId,
            request.GoalRevision,
            request.SourceFingerprint,
            request.Decision,
            related_task_ids = Normalize(request.RelatedTaskIds),
            current_decision_id = currentDecisionId
        })))).ToLowerInvariant();

    private sealed record DecisionReceipt(string DecisionId, string RequestFingerprint);
}

using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class ChangeOutcomeCoordinator(StateCoordinator state, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ChangeOutcomeResult> RecordAsync(
        ChangeOutcomeRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var receipt = await state.ReadOperationReceiptAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        var key = new StateKey(request.ProjectId, EntityKinds.ChangeOutcome, request.ChangeId);
        var requestFingerprint = ChangeStateCodec.Hash(ChangeStateCodec.Json(request));
        if (receipt?.Status == "COMPLETE")
        {
            var existing = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed Change outcome has no state");
            var replay = ChangeStateCodec.Parse<ChangeOutcomeTelemetry>(existing);
            if (receipt.OperationType != "CHANGE_OUTCOME_RECORDED" || replay.RequestFingerprint != requestFingerprint)
                throw new InvalidOperationException("Operation ID is already bound to a different Change outcome");
            return new ChangeOutcomeResult("IDEMPOTENT_REPLAY", replay, receipt.Generation ?? existing.Generation);
        }

        var contract = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ChangeContract, request.ChangeContractId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Observed Impact requires a Change Contract");
        if (contract.Lifecycle != StateLifecycle.Valid) throw new InvalidOperationException("Observed Impact requires an active Change Contract");
        if (!string.Equals(contract.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) throw new InvalidDataException("Change Contract has unknown authority");
        var definition = ChangeStateCodec.Parse<ChangeContractDefinition>(contract);
        if (definition.ChangeId != request.ChangeId)
            throw new InvalidOperationException("Observed Impact Change does not match the active Contract");
        var impactState = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ChangeImpact, $"{request.ChangeId}:predicted"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Observed Impact requires the authoritative predicted Blast");
        if (impactState.Lifecycle != StateLifecycle.Valid || !string.Equals(impactState.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Predicted Impact has unknown or inactive authority");
        var authoritativePrediction = ChangeStateCodec.Parse<PersistedChangeImpact>(impactState).Analysis;
        if (!string.Equals(ChangeStateCodec.Json(authoritativePrediction), ChangeStateCodec.Json(request.Predicted), StringComparison.Ordinal))
            throw new InvalidOperationException("Observed Impact cannot replace the authoritative predicted Blast");
        var predicted = authoritativePrediction.Nodes.Where(static node => node.Disposition == Hiker.Pro.Graph.ImpactDisposition.Affected)
            .Select(static node => Identity(node.Key)).ToHashSet(StringComparer.Ordinal);
        var observed = request.ObservedAffectedNodes.Select(static key => Identity(key.Normalize())).ToHashSet(StringComparer.Ordinal);
        var missing = observed.Except(predicted, StringComparer.Ordinal).Count();
        var falsePositive = predicted.Except(observed, StringComparer.Ordinal).Count();
        var findings = new List<string>();
        if (missing > 0) findings.Add("MISSING_DEPENDENCY_EDGE");
        if (falsePositive > Math.Max(3, observed.Count * 3)) findings.Add("OVER_CONSERVATIVE_GRAPH");
        var predictedDimensions = authoritativePrediction.Dimensions.ToHashSet(StringComparer.Ordinal);
        if (request.ObservedDimensions.Any(dimension => !predictedDimensions.Contains(dimension))) findings.Add("MISSING_BLAST_DIMENSION");
        var telemetry = new ChangeOutcomeTelemetry(
            request.ChangeId,
            requestFingerprint,
            authoritativePrediction.Classification,
            request.ObservedClassification,
            observed.Count,
            missing,
            request.FixAttempts,
            request.PatchDebtCreated,
            request.PatchDebtRemoved,
            request.RegressionsFound,
            falsePositive,
            missing,
            request.RollbackUsed,
            request.AcceptancePassed,
            request.TimeToAcceptedChangeMs,
            findings,
            _clock.GetUtcNow());
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is not null) throw new InvalidOperationException("Change outcome already exists without a matching operation receipt");
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            request.OperationId,
            new[] { ChangeStateCodec.Mutation(key, null, definition.GoalRevision, StateLifecycle.Valid, request.SourceFingerprint, telemetry, RetentionTier.Warm) },
            "CHANGE_OUTCOME_RECORDED",
            findings.Count == 0 ? "PREDICTED_AND_OBSERVED_IMPACT_ALIGNED" : "GRAPH_CALIBRATION_REQUIRED",
            ChangeStateCodec.Json(new
            {
                change_id = request.ChangeId,
                missing_edges = missing,
                false_positive_edges = falsePositive,
                acceptance_passed = request.AcceptancePassed,
                time_to_accepted_change_ms = request.TimeToAcceptedChangeMs
            }),
            new[] { new DependencyEdge(request.ProjectId, EntityKinds.ChangeContract, request.ChangeContractId, EntityKinds.ChangeOutcome, request.ChangeId, "OBSERVED_AS", request.SourceFingerprint) }), cancellationToken).ConfigureAwait(false);
        return new ChangeOutcomeResult(commit.Committed ? "RECORDED" : commit.Status, telemetry, commit.CurrentGeneration);
    }

    private static string Identity(StateKey key) => $"{key.Kind}|{key.EntityId}";

    private static void Validate(ChangeOutcomeRequest request)
    {
        ChangeStateCodec.Token(request.OperationId, nameof(request.OperationId), 120);
        _ = new StateKey(request.ProjectId, EntityKinds.ChangeOutcome, request.ChangeId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.ChangeContract, request.ChangeContractId).Normalize();
        ChangeStateCodec.Token(request.ObservedClassification, nameof(request.ObservedClassification));
        ChangeStateCodec.Token(request.SourceFingerprint, nameof(request.SourceFingerprint));
        _ = ChangeStateCodec.Tokens(request.ObservedDimensions, nameof(request.ObservedDimensions), 64);
        if (request.ObservedAffectedNodes.Any(key => key.Normalize().ProjectId != request.ProjectId))
            throw new ArgumentException("Observed Impact nodes must belong to the current project", nameof(request));
        if (request.ObservedAffectedNodes.Count > 512 || request.FixAttempts is < 0 or > 10_000 ||
            request.PatchDebtCreated is < 0 or > 10_000 || request.PatchDebtRemoved is < 0 or > 10_000 ||
            request.RegressionsFound is < 0 or > 10_000 || request.TimeToAcceptedChangeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Change outcome metadata exceeds bounded limits");
    }
}

using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class ChangeLoopCoordinator(StateCoordinator state, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ChangeAttemptResult> RecordAttemptAsync(
        string operationId,
        string projectId,
        string changeId,
        long goalRevision,
        ChangeAttempt attempt,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        Validate(operationId, projectId, changeId, goalRevision, attempt, sourceFingerprint);
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            var replay = ChangeStateCodec.Parse<ChangeLoopState>(await ReadRequiredAsync(projectId, changeId, cancellationToken).ConfigureAwait(false));
            var recorded = replay.RecentAttempts.SingleOrDefault(existing => existing.AttemptId == attempt.AttemptId);
            if (receipt.OperationType != "CHANGE_ATTEMPT_RECORDED" || receipt.EntityId != changeId || recorded is null || !SameAttempt(recorded, attempt))
                throw new InvalidOperationException("Operation ID is already bound to a different Change attempt");
            return Result("IDEMPOTENT_REPLAY", replay, receipt.Generation ?? 0);
        }

        var key = new StateKey(projectId, EntityKinds.ChangeLoop, changeId);
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is not null && !string.Equals(current.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Change loop state has unknown authority");
        var prior = current is null
            ? new ChangeLoopState(changeId, 0, 0, 0, 0, 0, 0, Array.Empty<ChangeAttempt>(), "PATCHING_ALLOWED", _clock.GetUtcNow())
            : ChangeStateCodec.Parse<ChangeLoopState>(current);
        if (prior.RecentAttempts.Any(existing => existing.AttemptId == attempt.AttemptId))
            throw new InvalidOperationException("Attempt ID already exists without a matching operation receipt");

        var expanded = ScopeExpanded(prior.RecentAttempts.LastOrDefault()?.AffectedScopes, attempt.AffectedScopes);
        var consecutive = attempt.AcceptancePassed ? 0 : prior.ConsecutiveFailures + 1;
        var fallback = prior.FallbackAdditions + attempt.FallbackAdditions;
        var special = prior.SpecialCaseAdditions + attempt.SpecialCaseAdditions;
        var stale = prior.EvidenceStaleCount + attempt.EvidenceStaleCount;
        var scopeExpansion = prior.ScopeExpansionCount + (expanded ? 1 : 0);
        var status = attempt.AcceptancePassed
            ? "ACCEPTED"
            : consecutive >= 3 && (fallback > 0 || special > 0 || stale > 0 || scopeExpansion > 0)
                ? ChangeControlStatuses.PatchingDisabled
                : consecutive >= 3
                    ? ChangeControlStatuses.FixLoopSuspected
                    : "PATCHING_ALLOWED";
        var observed = attempt with { ObservedAt = attempt.ObservedAt == default ? _clock.GetUtcNow() : attempt.ObservedAt };
        var next = new ChangeLoopState(
            changeId,
            prior.TotalAttempts + 1,
            consecutive,
            scopeExpansion,
            fallback,
            special,
            stale,
            prior.RecentAttempts.Append(observed).TakeLast(8).ToArray(),
            status,
            _clock.GetUtcNow());
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { ChangeStateCodec.Mutation(key, current, goalRevision, StateLifecycle.Valid, sourceFingerprint, next) },
            "CHANGE_ATTEMPT_RECORDED",
            status == ChangeControlStatuses.PatchingDisabled ? "FIX_LOOP_CONFIRMED_PATCHING_DISABLED" : status,
            ChangeStateCodec.Json(new
            {
                change_id = changeId,
                attempts = next.TotalAttempts,
                consecutive_failures = next.ConsecutiveFailures,
                fallback_additions = next.FallbackAdditions,
                special_case_additions = next.SpecialCaseAdditions,
                patching_allowed = next.Status != ChangeControlStatuses.PatchingDisabled
            })), cancellationToken).ConfigureAwait(false);
        if (!commit.Committed) return Result(commit.Status, prior, commit.CurrentGeneration);
        var resultStatus = next.Status == ChangeControlStatuses.PatchingDisabled
            ? ChangeControlStatuses.FixLoopConfirmed
            : next.Status;
        return Result(resultStatus, next, commit.CurrentGeneration);
    }

    public async Task<RootCausePlan> BuildRootCausePlanAsync(
        string projectId,
        string changeId,
        string changeContractId,
        string observedSymptomCode,
        string rootCauseHypothesisCode,
        IReadOnlyList<string> conflictingConstraints,
        IReadOnlyList<StateKey> affectedDependents,
        IReadOnlyList<string> verificationPlan,
        CancellationToken cancellationToken = default)
    {
        var loopState = await ReadRequiredAsync(projectId, changeId, cancellationToken).ConfigureAwait(false);
        var loop = ChangeStateCodec.Parse<ChangeLoopState>(loopState);
        if (loop.Status is not (ChangeControlStatuses.PatchingDisabled or ChangeControlStatuses.FixLoopSuspected))
            throw new InvalidOperationException("Root Cause Mode requires a suspected or confirmed Fix Loop");
        var contractState = await state.ReadAsync(new StateKey(projectId, EntityKinds.ChangeContract, changeContractId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Root Cause Mode requires the active Change Contract");
        if (contractState.Lifecycle != StateLifecycle.Valid || !string.Equals(contractState.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Change Contract has unknown authority");
        var contract = ChangeStateCodec.Parse<ChangeContractDefinition>(contractState);
        if (contract.ChangeId != changeId || contract.Status != "ACTIVE")
            throw new InvalidOperationException("Root Cause Mode requires the active Contract for the same Change");
        if (affectedDependents.Count > 256 || affectedDependents.Any(key => key.Normalize().ProjectId != projectId))
            throw new ArgumentException("Root Cause dependents must be bounded to the current project", nameof(affectedDependents));
        return new RootCausePlan(
            changeId,
            ChangeStateCodec.Token(observedSymptomCode, nameof(observedSymptomCode)),
            ChangeStateCodec.Token(rootCauseHypothesisCode, nameof(rootCauseHypothesisCode)),
            contract.IntendedInvariant,
            ChangeStateCodec.Tokens(conflictingConstraints, nameof(conflictingConstraints), 128),
            loop.RecentAttempts.Where(static attempt => !attempt.AcceptancePassed).Select(static attempt => attempt.FailureSignature).Distinct(StringComparer.Ordinal).ToArray(),
            loop.RecentAttempts.SelectMany(static attempt => attempt.RejectedSolutionCodes).Distinct(StringComparer.Ordinal).Take(128).ToArray(),
            affectedDependents.Select(static key => key.Normalize()).Distinct().ToArray(),
            ChangeStateCodec.Tokens(verificationPlan, nameof(verificationPlan), 128),
            contract.RollbackBoundary,
            ChangeControlStatuses.RootCauseRequired);
    }

    private async Task<StateSnapshot> ReadRequiredAsync(string projectId, string changeId, CancellationToken cancellationToken)
    {
        var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ChangeLoop, changeId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Completed Change attempt has no loop state");
        if (snapshot.Lifecycle != StateLifecycle.Valid || !string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Change loop state has unknown or inactive authority");
        return snapshot;
    }

    private static ChangeAttemptResult Result(string status, ChangeLoopState loop, long generation) => new(
        status,
        loop,
        generation,
        loop.Status != ChangeControlStatuses.PatchingDisabled,
        loop.Status is ChangeControlStatuses.PatchingDisabled or ChangeControlStatuses.FixLoopSuspected);

    private static bool ScopeExpanded(IReadOnlyList<string>? prior, IReadOnlyList<string> current)
    {
        if (prior is null || prior.Count == 0) return false;
        var normalized = AffectedScopePolicy.Normalize(prior);
        return AffectedScopePolicy.Normalize(current).Any(scope => !normalized.Any(allowed => AffectedScopePolicy.Overlap(new[] { allowed }, new[] { scope })));
    }

    private static bool SameAttempt(ChangeAttempt recorded, ChangeAttempt requested) =>
        recorded.AttemptId == requested.AttemptId && recorded.FailureSignature == requested.FailureSignature &&
        recorded.AcceptancePassed == requested.AcceptancePassed && recorded.FallbackAdditions == requested.FallbackAdditions &&
        recorded.SpecialCaseAdditions == requested.SpecialCaseAdditions && recorded.EvidenceStaleCount == requested.EvidenceStaleCount &&
        AffectedScopePolicy.Normalize(recorded.AffectedScopes).SequenceEqual(AffectedScopePolicy.Normalize(requested.AffectedScopes), StringComparer.Ordinal) &&
        recorded.RejectedSolutionCodes.OrderBy(static value => value, StringComparer.Ordinal)
            .SequenceEqual(requested.RejectedSolutionCodes.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static void Validate(
        string operationId,
        string projectId,
        string changeId,
        long goalRevision,
        ChangeAttempt attempt,
        string sourceFingerprint)
    {
        ChangeStateCodec.Token(operationId, nameof(operationId), 120);
        _ = new StateKey(projectId, EntityKinds.ChangeLoop, changeId).Normalize();
        ChangeStateCodec.Token(sourceFingerprint, nameof(sourceFingerprint));
        ChangeStateCodec.Token(attempt.AttemptId, nameof(attempt.AttemptId));
        ChangeStateCodec.Token(attempt.FailureSignature, nameof(attempt.FailureSignature));
        _ = ChangeStateCodec.Tokens(attempt.AffectedScopes, nameof(attempt.AffectedScopes), 128);
        _ = ChangeStateCodec.Tokens(attempt.RejectedSolutionCodes, nameof(attempt.RejectedSolutionCodes), 128);
        if (goalRevision < 1 || attempt.FallbackAdditions is < 0 or > 100 || attempt.SpecialCaseAdditions is < 0 or > 100 || attempt.EvidenceStaleCount is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(attempt), "Change attempt counters are invalid");
    }
}

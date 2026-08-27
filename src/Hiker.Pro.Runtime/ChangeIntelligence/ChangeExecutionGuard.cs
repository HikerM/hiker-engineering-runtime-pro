using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class ChangeExecutionGuard(
    StateCoordinator state,
    PatchDebtCoordinator? patchDebt = null)
{
    private readonly PatchDebtCoordinator _patchDebt = patchDebt ?? new PatchDebtCoordinator(state);

    public async Task<ChangeScopeEvaluation> EvaluateScopeAsync(
        ChangeScopeEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateScopeRequest(request);
        var requestFingerprint = ChangeStateCodec.Hash(ChangeStateCodec.Json(request));
        var receipt = await state.ReadOperationReceiptAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            if (receipt.OperationType != "CHANGE_CONTRACT_BREACHED" || receipt.ResultJson is null)
                throw new InvalidOperationException("Operation ID is bound to an incompatible Change operation");
            var prior = ChangeStateCodec.FromJson<ChangeScopeReceipt>(receipt.ResultJson);
            if (prior.RequestFingerprint != requestFingerprint)
                throw new InvalidOperationException("Operation ID is already bound to a different Change scope evaluation");
            return new ChangeScopeEvaluation(prior.Status, prior.OutOfScope, prior.UndeclaredSharedFacts, prior.UntrackedPatchMechanisms, receipt.Generation ?? 0);
        }
        var contractState = await RequiredContractAsync(request.ProjectId, request.ChangeContractId, cancellationToken).ConfigureAwait(false);
        var contract = ChangeStateCodec.Parse<ChangeContractDefinition>(contractState);
        var allowed = AffectedScopePolicy.Normalize(contract.AllowedAffectedScopes);
        var actual = AffectedScopePolicy.Normalize(request.ActualAffectedScopes);
        var outOfScope = actual.Where(scope => !allowed.Any(candidate => AffectedScopePolicy.Overlap(new[] { candidate }, new[] { scope }))).ToArray();
        var undeclared = request.NewlyAffectedSharedFacts.Select(static key => key.Normalize()).Where(key =>
        {
            var candidates = new[] { key.EntityId, $"{key.Kind.ToLowerInvariant()}/{key.EntityId}" };
            return !candidates.Any(candidate => allowed.Any(scope => AffectedScopePolicy.Overlap(new[] { scope }, new[] { candidate })));
        }).Distinct().ToArray();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var debtId in request.RegisteredPatchDebtIds.Distinct(StringComparer.Ordinal))
        {
            var snapshot = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.PatchDebt, debtId), cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.Lifecycle != StateLifecycle.Valid || !string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) continue;
            registered.Add(ChangeStateCodec.Parse<PatchDebtFact>(snapshot).Mechanism);
        }
        var untracked = request.PatchMechanisms.Where(PatchDebtCoordinator.RequiresDebt).Where(mechanism => !registered.Contains(mechanism)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (outOfScope.Length == 0 && undeclared.Length == 0 && untracked.Length == 0)
            return new ChangeScopeEvaluation("SCOPE_VALID", outOfScope, undeclared, untracked, contractState.Generation);

        var breached = contract with { Status = ChangeControlStatuses.ContractBreached };
        var result = new ChangeScopeReceipt(requestFingerprint, ChangeControlStatuses.ContractBreached, outOfScope, undeclared, untracked);
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            request.OperationId,
            new[] { ChangeStateCodec.Mutation(contractState.Key, contractState, contract.GoalRevision, StateLifecycle.Stale, request.SourceFingerprint, breached) },
            "CHANGE_CONTRACT_BREACHED",
            "PATCH_SCOPE_OR_TEMPORARY_MECHANISM_EXCEEDED",
            ChangeStateCodec.Json(new
            {
                out_of_scope = outOfScope,
                undeclared_shared_facts = undeclared.Select(static key => new { kind = key.Kind, id = key.EntityId }),
                untracked_patch_mechanisms = untracked
            }),
            ResultJson: ChangeStateCodec.Json(result)), cancellationToken).ConfigureAwait(false);
        return new ChangeScopeEvaluation(commit.Committed ? ChangeControlStatuses.ContractBreached : commit.Status, outOfScope, undeclared, untracked, commit.CurrentGeneration);
    }

    public async Task<ChangeVerificationResult> VerifyAsync(
        ChangeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var contractState = await RequiredContractAsync(request.ProjectId, request.ChangeContractId, cancellationToken).ConfigureAwait(false);
        var contract = ChangeStateCodec.Parse<ChangeContractDefinition>(contractState);
        if (contract.ChangeId != request.ChangeId)
            throw new InvalidOperationException("Invariant Guard Change does not match the active Contract");
        var impactState = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ChangeImpact, $"{request.ChangeId}:predicted"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Invariant Guard requires predicted Blast Radius");
        if (!string.Equals(impactState.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) throw new InvalidDataException("Predicted impact has unknown authority");
        var impact = ChangeStateCodec.Parse<PersistedChangeImpact>(impactState).Analysis;
        var missing = new List<string>();
        Missing(contract.AcceptanceCriteria, request.PrimaryAcceptance, missing);
        Missing(contract.ExpectedUnaffectedBehaviors, request.InvariantRegression, missing);
        Missing(impact.Regression.BlastDerivedRegression.Select(static key => key.EntityId), request.BlastDerivedRegression, missing);
        var all = request.PrimaryAcceptance.Concat(request.InvariantRegression).Concat(request.BlastDerivedRegression).ToArray();
        if (all.Length > 384) throw new ArgumentOutOfRangeException(nameof(request), "Change verification exceeds bounded evidence limits");
        var environmental = all.Where(static outcome => outcome.Status != "PASS" && outcome.FailureKind is "FLAKY" or "ENVIRONMENTAL" or "RESOURCE_CONFLICT" or "UNKNOWN")
            .Select(static outcome => outcome.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
        var regressions = all.Where(static outcome => outcome.Status != "PASS" && outcome.FailureKind == "REGRESSION")
            .Select(static outcome => outcome.EvidenceId).Distinct(StringComparer.Ordinal).ToArray();
        var invariantFailure = request.InvariantRegression.Any(static outcome => outcome.Status != "PASS" && outcome.FailureKind == "REGRESSION");
        var openDebt = await _patchDebt.OpenForChangeAsync(request.ProjectId, request.ChangeId, cancellationToken).ConfigureAwait(false);
        var status = invariantFailure
            ? ChangeControlStatuses.InvariantGuardFailed
            : regressions.Length > 0
                ? "REGRESSION"
                : missing.Count > 0 || environmental.Length > 0
                    ? "REQUIRES_REVIEW"
                    : openDebt.Count > 0
                        ? "PATCH_DEBT_REVIEW_REQUIRED"
                        : "ACCEPTED";
        return new ChangeVerificationResult(status, status == "ACCEPTED", regressions, environmental, missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<StateSnapshot> RequiredContractAsync(string projectId, string contractId, CancellationToken cancellationToken)
    {
        var snapshot = await state.ReadAsync(new StateKey(projectId, EntityKinds.ChangeContract, contractId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Change Contract does not exist");
        if (snapshot.Lifecycle != StateLifecycle.Valid) throw new InvalidOperationException("Change Contract is not active");
        if (!string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) throw new InvalidDataException("Change Contract has unknown authority");
        return snapshot;
    }

    private static void Missing(IEnumerable<string> expected, IReadOnlyList<ChangeVerificationOutcome> actual, List<string> missing)
    {
        var ids = actual.Select(static outcome => outcome.EvidenceId).ToHashSet(StringComparer.Ordinal);
        missing.AddRange(expected.Where(id => !ids.Contains(id)));
    }

    private static void ValidateScopeRequest(ChangeScopeEvaluationRequest request)
    {
        ChangeStateCodec.Token(request.OperationId, nameof(request.OperationId), 120);
        _ = new StateKey(request.ProjectId, EntityKinds.ChangeContract, request.ChangeContractId).Normalize();
        ChangeStateCodec.Token(request.SourceFingerprint, nameof(request.SourceFingerprint));
        _ = ChangeStateCodec.Tokens(request.ActualAffectedScopes, nameof(request.ActualAffectedScopes), 128);
        _ = ChangeStateCodec.Tokens(request.PatchMechanisms, nameof(request.PatchMechanisms), 64);
        _ = ChangeStateCodec.Tokens(request.RegisteredPatchDebtIds, nameof(request.RegisteredPatchDebtIds), 128);
        if (request.NewlyAffectedSharedFacts.Count > 128) throw new ArgumentOutOfRangeException(nameof(request), "Shared fact delta exceeds scope guard budget");
    }
}

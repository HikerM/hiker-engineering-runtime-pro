using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class PatchDebtCoordinator(StateCoordinator state, TimeProvider? timeProvider = null)
{
    private static readonly HashSet<string> TemporaryMechanisms = new(StringComparer.OrdinalIgnoreCase)
    {
        "fallback", "special-case", "temporary-bypass", "legacy-path", "dual-write", "compatibility-shim",
        "temporary-feature-flag", "duplicated-path", "temporary-retry"
    };
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<BatchTransactionResult> RecordAsync(
        string operationId,
        string projectId,
        PatchDebtFact debt,
        string changeContractId,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        Validate(debt);
        var debtKey = new StateKey(projectId, EntityKinds.PatchDebt, debt.DebtId);
        var priorReceipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (priorReceipt?.Status == "COMPLETE")
        {
            var existing = await state.ReadAsync(debtKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed Patch Debt operation has no state");
            var recorded = ChangeStateCodec.Parse<PatchDebtFact>(existing);
            if (priorReceipt.OperationType != "PATCH_DEBT_RECORDED" || priorReceipt.EntityId != debt.DebtId || !SameDebt(recorded, debt))
                throw new InvalidOperationException("Operation ID is already bound to different Patch Debt");
            return new BatchTransactionResult(true, false, priorReceipt.Generation ?? existing.Generation, new[] { existing }, "IDEMPOTENT_REPLAY");
        }
        var contract = await state.ReadAsync(new StateKey(projectId, EntityKinds.ChangeContract, changeContractId), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Patch Debt requires an active Change Contract");
        if (contract.Lifecycle != StateLifecycle.Valid || !string.Equals(contract.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Patch Debt cannot bind an inactive or unknown Change Contract");
        var definition = ChangeStateCodec.Parse<ChangeContractDefinition>(contract);
        if (definition.GoalRevision != debt.GoalRevision || definition.ChangeContractId != changeContractId || definition.ChangeId != debt.ChangeId)
            throw new InvalidOperationException("Patch Debt does not match the Change Contract revision");

        var key = debtKey;
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is not null) throw new InvalidOperationException("Patch Debt already exists without a matching operation receipt");
        var normalized = debt with
        {
            Scope = ChangeStateCodec.Tokens(debt.Scope, nameof(debt.Scope), 128),
            EvidenceIds = ChangeStateCodec.Tokens(debt.EvidenceIds, nameof(debt.EvidenceIds), 128),
            Status = "OPEN",
            UpdatedAt = _clock.GetUtcNow()
        };
        return await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { ChangeStateCodec.Mutation(key, null, debt.GoalRevision, StateLifecycle.Valid, sourceFingerprint, normalized, RetentionTier.Warm) },
            "PATCH_DEBT_RECORDED",
            "TEMPORARY_MECHANISM_TRACKED",
            ChangeStateCodec.Json(new { debt_id = debt.DebtId, change_id = debt.ChangeId, mechanism = debt.Mechanism }),
            new[] { new DependencyEdge(projectId, EntityKinds.ChangeContract, changeContractId, EntityKinds.PatchDebt, debt.DebtId, "INTRODUCES_PATCH_DEBT", sourceFingerprint) }), cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatchTransactionResult> ResolveAsync(
        string operationId,
        string projectId,
        string debtId,
        long goalRevision,
        string resolution,
        IReadOnlyList<string> evidenceIds,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (resolution is not ("REMOVED" or "ACCEPTED_DEBT")) throw new ArgumentException("Patch Debt resolution must be REMOVED or ACCEPTED_DEBT", nameof(resolution));
        var key = new StateKey(projectId, EntityKinds.PatchDebt, debtId);
        var priorReceipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (priorReceipt?.Status == "COMPLETE")
        {
            var replay = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed Patch Debt resolution has no state");
            var recorded = ChangeStateCodec.Parse<PatchDebtFact>(replay);
            if (priorReceipt.OperationType != "PATCH_DEBT_RESOLVED" || priorReceipt.EntityId != debtId || recorded.Status != resolution ||
                !recorded.EvidenceIds.OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(evidenceIds.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidOperationException("Operation ID is already bound to a different Patch Debt resolution");
            return new BatchTransactionResult(true, false, priorReceipt.Generation ?? replay.Generation, new[] { replay }, "IDEMPOTENT_REPLAY");
        }
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Patch Debt does not exist");
        if (!string.Equals(current.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) throw new InvalidDataException("Patch Debt has unknown authority");
        var debt = ChangeStateCodec.Parse<PatchDebtFact>(current);
        var evidence = ChangeStateCodec.Tokens(evidenceIds, nameof(evidenceIds), 128);
        if (resolution == "ACCEPTED_DEBT" && evidence.Count == 0) throw new InvalidOperationException("Accepted Patch Debt requires explicit evidence");
        var next = debt with { Status = resolution, EvidenceIds = evidence, UpdatedAt = _clock.GetUtcNow() };
        return await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { ChangeStateCodec.Mutation(key, current, goalRevision, StateLifecycle.Archived, sourceFingerprint, next, RetentionTier.Warm) },
            "PATCH_DEBT_RESOLVED",
            resolution,
            ChangeStateCodec.Json(new { debt_id = debtId, resolution, evidence_count = evidence.Count })), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PatchDebtFact>> OpenForChangeAsync(
        string projectId,
        string changeId,
        CancellationToken cancellationToken = default)
    {
        var states = await state.ReadAsync(projectId, EntityKinds.PatchDebt, StateLifecycle.Valid, 129, cancellationToken).ConfigureAwait(false);
        if (states.Count == 129) throw new InvalidOperationException("Active Patch Debt index budget is exhausted");
        var values = new List<PatchDebtFact>();
        foreach (var snapshot in states)
        {
            if (!string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal)) throw new InvalidDataException("Patch Debt index contains unknown authority");
            var debt = ChangeStateCodec.Parse<PatchDebtFact>(snapshot);
            if (debt.ChangeId == changeId && debt.Status == "OPEN") values.Add(debt);
        }
        return values.OrderBy(static debt => debt.DebtId, StringComparer.Ordinal).ToArray();
    }

    public static bool RequiresDebt(string mechanism) => TemporaryMechanisms.Contains(mechanism);

    private static void Validate(PatchDebtFact debt)
    {
        ChangeStateCodec.Token(debt.DebtId, nameof(debt.DebtId));
        ChangeStateCodec.Token(debt.ChangeId, nameof(debt.ChangeId));
        ChangeStateCodec.Token(debt.IntroducedBy, nameof(debt.IntroducedBy));
        ChangeStateCodec.Token(debt.Mechanism, nameof(debt.Mechanism));
        ChangeStateCodec.Token(debt.ReasonCode, nameof(debt.ReasonCode));
        ChangeStateCodec.Token(debt.RemovalCondition, nameof(debt.RemovalCondition), 512);
        ChangeStateCodec.Token(debt.ReviewCondition, nameof(debt.ReviewCondition), 512);
        if (debt.AffectedContract is not null) ChangeStateCodec.Token(debt.AffectedContract, nameof(debt.AffectedContract));
        if (!TemporaryMechanisms.Contains(debt.Mechanism) || debt.GoalRevision < 1)
            throw new ArgumentException("Patch Debt mechanism or Goal revision is invalid", nameof(debt));
    }

    private static bool SameDebt(PatchDebtFact recorded, PatchDebtFact requested) =>
        recorded.DebtId == requested.DebtId && recorded.ChangeId == requested.ChangeId && recorded.IntroducedBy == requested.IntroducedBy &&
        recorded.GoalRevision == requested.GoalRevision && recorded.Mechanism == requested.Mechanism && recorded.ReasonCode == requested.ReasonCode &&
        recorded.AffectedContract == requested.AffectedContract && recorded.RemovalCondition == requested.RemovalCondition && recorded.ReviewCondition == requested.ReviewCondition &&
        recorded.Scope.OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(requested.Scope.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal) &&
        recorded.EvidenceIds.OrderBy(static value => value, StringComparer.Ordinal).SequenceEqual(requested.EvidenceIds.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal);
}

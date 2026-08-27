using System.Diagnostics;
using System.Text;
using Hiker.Pro.Graph;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class ChangeIntelligencePlane(
    StateCoordinator state,
    ChangeRiskProbe? riskProbe = null,
    ImpactGraphEngine? impactGraph = null,
    TimeProvider? timeProvider = null)
{
    private readonly ChangeRiskProbe _probe = riskProbe ?? new ChangeRiskProbe(state);
    private readonly ImpactGraphEngine _impact = impactGraph ?? new ImpactGraphEngine(state);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ChangePreflightResult> PreflightAsync(
        ChangePreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var probe = await _probe.ProbeAsync(request.Probe, cancellationToken).ConfigureAwait(false);
        if (!probe.RequiresFullAnalysis)
        {
            return new ChangePreflightResult(
                ChangeControlStatuses.FastPath,
                probe,
                null,
                Policy(probe.Classification, null, probe.PatchingAllowed),
                0,
                0,
                false,
                false);
        }

        var contract = request.Contract ?? throw new InvalidOperationException("Shared, cross-cutting, systemic, or fix-loop change requires a Change Contract");
        ValidateContract(request, contract);
        var operationId = $"{request.OperationId}:preflight";
        var requestFingerprint = RequestFingerprint(request, contract);
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            return await ReplayAsync(request, probe, receipt, requestFingerprint, cancellationToken).ConfigureAwait(false);
        }

        var task = await state.ReadAsync(new StateKey(request.Probe.ProjectId, EntityKinds.Task, request.Probe.TaskId), cancellationToken).ConfigureAwait(false);
        if (task is null || task.Lifecycle != StateLifecycle.Valid || task.GoalRevision != contract.GoalRevision)
            throw new InvalidOperationException("Change Contract requires an active Task bound to the same Goal revision");

        var timer = Stopwatch.StartNew();
        var raw = await _impact.AnalyzeAsync(new ImpactAnalysisRequest(
            request.Probe.ProjectId,
            request.Probe.Seeds,
            request.MaxNodes,
            request.MaxDepth), cancellationToken).ConfigureAwait(false);
        var blast = BuildBlast(request, contract, probe, raw, timer, _clock.GetUtcNow());
        var policy = Policy(blast.Classification, contract.ChangeContractId, probe.PatchingAllowed);
        if (policy.RequiresRollbackBoundary && string.IsNullOrWhiteSpace(contract.RollbackBoundary))
            throw new InvalidOperationException("Systemic change requires an explicit rollback boundary");

        var contractKey = new StateKey(request.Probe.ProjectId, EntityKinds.ChangeContract, contract.ChangeContractId);
        var impactKey = new StateKey(request.Probe.ProjectId, EntityKinds.ChangeImpact, $"{request.Probe.ChangeId}:predicted");
        var currentContract = await state.ReadAsync(contractKey, cancellationToken).ConfigureAwait(false);
        var currentImpact = await state.ReadAsync(impactKey, cancellationToken).ConfigureAwait(false);
        EnsureAuthority(currentContract);
        EnsureAuthority(currentImpact);
        if ((currentContract is null) != (currentImpact is null))
            throw new InvalidDataException("Change Contract and predicted impact are not transactionally aligned");
        if (currentContract is not null)
        {
            if (currentContract.Lifecycle != StateLifecycle.Valid || currentImpact!.Lifecycle != StateLifecycle.Valid)
                throw new InvalidOperationException("Inactive Change Contract cannot be reused for a new preflight");
            var recordedContract = ChangeStateCodec.Parse<ChangeContractDefinition>(currentContract);
            if (!string.Equals(ChangeStateCodec.Json(recordedContract), ChangeStateCodec.Json(contract), StringComparison.Ordinal))
                throw new InvalidOperationException("Immutable Change Contract already exists with different content");
        }

        var persisted = Persisted(blast, _clock.GetUtcNow());
        var contractPayload = ChangeStateCodec.Json(contract);
        var impactPayload = ChangeStateCodec.Json(persisted);
        var mutations = new List<StateMutation>();
        if (currentContract is null)
            mutations.Add(ChangeStateCodec.Mutation(contractKey, null, contract.GoalRevision, StateLifecycle.Valid, contract.SourceFingerprint, contract));
        mutations.Add(ChangeStateCodec.Mutation(impactKey, currentImpact, contract.GoalRevision, StateLifecycle.Valid, contract.SourceFingerprint, persisted));
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "CHANGE_PREFLIGHT_COMMITTED",
            "CHANGE_CONTRACT_AND_BLAST_RECORDED",
            ChangeStateCodec.Json(new
            {
                change_id = request.Probe.ChangeId,
                classification = blast.Classification,
                dimensions = blast.Dimensions,
                nodes_visited = blast.NodesVisited,
                patching_allowed = policy.PatchingAllowed
            }),
            new[]
            {
                new DependencyEdge(request.Probe.ProjectId, EntityKinds.Task, request.Probe.TaskId, EntityKinds.ChangeContract, contract.ChangeContractId, "GOVERNED_BY", contract.SourceFingerprint),
                new DependencyEdge(request.Probe.ProjectId, EntityKinds.Task, request.Probe.TaskId, EntityKinds.ChangeImpact, impactKey.EntityId, "HAS_PREDICTED_IMPACT", contract.SourceFingerprint)
            },
            ChangeStateCodec.Json(new ChangePreflightReceipt(requestFingerprint, contract.ChangeContractId, impactKey.EntityId, blast.Classification))), cancellationToken).ConfigureAwait(false);
        if (!commit.Committed)
            return new ChangePreflightResult(commit.Status, probe, blast, policy, commit.CurrentGeneration, 0, true, false);

        return new ChangePreflightResult(
            ChangeControlStatuses.Ready,
            probe,
            blast,
            policy,
            commit.CurrentGeneration,
            Encoding.UTF8.GetByteCount(contractPayload) + Encoding.UTF8.GetByteCount(impactPayload),
            true,
            true);
    }

    private async Task<ChangePreflightResult> ReplayAsync(
        ChangePreflightRequest request,
        ChangeRiskProbeResult probe,
        OperationReceipt receipt,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        if (receipt.OperationType != "CHANGE_PREFLIGHT_COMMITTED" || receipt.ResultJson is null)
            throw new InvalidOperationException("Operation ID is bound to an incompatible Change operation");
        var prior = ChangeStateCodec.FromJson<ChangePreflightReceipt>(receipt.ResultJson);
        if (!string.Equals(prior.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Operation ID is already bound to a different Change preflight");
        var contract = request.Contract!;
        var impact = await state.ReadAsync(
            new StateKey(request.Probe.ProjectId, EntityKinds.ChangeImpact, prior.ImpactId),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Completed Change preflight has no predicted impact");
        EnsureAuthority(impact);
        var persisted = ChangeStateCodec.Parse<PersistedChangeImpact>(impact);
        var blast = persisted.Analysis;
        return new ChangePreflightResult(
            "IDEMPOTENT_REPLAY",
            probe,
            blast,
            Policy(blast.Classification, contract.ChangeContractId, probe.PatchingAllowed),
            receipt.Generation ?? impact.Generation,
            0,
            true,
            false);
    }

    private static ChangeBlastAnalysis BuildBlast(
        ChangePreflightRequest request,
        ChangeContractDefinition contract,
        ChangeRiskProbeResult probe,
        ImpactAnalysisResult raw,
        Stopwatch timer,
        DateTimeOffset observedAt)
    {
        var dimensions = raw.Nodes.Select(Dimension).Concat(request.Probe.Signals.DeclaredDimensions ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        AddSignalDimensions(request.Probe.Signals, dimensions);
        var relations = raw.Nodes.Where(static node => node.Distance > 0).Select(node => Relation(node, observedAt)).ToArray();
        var transitive = raw.Nodes.Count(static node => node.Distance > 0 && node.Disposition == ImpactDisposition.Affected);
        var centrality = probe.Centrality with
        {
            TransitiveConsumerCount = transitive,
            ServiceFanout = Math.Max(probe.Centrality.ServiceFanout, raw.Nodes.Count(static node => node.Key.Kind == EntityKinds.Service)),
            EvidenceFanout = Math.Max(probe.Centrality.EvidenceFanout, raw.Nodes.Count(static node => node.Key.Kind is EntityKinds.Evidence or EntityKinds.Test)),
            RuntimeFanout = Math.Max(probe.Centrality.RuntimeFanout, raw.Nodes.Count(static node => node.Key.Kind is EntityKinds.ServiceProcess or EntityKinds.EnvironmentInstance)),
            ResourceFanout = Math.Max(probe.Centrality.ResourceFanout, raw.Nodes.Count(static node => node.Key.Kind is EntityKinds.ResourceLease or EntityKinds.Database))
        };
        var classification = raw.Truncated ? ChangeRiskClasses.Systemic : Upgrade(probe.Classification, centrality, dimensions);
        var blastRegression = raw.Nodes
            .Where(static node => node.Disposition == ImpactDisposition.Affected && node.Key.Kind is EntityKinds.Evidence or EntityKinds.Test)
            .Select(static node => node.Key)
            .Distinct()
            .Take(256)
            .ToArray();
        var regression = new ChangeRegressionSet(
            contract.AcceptanceCriteria.Take(128).ToArray(),
            contract.ExpectedUnaffectedBehaviors.Take(128).ToArray(),
            blastRegression);
        timer.Stop();
        return new ChangeBlastAnalysis(
            classification,
            dimensions.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            raw.Nodes,
            centrality,
            relations,
            regression,
            classification switch { ChangeRiskClasses.Systemic => "CRITICAL", ChangeRiskClasses.CrossCutting => "HIGH", ChangeRiskClasses.Shared => "MEDIUM", _ => "LOW" },
            raw.Truncated || relations.Any(static relation => !relation.AuthoritativeDependency) ? "MEDIUM" : "HIGH",
            raw.DependencyQueries,
            raw.Nodes.Count,
            raw.Truncated,
            timer.Elapsed.TotalMilliseconds,
            raw.Truncated ? "REQUIRES_REVIEW" : "ANALYZED");
    }

    private static string Upgrade(string classification, ChangeCentrality centrality, IReadOnlySet<string> dimensions)
    {
        if (classification == ChangeRiskClasses.Systemic) return classification;
        if (centrality.ContractConsumerCount >= 2 || centrality.ServiceFanout >= 2 ||
            dimensions.Contains(BlastDimensions.Temporal) || dimensions.Contains(BlastDimensions.Security))
            return ChangeRiskClasses.CrossCutting;
        if (classification == ChangeRiskClasses.Local && centrality.TransitiveConsumerCount > 0) return ChangeRiskClasses.Shared;
        return classification;
    }

    private static string Dimension(ImpactedNode node) => node.Key.Kind switch
    {
        EntityKinds.Contract => BlastDimensions.Contract,
        EntityKinds.Database or EntityKinds.Migration => BlastDimensions.DataState,
        EntityKinds.EnvironmentProfile or EntityKinds.EnvironmentInstance or EntityKinds.ServiceProcess or EntityKinds.Service or EntityKinds.RuntimeProfile => BlastDimensions.Runtime,
        EntityKinds.ResourceLease => BlastDimensions.Resource,
        EntityKinds.Evidence or EntityKinds.Test => BlastDimensions.Verification,
        EntityKinds.Release => BlastDimensions.Delivery,
        _ when node.ViaRelation.Equals("CO_CHANGED_WITH", StringComparison.OrdinalIgnoreCase) => BlastDimensions.Historical,
        _ => BlastDimensions.Structural
    };

    private static void AddSignalDimensions(ChangeRiskSignals signals, HashSet<string> dimensions)
    {
        if (signals.DatabaseSchema || signals.SharedState) dimensions.Add(BlastDimensions.DataState);
        if (signals.TemporalCompatibilityWindow) dimensions.Add(BlastDimensions.Temporal);
        if (signals.SecurityBoundary) dimensions.Add(BlastDimensions.Security);
        if (signals.PerformanceCritical) dimensions.Add(BlastDimensions.Performance);
        if (signals.DeliveryCritical) dimensions.Add(BlastDimensions.Delivery);
        if (signals.RecentFailedAttempts > 0 || signals.FallbackAdditions > 0 || signals.SpecialCaseAdditions > 0) dimensions.Add(BlastDimensions.Historical);
    }

    private static ChangeRelationAssessment Relation(ImpactedNode node, DateTimeOffset observedAt)
    {
        var historical = node.ViaRelation.Equals("CO_CHANGED_WITH", StringComparison.OrdinalIgnoreCase);
        var observed = node.ViaRelation.StartsWith("OBSERVED_", StringComparison.OrdinalIgnoreCase);
        var ambiguous = node.Disposition == ImpactDisposition.RequiresReview;
        return new ChangeRelationAssessment(
            node.Key,
            node.ViaRelation,
            node.RelationFingerprint,
            historical ? "git-history" : observed ? "runtime-observation" : "state-dependency-index",
            historical || ambiguous ? "MEDIUM" : "HIGH",
            historical ? "HISTORICAL_SIGNAL" : "CURRENT_INDEX",
            historical ? "RISK_SIGNAL_ONLY" : "STATE_COORDINATOR",
            !historical && !ambiguous,
            observedAt);
    }

    private static ChangeExecutionPolicy Policy(string classification, string? contractId, bool patchingAllowed)
    {
        var cross = classification is ChangeRiskClasses.CrossCutting or ChangeRiskClasses.Systemic;
        var systemic = classification == ChangeRiskClasses.Systemic;
        return new ChangeExecutionPolicy(classification, contractId, cross, cross, systemic, systemic, patchingAllowed, !patchingAllowed);
    }

    private static PersistedChangeImpact Persisted(ChangeBlastAnalysis blast, DateTimeOffset recordedAt) => new(
        blast with
        {
            Nodes = blast.Nodes.Take(256).ToArray(),
            Relations = blast.Relations.Take(256).ToArray(),
            Truncated = blast.Truncated || blast.Nodes.Count > 256,
            Status = blast.Nodes.Count > 256 ? "BOUNDED_SUMMARY" : blast.Status
        },
        blast.Nodes.Count,
        recordedAt);

    private static void EnsureAuthority(StateSnapshot? snapshot)
    {
        if (snapshot is not null && !string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException($"{snapshot.Key.Kind} has unknown authority");
    }

    private static void Validate(ChangePreflightRequest request)
    {
        ChangeStateCodec.Token(request.OperationId, nameof(request.OperationId), 120);
        if (request.MaxNodes is < 1 or > 4096 || request.MaxDepth is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(request), "Change blast traversal exceeds bounded limits");
    }

    private static void ValidateContract(ChangePreflightRequest request, ChangeContractDefinition contract)
    {
        _ = new StateKey(request.Probe.ProjectId, EntityKinds.ChangeContract, contract.ChangeContractId).Normalize();
        if (contract.ChangeId != request.Probe.ChangeId || contract.TaskId != request.Probe.TaskId || contract.GoalRevision < 1 ||
            contract.SourceFingerprint != request.Probe.SourceFingerprint || contract.Status != "ACTIVE")
            throw new ArgumentException("Change Contract does not bind the probed Change, Task, Goal revision, source fingerprint, and active status", nameof(request));
        ChangeStateCodec.Token(contract.ProblemCode, nameof(contract.ProblemCode));
        ChangeStateCodec.Token(contract.IntendedInvariant, nameof(contract.IntendedInvariant), 512);
        _ = ChangeStateCodec.Tokens(contract.AllowedAffectedScopes, nameof(contract.AllowedAffectedScopes), 128);
        _ = ChangeStateCodec.Tokens(contract.ExpectedBehaviorChanges, nameof(contract.ExpectedBehaviorChanges), 128);
        _ = ChangeStateCodec.Tokens(contract.ExpectedUnaffectedBehaviors, nameof(contract.ExpectedUnaffectedBehaviors), 128);
        _ = ChangeStateCodec.Tokens(contract.ForbiddenSideEffects, nameof(contract.ForbiddenSideEffects), 128);
        _ = ChangeStateCodec.Tokens(contract.AcceptanceCriteria, nameof(contract.AcceptanceCriteria), 128);
        ChangeStateCodec.Token(contract.RollbackBoundary, nameof(contract.RollbackBoundary), 512);
    }

    private static string RequestFingerprint(ChangePreflightRequest request, ChangeContractDefinition contract) => ChangeStateCodec.Hash(ChangeStateCodec.Json(new
    {
        request.Probe,
        contract,
        request.MaxNodes,
        request.MaxDepth
    }));

}

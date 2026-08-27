using System.Diagnostics;
using Hiker.Pro.Environment;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public sealed class ChangeRiskProbe(StateCoordinator state)
{
    private const int EdgeBudgetPerSeed = 65;

    public async Task<ChangeRiskProbeResult> ProbeAsync(
        ChangeProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var timer = Stopwatch.StartNew();
        var incoming = new List<DependencyEdge>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var budgetExhausted = false;
        foreach (var seed in request.Seeds)
        {
            var edges = await state.QueryDependenciesBoundedAsync(
                request.ProjectId,
                seed.Key.Kind,
                seed.Key.EntityId,
                outgoing: false,
                EdgeBudgetPerSeed,
                cancellationToken).ConfigureAwait(false);
            if (edges.Count == EdgeBudgetPerSeed)
            {
                budgetExhausted = true;
                reasons.Add("DIRECT_CONSUMER_INDEX_BUDGET_EXHAUSTED");
            }
            incoming.AddRange(edges.Take(EdgeBudgetPerSeed - 1));
        }

        var loop = await ReadLoopAsync(request, cancellationToken).ConfigureAwait(false);
        var direct = incoming.Select(static edge => $"{edge.FromKind}|{edge.FromId}").Distinct(StringComparer.Ordinal).Count();
        var contracts = incoming.Where(static edge => edge.Relation is "CONSUMES" or "IMPLEMENTS" or "OWNS_CONTRACT")
            .Select(static edge => $"{edge.FromKind}|{edge.FromId}").Distinct(StringComparer.Ordinal).Count();
        var services = incoming.Where(static edge => edge.FromKind == EntityKinds.Service).Select(static edge => edge.FromId).Distinct(StringComparer.Ordinal).Count();
        var evidence = incoming.Where(static edge => edge.FromKind is EntityKinds.Evidence or EntityKinds.Test).Select(static edge => edge.FromId).Distinct(StringComparer.Ordinal).Count();
        var runtime = incoming.Where(static edge => edge.FromKind is EntityKinds.ServiceProcess or EntityKinds.EnvironmentInstance or EntityKinds.RuntimeProfile)
            .Select(static edge => $"{edge.FromKind}|{edge.FromId}").Distinct(StringComparer.Ordinal).Count();
        var resource = incoming.Where(static edge => edge.FromKind is EntityKinds.ResourceLease or EntityKinds.Database || edge.Relation is "HOLDS_RESOURCE" or "ALLOCATES_RESOURCE")
            .Select(static edge => $"{edge.FromKind}|{edge.FromId}").Distinct(StringComparer.Ordinal).Count();
        var coChange = incoming.Count(static edge => edge.Relation.Equals("CO_CHANGED_WITH", StringComparison.OrdinalIgnoreCase));
        var centrality = new ChangeCentrality(direct, 0, contracts, services, evidence, request.Signals.CriticalPath ? 1 : 0, runtime, resource, coChange);

        var patchingAllowed = loop?.Status is not (ChangeControlStatuses.FixLoopConfirmed or ChangeControlStatuses.PatchingDisabled);
        var classification = Classify(request, centrality, loop, budgetExhausted, reasons);
        if (classification == ChangeRiskClasses.Local) reasons.Add("NO_SHARED_OR_PROPAGATING_SIGNAL");
        timer.Stop();
        return new ChangeRiskProbeResult(
            classification,
            classification != ChangeRiskClasses.Local || !patchingAllowed,
            patchingAllowed,
            reasons.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            centrality,
            request.Seeds.Count,
            incoming.Count,
            timer.Elapsed.TotalMilliseconds,
            classification == ChangeRiskClasses.Local ? ChangeControlStatuses.FastPath : ChangeControlStatuses.AnalysisRequired);
    }

    private async Task<ChangeLoopState?> ReadLoopAsync(ChangeProbeRequest request, CancellationToken cancellationToken)
    {
        var snapshot = await state.ReadAsync(new StateKey(request.ProjectId, EntityKinds.ChangeLoop, request.ChangeId), cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        if (!string.Equals(snapshot.Authority, ChangeStateCodec.Authority, StringComparison.Ordinal))
            throw new InvalidDataException("Change loop state has unknown authority");
        return ChangeStateCodec.Parse<ChangeLoopState>(snapshot);
    }

    private static string Classify(
        ChangeProbeRequest request,
        ChangeCentrality centrality,
        ChangeLoopState? loop,
        bool budgetExhausted,
        HashSet<string> reasons)
    {
        var signals = request.Signals;
        var resources = request.ResourceRequirements ?? Array.Empty<ResourceRequirement>();
        var exclusiveDestructive = resources.Any(static requirement => requirement.Destructive && requirement.SharingMode == ResourceSharingMode.Exclusive);
        if (loop?.Status is ChangeControlStatuses.FixLoopConfirmed or ChangeControlStatuses.PatchingDisabled)
        {
            reasons.Add("FIX_LOOP_CONFIRMED");
            return ChangeRiskClasses.Systemic;
        }
        if (budgetExhausted || signals.Irreversible && (signals.DatabaseSchema || signals.SecurityBoundary || signals.DeliveryCritical) ||
            signals.CriticalPath && signals.Uncertainty is "HIGH" or "UNKNOWN")
        {
            reasons.Add(budgetExhausted ? "UNCERTAIN_HIGH_FANOUT" : "IRREVERSIBLE_OR_UNCERTAIN_CRITICAL_CHANGE");
            return ChangeRiskClasses.Systemic;
        }
        if (signals.DatabaseSchema || signals.CrossServiceBoundary || signals.SecurityBoundary || signals.TemporalCompatibilityWindow ||
            signals.CriticalPath || signals.RecentFailedAttempts >= 3 || exclusiveDestructive || centrality.ContractConsumerCount >= 2 || centrality.ServiceFanout >= 2)
        {
            if (signals.DatabaseSchema) reasons.Add("DATABASE_SCHEMA_CHANGE");
            if (signals.TemporalCompatibilityWindow) reasons.Add("TEMPORAL_COMPATIBILITY_WINDOW");
            if (centrality.ContractConsumerCount >= 2) reasons.Add("SHARED_CONTRACT_FANOUT");
            if (exclusiveDestructive) reasons.Add("DESTRUCTIVE_EXCLUSIVE_RESOURCE");
            return ChangeRiskClasses.CrossCutting;
        }
        if (signals.SharedState || centrality.DirectConsumerCount > 0 || centrality.EvidenceFanout > 1 || centrality.ResourceFanout > 0 || resources.Count > 0)
        {
            reasons.Add(signals.SharedState ? "SHARED_STATE" : "SHARED_DEPENDENCY_OR_RESOURCE");
            return ChangeRiskClasses.Shared;
        }
        return ChangeRiskClasses.Local;
    }

    private static void Validate(ChangeProbeRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.ChangeImpact, request.ChangeId).Normalize();
        ChangeStateCodec.Token(request.SourceFingerprint, nameof(request.SourceFingerprint));
        if (request.Seeds.Count is < 1 or > 16 || request.Signals.RecentFailedAttempts is < 0 or > 1000 ||
            request.Signals.FallbackAdditions is < 0 or > 1000 || request.Signals.SpecialCaseAdditions is < 0 or > 1000 ||
            (request.ResourceRequirements?.Count ?? 0) > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Change probe inputs exceed hot-path budgets");
        }
        foreach (var seed in request.Seeds)
        {
            if (seed.Key.Normalize().ProjectId != request.ProjectId) throw new ArgumentException("Change seed belongs to another project", nameof(request));
            ChangeStateCodec.Token(seed.SourceFingerprint, nameof(seed.SourceFingerprint));
        }
        if (request.Signals.Uncertainty is not ("LOW" or "MEDIUM" or "HIGH" or "UNKNOWN"))
            throw new ArgumentException("Change uncertainty must be LOW, MEDIUM, HIGH, or UNKNOWN", nameof(request));
        _ = ChangeStateCodec.Tokens(request.Signals.DeclaredDimensions ?? Array.Empty<string>(), nameof(request.Signals.DeclaredDimensions), 32);
        foreach (var requirement in request.ResourceRequirements ?? Array.Empty<ResourceRequirement>())
            EnvironmentContractValidation.Requirement(requirement);
    }
}

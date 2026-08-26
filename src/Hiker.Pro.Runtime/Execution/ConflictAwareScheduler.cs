using Hiker.Pro.State;

namespace Hiker.Pro.Execution;

public enum EngineeringRisk
{
    Low,
    Medium,
    High,
    Critical
}

public enum AgentLaneRole
{
    Writer,
    Assurance
}

public sealed record SchedulingCandidate(
    string TaskId,
    string LaneId,
    AgentLaneRole Role,
    EngineeringRisk Risk,
    IReadOnlyList<string> PredictedAffectedScopes,
    string? AssuresTaskId = null);

public sealed record SchedulingDisposition(
    string TaskId,
    string LaneId,
    string Status,
    string ReasonCode,
    IReadOnlyList<string> NormalizedScopes);

public sealed record SchedulingPlan(
    string Status,
    int WriterCapacity,
    int ActiveWriters,
    IReadOnlyList<SchedulingDisposition> Dispositions);

public sealed class ConflictAwareScheduler(StateCoordinator state)
{
    public async Task<SchedulingPlan> EvaluateAsync(
        string projectId,
        IReadOnlyList<SchedulingCandidate> candidates,
        int maximumWriterLanes = 3,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(projectId, EntityKinds.Project, "project").Normalize();
        if (candidates.Count is < 1 or > 64 || maximumWriterLanes is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(candidates), "Scheduling batch or writer limit is invalid");
        }

        if (candidates.Select(static candidate => candidate.LaneId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            throw new ArgumentException("Each scheduling lane must be unique", nameof(candidates));
        }

        var writers = candidates.Where(static candidate => candidate.Role == AgentLaneRole.Writer).ToArray();
        if (writers.Select(static candidate => candidate.TaskId).Distinct(StringComparer.Ordinal).Count() != writers.Length)
        {
            throw new ArgumentException("Each writer task may have only one active scheduling candidate", nameof(candidates));
        }

        var activeStates = await state.ReadAsync(projectId, EntityKinds.Worktree, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        var active = activeStates.Select(RuntimeStateCodec.Parse<RuntimeWorktreeState>)
            .Where(static worktree => worktree.Status == "ACTIVE")
            .ToArray();
        var highRiskPresent = candidates.Any(static candidate => candidate.Role == AgentLaneRole.Writer && candidate.Risk >= EngineeringRisk.High);
        var writerCapacity = highRiskPresent ? 1 : maximumWriterLanes;
        var occupiedScopes = active.Select(static worktree => worktree.PredictedAffectedScopes).ToList();
        var admittedWriters = 0;
        var dispositions = new List<SchedulingDisposition>();

        foreach (var candidate in candidates.OrderBy(static item => item.Role).ThenByDescending(static item => item.Risk).ThenBy(static item => item.TaskId, StringComparer.Ordinal))
        {
            Validate(candidate);
            var scopes = AffectedScopePolicy.Normalize(candidate.PredictedAffectedScopes);
            if (candidate.Role == AgentLaneRole.Assurance)
            {
                var target = candidates.SingleOrDefault(item => item.Role == AgentLaneRole.Writer && item.TaskId == candidate.AssuresTaskId);
                var separated = target is not null && target.LaneId != candidate.LaneId && target.TaskId != candidate.TaskId;
                dispositions.Add(new SchedulingDisposition(candidate.TaskId, candidate.LaneId, separated ? "ADMITTED" : "DEFERRED", separated ? "INDEPENDENT_ASSURANCE" : "ASSURANCE_NOT_SEPARATE", scopes));
                continue;
            }

            var assuranceRequired = candidate.Risk >= EngineeringRisk.High;
            var assuranceAvailable = !assuranceRequired || candidates.Any(item =>
                item.Role == AgentLaneRole.Assurance && item.AssuresTaskId == candidate.TaskId && item.LaneId != candidate.LaneId && item.TaskId != candidate.TaskId);
            if (!assuranceAvailable)
            {
                dispositions.Add(new SchedulingDisposition(candidate.TaskId, candidate.LaneId, "DEFERRED", "INDEPENDENT_ASSURANCE_REQUIRED", scopes));
                continue;
            }

            if (active.Length + admittedWriters >= writerCapacity)
            {
                dispositions.Add(new SchedulingDisposition(candidate.TaskId, candidate.LaneId, "DEFERRED", "WRITER_CAPACITY", scopes));
                continue;
            }

            if (occupiedScopes.Any(existing => AffectedScopePolicy.Overlap(existing, scopes)))
            {
                dispositions.Add(new SchedulingDisposition(candidate.TaskId, candidate.LaneId, "DEFERRED", "AFFECTED_SCOPE_CONFLICT", scopes));
                continue;
            }

            admittedWriters++;
            occupiedScopes.Add(scopes);
            dispositions.Add(new SchedulingDisposition(candidate.TaskId, candidate.LaneId, "ADMITTED", assuranceRequired ? "HIGH_RISK_WITH_ASSURANCE" : "DISJOINT_WRITER", scopes));
        }

        var admitted = dispositions.Count(static item => item.Status == "ADMITTED");
        return new SchedulingPlan(admitted == 0 ? "DRAINING" : "ADMITTED", writerCapacity, active.Length, ContractCollections.Freeze(dispositions));
    }

    private static void Validate(SchedulingCandidate candidate)
    {
        _ = new StateKey("project", EntityKinds.Task, candidate.TaskId).Normalize();
        if (string.IsNullOrWhiteSpace(candidate.LaneId) || candidate.LaneId.Length > 160 ||
            (candidate.Role == AgentLaneRole.Assurance && string.IsNullOrWhiteSpace(candidate.AssuresTaskId)) ||
            (candidate.Role == AgentLaneRole.Writer && candidate.AssuresTaskId is not null))
        {
            throw new ArgumentException("Scheduling candidate is invalid", nameof(candidate));
        }
    }
}

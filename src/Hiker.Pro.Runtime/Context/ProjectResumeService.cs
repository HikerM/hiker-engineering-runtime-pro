using System.Text;
using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public sealed class ProjectResumeService(IStateStore stateStore)
{
    public async Task<ProjectResumeSummary> ResumeAsync(
        string projectId,
        string goalId,
        int maxBytes = 48_000,
        CancellationToken cancellationToken = default)
    {
        var currentGoal = await stateStore.ReadAsync(new StateKey(projectId, EntityKinds.Goal, goalId), cancellationToken).ConfigureAwait(false);
        var tasks = await stateStore.ReadAsync(projectId, EntityKinds.Task, StateLifecycle.Valid, 100, cancellationToken).ConfigureAwait(false);
        var decisions = await stateStore.ReadAsync(projectId, EntityKinds.Decision, StateLifecycle.Valid, 20, cancellationToken).ConfigureAwait(false);
        var evidence = await stateStore.ReadAsync(projectId, EntityKinds.Evidence, lifecycle: null, limit: 100, cancellationToken).ConfigureAwait(false);
        var worktrees = (await stateStore.ReadAsync(projectId, EntityKinds.Worktree, StateLifecycle.Valid, 32, cancellationToken).ConfigureAwait(false))
            .Where(static state => !RuntimeStateCodec.IsLegacyImportedFact(state))
            .ToArray();
        var leases = await stateStore.ReadAsync(projectId, EntityKinds.RuntimeLease, StateLifecycle.Valid, 32, cancellationToken).ConfigureAwait(false);
        var open = tasks.Where(static task => !PayloadStatus(task.PayloadJson, "COMPLETED", "CANCELLED", "FAILED")).Take(32).Select(ToHot).ToArray();
        var blocked = tasks.Where(static task => PayloadStatus(task.PayloadJson, "BLOCKED", "INTERRUPTED")).Take(32).Select(ToHot).ToArray();
        var validEvidence = evidence.Where(static item => item.Lifecycle == StateLifecycle.Valid).Take(32).Select(ToWarm).ToArray();
        var staleEvidence = evidence.Where(static item => item.Lifecycle == StateLifecycle.Stale).Take(32).Select(ToWarm).ToArray();
        var summary = new ProjectResumeSummary(
            projectId,
            currentGoal is null ? null : ToHot(currentGoal),
            open,
            blocked,
            decisions.Take(20).Select(ToWarm).ToArray(),
            validEvidence,
            staleEvidence,
            worktrees.Take(16).Select(ToHot).ToArray(),
            leases.Take(16).Select(ToHot).ToArray(),
            blocked.Length > 0 ? "REVIEW_BLOCKED_TASK" : open.Length > 0 ? "CONTINUE_HIGHEST_PRIORITY_OPEN_TASK" : "PLAN_NEXT_GOAL_SLICE",
            0,
            false);
        var encoded = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(summary));
        if (encoded <= maxBytes)
        {
            return summary with { EncodedBytes = encoded };
        }

        var bounded = summary with
        {
            OpenTasks = summary.OpenTasks.Take(8).ToArray(),
            BlockedTasks = summary.BlockedTasks.Take(8).ToArray(),
            LatestDecisions = summary.LatestDecisions.Take(8).ToArray(),
            ValidEvidence = summary.ValidEvidence.Take(8).ToArray(),
            StaleEvidence = summary.StaleEvidence.Take(8).ToArray(),
            ActiveWorktrees = summary.ActiveWorktrees.Take(8).ToArray(),
            RuntimeLeases = summary.RuntimeLeases.Take(8).ToArray(),
            Truncated = true
        };
        var boundedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(bounded));
        if (boundedBytes <= maxBytes) return bounded with { EncodedBytes = boundedBytes };
        var minimal = bounded with
        {
            OpenTasks = Array.Empty<ContextItem>(),
            BlockedTasks = Array.Empty<ContextItem>(),
            LatestDecisions = Array.Empty<ContextItem>(),
            ValidEvidence = Array.Empty<ContextItem>(),
            StaleEvidence = Array.Empty<ContextItem>(),
            ActiveWorktrees = Array.Empty<ContextItem>(),
            RuntimeLeases = Array.Empty<ContextItem>(),
            RecommendedNextAction = "CONTEXT_BUDGET_REACHED_READ_EXPLICIT_REFERENCES_ONLY",
            Truncated = true
        };
        var minimalBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(minimal));
        if (minimalBytes > maxBytes) minimal = minimal with { CurrentGoal = null };
        return minimal with { EncodedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(minimal)) };
    }

    private static bool PayloadStatus(string payload, params string[] values)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("status", out var status) &&
                values.Contains(status.GetString(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ContextItem ToHot(StateSnapshot state) => ContextResolver.ToItem(state, ContextTemperature.Hot, 2048);
    private static ContextItem ToWarm(StateSnapshot state) => ContextResolver.ToItem(state, ContextTemperature.Warm, 1536);
}

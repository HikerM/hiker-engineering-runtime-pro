using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

internal sealed class LiveAdoptionStateResolver(IStateStore store)
{
    public async Task<LiveStateResolution> ResolveGoalAsync(
        string projectId,
        string? requestedGoalId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedGoalId))
        {
            var exact = await store.ReadAsync(new StateKey(projectId, EntityKinds.Goal, requestedGoalId), cancellationToken).ConfigureAwait(false);
            return exact is { Lifecycle: StateLifecycle.Valid }
                ? new LiveStateResolution("RESOLVED", exact, 1, Array.Empty<string>())
                : new LiveStateResolution(LiveAdoptionStatuses.GoalRequired, null, 1, new[] { "REQUESTED_GOAL_NOT_ACTIVE" });
        }

        var goals = await store.ReadAsync(projectId, EntityKinds.Goal, StateLifecycle.Valid, 4, cancellationToken).ConfigureAwait(false);
        var current = goals.Where(IsCurrentGoal).ToArray();
        var candidates = current.Length > 0 ? current : goals.ToArray();
        return candidates.Length switch
        {
            1 => new LiveStateResolution("RESOLVED", candidates[0], 1, Array.Empty<string>()),
            0 => new LiveStateResolution(LiveAdoptionStatuses.GoalRequired, null, 1, new[] { "NO_PROVABLE_CURRENT_GOAL" }),
            _ => new LiveStateResolution(
                LiveAdoptionStatuses.AmbiguousGoal,
                null,
                1,
                candidates.Take(4).Select(static item => item.Key.EntityId).ToArray())
        };
    }

    public async Task<LiveStateResolution> ResolveTaskAsync(
        string projectId,
        StateSnapshot goal,
        string? requestedTaskId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedTaskId))
        {
            var exact = await store.ReadAsync(new StateKey(projectId, EntityKinds.Task, requestedTaskId), cancellationToken).ConfigureAwait(false);
            return exact is not null && IsCurrentTask(exact, goal.Key.EntityId)
                ? new LiveStateResolution("RESOLVED", exact, 1, Array.Empty<string>())
                : new LiveStateResolution(LiveAdoptionStatuses.TaskRequired, null, 1, new[] { "REQUESTED_TASK_NOT_CURRENT" });
        }

        var tasks = await store.ReadAsync(projectId, EntityKinds.Task, lifecycle: null, limit: 32, cancellationToken).ConfigureAwait(false);
        var candidates = tasks.Where(task => IsCurrentTask(task, goal.Key.EntityId)).Take(4).ToArray();
        return candidates.Length switch
        {
            1 => new LiveStateResolution("RESOLVED", candidates[0], 1, Array.Empty<string>()),
            0 => new LiveStateResolution(LiveAdoptionStatuses.TaskRequired, null, 1, new[] { "NO_PROVABLE_CURRENT_TASK" }),
            _ => new LiveStateResolution(
                LiveAdoptionStatuses.AmbiguousTask,
                null,
                1,
                candidates.Select(static item => item.Key.EntityId).ToArray())
        };
    }

    public async Task<LiveCheckpointResolution> ResolveCheckpointAsync(
        string projectId,
        string taskId,
        CancellationToken cancellationToken)
    {
        var checkpoints = await store.ReadAsync(projectId, EntityKinds.Checkpoint, lifecycle: null, limit: 32, cancellationToken).ConfigureAwait(false);
        var selected = checkpoints
            .Where(checkpoint => CheckpointBelongsTo(checkpoint, taskId))
            .OrderByDescending(static checkpoint => checkpoint.UpdatedAt)
            .FirstOrDefault();
        return new LiveCheckpointResolution(selected, 1);
    }

    private static bool IsCurrentGoal(StateSnapshot goal)
    {
        if (!LiveAdoptionStateCodec.TryPayload(goal.PayloadJson, out var payload)) return false;
        return !payload.TryGetProperty("status", out var status) ||
               status.ValueKind != JsonValueKind.String ||
               status.GetString() is "CURRENT" or "ACTIVE" or "IN_PROGRESS";
    }

    private static bool IsCurrentTask(StateSnapshot task, string goalId)
    {
        if (task.Lifecycle is StateLifecycle.Corrupt or StateLifecycle.Incompatible or StateLifecycle.Orphaned or StateLifecycle.Superseded)
        {
            return false;
        }
        if (!LiveAdoptionStateCodec.TryPayload(task.PayloadJson, out var payload)) return false;
        if (!payload.TryGetProperty("task_id", out var taskId) || taskId.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        if (payload.TryGetProperty("goal_id", out var boundGoal) &&
            boundGoal.ValueKind == JsonValueKind.String &&
            !string.Equals(boundGoal.GetString(), goalId, StringComparison.Ordinal))
        {
            return false;
        }
        if (!payload.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
        {
            return true;
        }
        return status.GetString()?.ToUpperInvariant() is not
            ("COMPLETED" or "COMPLETE" or "CANCELLED" or "FAILED" or "SUPERSEDED" or "ARCHIVED");
    }

    private static bool CheckpointBelongsTo(StateSnapshot checkpoint, string taskId)
    {
        if (!LiveAdoptionStateCodec.TryPayload(checkpoint.PayloadJson, out var payload)) return false;
        return payload.TryGetProperty("task_id", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               string.Equals(value.GetString(), taskId, StringComparison.Ordinal);
    }
}

internal sealed record LiveStateResolution(
    string Status,
    StateSnapshot? State,
    int Reads,
    IReadOnlyList<string> Reasons);

internal sealed record LiveCheckpointResolution(StateSnapshot? State, int Reads);

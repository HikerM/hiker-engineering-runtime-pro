using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public sealed class LiveTurnLifecycleService(
    IStateStore store,
    StateCoordinator coordinator,
    ProjectBootstrapService bootstrap,
    TimeProvider? timeProvider = null)
{
    private static readonly IReadOnlyList<string> AcceptedTerminalOutcomes =
        new[] { "TURN_TERMINAL", "NATURAL_CHECKPOINT", "INTERRUPTED_UNKNOWN" };
    private static readonly IReadOnlySet<string> TerminalOutcomes = new HashSet<string>(
        AcceptedTerminalOutcomes,
        StringComparer.Ordinal);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<LiveTurnCheckpointResult> CheckpointAsync(
        LiveTurnCheckpointRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var provider = request.Provider.Trim().ToLowerInvariant();
        var sessionFingerprint = Hash($"{provider}\0{request.ProviderSessionId.Trim()}");
        var bindingId = $"{provider}-{sessionFingerprint[..24]}";
        var project = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, cancellationToken: cancellationToken).ConfigureAwait(false);
        var reads = 1;
        if (!project.CanProceed || project.ProjectId is null)
        {
            return Result(
                LiveTurnCheckpointStatuses.Blocked,
                project.ProjectId,
                null,
                null,
                null,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                null,
                new[] { project.ReasonCode });
        }

        var projectId = project.ProjectId;
        var bindingKey = new StateKey(projectId, EntityKinds.ProviderSessionBinding, bindingId);
        var binding = await store.ReadAsync(bindingKey, cancellationToken).ConfigureAwait(false);
        reads++;
        if (binding is null)
        {
            return Result(
                LiveTurnCheckpointStatuses.NotAdopted,
                projectId,
                null,
                null,
                null,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                null,
                new[] { "NO_PROVIDER_SESSION_BINDING" });
        }

        if (!TryBinding(binding, out var bound) ||
            !string.Equals(bound.ProviderSessionFingerprint, sessionFingerprint, StringComparison.Ordinal))
        {
            return Result(
                LiveTurnCheckpointStatuses.Blocked,
                projectId,
                null,
                null,
                null,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                null,
                new[] { "PROVIDER_SESSION_BINDING_INVALID" });
        }

        if (binding.Lifecycle == StateLifecycle.Archived ||
            string.Equals(bound.Status, "CHECKPOINTED", StringComparison.Ordinal))
        {
            return Result(
                LiveTurnCheckpointStatuses.AlreadyCheckpointed,
                projectId,
                bound.GoalId,
                bound.TaskId,
                bound.CheckpointId,
                sessionFingerprint,
                false,
                true,
                true,
                reads,
                0,
                null,
                Array.Empty<string>());
        }

        if (binding.Lifecycle != StateLifecycle.Valid ||
            !string.Equals(bound.Status, "ACTIVE", StringComparison.Ordinal))
        {
            return Result(
                LiveTurnCheckpointStatuses.Blocked,
                projectId,
                bound.GoalId,
                bound.TaskId,
                bound.CheckpointId,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                null,
                new[] { "PROVIDER_SESSION_NOT_ACTIVE" });
        }

        var goalKey = new StateKey(projectId, EntityKinds.Goal, bound.GoalId);
        var taskKey = new StateKey(projectId, EntityKinds.Task, bound.TaskId);
        var ownershipKey = new StateKey(projectId, EntityKinds.Ownership, bound.TaskId);
        var goal = await store.ReadAsync(goalKey, cancellationToken).ConfigureAwait(false);
        var task = await store.ReadAsync(taskKey, cancellationToken).ConfigureAwait(false);
        var ownership = await store.ReadAsync(ownershipKey, cancellationToken).ConfigureAwait(false);
        reads += 3;
        if (goal is not { Lifecycle: StateLifecycle.Valid } ||
            task is not { Lifecycle: StateLifecycle.Valid } ||
            ownership is not { Lifecycle: StateLifecycle.Valid } ||
            !OwnershipMatches(ownership, bound))
        {
            return Result(
                LiveTurnCheckpointStatuses.Blocked,
                projectId,
                bound.GoalId,
                bound.TaskId,
                bound.CheckpointId,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                null,
                new[] { "ADOPTION_RUNTIME_STATE_INCONSISTENT" });
        }

        var context = await new ContextResolver(store, _clock).ResolveAsync(
            new ContextRequest(projectId, bound.TaskId, bound.GoalId, request.MaxContextBytes, 32),
            cancellationToken).ConfigureAwait(false);
        reads += context.StateReads;
        var outcome = request.Outcome.Trim().ToUpperInvariant();
        var changedSurfaces = BoundRefs(request.ChangedSurfaces);
        var evidenceRefs = BoundRefs(request.EvidenceRefs);
        var testRefs = BoundRefs(request.TestRefs);
        var checkpointId = $"turn-{Hash($"{bindingId}\0{bound.CheckpointId}\0{outcome}")[..24]}";
        var checkpointKey = new StateKey(projectId, EntityKinds.Checkpoint, checkpointId);
        var evidenceKey = new StateKey(projectId, EntityKinds.Evidence, $"turn-{Hash($"{bindingId}\0{bound.CheckpointId}")[..24]}");
        var currentCheckpoint = await store.ReadAsync(checkpointKey, cancellationToken).ConfigureAwait(false);
        var currentEvidence = await store.ReadAsync(evidenceKey, cancellationToken).ConfigureAwait(false);
        var existingContextEdges = await store.QueryDependenciesBoundedAsync(
            projectId,
            EntityKinds.Task,
            bound.TaskId,
            outgoing: true,
            limit: 257,
            cancellationToken).ConfigureAwait(false);
        reads += 3;
        var now = _clock.GetUtcNow();
        var sourceFingerprint = Hash(string.Join('\n',
            projectId,
            bound.GoalId,
            bound.TaskId,
            bound.CheckpointId,
            outcome,
            context.Fingerprint,
            string.Join('|', changedSurfaces),
            string.Join('|', evidenceRefs),
            string.Join('|', testRefs)));
        if (!TryTask(task, out var adoptedTask))
        {
            return Result(
                LiveTurnCheckpointStatuses.Blocked,
                projectId,
                bound.GoalId,
                bound.TaskId,
                bound.CheckpointId,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                context,
                new[] { "ADOPTED_TASK_PAYLOAD_INVALID" });
        }

        var mutations = new[]
        {
            Mutation(taskKey, task, task.GoalRevision, sourceFingerprint, adoptedTask with
            {
                Status = outcome == "INTERRUPTED_UNKNOWN" ? "INTERRUPTED_UNKNOWN" : "CHECKPOINTED",
                CheckpointId = checkpointId,
                UpdatedAt = now
            }),
            Mutation(bindingKey, binding, task.GoalRevision, sourceFingerprint, bound with
            {
                CheckpointId = checkpointId,
                Status = "CHECKPOINTED"
            }) with { Lifecycle = StateLifecycle.Archived, Retention = RetentionTier.Warm },
            Mutation(
                checkpointKey,
                currentCheckpoint,
                task.GoalRevision,
                sourceFingerprint,
                new LiveTurnCheckpointState(
                    "hiker-live-turn-checkpoint/v1",
                    checkpointId,
                    bound.TaskId,
                    bound.GoalId,
                    bindingId,
                    bound.CheckpointId,
                    outcome,
                    changedSurfaces,
                    evidenceRefs,
                    testRefs,
                    context.Fingerprint,
                    now,
                    "VALID"),
                RetentionTier.Warm),
            Mutation(
                evidenceKey,
                currentEvidence,
                task.GoalRevision,
                sourceFingerprint,
                new
                {
                    schema_version = "hiker-live-turn-evidence/v1",
                    evidence_id = evidenceKey.EntityId,
                    project_id = projectId,
                    goal_id = bound.GoalId,
                    task_id = bound.TaskId,
                    checkpoint_id = checkpointId,
                    provider,
                    provider_session_fingerprint = sessionFingerprint,
                    context_fingerprint = context.Fingerprint,
                    outcome,
                    changed_surfaces = changedSurfaces,
                    evidence_refs = evidenceRefs,
                    test_refs = testRefs,
                    status = "PASS",
                    observed_at = now
                },
                RetentionTier.Warm),
            Mutation(ownershipKey, ownership, task.GoalRevision, sourceFingerprint, SourcePayload(ownership.PayloadJson)) with
            {
                Lifecycle = StateLifecycle.Archived,
                Retention = RetentionTier.Warm
            }
        };
        var dependencyChanges = LiveAdoptionStateCodec.ReplaceLiveContextEdges(
            existingContextEdges,
            new DependencyEdge(projectId, EntityKinds.Task, bound.TaskId, EntityKinds.Checkpoint, checkpointId, "CHECKPOINTED_AT", sourceFingerprint),
            new DependencyEdge(projectId, EntityKinds.Task, bound.TaskId, EntityKinds.Evidence, evidenceKey.EntityId, "PROVEN_BY", sourceFingerprint));
        var operationId = $"live-turn-checkpoint-{Hash($"{bindingId}\0{bound.CheckpointId}")[..32]}";
        var commit = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "LIVE_TURN_CHECKPOINTED",
            "PROVIDER_SESSION_RELEASED",
            JsonSerializer.Serialize(new
            {
                project_id = projectId,
                goal_id = bound.GoalId,
                task_id = bound.TaskId,
                checkpoint_id = checkpointId,
                provider,
                provider_session_fingerprint = sessionFingerprint,
                context_fingerprint = context.Fingerprint,
                outcome
            }, JsonOptions()),
            dependencyChanges,
            JsonSerializer.Serialize(new { checkpoint_id = checkpointId, ownership_released = true }, JsonOptions())),
            cancellationToken).ConfigureAwait(false);
        return commit.Committed
            ? Result(
                commit.Status == "IDEMPOTENT_REPLAY" ? LiveTurnCheckpointStatuses.AlreadyCheckpointed : LiveTurnCheckpointStatuses.Checkpointed,
                projectId,
                bound.GoalId,
                bound.TaskId,
                checkpointId,
                sessionFingerprint,
                commit.Status != "IDEMPOTENT_REPLAY",
                commit.Status == "IDEMPOTENT_REPLAY",
                true,
                reads,
                commit.Status == "IDEMPOTENT_REPLAY" ? 0 : mutations.Length,
                context,
                Array.Empty<string>())
            : Result(
                LiveTurnCheckpointStatuses.Blocked,
                projectId,
                bound.GoalId,
                bound.TaskId,
                checkpointId,
                sessionFingerprint,
                false,
                false,
                false,
                reads,
                0,
                context,
                new[] { commit.Status });
    }

    private static bool OwnershipMatches(StateSnapshot ownership, ProviderSessionBindingState binding)
    {
        if (!TryPayload(ownership.PayloadJson, out var payload) ||
            !payload.TryGetProperty("task_id", out var task) ||
            !payload.TryGetProperty("writer_runtime_id", out var writer))
        {
            return false;
        }

        return string.Equals(task.GetString(), binding.TaskId, StringComparison.Ordinal) &&
               string.Equals(writer.GetString(), binding.ExecutionId, StringComparison.Ordinal);
    }

    private static bool TryBinding(StateSnapshot snapshot, out ProviderSessionBindingState binding)
    {
        try
        {
            binding = JsonSerializer.Deserialize<ProviderSessionBindingState>(snapshot.PayloadJson, JsonOptions())!;
            return binding is not null;
        }
        catch (JsonException)
        {
            binding = default!;
            return false;
        }
    }

    private static bool TryTask(StateSnapshot snapshot, out AdoptedTaskState task)
    {
        try
        {
            task = JsonSerializer.Deserialize<AdoptedTaskState>(snapshot.PayloadJson, JsonOptions())!;
            return task is not null;
        }
        catch (JsonException)
        {
            task = default!;
            return false;
        }
    }

    private static object SourcePayload(string json) =>
        TryPayload(json, out var payload) ? payload : new { preserved_source_fingerprint_only = true };

    private static bool TryPayload(string json, out JsonElement payload)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            payload = document.RootElement.Clone();
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        string sourceFingerprint,
        object payload,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        StateLifecycle.Valid,
        LiveSessionAdoptionService.Authority,
        "3.0.0",
        sourceFingerprint,
        sourceFingerprint,
        JsonSerializer.Serialize(payload, JsonOptions()),
        retention);

    private static IReadOnlyList<string> BoundRefs(IReadOnlyList<string>? refs)
    {
        if (refs is null || refs.Count == 0) return Array.Empty<string>();
        if (refs.Count > 32 || refs.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 256))
        {
            throw new ArgumentException("Turn evidence references exceed the bounded contract");
        }

        return refs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void Validate(LiveTurnCheckpointRequest request)
    {
        var outcome = request.Outcome.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(request.Provider) || request.Provider.Length > 64)
        {
            throw new LiveTurnCheckpointValidationException(
                "INVALID_CHECKPOINT_PROVIDER",
                "provider",
                "Checkpoint provider must contain 1 to 64 characters");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderSessionId) || request.ProviderSessionId.Length > 512)
        {
            throw new LiveTurnCheckpointValidationException(
                "INVALID_PROVIDER_SESSION_ID",
                "provider_session_id",
                "Checkpoint provider session ID must contain 1 to 512 characters");
        }

        if (!TerminalOutcomes.Contains(outcome))
        {
            throw new LiveTurnCheckpointValidationException(
                "INVALID_CHECKPOINT_OUTCOME",
                "outcome",
                $"Checkpoint outcome must be one of: {string.Join(", ", AcceptedTerminalOutcomes)}",
                AcceptedTerminalOutcomes);
        }

        if (request.MaxContextBytes is < 4096 or > 48_000)
        {
            throw new LiveTurnCheckpointValidationException(
                "INVALID_MAX_CONTEXT_BYTES",
                "max_context_bytes",
                "Checkpoint max context bytes must be between 4096 and 48000");
        }
    }

    private static LiveTurnCheckpointResult Result(
        string status,
        string? projectId,
        string? goalId,
        string? taskId,
        string? checkpointId,
        string sessionFingerprint,
        bool committed,
        bool idempotent,
        bool ownershipReleased,
        int reads,
        int writes,
        VerifiedContextPackage? context,
        IReadOnlyList<string> reasons) => new(
        status,
        projectId,
        goalId,
        taskId,
        checkpointId,
        sessionFingerprint,
        committed,
        idempotent,
        ownershipReleased,
        reads,
        writes,
        context?.EncodedBytes ?? 0,
        context?.Fingerprint,
        reasons);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

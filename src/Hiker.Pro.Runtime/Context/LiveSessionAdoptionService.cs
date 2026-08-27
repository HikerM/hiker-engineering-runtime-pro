using System.Text.Json;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using static Hiker.Pro.Context.LiveAdoptionStateCodec;

namespace Hiker.Pro.Context;

public sealed class LiveSessionAdoptionService(
    ProjectLayout layout,
    IStateStore store,
    StateCoordinator coordinator,
    ProjectBootstrapService bootstrap,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null)
{
    public const string Authority = "LIVE_SESSION_ADOPTION";
    private static readonly IReadOnlySet<string> SafeBoundaryProofs = new HashSet<string>(
        new[] { "TURN_TERMINAL", "NATURAL_CHECKPOINT", "NEW_TASK", "RECOVERY_CHECKPOINT" },
        StringComparer.Ordinal);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<LiveAdoptionResult> AdoptAsync(
        LiveAdoptionRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var provider = request.Provider.Trim().ToLowerInvariant();
        var sessionFingerprint = LiveAdoptionStateCodec.Hash($"{provider}\0{request.ProviderSessionId.Trim()}");
        var initial = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, cancellationToken: cancellationToken).ConfigureAwait(false);
        var reads = 1;
        if (!SafeBoundaryProofs.Contains(request.BoundaryProof.Trim().ToUpperInvariant()))
        {
            return Result(
                LiveAdoptionStatuses.WaitSafeBoundary,
                initial,
                provider,
                sessionFingerprint,
                null,
                null,
                null,
                false,
                false,
                "COMPLETE_CURRENT_TURN_WITHOUT_NEW_DISPATCH",
                reads,
                0,
                null,
                new[] { "SAFE_BOUNDARY_NOT_CONFIRMED" });
        }

        if (initial.State is ProjectBootstrapState.NewProject or ProjectBootstrapState.IdentityMissing &&
            !Community518SourceCatalog.HasAny(layout))
        {
            return Result(
                LiveAdoptionStatuses.GoalRequired,
                initial,
                provider,
                sessionFingerprint,
                null,
                null,
                null,
                false,
                false,
                "CREATE_STRUCTURED_CURRENT_GOAL",
                reads,
                0,
                null,
                new[] { "NO_PROVABLE_COMMUNITY_GOAL" });
        }

        var project = await new LiveAdoptionProjectBootstrap(
            layout,
            store,
            coordinator,
            bootstrap,
            processProbe).EnsureReadyAsync(initial, cancellationToken).ConfigureAwait(false);
        reads++;
        if (!project.CanProceed || project.ProjectId is null)
        {
            return Result(
                LiveAdoptionStatuses.Blocked,
                project,
                provider,
                sessionFingerprint,
                null,
                null,
                null,
                false,
                false,
                "ISOLATE_PROJECT_AUTHORITY_CONFLICT",
                reads,
                0,
                null,
                new[] { project.ReasonCode });
        }

        var projectId = project.ProjectId;
        var community = request.GoalId is null && request.TaskId is null
            ? await new CommunityCurrentWorkInspector(layout, store).InspectAsync(projectId, cancellationToken).ConfigureAwait(false)
            : null;
        if (community is not null) reads += community.Reads;
        var resolver = new LiveAdoptionStateResolver(store);
        var goalResolution = community is null
            ? await resolver.ResolveGoalAsync(projectId, request.GoalId, cancellationToken).ConfigureAwait(false)
            : new LiveStateResolution("RESOLVED", community.Goal, 0, Array.Empty<string>());
        reads += goalResolution.Reads;
        if (goalResolution.State is null)
        {
            return Result(
                goalResolution.Status,
                project,
                provider,
                sessionFingerprint,
                null,
                null,
                null,
                false,
                false,
                goalResolution.Status == LiveAdoptionStatuses.AmbiguousGoal ? "SELECT_UNIQUE_CURRENT_GOAL" : "CREATE_STRUCTURED_CURRENT_GOAL",
                reads,
                0,
                null,
                goalResolution.Reasons);
        }

        var goal = goalResolution.State;
        var taskResolution = community is null
            ? await resolver.ResolveTaskAsync(projectId, goal, request.TaskId, cancellationToken).ConfigureAwait(false)
            : new LiveStateResolution("RESOLVED", community.Task, 0, Array.Empty<string>());
        reads += taskResolution.Reads;
        if (taskResolution.State is null)
        {
            return Result(
                taskResolution.Status,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                null,
                null,
                false,
                false,
                taskResolution.Status == LiveAdoptionStatuses.AmbiguousTask ? "SELECT_UNIQUE_CURRENT_TASK" : "CREATE_STRUCTURED_CURRENT_TASK",
                reads,
                0,
                null,
                taskResolution.Reasons);
        }

        var task = taskResolution.State;
        var checkpoint = await resolver.ResolveCheckpointAsync(projectId, task.Key.EntityId, cancellationToken).ConfigureAwait(false);
        reads += checkpoint.Reads;
        var bindingId = $"{provider}-{sessionFingerprint[..24]}";
        var executionId = $"execution-{LiveAdoptionStateCodec.Hash($"{projectId}\0{task.Key.EntityId}\0{bindingId}")[..24]}";
        var bindingKey = new StateKey(projectId, EntityKinds.ProviderSessionBinding, bindingId);
        var existingBinding = await store.ReadAsync(bindingKey, cancellationToken).ConfigureAwait(false);
        reads++;
        if (existingBinding is { Lifecycle: StateLifecycle.Valid } &&
            LiveAdoptionStateCodec.TryBinding(existingBinding, out var bound) &&
            string.Equals(bound.GoalId, goal.Key.EntityId, StringComparison.Ordinal) &&
            string.Equals(bound.TaskId, task.Key.EntityId, StringComparison.Ordinal))
        {
            var context = await new ContextResolver(store, _clock).ResolveAsync(
                new ContextRequest(projectId, task.Key.EntityId, goal.Key.EntityId, request.MaxContextBytes, 32),
                cancellationToken).ConfigureAwait(false);
            return Result(
                LiveAdoptionStatuses.AlreadyAdopted,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                task.Key.EntityId,
                bound.CheckpointId,
                false,
                true,
                "CONTINUE_BOUND_TASK",
                reads + context.StateReads,
                0,
                context,
                Array.Empty<string>());
        }

        var activeBindings = await store.ReadAsync(
            projectId,
            EntityKinds.ProviderSessionBinding,
            StateLifecycle.Valid,
            4,
            cancellationToken).ConfigureAwait(false);
        reads++;
        var competingBinding = activeBindings.FirstOrDefault(candidate =>
            candidate.Key.EntityId != bindingId &&
            LiveAdoptionStateCodec.TryBinding(candidate, out var active) &&
            string.Equals(active.TaskId, task.Key.EntityId, StringComparison.Ordinal) &&
            string.Equals(active.Status, "ACTIVE", StringComparison.Ordinal));
        ProviderSessionBindingState? interruptedBinding = null;
        if (competingBinding is not null &&
            !string.Equals(request.BoundaryProof.Trim(), "RECOVERY_CHECKPOINT", StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                LiveAdoptionStatuses.WaitSafeBoundary,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                task.Key.EntityId,
                checkpoint.State?.Key.EntityId,
                false,
                false,
                "WAIT_EXISTING_PROVIDER_SESSION_CHECKPOINT",
                reads,
                0,
                null,
                new[] { "ACTIVE_PROVIDER_SESSION_BOUND" });
        }
        if (competingBinding is not null &&
            !LiveAdoptionStateCodec.TryBinding(competingBinding, out interruptedBinding))
        {
            return Result(
                LiveAdoptionStatuses.Blocked,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                task.Key.EntityId,
                checkpoint.State?.Key.EntityId,
                false,
                false,
                "REVIEW_PROVIDER_SESSION_BINDING",
                reads,
                0,
                null,
                new[] { "PROVIDER_SESSION_BINDING_INVALID" });
        }

        var ownershipKey = new StateKey(projectId, EntityKinds.Ownership, task.Key.EntityId);
        var currentOwnership = await store.ReadAsync(ownershipKey, cancellationToken).ConfigureAwait(false);
        reads++;

        var leaseKey = new StateKey(projectId, EntityKinds.RuntimeLease, task.Key.EntityId);
        var leaseState = await store.ReadAsync(leaseKey, cancellationToken).ConfigureAwait(false);
        reads++;
        var recoverableInterruptedLease = false;
        if (leaseState is { Lifecycle: StateLifecycle.Valid } && !RuntimeStateCodec.IsLegacyImportedFact(leaseState))
        {
            var lease = RuntimeStateCodec.Parse<RuntimeLease>(leaseState);
            var live = lease.ProcessIdentity is not null &&
                await processProbe.IsSameLiveProcessAsync(lease.ProcessIdentity, cancellationToken).ConfigureAwait(false);
            if (live || lease.ProcessIdentity is null && lease.ExpiresAt > _clock.GetUtcNow())
            {
                return Result(
                    LiveAdoptionStatuses.WaitSafeBoundary,
                    project,
                    provider,
                    sessionFingerprint,
                    goal.Key.EntityId,
                    task.Key.EntityId,
                    checkpoint.State?.Key.EntityId,
                    false,
                    false,
                    "WAIT_ACTIVE_WRITER_RELEASE",
                    reads,
                    0,
                    null,
                    new[] { live ? "ACTIVE_WRITER_IDENTITY_LIVE" : "ACTIVE_LEASE_NOT_EXPIRED" });
            }

            recoverableInterruptedLease = true;
        }

        if (currentOwnership is { Lifecycle: StateLifecycle.Valid } &&
            !RuntimeStateCodec.IsLegacyImportedFact(currentOwnership) &&
            !recoverableInterruptedLease &&
            (!LiveAdoptionStateCodec.TryPayload(currentOwnership.PayloadJson, out var ownershipPayload) ||
             !ownershipPayload.TryGetProperty("writer_runtime_id", out var writer) ||
             writer.ValueKind != JsonValueKind.String ||
             (!string.Equals(writer.GetString(), executionId, StringComparison.Ordinal) &&
              !string.Equals(writer.GetString(), interruptedBinding?.ExecutionId, StringComparison.Ordinal))))
        {
            return Result(
                LiveAdoptionStatuses.WaitSafeBoundary,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                task.Key.EntityId,
                checkpoint.State?.Key.EntityId,
                false,
                false,
                "WAIT_EXISTING_WRITER_RELEASE",
                reads,
                0,
                null,
                new[] { "ACTIVE_OWNERSHIP_NOT_RELEASED" });
        }

        var now = _clock.GetUtcNow();
        var sourceFingerprint = LiveAdoptionStateCodec.Hash(string.Join('\n',
            projectId,
            goal.SourceFingerprint,
            task.SourceFingerprint,
            checkpoint.State?.SourceFingerprint ?? "no-source-checkpoint"));
        var checkpointId = $"adoption-{sourceFingerprint[..24]}";
        var checkpointKey = new StateKey(projectId, EntityKinds.Checkpoint, checkpointId);
        var evidenceKey = new StateKey(projectId, EntityKinds.Evidence, $"live-adoption-{sessionFingerprint[..24]}");
        var currentCheckpoint = await store.ReadAsync(checkpointKey, cancellationToken).ConfigureAwait(false);
        var currentEvidence = await store.ReadAsync(evidenceKey, cancellationToken).ConfigureAwait(false);
        var existingContextEdges = await store.QueryDependenciesBoundedAsync(
            projectId,
            EntityKinds.Task,
            task.Key.EntityId,
            outgoing: true,
            limit: 257,
            cancellationToken).ConfigureAwait(false);
        reads += 3;

        var mutations = new List<StateMutation>();
        if (competingBinding is not null && interruptedBinding is not null)
        {
            mutations.Add(Mutation(
                competingBinding.Key,
                competingBinding,
                goal.GoalRevision,
                sourceFingerprint,
                interruptedBinding with { Status = "INTERRUPTED_UNKNOWN" },
                RetentionTier.Warm) with
            {
                Lifecycle = StateLifecycle.Archived
            });
        }
        if (!string.Equals(goal.Authority, Authority, StringComparison.Ordinal) || goal.SchemaVersion != SqliteSchema.CurrentVersion)
        {
            mutations.Add(Mutation(
                goal.Key,
                goal,
                goal.GoalRevision,
                sourceFingerprint,
                new AdoptedGoalState(
                    "hiker-live-goal/v1",
                    goal.Key.EntityId,
                    "CURRENT",
                    goal.GoalRevision,
                    goal.Authority,
                    goal.SourceFingerprint,
                    GoalSourceRef(goal),
                    SourcePayload(goal.PayloadJson))));
        }

        mutations.Add(Mutation(
            task.Key,
            task,
            Math.Max(task.GoalRevision, goal.GoalRevision),
            sourceFingerprint,
            new AdoptedTaskState(
                "hiker-live-task/v1",
                task.Key.EntityId,
                goal.Key.EntityId,
                "RUNNING",
                bindingId,
                executionId,
                checkpointId,
                task.Authority,
                task.SourceFingerprint,
                TaskSourceRef(task),
                now)));
        mutations.Add(Mutation(
            bindingKey,
            existingBinding,
            goal.GoalRevision,
            sourceFingerprint,
            new ProviderSessionBindingState(
                bindingId,
                provider,
                sessionFingerprint,
                projectId,
                goal.Key.EntityId,
                task.Key.EntityId,
                checkpointId,
                executionId,
                "ACTIVE",
                now)));
        mutations.Add(Mutation(
            checkpointKey,
            currentCheckpoint,
            goal.GoalRevision,
            sourceFingerprint,
            new AdoptionCheckpointState(
                "hiker-adoption-checkpoint/v1",
                checkpointId,
                task.Key.EntityId,
                goal.Key.EntityId,
                bindingId,
                checkpoint.State?.Key.EntityId,
                checkpoint.State?.SourceFingerprint,
                now,
                "VALIDATED_ADOPTION_BOUNDARY"),
            RetentionTier.Warm));
        mutations.Add(Mutation(
            evidenceKey,
            currentEvidence,
            goal.GoalRevision,
            sourceFingerprint,
            new
            {
                schema_version = "hiker-live-adoption-evidence/v1",
                evidence_id = evidenceKey.EntityId,
                project_id = projectId,
                goal_id = goal.Key.EntityId,
                task_id = task.Key.EntityId,
                checkpoint_id = checkpointId,
                provider,
                provider_session_fingerprint = sessionFingerprint,
                safe_boundary = request.BoundaryProof.Trim().ToUpperInvariant(),
                status = "PASS",
                observed_at = now
            },
            RetentionTier.Warm));
        mutations.Add(Mutation(
            ownershipKey,
            currentOwnership,
            goal.GoalRevision,
            sourceFingerprint,
            new RuntimeOwnershipState(task.Key.EntityId, executionId, layout.ProjectRoot, sourceFingerprint, now)));
        if (leaseState is { Lifecycle: StateLifecycle.Valid } && !RuntimeStateCodec.IsLegacyImportedFact(leaseState))
        {
            var interrupted = RuntimeStateCodec.Parse<RuntimeLease>(leaseState) with
            {
                Status = "INTERRUPTED_UNKNOWN",
                HeartbeatAt = now,
                ExpiresAt = now
            };
            mutations.Add(Mutation(leaseKey, leaseState, goal.GoalRevision, sourceFingerprint, interrupted, RetentionTier.Warm) with
            {
                Lifecycle = StateLifecycle.Archived
            });
        }

        var dependencyChanges = LiveAdoptionStateCodec.ReplaceLiveContextEdges(
            existingContextEdges,
            new DependencyEdge(projectId, EntityKinds.Task, task.Key.EntityId, EntityKinds.Checkpoint, checkpointId, "RESUMES_FROM", sourceFingerprint),
            new DependencyEdge(projectId, EntityKinds.Task, task.Key.EntityId, EntityKinds.Evidence, evidenceKey.EntityId, "PROVEN_BY", sourceFingerprint),
            new DependencyEdge(projectId, EntityKinds.Task, task.Key.EntityId, EntityKinds.ProviderSessionBinding, bindingId, "BOUND_TO", sourceFingerprint));
        var operationId = $"live-adoption-{sessionFingerprint[..24]}-{sourceFingerprint[..16]}";
        var commit = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "LIVE_SESSION_ADOPTED",
            "PROVIDER_SESSION_BOUND",
            RuntimeStateCodec.Json(new
            {
                project_id = projectId,
                goal_id = goal.Key.EntityId,
                task_id = task.Key.EntityId,
                checkpoint_id = checkpointId,
                provider,
                provider_session_fingerprint = sessionFingerprint
            }),
            dependencyChanges,
            RuntimeStateCodec.Json(new { binding_id = bindingId, checkpoint_id = checkpointId })), cancellationToken).ConfigureAwait(false);
        if (!commit.Committed)
        {
            return Result(
                LiveAdoptionStatuses.Blocked,
                project,
                provider,
                sessionFingerprint,
                goal.Key.EntityId,
                task.Key.EntityId,
                checkpointId,
                false,
                false,
                "RETRY_SAME_ADOPTION_OPERATION",
                reads,
                0,
                null,
                new[] { commit.Status });
        }

        var contextPackage = await new ContextResolver(store, _clock).ResolveAsync(
            new ContextRequest(projectId, task.Key.EntityId, goal.Key.EntityId, request.MaxContextBytes, 32),
            cancellationToken).ConfigureAwait(false);
        return Result(
            commit.Status == "IDEMPOTENT_REPLAY" ? LiveAdoptionStatuses.AlreadyAdopted : LiveAdoptionStatuses.Adopted,
            project,
            provider,
            sessionFingerprint,
            goal.Key.EntityId,
            task.Key.EntityId,
            checkpointId,
            commit.Status != "IDEMPOTENT_REPLAY",
            commit.Status == "IDEMPOTENT_REPLAY",
            "CONTINUE_BOUND_TASK",
            reads + contextPackage.StateReads,
            commit.Status == "IDEMPOTENT_REPLAY" ? 0 : mutations.Count,
            contextPackage,
            Array.Empty<string>());
    }

    private static LiveAdoptionResult Result(
        string status,
        ProjectPreflightResult project,
        string provider,
        string sessionFingerprint,
        string? goalId,
        string? taskId,
        string? checkpointId,
        bool committed,
        bool idempotent,
        string nextAction,
        int reads,
        int writes,
        VerifiedContextPackage? context,
        IReadOnlyList<string> reasons) => new(
        status,
        project.ProjectId,
        goalId,
        taskId,
        checkpointId,
        provider,
        sessionFingerprint,
        committed,
        idempotent,
        nextAction,
        reads,
        writes,
        context?.EncodedBytes ?? 0,
        context,
        project,
        reasons);

    private static void Validate(LiveAdoptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Provider) || request.Provider.Length > 64 ||
            string.IsNullOrWhiteSpace(request.ProviderSessionId) || request.ProviderSessionId.Length > 512 ||
            string.IsNullOrWhiteSpace(request.BoundaryProof) ||
            request.MaxContextBytes is < 4096 or > 48_000)
        {
            throw new ArgumentException("Live adoption request is invalid", nameof(request));
        }
    }

}

using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public sealed class CurrentAuthorityEstablishmentService
{
    public const string Authority = "CURRENT_AUTHORITY_ESTABLISHMENT";
    public const string SchemaVersion = "hiker-current-authority/v1";
    private const int InventoryLimit = 257;
    private static readonly IReadOnlySet<string> ActiveGoalStates = new HashSet<string>(
        new[] { "ACTIVE", "CURRENT", "IN_PROGRESS" }, StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ActiveTaskStates = new HashSet<string>(
        new[] { "ACTIVE", "CURRENT", "IN_PROGRESS", "RUNNING" }, StringComparer.Ordinal);

    private readonly IStateStore store;
    private readonly StateCoordinator coordinator;
    private readonly ProjectBootstrapService bootstrap;
    private readonly ICurrentAuthorityFailureInjector failureInjector;

    public CurrentAuthorityEstablishmentService(
        IStateStore store,
        StateCoordinator coordinator,
        ProjectBootstrapService bootstrap)
        : this(store, coordinator, bootstrap, NoCurrentAuthorityFailureInjector.Instance)
    {
    }

    internal CurrentAuthorityEstablishmentService(
        IStateStore store,
        StateCoordinator coordinator,
        ProjectBootstrapService bootstrap,
        ICurrentAuthorityFailureInjector failureInjector)
    {
        this.store = store;
        this.coordinator = coordinator;
        this.bootstrap = bootstrap;
        this.failureInjector = failureInjector;
    }

    public async Task<CurrentAuthorityEstablishmentResult> EstablishAsync(
        CurrentAuthorityEstablishmentRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var provider = request.Provider.Trim().ToLowerInvariant();
        var providerSessionFingerprint = LiveAdoptionStateCodec.Hash($"{provider}\0{request.ProviderSessionId.Trim()}");
        var initialMetrics = await store.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        var reads = 1;
        var project = await bootstrap.PreflightAsync(
            ProjectBootstrapIntent.Read,
            request.ProjectId,
            cancellationToken).ConfigureAwait(false);
        reads++;

        if (!LiveSafeBoundary.IsAccepted(request.BoundaryProof))
        {
            return Result(
                CurrentAuthorityStatuses.WaitSafeBoundary,
                project,
                request,
                provider,
                providerSessionFingerprint,
                null,
                null,
                null,
                false,
                initialMetrics.Generation,
                initialMetrics.Generation,
                reads,
                0,
                false,
                "SAFE_BOUNDARY_NOT_CONFIRMED");
        }

        if (!project.CanProceed || project.ProjectId is null ||
            !string.Equals(project.ProjectId, request.ProjectId, StringComparison.Ordinal) ||
            project.State is ProjectBootstrapState.IdentityMismatch or ProjectBootstrapState.ProjectMoved)
        {
            return Result(
                CurrentAuthorityStatuses.ProjectMismatch,
                project,
                request,
                provider,
                providerSessionFingerprint,
                null,
                null,
                null,
                true,
                initialMetrics.Generation,
                initialMetrics.Generation,
                reads,
                0,
                false,
                project.State == ProjectBootstrapState.ProjectMoved ? "WORKTREE_IDENTITY_MISMATCH" : project.ReasonCode);
        }

        var goalFact = NormalizeFact(request.Goal, ActiveGoalStates, CurrentAuthoritySources.Goal, "goal");
        var taskFact = NormalizeFact(request.Task, ActiveTaskStates, CurrentAuthoritySources.Task, "task");
        var operationId = OperationId(request.ProjectId, providerSessionFingerprint, goalFact, taskFact);
        if (request.OperationId is not null && !string.Equals(request.OperationId, operationId, StringComparison.Ordinal))
        {
            return Result(
                CurrentAuthorityStatuses.AuthorityConflict,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                null,
                null,
                true,
                initialMetrics.Generation,
                initialMetrics.Generation,
                reads,
                0,
                false,
                "OPERATION_ID_FACT_MISMATCH");
        }

        var receipt = await store.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        reads++;
        if (receipt is not null)
        {
            var replay = await ReplayAsync(
                receipt,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                initialMetrics.Generation,
                reads,
                cancellationToken).ConfigureAwait(false);
            return replay;
        }

        failureInjector.ThrowIfRequested(CurrentAuthorityFailurePoint.BeforePreparation);
        var goals = await store.ReadAsync(
            request.ProjectId,
            EntityKinds.Goal,
            lifecycle: null,
            InventoryLimit,
            cancellationToken).ConfigureAwait(false);
        reads++;
        if (goals.Count >= InventoryLimit)
        {
            return Conflict(CurrentAuthorityStatuses.AuthorityAmbiguous, "GOAL_INDEX_BUDGET_EXCEEDED");
        }

        var goalSelection = SelectGoal(request.ProjectId, goals, goalFact);
        if (goalSelection.Status is not null)
        {
            return Conflict(goalSelection.Status, goalSelection.Reason!);
        }

        var goalId = goalSelection.Id!;
        var goalPayload = Payload("GOAL", goalId, null, goalFact);
        var goalMutation = MutationIfRequired(
            new StateKey(request.ProjectId, EntityKinds.Goal, goalId),
            goalSelection.Snapshot,
            goalFact,
            goalPayload,
            goalFact.AuthorityGeneration);
        failureInjector.ThrowIfRequested(CurrentAuthorityFailurePoint.AfterGoalPreparation);
        failureInjector.ThrowIfRequested(CurrentAuthorityFailurePoint.BeforeTaskPreparation);

        var tasks = await store.ReadAsync(
            request.ProjectId,
            EntityKinds.Task,
            lifecycle: null,
            InventoryLimit,
            cancellationToken).ConfigureAwait(false);
        reads++;
        if (tasks.Count >= InventoryLimit)
        {
            return Conflict(CurrentAuthorityStatuses.AuthorityAmbiguous, "TASK_INDEX_BUDGET_EXCEEDED");
        }

        var taskSelection = SelectTask(request.ProjectId, goalId, tasks, taskFact);
        if (taskSelection.Status is not null)
        {
            return Conflict(taskSelection.Status, taskSelection.Reason!);
        }

        var taskId = taskSelection.Id!;
        var taskPayload = Payload("TASK", goalId, taskId, taskFact);
        var taskMutation = MutationIfRequired(
            new StateKey(request.ProjectId, EntityKinds.Task, taskId),
            taskSelection.Snapshot,
            taskFact,
            taskPayload,
            goalFact.AuthorityGeneration);

        var bindingId = $"{provider}-{providerSessionFingerprint[..24]}";
        var binding = await store.ReadAsync(
            new StateKey(request.ProjectId, EntityKinds.ProviderSessionBinding, bindingId),
            cancellationToken).ConfigureAwait(false);
        reads++;
        if (binding is { Lifecycle: StateLifecycle.Valid } &&
            (!LiveAdoptionStateCodec.TryBinding(binding, out var bound) ||
             !string.Equals(bound.GoalId, goalId, StringComparison.Ordinal) ||
             !string.Equals(bound.TaskId, taskId, StringComparison.Ordinal)))
        {
            return Conflict(CurrentAuthorityStatuses.ProviderSessionMismatch, "PROVIDER_SESSION_BOUND_TO_INCOMPATIBLE_AUTHORITY");
        }

        var mutations = new[] { goalMutation, taskMutation }.Where(static item => item is not null).Cast<StateMutation>().ToArray();
        if (mutations.Length == 0)
        {
            return Result(
                CurrentAuthorityStatuses.AdoptExistingAuthority,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                goalId,
                taskId,
                true,
                initialMetrics.Generation,
                initialMetrics.Generation,
                reads,
                0,
                false);
        }

        var operationReceipt = new CurrentAuthorityOperationReceipt(
            SchemaVersion,
            request.ProjectId,
            goalId,
            taskId,
            goalFact.AuthoritySource,
            taskFact.AuthoritySource,
            providerSessionFingerprint,
            operationId);
        var eventPayload = JsonSerializer.Serialize(new
        {
            schema_version = SchemaVersion,
            project_id = request.ProjectId,
            goal_id = goalId,
            task_id = taskId,
            goal_authority_source = goalFact.AuthoritySource,
            task_authority_source = taskFact.AuthoritySource,
            provider_session_fingerprint = providerSessionFingerprint,
            operation_id = operationId
        });

        failureInjector.ThrowIfRequested(CurrentAuthorityFailurePoint.BeforeCasCommit);
        var committed = await coordinator.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "CURRENT_AUTHORITY_ESTABLISHED",
            "BROWNFIELD_CURRENT_AUTHORITY",
            eventPayload,
            ResultJson: JsonSerializer.Serialize(operationReceipt, LiveAdoptionStateCodec.JsonOptions())), cancellationToken).ConfigureAwait(false);
        if (!committed.Committed)
        {
            var afterFailure = await store.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            return Result(
                committed.Status == "OPERATION_ID_CONFLICT" ? CurrentAuthorityStatuses.AuthorityConflict : CurrentAuthorityStatuses.ExecutionFailure,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                goalId,
                taskId,
                true,
                initialMetrics.Generation,
                afterFailure.Generation,
                reads + 1,
                0,
                false,
                committed.Status);
        }

        failureInjector.ThrowIfRequested(CurrentAuthorityFailurePoint.AfterDurableCommitBeforeResponse);
        var finalMetrics = await store.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        var reused = goalSelection.Snapshot is not null || taskSelection.Snapshot is not null;
        return Result(
            committed.Status == "IDEMPOTENT_REPLAY"
                ? CurrentAuthorityStatuses.AlreadyEstablished
                : reused ? CurrentAuthorityStatuses.AdoptExistingAuthority : CurrentAuthorityStatuses.Established,
            project,
            request,
            provider,
            providerSessionFingerprint,
            operationId,
            goalId,
            taskId,
            true,
            initialMetrics.Generation,
            finalMetrics.Generation,
            reads + 1,
            committed.Status == "IDEMPOTENT_REPLAY" ? 0 : mutations.Length,
            committed.Status == "IDEMPOTENT_REPLAY");

        CurrentAuthorityEstablishmentResult Conflict(string status, string reason) => Result(
            status,
            project,
            request,
            provider,
            providerSessionFingerprint,
            operationId,
            null,
            null,
            true,
            initialMetrics.Generation,
            initialMetrics.Generation,
            reads,
            0,
            false,
            reason);
    }

    public static string OperationId(
        string projectId,
        string providerSessionFingerprint,
        CurrentAuthorityFact goal,
        CurrentAuthorityFact task)
    {
        var canonical = string.Join('\n',
            "ESTABLISH_CURRENT_AUTHORITY",
            projectId,
            providerSessionFingerprint,
            goal.AuthoritySource,
            goal.SourceFingerprint,
            goal.AuthorityGeneration,
            task.AuthoritySource,
            task.SourceFingerprint,
            task.AuthorityGeneration);
        return $"current-authority-{LiveAdoptionStateCodec.Hash(canonical)[..32]}";
    }

    private async Task<CurrentAuthorityEstablishmentResult> ReplayAsync(
        OperationReceipt receipt,
        ProjectPreflightResult project,
        CurrentAuthorityEstablishmentRequest request,
        string provider,
        string providerSessionFingerprint,
        string operationId,
        long generation,
        int reads,
        CancellationToken cancellationToken)
    {
        CurrentAuthorityOperationReceipt? prior = null;
        try
        {
            prior = receipt.ResultJson is null
                ? null
                : JsonSerializer.Deserialize<CurrentAuthorityOperationReceipt>(receipt.ResultJson, LiveAdoptionStateCodec.JsonOptions());
        }
        catch (JsonException)
        {
        }

        if (prior is null ||
            !string.Equals(prior.ProjectId, request.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(prior.ProviderSessionFingerprint, providerSessionFingerprint, StringComparison.Ordinal) ||
            !string.Equals(prior.OperationId, operationId, StringComparison.Ordinal))
        {
            return Result(
                CurrentAuthorityStatuses.AuthorityConflict,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                null,
                null,
                true,
                generation,
                generation,
                reads,
                0,
                false,
                "OPERATION_RECEIPT_FACT_MISMATCH");
        }

        var goal = await store.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Goal, prior.GoalId), cancellationToken).ConfigureAwait(false);
        var task = await store.ReadAsync(new StateKey(request.ProjectId, EntityKinds.Task, prior.TaskId), cancellationToken).ConfigureAwait(false);
        reads += 2;
        if (goal is not { Lifecycle: StateLifecycle.Valid } || task is not { Lifecycle: StateLifecycle.Valid })
        {
            return Result(
                CurrentAuthorityStatuses.AuthorityConflict,
                project,
                request,
                provider,
                providerSessionFingerprint,
                operationId,
                prior.GoalId,
                prior.TaskId,
                true,
                generation,
                generation,
                reads,
                0,
                false,
                "ESTABLISHED_AUTHORITY_NO_LONGER_CURRENT");
        }

        return Result(
            CurrentAuthorityStatuses.AlreadyEstablished,
            project,
            request,
            provider,
            providerSessionFingerprint,
            operationId,
            prior.GoalId,
            prior.TaskId,
            true,
            generation,
            generation,
            reads,
            0,
            true);
    }

    private static Selection SelectGoal(string projectId, IReadOnlyList<StateSnapshot> goals, CurrentAuthorityFact fact)
    {
        var provenanceConflicts = goals.Where(item => SameProvenance(item, fact) && !Equivalent(item, fact, "GOAL", null)).ToArray();
        if (provenanceConflicts.Length > 0)
            return Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "GOAL_AUTHORITY_PROVENANCE_CONFLICT");
        var active = goals.Where(static item => item.Lifecycle == StateLifecycle.Valid)
            .Where(item => IsActive(item, ActiveGoalStates)).ToArray();
        if (active.Length > 1) return Selection.Fail(CurrentAuthorityStatuses.AuthorityAmbiguous, "MULTIPLE_CURRENT_GOALS");
        if (active.Length == 1)
        {
            return Equivalent(active[0], fact, "GOAL", null)
                ? Selection.Use(active[0])
                : Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "CURRENT_GOAL_SEMANTICS_CONFLICT");
        }

        var equivalent = goals.Where(item => Equivalent(item, fact, "GOAL", null)).ToArray();
        if (equivalent.Length > 1) return Selection.Fail(CurrentAuthorityStatuses.AuthorityAmbiguous, "MULTIPLE_EQUIVALENT_GOALS");
        if (equivalent.Length == 1) return Selection.Use(equivalent[0]);
        var id = $"goal-{LiveAdoptionStateCodec.Hash($"{projectId}\0{fact.AuthoritySource}\0{fact.SourceFingerprint}\0{fact.AuthorityGeneration}")[..24]}";
        return goals.Any(item => string.Equals(item.Key.EntityId, id, StringComparison.Ordinal))
            ? Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "GOAL_ID_ALREADY_BOUND_TO_DIFFERENT_FACTS")
            : Selection.Create(id);
    }

    private static Selection SelectTask(
        string projectId,
        string goalId,
        IReadOnlyList<StateSnapshot> tasks,
        CurrentAuthorityFact fact)
    {
        var provenanceConflicts = tasks.Where(item => SameProvenance(item, fact) && !Equivalent(item, fact, "TASK", goalId)).ToArray();
        if (provenanceConflicts.Length > 0)
            return Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "TASK_AUTHORITY_PROVENANCE_CONFLICT");
        var active = tasks.Where(static item => item.Lifecycle == StateLifecycle.Valid)
            .Where(item => IsActiveTaskForGoal(item, goalId)).ToArray();
        if (active.Length > 1) return Selection.Fail(CurrentAuthorityStatuses.AuthorityAmbiguous, "MULTIPLE_CURRENT_TASKS");
        if (active.Length == 1)
        {
            return Equivalent(active[0], fact, "TASK", goalId)
                ? Selection.Use(active[0])
                : Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "CURRENT_TASK_SEMANTICS_CONFLICT");
        }

        var equivalent = tasks.Where(item => Equivalent(item, fact, "TASK", goalId)).ToArray();
        if (equivalent.Length > 1) return Selection.Fail(CurrentAuthorityStatuses.AuthorityAmbiguous, "MULTIPLE_EQUIVALENT_TASKS");
        if (equivalent.Length == 1) return Selection.Use(equivalent[0]);
        var id = $"task-{LiveAdoptionStateCodec.Hash($"{projectId}\0{goalId}\0{fact.AuthoritySource}\0{fact.SourceFingerprint}\0{fact.AuthorityGeneration}")[..24]}";
        return tasks.Any(item => string.Equals(item.Key.EntityId, id, StringComparison.Ordinal))
            ? Selection.Fail(CurrentAuthorityStatuses.AuthorityConflict, "TASK_ID_ALREADY_BOUND_TO_DIFFERENT_FACTS")
            : Selection.Create(id);
    }

    private static bool IsActive(StateSnapshot snapshot, IReadOnlySet<string> accepted)
    {
        if (!LiveAdoptionStateCodec.TryPayload(snapshot.PayloadJson, out var payload)) return false;
        if (!payload.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String) return true;
        return accepted.Contains(state.GetString()!.Trim().ToUpperInvariant());
    }

    private static bool IsActiveTaskForGoal(StateSnapshot snapshot, string goalId)
    {
        if (!IsActive(snapshot, ActiveTaskStates) || !LiveAdoptionStateCodec.TryPayload(snapshot.PayloadJson, out var payload)) return false;
        return !payload.TryGetProperty("goal_id", out var bound) ||
               bound.ValueKind != JsonValueKind.String ||
               string.Equals(bound.GetString(), goalId, StringComparison.Ordinal);
    }

    private static bool Equivalent(StateSnapshot snapshot, CurrentAuthorityFact fact, string entityType, string? goalId)
    {
        if (!LiveAdoptionStateCodec.TryPayload(snapshot.PayloadJson, out var payload) ||
            !Property(payload, "statement", fact.Statement) ||
            !Property(payload, "status", fact.State) ||
            !Property(payload, "authority_source", fact.AuthoritySource) ||
            !Property(payload, "source_fingerprint", fact.SourceFingerprint) ||
            !Property(payload, "entity_type", entityType) ||
            !payload.TryGetProperty("authority_generation", out var generation) ||
            !generation.TryGetInt64(out var value) || value != fact.AuthorityGeneration)
        {
            return false;
        }

        return goalId is null || Property(payload, "goal_id", goalId);
    }

    private static bool SameProvenance(StateSnapshot snapshot, CurrentAuthorityFact fact)
    {
        if (!LiveAdoptionStateCodec.TryPayload(snapshot.PayloadJson, out var payload)) return false;
        return Property(payload, "authority_source", fact.AuthoritySource) &&
               Property(payload, "source_fingerprint", fact.SourceFingerprint);
    }

    private static bool Property(JsonElement payload, string name, string expected) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static StateMutation? MutationIfRequired(
        StateKey key,
        StateSnapshot? existing,
        CurrentAuthorityFact fact,
        CurrentAuthorityEntityPayload payload,
        long goalRevision)
    {
        if (existing is { Lifecycle: StateLifecycle.Valid }) return null;
        var json = JsonSerializer.Serialize(payload, LiveAdoptionStateCodec.JsonOptions());
        var fingerprint = LiveAdoptionStateCodec.Hash(json);
        return new StateMutation(
            key,
            existing?.Generation ?? 0,
            goalRevision,
            StateLifecycle.Valid,
            Authority,
            SqliteSchema.CurrentVersion,
            fingerprint,
            fingerprint,
            json,
            RetentionTier.Hot);
    }

    private static CurrentAuthorityEntityPayload Payload(
        string entityType,
        string goalId,
        string? taskId,
        CurrentAuthorityFact fact) => new(
            SchemaVersion,
            entityType,
            goalId,
            taskId,
            fact.Statement,
            fact.State,
            fact.AuthoritySource,
            fact.SourceFingerprint,
            fact.AuthorityGeneration);

    private static CurrentAuthorityFact NormalizeFact(
        CurrentAuthorityFact fact,
        IReadOnlySet<string> acceptedStates,
        IReadOnlySet<string> acceptedSources,
        string name)
    {
        var statement = fact.Statement.Trim();
        var state = fact.State.Trim().ToUpperInvariant();
        var source = fact.AuthoritySource.Trim().ToUpperInvariant();
        var fingerprint = fact.SourceFingerprint.Trim().ToLowerInvariant();
        if (statement.Length is < 1 or > 4096) throw new ArgumentException($"{name} statement must contain 1-4096 characters");
        if (!acceptedStates.Contains(state)) throw new ArgumentException($"{name} state is not active");
        if (source.Length is < 1 or > 160 || source.Any(static value => !(char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.')))
            throw new ArgumentException($"{name} authority source is invalid");
        if (!acceptedSources.Contains(source)) throw new ArgumentException($"{name} authority source is not trusted");
        if (fingerprint.Length != 64 || fingerprint.Any(static value => !Uri.IsHexDigit(value)))
            throw new ArgumentException($"{name} source fingerprint must be SHA-256 hex");
        if (fact.AuthorityGeneration < 0) throw new ArgumentOutOfRangeException($"{name} authority generation");
        return new CurrentAuthorityFact(statement, state, source, fingerprint, fact.AuthorityGeneration);
    }

    private static void Validate(CurrentAuthorityEstablishmentRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Project, "current-authority").Normalize();
        if (string.IsNullOrWhiteSpace(request.Provider)) throw new ArgumentException("Provider is required");
        if (string.IsNullOrWhiteSpace(request.ProviderSessionId)) throw new ArgumentException("Provider session is required");
        if (string.IsNullOrWhiteSpace(request.BoundaryProof)) throw new ArgumentException("Boundary proof is required");
        if (request.OperationId is { Length: > 160 }) throw new ArgumentException("Operation id is too long");
    }

    private static CurrentAuthorityEstablishmentResult Result(
        string status,
        ProjectPreflightResult project,
        CurrentAuthorityEstablishmentRequest request,
        string provider,
        string providerSessionFingerprint,
        string? operationId,
        string? goalId,
        string? taskId,
        bool safeBoundary,
        long generationBefore,
        long generationAfter,
        int reads,
        int writes,
        bool replay,
        params string[] reasons) => new(
            status,
            project.ProjectId,
            goalId,
            taskId,
            request.Goal.AuthoritySource.Trim().ToUpperInvariant(),
            request.Task.AuthoritySource.Trim().ToUpperInvariant(),
            provider,
            providerSessionFingerprint,
            operationId,
            safeBoundary,
            generationBefore,
            generationAfter,
            reads,
            writes,
            replay,
            project,
            reasons);

    private sealed record Selection(string? Id, StateSnapshot? Snapshot, string? Status, string? Reason)
    {
        public static Selection Use(StateSnapshot value) => new(value.Key.EntityId, value, null, null);
        public static Selection Create(string id) => new(id, null, null, null);
        public static Selection Fail(string status, string reason) => new(null, null, status, reason);
    }
}

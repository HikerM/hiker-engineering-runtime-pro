using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Workspace;

public enum MergeQueueStatus
{
    Queued,
    Ready,
    Authorized,
    Merged
}

public sealed record MergeCandidateRequest(
    string OperationId,
    string ProjectId,
    string CandidateId,
    string TaskId,
    string WorktreePath,
    string Branch,
    string TargetBranch,
    string BaseCommit,
    string CandidateCommit,
    string WriterRuntimeId,
    string? AssuranceRuntimeId,
    long GoalRevision,
    EngineeringRisk Risk,
    IReadOnlyList<string> PredictedAffectedScopes,
    IReadOnlyList<string> TestEvidenceRefs,
    IReadOnlyList<string> AssuranceEvidenceRefs,
    string SourceFingerprint);

public sealed record MergeQueueEntry(
    string CandidateId,
    string TaskId,
    string WorktreePath,
    string Branch,
    string TargetBranch,
    string BaseCommit,
    string CandidateCommit,
    string WriterRuntimeId,
    string? AssuranceRuntimeId,
    EngineeringRisk Risk,
    IReadOnlyList<string> PredictedAffectedScopes,
    IReadOnlyList<string> TestEvidenceRefs,
    IReadOnlyList<string> AssuranceEvidenceRefs,
    MergeQueueStatus Status,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset UpdatedAt);

public sealed record MergeQueueResult(string Status, MergeQueueEntry? Entry, long Generation, string ReasonCode);

internal sealed record MergeQueueControl(string TargetBranch, string? ActiveCandidateId, DateTimeOffset UpdatedAt);

public sealed class MergeQueueCoordinator(
    StateCoordinator state,
    IWorkspaceInspector workspaceInspector,
    IGitAncestryVerifier ancestry,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<MergeQueueResult> EnqueueAsync(MergeCandidateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var key = CandidateKey(request.ProjectId, request.CandidateId);
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            var existing = ParseEntry(current);
            if (existing.CandidateCommit == request.CandidateCommit && existing.TaskId == request.TaskId)
            {
                return new MergeQueueResult("IDEMPOTENT_REPLAY", existing, current.Generation, "CANDIDATE_ALREADY_EXISTS");
            }

            throw new InvalidOperationException("Merge candidate ID is already bound to another commit or task");
        }

        var workspace = await workspaceInspector.InspectAsync(request.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (workspace.Dirty || workspace.Head != request.CandidateCommit || workspace.Branch != request.Branch)
        {
            throw new InvalidOperationException("Merge candidate worktree is dirty or does not match its declared branch and commit");
        }

        if (!await ancestry.IsAncestorAsync(workspace.Root, request.BaseCommit, request.CandidateCommit, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Merge candidate commit is not descended from its declared base");
        }

        var now = _clock.GetUtcNow();
        var entry = new MergeQueueEntry(
            request.CandidateId,
            request.TaskId,
            workspace.Root,
            request.Branch,
            request.TargetBranch,
            request.BaseCommit,
            request.CandidateCommit,
            request.WriterRuntimeId,
            request.AssuranceRuntimeId,
            request.Risk,
            AffectedScopePolicy.Normalize(request.PredictedAffectedScopes),
            FreezeRefs(request.TestEvidenceRefs),
            FreezeRefs(request.AssuranceEvidenceRefs),
            MergeQueueStatus.Queued,
            now,
            now);
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            request.OperationId,
            new[] { Mutation(key, current, request.GoalRevision, request.SourceFingerprint, entry) },
            "MERGE_CANDIDATE_ENQUEUED",
            "MERGE_QUEUE_ENQUEUED",
            JsonSerializer.Serialize(new { request.CandidateId, request.TaskId, request.CandidateCommit })), cancellationToken).ConfigureAwait(false);
        return new MergeQueueResult(commit.Status, commit.Committed ? entry : null, commit.CurrentGeneration, commit.Status);
    }

    public async Task<MergeQueueResult> MarkReadyAsync(
        string operationId,
        string projectId,
        string candidateId,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var key = CandidateKey(projectId, candidateId);
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Merge candidate does not exist");
        var entry = ParseEntry(current);
        if (entry.Status >= MergeQueueStatus.Ready)
        {
            return new MergeQueueResult("IDEMPOTENT_REPLAY", entry, current.Generation, "CANDIDATE_ALREADY_READY");
        }

        RequireEvidence(entry);
        var workspace = await workspaceInspector.InspectAsync(entry.WorktreePath, cancellationToken).ConfigureAwait(false);
        if (workspace.Dirty || workspace.Head != entry.CandidateCommit || workspace.Branch != entry.Branch)
        {
            throw new InvalidOperationException("Merge candidate changed after enqueue");
        }

        var ready = entry with { Status = MergeQueueStatus.Ready, UpdatedAt = _clock.GetUtcNow() };
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { Mutation(key, current, current.GoalRevision, sourceFingerprint, ready) },
            "MERGE_CANDIDATE_READY",
            "MERGE_QUEUE_READY",
            JsonSerializer.Serialize(new { candidateId })), cancellationToken).ConfigureAwait(false);
        return new MergeQueueResult(commit.Status, commit.Committed ? ready : null, commit.CurrentGeneration, commit.Status);
    }

    public async Task<MergeQueueResult> AuthorizeNextAsync(
        string operationId,
        string projectId,
        string targetRoot,
        string targetBranch,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var controlKey = ControlKey(projectId, targetBranch);
        var controlState = await state.ReadAsync(controlKey, cancellationToken).ConfigureAwait(false);
        var control = controlState is null ? new MergeQueueControl(targetBranch, null, _clock.GetUtcNow()) : ParseControl(controlState);
        if (control.ActiveCandidateId is not null)
        {
            return new MergeQueueResult("BLOCKED", null, controlState!.Generation, "MERGE_ALREADY_AUTHORIZED");
        }

        var candidates = await state.ReadAsync(projectId, EntityKinds.Candidate, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        var ready = candidates
            .Where(static snapshot => snapshot.Key.EntityId.StartsWith("merge:", StringComparison.Ordinal))
            .Select(snapshot => (Snapshot: snapshot, Entry: ParseEntry(snapshot)))
            .Where(item => item.Entry.TargetBranch == targetBranch && item.Entry.Status == MergeQueueStatus.Ready)
            .OrderBy(static item => item.Entry.EnqueuedAt)
            .ThenBy(static item => item.Entry.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (ready.Entry is null)
        {
            return new MergeQueueResult("EMPTY", null, controlState?.Generation ?? 0, "NO_READY_CANDIDATE");
        }

        var target = await workspaceInspector.InspectAsync(targetRoot, cancellationToken).ConfigureAwait(false);
        if (target.Dirty || target.Branch != targetBranch || target.Head != ready.Entry.BaseCommit)
        {
            return new MergeQueueResult("BLOCKED", ready.Entry, ready.Snapshot.Generation, "TARGET_MOVED_OR_DIRTY");
        }

        var authorized = ready.Entry with { Status = MergeQueueStatus.Authorized, UpdatedAt = _clock.GetUtcNow() };
        var updatedControl = control with { ActiveCandidateId = authorized.CandidateId, UpdatedAt = _clock.GetUtcNow() };
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[]
            {
                Mutation(ready.Snapshot.Key, ready.Snapshot, ready.Snapshot.GoalRevision, sourceFingerprint, authorized),
                Mutation(controlKey, controlState, ready.Snapshot.GoalRevision, sourceFingerprint, updatedControl)
            },
            "MERGE_AUTHORIZED",
            "MERGE_QUEUE_SERIAL_AUTHORIZATION",
            JsonSerializer.Serialize(new { authorized.CandidateId, targetBranch })), cancellationToken).ConfigureAwait(false);
        return new MergeQueueResult(commit.Status, commit.Committed ? authorized : null, commit.CurrentGeneration, commit.Status);
    }

    public async Task<MergeQueueResult> CompleteAsync(
        string operationId,
        string projectId,
        string candidateId,
        string targetRoot,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var candidateKey = CandidateKey(projectId, candidateId);
        var candidateState = await state.ReadAsync(candidateKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Merge candidate does not exist");
        var entry = ParseEntry(candidateState);
        if (entry.Status == MergeQueueStatus.Merged || candidateState.Lifecycle == StateLifecycle.Archived)
        {
            return new MergeQueueResult("IDEMPOTENT_REPLAY", entry, candidateState.Generation, "CANDIDATE_ALREADY_MERGED");
        }

        if (entry.Status != MergeQueueStatus.Authorized)
        {
            throw new InvalidOperationException("Only an authorized merge candidate can complete");
        }

        var controlKey = ControlKey(projectId, entry.TargetBranch);
        var controlState = await state.ReadAsync(controlKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Merge queue control is missing");
        var control = ParseControl(controlState);
        if (control.ActiveCandidateId != candidateId)
        {
            throw new InvalidOperationException("Merge queue authorization belongs to another candidate");
        }

        var target = await workspaceInspector.InspectAsync(targetRoot, cancellationToken).ConfigureAwait(false);
        if (target.Dirty || target.Branch != entry.TargetBranch ||
            !await ancestry.IsAncestorAsync(target.Root, entry.CandidateCommit, target.Head, cancellationToken).ConfigureAwait(false))
        {
            return new MergeQueueResult("BLOCKED", entry, candidateState.Generation, "MERGE_NOT_OBSERVED_ON_CLEAN_TARGET");
        }

        var merged = entry with { Status = MergeQueueStatus.Merged, UpdatedAt = _clock.GetUtcNow() };
        var released = control with { ActiveCandidateId = null, UpdatedAt = _clock.GetUtcNow() };
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[]
            {
                Mutation(candidateKey, candidateState, candidateState.GoalRevision, sourceFingerprint, merged, StateLifecycle.Archived, RetentionTier.Warm),
                Mutation(controlKey, controlState, candidateState.GoalRevision, sourceFingerprint, released)
            },
            "MERGE_COMPLETED",
            "MERGE_QUEUE_RELEASED",
            JsonSerializer.Serialize(new { candidateId, target_head = target.Head })), cancellationToken).ConfigureAwait(false);
        return new MergeQueueResult(commit.Status, commit.Committed ? merged : null, commit.CurrentGeneration, commit.Status);
    }

    private static void Validate(MergeCandidateRequest request)
    {
        _ = CandidateKey(request.ProjectId, request.CandidateId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.WorktreePath) ||
            string.IsNullOrWhiteSpace(request.Branch) || string.IsNullOrWhiteSpace(request.TargetBranch) ||
            string.IsNullOrWhiteSpace(request.BaseCommit) || string.IsNullOrWhiteSpace(request.CandidateCommit) ||
            string.IsNullOrWhiteSpace(request.WriterRuntimeId) || string.IsNullOrWhiteSpace(request.SourceFingerprint) ||
            request.TestEvidenceRefs.Count is < 1 or > 256 || request.AssuranceEvidenceRefs.Count > 256)
        {
            throw new ArgumentException("Merge candidate is incomplete or exceeds bounded evidence limits", nameof(request));
        }

        if (request.Risk >= EngineeringRisk.High &&
            (string.IsNullOrWhiteSpace(request.AssuranceRuntimeId) || request.AssuranceRuntimeId == request.WriterRuntimeId || request.AssuranceEvidenceRefs.Count == 0))
        {
            throw new InvalidOperationException("High-risk merge candidates require independent assurance evidence");
        }
    }

    private static void RequireEvidence(MergeQueueEntry entry)
    {
        if (entry.TestEvidenceRefs.Count == 0 ||
            (entry.Risk >= EngineeringRisk.High && (entry.AssuranceEvidenceRefs.Count == 0 || entry.AssuranceRuntimeId == entry.WriterRuntimeId)))
        {
            throw new InvalidOperationException("Merge evidence is insufficient for the candidate risk");
        }
    }

    private static IReadOnlyList<string> FreezeRefs(IReadOnlyList<string> values) => values
        .Select(static value => value.Trim())
        .Where(static value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static value => value, StringComparer.Ordinal)
        .ToArray();

    private static StateKey CandidateKey(string projectId, string candidateId) => new(projectId, EntityKinds.Candidate, $"merge:{candidateId}");
    private static StateKey ControlKey(string projectId, string targetBranch) => new(projectId, EntityKinds.Candidate, $"merge-control:{targetBranch}");
    private static MergeQueueEntry ParseEntry(StateSnapshot snapshot) => JsonSerializer.Deserialize<MergeQueueEntry>(snapshot.PayloadJson)
        ?? throw new InvalidDataException("Invalid merge queue entry");
    private static MergeQueueControl ParseControl(StateSnapshot snapshot) => JsonSerializer.Deserialize<MergeQueueControl>(snapshot.PayloadJson)
        ?? throw new InvalidDataException("Invalid merge queue control");
    private static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        string sourceFingerprint,
        object payload,
        StateLifecycle lifecycle = StateLifecycle.Valid,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        lifecycle,
        "MERGE_QUEUE_COORDINATOR",
        SqliteSchema.CurrentVersion,
        sourceFingerprint,
        sourceFingerprint,
        JsonSerializer.Serialize(payload),
        retention);
}

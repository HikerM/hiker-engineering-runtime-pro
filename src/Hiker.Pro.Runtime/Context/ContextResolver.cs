using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;
using Hiker.Pro.Storage;

namespace Hiker.Pro.Context;

public sealed class ContextResolver(IStateStore stateStore, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<VerifiedContextPackage> ResolveAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        Validate(request);
        var reads = 0;
        var graphQueries = 0;
        var staleRejected = 0;
        var supersededRejected = 0;
        var authorityConflicts = 0;
        var candidates = new List<ContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var goal = await ReadRootAsync(new StateKey(request.ProjectId, EntityKinds.Goal, request.GoalId)).ConfigureAwait(false);
        var task = await ReadRootAsync(new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId)).ConfigureAwait(false);
        if (goal.GoalRevision != task.GoalRevision)
        {
            throw new InvalidOperationException("Current Goal and Task revisions do not match");
        }
        candidates.Add(ToItem(goal, ContextTemperature.Hot, maxPayloadBytes: 4096));
        candidates.Add(ToItem(task, ContextTemperature.Hot, maxPayloadBytes: 4096));
        seen.Add($"{EntityKinds.Goal}|{request.GoalId}");
        seen.Add($"{EntityKinds.Task}|{request.TaskId}");
        var currentRevision = task.GoalRevision;

        var outgoing = await stateStore.QueryDependenciesBoundedAsync(
            request.ProjectId,
            EntityKinds.Task,
            request.TaskId,
            outgoing: true,
            limit: 257,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        reads++;
        graphQueries++;
        foreach (var edge in outgoing.Take(256))
        {
            var temperature = edge.ToKind is EntityKinds.Decision or EntityKinds.Service or EntityKinds.Contract
                ? ContextTemperature.Warm
                : ContextTemperature.Hot;
            if (temperature == ContextTemperature.Warm && !request.IncludeWarm)
            {
                continue;
            }

            await AddExactAsync(new StateKey(request.ProjectId, edge.ToKind, edge.ToId), temperature).ConfigureAwait(false);
        }

        var ordered = candidates
            .OrderBy(static item => item.Temperature)
            .ThenBy(static item => AuthorityPriority(ClassifyAuthority(item)))
            .ThenBy(static item => Priority(item.Kind))
            .ThenByDescending(static item => item.Generation)
            .ToArray();
        var selected = new List<ContextItem>();
        var bytes = 0;
        var truncated = outgoing.Count > 256;
        foreach (var item in ordered)
        {
            if (selected.Count >= request.MaxItems)
            {
                truncated = true;
                break;
            }

            var itemBytes = Encoding.UTF8.GetByteCount(item.Ref) + Encoding.UTF8.GetByteCount(item.PayloadJson) + 128;
            if (bytes + itemBytes > request.MaxBytes)
            {
                truncated = true;
                continue;
            }

            selected.Add(item);
            bytes += itemBytes;
        }

        var metrics = await stateStore.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        reads++;
        var fingerprint = Fingerprint(selected);
        timer.Stop();
        var resolvedAt = _clock.GetUtcNow();
        var selectedRefs = ContractCollections.Freeze(selected.Select(static item => item.Ref));
        var receiptFingerprint = ReceiptFingerprint(
            request,
            metrics.Generation,
            selectedRefs,
            bytes,
            reads,
            graphQueries,
            staleRejected,
            supersededRejected,
            authorityConflicts,
            fingerprint);
        var receipt = new RetrievalReceipt(
            request.ProjectId,
            request.GoalId,
            request.TaskId,
            metrics.Generation,
            selectedRefs,
            bytes,
            reads,
            graphQueries,
            0,
            0,
            staleRejected,
            supersededRejected,
            authorityConflicts,
            fingerprint,
            receiptFingerprint,
            timer.Elapsed.TotalMilliseconds,
            resolvedAt);
        return new VerifiedContextPackage(
            "hiker-context/v1",
            request.ProjectId,
            request.TaskId,
            request.GoalId,
            metrics.Generation,
            ContractCollections.Freeze(selected),
            bytes,
            reads,
            truncated,
            fingerprint,
            resolvedAt,
            receipt);

        async Task<StateSnapshot> ReadRootAsync(StateKey key)
        {
            var state = await stateStore.ReadAsync(key.Normalize(), cancellationToken).ConfigureAwait(false);
            reads++;
            if (state is null)
            {
                throw new InvalidOperationException($"Current context root does not exist: {key.Kind}/{key.EntityId}");
            }
            if (state.Lifecycle != StateLifecycle.Valid)
            {
                throw new InvalidOperationException($"Current context root is not valid: {key.Kind}/{key.EntityId}/{state.Lifecycle}");
            }
            var authority = ClassifyAuthority(ToItem(state, ContextTemperature.Hot, 64));
            if (authority is not (RetrievalAuthority.AuthoritativeCurrent or RetrievalAuthority.VerifiedDerived))
            {
                throw new InvalidOperationException($"Current context root has non-current authority: {key.Kind}/{key.EntityId}/{state.Authority}");
            }
            return state;
        }

        async Task AddExactAsync(StateKey key, ContextTemperature temperature)
        {
            var normalized = key.Normalize();
            var identity = $"{normalized.Kind}|{normalized.EntityId}";
            if (!seen.Add(identity))
            {
                return;
            }

            var state = await stateStore.ReadAsync(normalized, cancellationToken).ConfigureAwait(false);
            reads++;
            if (state is null)
            {
                return;
            }

            if (state.Lifecycle == StateLifecycle.Stale)
            {
                staleRejected++;
                return;
            }
            if (state.Lifecycle == StateLifecycle.Superseded)
            {
                supersededRejected++;
                return;
            }
            if (state.Lifecycle != StateLifecycle.Valid)
            {
                return;
            }
            if (state.GoalRevision != 0 && state.GoalRevision != currentRevision)
            {
                staleRejected++;
                return;
            }

            var item = ToItem(state, temperature, maxPayloadBytes: 4096);
            if (ClassifyAuthority(item) is not (RetrievalAuthority.AuthoritativeCurrent or RetrievalAuthority.VerifiedDerived))
            {
                authorityConflicts++;
                return;
            }

            candidates.Add(item);
        }
    }

    internal static RetrievalAuthority ClassifyAuthority(ContextItem item) => item.AuthorityClass;

    private static int AuthorityPriority(RetrievalAuthority authority) => authority switch
    {
        RetrievalAuthority.AuthoritativeCurrent => 0,
        RetrievalAuthority.VerifiedDerived => 1,
        RetrievalAuthority.RuntimeObservation => 2,
        RetrievalAuthority.Historical => 3,
        RetrievalAuthority.LegacyPreserved => 4,
        RetrievalAuthority.Cache => 5,
        _ => 6
    };

    internal static ContextItem ToItem(StateSnapshot state, ContextTemperature temperature, int maxPayloadBytes)
    {
        var payload = BoundUtf8(state.PayloadJson, maxPayloadBytes);
        return new ContextItem(
            $"state://{state.Key.Kind.ToLowerInvariant()}/{state.Key.EntityId}",
            state.Key.Kind,
            state.Key.EntityId,
            state.Generation,
            state.GoalRevision,
            state.Lifecycle,
            temperature,
            state.Authority,
            ClassifyAuthority(state, temperature),
            state.SourceFingerprint,
            payload);
    }

    private static RetrievalAuthority ClassifyAuthority(StateSnapshot state, ContextTemperature temperature)
    {
        if (temperature == ContextTemperature.Cold || state.Lifecycle != StateLifecycle.Valid)
            return RetrievalAuthority.Historical;
        if (state.Retention is RetentionTier.Cache or RetentionTier.Temp)
            return RetrievalAuthority.Cache;
        if (state.Authority.Contains("COMMUNITY_5_18_IMPORT", StringComparison.OrdinalIgnoreCase) ||
            state.Authority.Contains("LEGACY", StringComparison.OrdinalIgnoreCase))
            return RetrievalAuthority.LegacyPreserved;
        if (state.Key.Kind is EntityKinds.EnvironmentObservation or EntityKinds.ServiceProcess)
            return RetrievalAuthority.RuntimeObservation;
        if (state.Key.Kind is EntityKinds.Evidence or EntityKinds.Test)
            return RetrievalAuthority.VerifiedDerived;
        return RetrievalAuthority.AuthoritativeCurrent;
    }

    private static string BoundUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes / 2)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return JsonSerializer.Serialize(new { truncated = true, source_prefix = builder.ToString() });
    }

    private static int Priority(string kind) => kind switch
    {
        EntityKinds.Goal => 0,
        EntityKinds.GoalRevision => 1,
        EntityKinds.Task => 2,
        EntityKinds.Decision => 3,
        EntityKinds.Contract => 4,
        EntityKinds.Service => 5,
        EntityKinds.EnvironmentProfile => 6,
        EntityKinds.EnvironmentInstance => 7,
        EntityKinds.ResourceLease => 8,
        EntityKinds.ServiceProcess => 9,
        EntityKinds.ChangeContract => 10,
        EntityKinds.ChangeImpact => 11,
        EntityKinds.Evidence => 12,
        EntityKinds.Checkpoint => 13,
        _ => 20
    };

    private static string Fingerprint(IEnumerable<ContextItem> items)
    {
        var canonical = string.Join('\n', items.Select(static item => $"{item.Ref}|{item.Generation}|{item.SourceFingerprint}|{item.Lifecycle}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ReceiptFingerprint(
        ContextRequest request,
        long generation,
        IReadOnlyList<string> selected,
        int bytes,
        int reads,
        int graphQueries,
        int staleRejected,
        int supersededRejected,
        int authorityConflicts,
        string contextFingerprint)
    {
        var canonical = string.Join('\n',
            request.ProjectId,
            request.GoalId,
            request.TaskId,
            generation,
            string.Join('|', selected),
            bytes,
            reads,
            graphQueries,
            staleRejected,
            supersededRejected,
            authorityConflicts,
            contextFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void Validate(ContextRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        _ = new StateKey(request.ProjectId, EntityKinds.Goal, request.GoalId).Normalize();
        if (request.MaxBytes is < 4096 or > 262_144 || request.MaxItems is < 2 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Context budgets are outside product limits");
        }
    }
}

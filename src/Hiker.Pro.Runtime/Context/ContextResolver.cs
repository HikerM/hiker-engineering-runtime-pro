using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

public sealed class ContextResolver(IStateStore stateStore, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<VerifiedContextPackage> ResolveAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var reads = 0;
        var candidates = new List<ContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await AddExactAsync(new StateKey(request.ProjectId, EntityKinds.Goal, request.GoalId), ContextTemperature.Hot).ConfigureAwait(false);
        await AddExactAsync(new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId), ContextTemperature.Hot).ConfigureAwait(false);

        var outgoing = await stateStore.QueryDependenciesBoundedAsync(
            request.ProjectId,
            EntityKinds.Task,
            request.TaskId,
            outgoing: true,
            limit: 257,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        reads++;
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
            _clock.GetUtcNow());

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
            if (state is null || state.Lifecycle is StateLifecycle.Archived or StateLifecycle.Corrupt or StateLifecycle.Incompatible)
            {
                return;
            }

            candidates.Add(ToItem(state, temperature, maxPayloadBytes: 4096));
        }
    }

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
            state.SourceFingerprint,
            payload);
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
        EntityKinds.Evidence => 10,
        EntityKinds.Checkpoint => 11,
        _ => 20
    };

    private static string Fingerprint(IEnumerable<ContextItem> items)
    {
        var canonical = string.Join('\n', items.Select(static item => $"{item.Ref}|{item.Generation}|{item.SourceFingerprint}|{item.Lifecycle}"));
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

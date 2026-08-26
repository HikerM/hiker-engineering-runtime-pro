using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Services;

public sealed record ResourceLeaseRequest(
    string OperationId,
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string Kind,
    string? PreferredToken,
    int PortRangeStart,
    int PortRangeEnd,
    RuntimeProcessIdentity? ProcessIdentity,
    TimeSpan Ttl,
    string SourceFingerprint);

public sealed record EngineeringResourceLease(
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string Kind,
    string Token,
    RuntimeProcessIdentity? ProcessIdentity,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    string Status);

public sealed record ResourceLeaseResult(string Status, EngineeringResourceLease Lease, long Generation);

public sealed class ResourceLeaseCoordinator(
    IStateStore stateStore,
    ProcessIdentityProbe processProbe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ResourceLeaseResult> AcquireAsync(ResourceLeaseRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var receipt = await stateStore.ReadOperationReceiptAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE" && receipt.ResultJson is not null)
        {
            using var result = JsonDocument.Parse(receipt.ResultJson);
            var token = result.RootElement.GetProperty("token").GetString()!;
            var state = await stateStore.ReadAsync(Key(request.ProjectId, request.Kind, token), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed resource operation has no lease state");
            return new ResourceLeaseResult("IDEMPOTENT_REPLAY", Parse(state), receipt.Generation ?? state.Generation);
        }

        foreach (var token in CandidateTokens(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Kind.Equals("PORT", StringComparison.OrdinalIgnoreCase) && !PortAppearsAvailable(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture)))
            {
                continue;
            }

            var key = Key(request.ProjectId, request.Kind, token);
            var current = await stateStore.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Lifecycle == StateLifecycle.Valid)
            {
                var existing = Parse(current);
                if (existing.TaskId == request.TaskId && existing.RuntimeId == request.RuntimeId)
                {
                    return new ResourceLeaseResult("ALREADY_OWNED", existing, current.Generation);
                }

                var live = existing.ProcessIdentity is not null &&
                    await processProbe.IsSameLiveProcessAsync(existing.ProcessIdentity, cancellationToken).ConfigureAwait(false);
                if (live || existing.ExpiresAt > _clock.GetUtcNow())
                {
                    continue;
                }
            }

            var now = _clock.GetUtcNow();
            var lease = new EngineeringResourceLease(
                request.ProjectId,
                request.TaskId,
                request.RuntimeId,
                request.Kind.ToUpperInvariant(),
                token,
                request.ProcessIdentity,
                now,
                now.Add(request.Ttl),
                "ACTIVE");
            var mutation = new StateMutation(
                key,
                current?.Generation ?? 0,
                0,
                StateLifecycle.Valid,
                "RESOURCE_LEASE_COORDINATOR",
                "3.0.0",
                request.SourceFingerprint,
                request.SourceFingerprint,
                JsonSerializer.Serialize(lease),
                RetentionTier.Hot);
            var commit = await stateStore.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                request.OperationId,
                new[] { mutation },
                "RESOURCE_LEASE_ACQUIRED",
                "RESOURCE_RESERVED",
                JsonSerializer.Serialize(new { request.TaskId, request.RuntimeId, kind = lease.Kind, token }),
                ResultJson: JsonSerializer.Serialize(new { token })), cancellationToken).ConfigureAwait(false);
            if (commit.Committed)
            {
                return new ResourceLeaseResult(commit.Status, lease, commit.CurrentGeneration);
            }

            if (commit.Status == "OPERATION_ID_CONFLICT")
            {
                throw new InvalidOperationException("Operation ID is bound to a different resource request");
            }
        }

        throw new InvalidOperationException("No safe resource candidate is available");
    }

    public async Task<BatchTransactionResult> ReleaseAsync(
        string operationId,
        EngineeringResourceLease lease,
        long expectedGeneration,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        var receipt = await stateStore.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            return new BatchTransactionResult(true, false, receipt.Generation ?? expectedGeneration, Array.Empty<StateSnapshot>(), "IDEMPOTENT_REPLAY");
        }

        var key = Key(lease.ProjectId, lease.Kind, lease.Token);
        var current = await stateStore.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Resource lease is missing");
        var stored = Parse(current);
        if (stored.TaskId != lease.TaskId || stored.RuntimeId != lease.RuntimeId)
        {
            throw new InvalidOperationException("Resource release is not owned by this runtime");
        }

        return await stateStore.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            operationId,
            new[]
            {
                new StateMutation(key, expectedGeneration, 0, StateLifecycle.Archived, "RESOURCE_LEASE_COORDINATOR", "3.0.0", sourceFingerprint, sourceFingerprint,
                    JsonSerializer.Serialize(stored with { Status = "RELEASED", ExpiresAt = _clock.GetUtcNow() }), RetentionTier.Warm)
            },
            "RESOURCE_LEASE_RELEASED",
            "RESOURCE_RELEASED",
            JsonSerializer.Serialize(new { lease.TaskId, lease.Kind, lease.Token })), cancellationToken).ConfigureAwait(false);
    }

    private static StateKey Key(string projectId, string kind, string token) =>
        new(projectId, EntityKinds.ResourceLease, $"{kind.ToUpperInvariant()}:{token}");

    private static EngineeringResourceLease Parse(StateSnapshot state) =>
        JsonSerializer.Deserialize<EngineeringResourceLease>(state.PayloadJson)
        ?? throw new InvalidDataException("Invalid resource lease payload");

    private static IEnumerable<string> CandidateTokens(ResourceLeaseRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PreferredToken))
        {
            yield return request.PreferredToken.Trim();
            yield break;
        }

        if (!request.Kind.Equals("PORT", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Non-port resources require PreferredToken", nameof(request));
        }

        for (var port = request.PortRangeStart; port <= request.PortRangeEnd; port++)
        {
            yield return port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static bool PortAppearsAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void Validate(ResourceLeaseRequest request)
    {
        _ = new StateKey(request.ProjectId, EntityKinds.Task, request.TaskId).Normalize();
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.RuntimeId) ||
            string.IsNullOrWhiteSpace(request.Kind) || request.Ttl <= TimeSpan.Zero || request.Ttl > TimeSpan.FromHours(24) ||
            (request.Kind.Equals("PORT", StringComparison.OrdinalIgnoreCase) &&
             (request.PortRangeStart is < 1024 or > 65535 || request.PortRangeEnd < request.PortRangeStart || request.PortRangeEnd > 65535)))
        {
            throw new ArgumentException("Resource lease request is invalid", nameof(request));
        }
    }
}

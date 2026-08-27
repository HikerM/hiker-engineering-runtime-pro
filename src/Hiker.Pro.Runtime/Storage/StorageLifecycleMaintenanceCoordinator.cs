using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.State;

namespace Hiker.Pro.Storage;

public sealed class StorageLifecycleMaintenanceCoordinator(StateCoordinator state)
{
    public async Task<StorageLifecycleMaintenanceResult> RunAsync(
        string operationId,
        string projectId,
        StorageMaintenancePolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 120)
            throw new ArgumentException("Maintenance operation ID must contain 1-120 characters", nameof(operationId));
        if (policy.HistoricalBefore > DateTimeOffset.UtcNow || policy.ArtifactRetentionBefore > DateTimeOffset.UtcNow)
            throw new ArgumentException("Storage retention cutoffs cannot be in the future", nameof(policy));
        if (policy.KeepRecentEvents is < 1 or > 100_000 || policy.KeepRecentOperations is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(policy), "Storage maintenance windows are invalid");

        var before = await state.GetStorageMetricsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var events = await state.RollupEventsAsync(new EventRollupRequest(
            Child(operationId, "events"), projectId, policy.KeepRecentEvents, 100_000), cancellationToken).ConfigureAwait(false);
        var operations = await state.RetainOperationsAsync(
            Child(operationId, "operations"), projectId, policy.HistoricalBefore, policy.KeepRecentOperations, cancellationToken).ConfigureAwait(false);
        var dependencies = await state.RetainDependenciesAsync(
            Child(operationId, "dependencies"), projectId, policy.HistoricalBefore, cancellationToken).ConfigureAwait(false);
        var entities = await state.RetainEntitiesAsync(
            Child(operationId, "entities"), projectId, policy.HistoricalBefore, cancellationToken).ConfigureAwait(false);
        var artifacts = await state.CollectUnreachableArtifactsAsync(
            Child(operationId, "artifacts"), projectId, policy.ArtifactRetentionBefore, cancellationToken).ConfigureAwait(false);
        var compaction = policy.CompactWhenSafe
            ? await state.CompactStorageAsync(Child(operationId, "compact"), projectId, cancellationToken).ConfigureAwait(false)
            : null;
        var after = await state.GetStorageMetricsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var blocked = compaction?.Status == "BLOCKED_ACTIVE_WRITER_OR_RECOVERY";
        return new StorageLifecycleMaintenanceResult(
            blocked ? "PARTIAL_COMPACTION_BLOCKED" : "COMPLETE",
            events,
            operations,
            dependencies,
            entities,
            artifacts,
            compaction,
            before,
            after);
    }

    private static string Child(string operationId, string suffix)
    {
        var candidate = $"{operationId}:{suffix}";
        if (candidate.Length <= 160) return candidate;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();
        return $"storage-{hash[..32]}:{suffix}";
    }
}

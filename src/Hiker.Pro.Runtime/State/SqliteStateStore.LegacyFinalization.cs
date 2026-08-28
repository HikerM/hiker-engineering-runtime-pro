using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<LegacyRuntimeInspection> InspectLegacyRuntimeAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalized = new StateKey(projectId, EntityKinds.Project, "legacy-finalize").Normalize().ProjectId;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await ReadLegacyRuntimeAsync(connection, transaction: null, normalized, cancellationToken).ConfigureAwait(false);
        await using var completed = connection.CreateCommand();
        completed.CommandText = "SELECT COUNT(*) FROM operations WHERE project_id=$project AND operation_type='LEGACY_RUNTIME_FINALIZED' AND status='COMPLETE';";
        completed.Parameters.AddWithValue("$project", normalized);
        var completionRecorded = Convert.ToInt64(
            await completed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) > 0;
        return Inspect(normalized, snapshots, completionRecorded);
    }

    public async Task<LegacyRuntimeFinalizeResult> FinalizeLegacyRuntimeAsync(
        LegacyRuntimeFinalizeRequest request,
        CancellationToken cancellationToken = default)
    {
        var projectId = new StateKey(request.ProjectId, EntityKinds.Project, "legacy-finalize").Normalize().ProjectId;
        var expectedOperationId = LegacyRuntimeFinalizationFacts.OperationId(
            projectId,
            request.SourceFingerprint,
            request.MigrationGeneration);
        if (!string.Equals(request.OperationId, expectedOperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Legacy finalization operation id does not match the project", nameof(request));
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var snapshots = await ReadLegacyRuntimeAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
        var inspection = Inspect(projectId, snapshots, completionRecorded: false);
        var prior = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);

        if (inspection.RequiresReview > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new LegacyRuntimeFinalizeResult(
                "REQUIRES_REVIEW",
                projectId,
                await CurrentGenerationAsync(connection, cancellationToken).ConfigureAwait(false),
                0,
                new Dictionary<string, int>(StringComparer.Ordinal),
                inspection.ReviewFacts.Select(static fact => fact.Description).ToArray());
        }

        var candidates = snapshots.Where(IsFinalizable).ToArray();
        if (prior is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Length > 0)
            {
                return new LegacyRuntimeFinalizeResult(
                    "RECOVERY_REQUIRED",
                    projectId,
                    prior.Value.Generation,
                    0,
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    new[] { "LEGACY_RUNTIME_CHANGED_AFTER_FINALIZATION" });
            }

            try
            {
                var replay = JsonSerializer.Deserialize<LegacyRuntimeFinalizeResult>(prior.Value.ResultJson ?? string.Empty);
                return replay is null
                    ? RecoveryRequired(projectId, prior.Value.Generation, "FINALIZATION_RECEIPT_INVALID")
                    : replay with { Status = "ALREADY_FINALIZED" };
            }
            catch (JsonException)
            {
                return RecoveryRequired(projectId, prior.Value.Generation, "FINALIZATION_RECEIPT_INVALID");
            }
        }

        if (candidates.Length == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var currentGeneration = await CurrentGenerationAsync(connection, cancellationToken).ConfigureAwait(false);
            return new LegacyRuntimeFinalizeResult(
                "ALREADY_FINALIZED",
                projectId,
                currentGeneration,
                0,
                new Dictionary<string, int>(StringComparer.Ordinal),
                Array.Empty<string>());
        }

        var byKind = candidates
            .GroupBy(static snapshot => snapshot.Key.Kind, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var requestFingerprint = RequestHash(string.Join('\n', candidates.Select(static snapshot => string.Join('|',
            snapshot.Key.ProjectId,
            snapshot.Key.Kind,
            snapshot.Key.EntityId,
            snapshot.Generation,
            snapshot.SourceFingerprint,
            snapshot.DependencyFingerprint))));
        var representative = candidates[0];
        var eventPayload = JsonSerializer.Serialize(new
        {
            source_authority = LegacyRuntimeFinalizationFacts.Authority,
            source_schema = LegacyRuntimeFinalizationFacts.SchemaVersion,
            finalized = candidates.Length,
            kinds = byKind
        });
        await InsertPreparedOperationAsync(
            connection,
            transaction,
            new TransactionRequest(
                request.OperationId,
                ArchiveMutation(representative),
                "LEGACY_RUNTIME_FINALIZED",
                "COMMUNITY_5_18_RUNTIME_ARCHIVED",
                eventPayload),
            representative.Key,
            requestFingerprint,
            cancellationToken).ConfigureAwait(false);

        var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var updatedAt = DateTimeOffset.UtcNow;
        foreach (var snapshot in candidates)
        {
            await UpsertEntityAsync(
                connection,
                transaction,
                ArchiveMutation(snapshot),
                generation,
                updatedAt,
                cancellationToken).ConfigureAwait(false);
        }

        var result = new LegacyRuntimeFinalizeResult(
            "FINALIZED",
            projectId,
            generation,
            candidates.Length,
            byKind,
            Array.Empty<string>());
        await InsertEventAsync(
            connection,
            transaction,
            new EngineeringEvent(
                request.OperationId,
                projectId,
                "LEGACY_RUNTIME_FINALIZED",
                null,
                null,
                generation,
                "COMMUNITY_5_18_RUNTIME_ARCHIVED",
                eventPayload,
                updatedAt),
            cancellationToken).ConfigureAwait(false);
        await CompleteOperationAsync(
            connection,
            transaction,
            request.OperationId,
            generation,
            updatedAt,
            cancellationToken,
            JsonSerializer.Serialize(result)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static LegacyRuntimeInspection Inspect(
        string projectId,
        IReadOnlyList<StateSnapshot> snapshots,
        bool completionRecorded)
    {
        var finalizable = snapshots.Where(IsFinalizable).ToArray();
        var review = snapshots.Where(RequiresReview).ToArray();
        return new LegacyRuntimeInspection(
            projectId,
            finalizable.Length,
            snapshots.Count(static snapshot => snapshot.Lifecycle == StateLifecycle.Archived),
            review.Length,
            finalizable
                .GroupBy(static snapshot => snapshot.Key.Kind, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
            ContractCollections.Freeze(finalizable.Take(32).Select(Reference)),
            ContractCollections.Freeze(review.Take(32).Select(Reference)),
            completionRecorded);
    }

    private static LegacyRuntimeFactReference Reference(StateSnapshot snapshot) => new(
        snapshot.Key.Kind,
        snapshot.Key.EntityId,
        snapshot.SchemaVersion,
        snapshot.Lifecycle);

    private static bool IsFinalizable(StateSnapshot snapshot) =>
        string.Equals(snapshot.SchemaVersion, LegacyRuntimeFinalizationFacts.SchemaVersion, StringComparison.Ordinal) &&
        snapshot.Lifecycle is not StateLifecycle.Archived and not StateLifecycle.Corrupt;

    private static bool RequiresReview(StateSnapshot snapshot) =>
        !string.Equals(snapshot.SchemaVersion, LegacyRuntimeFinalizationFacts.SchemaVersion, StringComparison.Ordinal) ||
        snapshot.Lifecycle == StateLifecycle.Corrupt;

    private static StateMutation ArchiveMutation(StateSnapshot snapshot) => new(
        snapshot.Key,
        snapshot.Generation,
        snapshot.GoalRevision,
        StateLifecycle.Archived,
        snapshot.Authority,
        snapshot.SchemaVersion,
        snapshot.SourceFingerprint,
        snapshot.DependencyFingerprint,
        snapshot.PayloadJson,
        RetentionTier.Archived);

    private static LegacyRuntimeFinalizeResult RecoveryRequired(string projectId, long generation, string reason) => new(
        "RECOVERY_REQUIRED",
        projectId,
        generation,
        0,
        new Dictionary<string, int>(StringComparer.Ordinal),
        new[] { reason });

    private static async Task<IReadOnlyList<StateSnapshot>> ReadLegacyRuntimeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        var kinds = LegacyRuntimeFinalizationFacts.RuntimeKinds.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = kinds.Select((_, index) => $"$kind{index}").ToArray();
        command.CommandText = $"""
            SELECT {SnapshotColumns}
            FROM entities
            WHERE project_id=$project
              AND authority=$authority
              AND kind IN ({string.Join(',', parameters)})
            ORDER BY kind,entity_id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$authority", LegacyRuntimeFinalizationFacts.Authority);
        for (var index = 0; index < kinds.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], kinds[index]);
        }

        var values = new List<StateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteMapping.ReadSnapshot(reader));
        }

        return ContractCollections.Freeze(values);
    }

    private static async Task<long> CurrentGenerationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='global_generation';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}

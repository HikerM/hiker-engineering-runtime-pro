using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Storage;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<EventRollupResult> RollupEventsAsync(EventRollupRequest request, CancellationToken cancellationToken = default)
    {
        ValidateStorageOperation(request.OperationId, request.ProjectId);
        if (request.KeepRecentEvents is < 1 or > 100_000 || request.MaximumEvents is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(request), "Event rollup budgets are invalid");
        var fingerprint = RequestHash($"event-rollup|{request.ProjectId}|{request.KeepRecentEvents}|{request.MaximumEvents}");
        var prior = await ReadStorageMaintenanceAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            EnsureStorageReplay(prior.Value, "EVENT_ROLLUP", fingerprint);
            if (prior.Value.Status == "COMPLETE" && prior.Value.ResultJson is not null)
                return JsonSerializer.Deserialize<EventRollupResult>(prior.Value.ResultJson, StorageJson.Options)! with { Idempotent = true };
        }
        else
        {
            await BeginStorageMaintenanceAsync(request.OperationId, request.ProjectId, "EVENT_ROLLUP", fingerprint, "MARKED_FOR_ARCHIVE", null, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await CreateRuntimeSnapshotAsync($"{request.OperationId}:snapshot", request.ProjectId, cancellationToken).ConfigureAwait(false);
        var plan = prior?.Status is "ARCHIVE_WRITTEN" or "ARCHIVE_VERIFIED" && prior.Value.ResultJson is not null
            ? JsonSerializer.Deserialize<EventArchivePlan>(prior.Value.ResultJson, StorageJson.Options)
              ?? throw new InvalidDataException("Event archive recovery plan is invalid")
            : await WriteEventArchiveAsync(request, snapshot, cancellationToken).ConfigureAwait(false);
        if (plan.Events.Count == 0)
        {
            var empty = new EventRollupResult("NO_ROLLUP_REQUIRED", null, snapshot.SnapshotId, 0, 0, 0, 0, 0, null, false);
            await CompleteStorageMaintenanceAsync(request.OperationId, JsonSerializer.Serialize(empty, StorageJson.Options), cancellationToken).ConfigureAwait(false);
            return empty;
        }
        await VerifyEventArchiveAsync(plan.Segment, cancellationToken).ConfigureAwait(false);
        if (prior?.Status != "ARCHIVE_VERIFIED")
        {
            await using var receiptConnection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var receiptTransaction = receiptConnection.BeginTransaction(deferred: false);
            await UpdateStorageMaintenanceAsync(receiptConnection, receiptTransaction, request.OperationId, "ARCHIVE_VERIFIED",
                JsonSerializer.Serialize(plan, StorageJson.Options), cancellationToken).ConfigureAwait(false);
            await receiptTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var segment = plan.Segment;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var item in plan.Events)
        {
            await using var dedup = connection.CreateCommand();
            dedup.Transaction = transaction;
            dedup.CommandText = """
                INSERT INTO event_dedup(event_id,project_id,generation,fingerprint,segment_id)
                VALUES($id,$project,$generation,$fingerprint,$segment)
                ON CONFLICT(event_id) DO NOTHING;
                """;
            dedup.Parameters.AddWithValue("$id", item.Event.EventId);
            dedup.Parameters.AddWithValue("$project", request.ProjectId);
            dedup.Parameters.AddWithValue("$generation", item.Event.Generation);
            dedup.Parameters.AddWithValue("$fingerprint", item.Fingerprint);
            dedup.Parameters.AddWithValue("$segment", segment.SegmentId);
            await dedup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertEventSegmentAsync(connection, transaction, segment, cancellationToken).ConfigureAwait(false);
        await using (var deleteEvents = connection.CreateCommand())
        {
            deleteEvents.Transaction = transaction;
            deleteEvents.CommandText = "DELETE FROM engineering_events WHERE project_id=$project AND sequence BETWEEN $first AND $last;";
            deleteEvents.Parameters.AddWithValue("$project", request.ProjectId);
            deleteEvents.Parameters.AddWithValue("$first", segment.FirstSequence);
            deleteEvents.Parameters.AddWithValue("$last", segment.LastSequence);
            await deleteEvents.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var deleteDeltas = connection.CreateCommand())
        {
            deleteDeltas.Transaction = transaction;
            deleteDeltas.CommandText = "DELETE FROM state_deltas WHERE project_id=$project AND generation <= $generation;";
            deleteDeltas.Parameters.AddWithValue("$project", request.ProjectId);
            deleteDeltas.Parameters.AddWithValue("$generation", segment.ToGeneration);
            await deleteDeltas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var result = new EventRollupResult("COMPLETE", segment.SegmentId, snapshot.SnapshotId, segment.EventCount, segment.DeltaCount,
            segment.FirstSequence, segment.LastSequence, segment.Bytes, segment.Sha256, false);
        await UpdateStorageMaintenanceAsync(connection, transaction, request.OperationId, "COMPLETE", JsonSerializer.Serialize(result, StorageJson.Options), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<EngineeringEvent>> ReadEventSegmentAsync(
        string projectId,
        string segmentId,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(projectId, "EVENT_SEGMENT", segmentId).Normalize();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var segment = await ReadEventSegmentRecordAsync(connection, projectId, segmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Event Segment does not exist");
        var path = _layout.ResolveAiRelativePath(segment.RelativeRef);
        var archiveRoot = Path.GetFullPath(_layout.EventArchiveRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("Event Segment reference is missing or escapes Archive");
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var actual = Hash(text);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(segment.Sha256)))
            throw new InvalidDataException("Event Segment fingerprint mismatch");
        var payload = JsonSerializer.Deserialize<EventArchivePayload>(text, StorageJson.Options)
            ?? throw new InvalidDataException("Event Segment payload is invalid");
        return ContractCollections.Freeze(payload.Events.Select(static value => value.Event));
    }

    private async Task<EventArchivePlan> WriteEventArchiveAsync(EventRollupRequest request, RuntimeStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var total = await CountProjectAsync(connection, "engineering_events", request.ProjectId, cancellationToken).ConfigureAwait(false);
        if (total <= request.KeepRecentEvents) return EventArchivePlan.Empty(snapshot.SnapshotId);
        var archiveCount = (int)Math.Min(request.MaximumEvents, total - request.KeepRecentEvents);
        var events = await ReadOldestEventsAsync(connection, request.ProjectId, archiveCount, cancellationToken).ConfigureAwait(false);
        if (events.Count == 0) return EventArchivePlan.Empty(snapshot.SnapshotId);
        var toGeneration = events.Max(static value => value.Event.Generation);
        var deltas = await ReadDeltasThroughAsync(connection, request.ProjectId, toGeneration, cancellationToken).ConfigureAwait(false);
        var payload = new EventArchivePayload(snapshot.SnapshotId, events, deltas);
        var json = JsonSerializer.Serialize(payload, StorageJson.Options);
        StoragePayloadPolicy.EnsureArchiveSafe(json);
        var hash = Hash(json);
        _layout.EnsureDirectories();
        var final = Path.Combine(_layout.EventArchiveRoot, $"{hash}.json");
        var temporary = Path.Combine(_layout.EventArchiveRoot, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (!File.Exists(final))
            {
                await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
                File.Move(temporary, final);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var first = events.Min(static value => value.Sequence);
        var last = events.Max(static value => value.Sequence);
        var relative = Path.GetRelativePath(_layout.AiRoot, final).Replace(Path.DirectorySeparatorChar, '/');
        var segment = new ArchivedEventSegment($"event-segment-{first:D12}-{last:D12}-{hash[..12]}", request.ProjectId,
            events.Min(static value => value.Event.Generation), toGeneration, first, last, events.Count, deltas.Count, relative, hash,
            new FileInfo(final).Length, "VERIFIED", DateTimeOffset.UtcNow);
        return new EventArchivePlan(snapshot.SnapshotId, segment, events, deltas.Count);
    }

    private static async Task<IReadOnlyList<SequencedEvent>> ReadOldestEventsAsync(SqliteConnection connection, string projectId, int count, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence,event_id,event_type,entity_kind,entity_id,generation,summary_code,payload_json,occurred_at
            FROM engineering_events WHERE project_id=$project ORDER BY sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$limit", count);
        var values = new List<SequencedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var engineeringEvent = new EngineeringEvent(reader.GetString(1), projectId, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture));
            values.Add(new SequencedEvent(reader.GetInt64(0), engineeringEvent, EventFingerprint(engineeringEvent)));
        }
        return values;
    }

    private async Task VerifyEventArchiveAsync(ArchivedEventSegment segment, CancellationToken cancellationToken)
    {
        var path = _layout.ResolveAiRelativePath(segment.RelativeRef);
        var archiveRoot = Path.GetFullPath(_layout.EventArchiveRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("Event archive verification source is missing or escapes Archive");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(segment.Sha256)))
            throw new InvalidDataException("Event archive verification fingerprint mismatch");
    }

    private static async Task<IReadOnlyList<StateSnapshot>> ReadDeltasThroughAsync(SqliteConnection connection, string projectId, long generation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation,kind,entity_id,goal_revision,lifecycle,retention,authority,schema_version,source_fingerprint,dependency_fingerprint,payload_json,updated_at
            FROM state_deltas WHERE project_id=$project AND generation <= $generation ORDER BY generation,kind,entity_id LIMIT 200001;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$generation", generation);
        var values = new List<StateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(DeltaSnapshot(projectId, reader));
        if (values.Count > 200_000) throw new InvalidOperationException("Event rollup delta budget is exhausted");
        return values;
    }

    private static async Task InsertEventSegmentAsync(SqliteConnection connection, SqliteTransaction transaction, ArchivedEventSegment segment, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_segments(segment_id,project_id,from_generation,to_generation,first_sequence,last_sequence,event_count,delta_count,relative_ref,sha256,bytes,status,created_at)
            VALUES($id,$project,$from,$to,$first,$last,$events,$deltas,$ref,$hash,$bytes,$status,$created)
            ON CONFLICT(segment_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", segment.SegmentId);
        command.Parameters.AddWithValue("$project", segment.ProjectId);
        command.Parameters.AddWithValue("$from", segment.FromGeneration);
        command.Parameters.AddWithValue("$to", segment.ToGeneration);
        command.Parameters.AddWithValue("$first", segment.FirstSequence);
        command.Parameters.AddWithValue("$last", segment.LastSequence);
        command.Parameters.AddWithValue("$events", segment.EventCount);
        command.Parameters.AddWithValue("$deltas", segment.DeltaCount);
        command.Parameters.AddWithValue("$ref", segment.RelativeRef);
        command.Parameters.AddWithValue("$hash", segment.Sha256);
        command.Parameters.AddWithValue("$bytes", segment.Bytes);
        command.Parameters.AddWithValue("$status", segment.Status);
        command.Parameters.AddWithValue("$created", segment.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Fingerprint, long Generation)?> ReadArchivedEventFingerprintAsync(
        SqliteConnection connection, SqliteTransaction transaction, string eventId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT fingerprint,generation FROM event_dedup WHERE event_id=$id;";
        command.Parameters.AddWithValue("$id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static async Task<long> CountProjectAsync(SqliteConnection connection, string table, string projectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE project_id=$project;";
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ArchivedEventSegment?> ReadEventSegmentRecordAsync(SqliteConnection connection, string projectId, string segmentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_generation,to_generation,first_sequence,last_sequence,event_count,delta_count,relative_ref,sha256,bytes,status,created_at
            FROM event_segments WHERE project_id=$project AND segment_id=$id;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$id", segmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ArchivedEventSegment(segmentId, projectId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private sealed record SequencedEvent(long Sequence, EngineeringEvent Event, string Fingerprint);
    private sealed record EventArchivePayload(string SnapshotId, IReadOnlyList<SequencedEvent> Events, IReadOnlyList<StateSnapshot> Deltas);
    private sealed record EventArchivePlan(string SnapshotId, ArchivedEventSegment Segment, IReadOnlyList<SequencedEvent> Events, int DeltaCount)
    {
        public static EventArchivePlan Empty(string snapshotId) => new(snapshotId,
            new ArchivedEventSegment("", "", 0, 0, 0, 0, 0, 0, "", "", 0, "EMPTY", default), Array.Empty<SequencedEvent>(), 0);
    }
}

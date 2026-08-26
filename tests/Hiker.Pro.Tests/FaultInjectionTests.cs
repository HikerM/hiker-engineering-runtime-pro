using System.Text;
using Hiker.Pro.Artifacts;
using Hiker.Pro.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class FaultInjectionTests
{
    [Fact]
    public async Task FaultBeforeEventInsert_RollsBackDomainAndJournalThenAllowsSafeRetry()
    {
        await using var project = await TestProject.CreateAsync();
        await ExecuteSqlAsync(project.Layout.DatabasePath, """
            CREATE TRIGGER inject_event_failure
            BEFORE INSERT ON engineering_events
            BEGIN
                SELECT RAISE(ABORT, 'injected event failure');
            END;
            """);
        var request = Request("fault-event", EntityKinds.Task, "task-fault");
        await Assert.ThrowsAsync<SqliteException>(() => project.Store.CompareAndSwapAsync(request));
        Assert.Null(await project.Store.ReadAsync(request.Mutation.Key));
        Assert.Null(await project.Store.ReadOperationReceiptAsync(request.OperationId));
        await ExecuteSqlAsync(project.Layout.DatabasePath, "DROP TRIGGER inject_event_failure;");
        var recovered = await project.Store.CompareAndSwapAsync(request);
        Assert.True(recovered.Committed);
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Events);
    }

    [Fact]
    public async Task FaultDuringReleaseCandidateCommit_CreatesNeitherHalfCandidateNorDuplicate()
    {
        await using var project = await TestProject.CreateAsync();
        await ExecuteSqlAsync(project.Layout.DatabasePath, """
            CREATE TRIGGER inject_candidate_failure
            BEFORE INSERT ON engineering_events
            WHEN NEW.event_type = 'RELEASE_CREATED'
            BEGIN
                SELECT RAISE(ABORT, 'injected release failure');
            END;
            """);
        var request = Request("fault-candidate", EntityKinds.Candidate, "candidate-1") with
        {
            EventType = "RELEASE_CREATED",
            SummaryCode = "CANDIDATE_CREATED"
        };
        await Assert.ThrowsAsync<SqliteException>(() => project.Store.CompareAndSwapAsync(request));
        Assert.Null(await project.Store.ReadAsync(request.Mutation.Key));
        await ExecuteSqlAsync(project.Layout.DatabasePath, "DROP TRIGGER inject_candidate_failure;");
        Assert.True((await project.Store.CompareAndSwapAsync(request)).Committed);
        Assert.Equal("IDEMPOTENT_REPLAY", (await project.Store.CompareAndSwapAsync(request)).Status);
        Assert.Equal(1, (await project.Store.GetMetricsAsync()).Events);
    }

    [Fact]
    public async Task FaultDuringArtifactWrite_RemovesTemporaryBytesAndDoesNotRegisterArtifact()
    {
        await using var project = await TestProject.CreateAsync();
        var store = new ContentAddressedArtifactStore(project.Layout, project.Store);
        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(new ArtifactPutRequest(
            "project-1",
            "FAULT_FIXTURE",
            new ThrowingStream(Encoding.UTF8.GetBytes(new string('x', 200_000)), 64_000),
            "task-1",
            "evidence-1",
            "source-1",
            RetentionTier.Temp)));
        Assert.Empty(Directory.EnumerateFiles(project.Layout.ArtifactsRoot, ".upload-*.tmp", SearchOption.TopDirectoryOnly));
        Assert.Equal(0, (await project.Store.GetMetricsAsync()).Artifacts);
    }

    [Fact]
    public async Task FaultBeforeCasFromGoalChange_RejectsStaleWriteAndPreservesNewGoalFact()
    {
        await using var project = await TestProject.CreateAsync();
        var original = Request("goal-original", EntityKinds.Goal, "goal-1");
        var created = await project.Store.CompareAndSwapAsync(original);
        var staleGeneration = created.CurrentGeneration;
        var changed = Request("goal-change", EntityKinds.Goal, "goal-1") with
        {
            Mutation = original.Mutation with
            {
                ExpectedGeneration = staleGeneration,
                GoalRevision = 2,
                PayloadJson = "{\"revision\":2}"
            },
            EventType = "GOAL_CHANGED"
        };
        Assert.True((await project.Store.CompareAndSwapAsync(changed)).Committed);
        var stale = Request("stale-agent-write", EntityKinds.Goal, "goal-1") with
        {
            Mutation = original.Mutation with
            {
                ExpectedGeneration = staleGeneration,
                PayloadJson = "{\"revision\":1,\"stale\":true}"
            }
        };
        var rejected = await project.Store.CompareAndSwapAsync(stale);
        Assert.True(rejected.Conflict);
        Assert.Equal("STALE_CONTEXT", rejected.Status);
        var current = await project.Store.ReadAsync(original.Mutation.Key);
        Assert.Equal(2, current?.GoalRevision);
        Assert.Contains("revision\":2", current?.PayloadJson);
    }

    [Fact]
    public async Task InvalidMigrationBackup_IsRejectedBeforeCurrentStateChanges()
    {
        await using var project = await TestProject.CreateAsync();
        var request = Request("migration-seed", EntityKinds.Migration, "schema-3");
        Assert.True((await project.Store.CompareAndSwapAsync(request)).Committed);
        var corrupt = Path.Combine(project.Root, "corrupt-backup.db");
        await File.WriteAllTextAsync(corrupt, "not a sqlite database");
        var service = new StateBackupService(project.Layout);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RestoreAsync(corrupt));
        var current = await project.Store.ReadAsync(request.Mutation.Key);
        Assert.NotNull(current);
        Assert.Equal("{\"status\":\"VALID\"}", current?.PayloadJson);
        Assert.Equal("HEALTHY", (await project.Store.HealthCheckAsync()).Status);
    }

    private static TransactionRequest Request(string operationId, string kind, string id) => new(
        operationId,
        new StateMutation(
            new StateKey("project-1", kind, id),
            0,
            1,
            StateLifecycle.Valid,
            "FAULT_INJECTION",
            "3.0.0",
            "source-1",
            "deps-1",
            "{\"status\":\"VALID\"}"),
        "TASK_UPDATED",
        "FAULT_INJECTION",
        "{}");

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ThrowingStream(byte[] content, int throwAfter) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(Span<byte> buffer)
        {
            if (_position >= throwAfter) throw new IOException("Injected artifact stream failure");
            var count = Math.Min(buffer.Length, Math.Min(content.Length - _position, throwAfter - _position));
            content.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

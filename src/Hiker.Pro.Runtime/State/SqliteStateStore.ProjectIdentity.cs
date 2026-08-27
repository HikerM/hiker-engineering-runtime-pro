using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    public async Task<ProjectIdentityInventory> InspectProjectIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_layout.DatabasePath))
        {
            return EmptyIdentityInventory(databaseExists: false);
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _layout.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var quickCheck = connection.CreateCommand())
            {
                quickCheck.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(await quickCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return EmptyIdentityInventory(databaseExists: true, healthy: false, errorCode: "SQLITE_QUICK_CHECK_FAILED");
                }
            }

            if (!await HasRequiredIdentityTablesAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                return EmptyIdentityInventory(databaseExists: true, healthy: false, errorCode: "STATE_SCHEMA_INCOMPLETE");
            }

            var activeIds = await ReadProjectIdsAsync(connection, activeOnly: true, cancellationToken).ConfigureAwait(false);
            var identities = await ReadIdentityRecordsAsync(connection, activeIds, cancellationToken).ConfigureAwait(false);
            var historicalIds = activeIds;
            var completedImports = await ReadCompletedImportIdsAsync(connection, activeIds, cancellationToken).ConfigureAwait(false);
            var legacyIdentity = identities.Any(static identity => IdentityOriginMayImportLegacy(identity.PayloadJson));
            var importedFacts = completedImports.Count == 0 && (identities.Count == 0 || legacyIdentity) && await ExistsAsync(
                    connection,
                    "SELECT EXISTS(SELECT 1 FROM entities WHERE authority='COMMUNITY_5_18_IMPORT' LIMIT 1);",
                    cancellationToken).ConfigureAwait(false);
            var entities = await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM entities LIMIT 1);", cancellationToken).ConfigureAwait(false);
            var operations = await ExistsAsync(connection, "SELECT EXISTS(SELECT 1 FROM operations LIMIT 1);", cancellationToken).ConfigureAwait(false);
            return new ProjectIdentityInventory(
                true,
                true,
                null,
                ContractCollections.Freeze(identities),
                ContractCollections.Freeze(activeIds),
                ContractCollections.Freeze(historicalIds),
                ContractCollections.Freeze(completedImports),
                importedFacts,
                entities,
                operations);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return EmptyIdentityInventory(databaseExists: true, healthy: false, errorCode: exception.GetType().Name.ToUpperInvariant());
        }
    }

    public async Task<ProjectForkResult> ForkProjectAsync(
        ProjectForkRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceProject = new StateKey(request.SourceProjectId, EntityKinds.Project, ProjectIdentityFacts.EntityId).Normalize().ProjectId;
        var newProject = new StateKey(request.NewProjectId, EntityKinds.Project, ProjectIdentityFacts.EntityId).Normalize().ProjectId;
        if (string.Equals(sourceProject, newProject, StringComparison.Ordinal))
        {
            throw new ArgumentException("Fork requires a new project identity", nameof(request));
        }

        if (request.InheritedKinds.Count == 0 || request.InheritedKinds.Any(IsOperationalKind))
        {
            throw new InvalidDataException("Fork inheritance cannot be empty or contain operational state kinds");
        }

        SqliteMapping.ValidateJson(request.IdentityPayloadJson, nameof(request.IdentityPayloadJson));
        var fingerprint = ForkFingerprint(request with { SourceProjectId = sourceProject, NewProjectId = newProject });
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadOperationAsync(connection, transaction, request.OperationId, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (!string.Equals(prior.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ProjectForkResult("OPERATION_ID_CONFLICT", sourceProject, newProject, prior.Value.Generation, 0, 0, 0);
            }

            var replay = prior.Value.ResultJson is null
                ? new ProjectForkResult("IDEMPOTENT_REPLAY", sourceProject, newProject, prior.Value.Generation, 0, 0, 0)
                : JsonSerializer.Deserialize<ProjectForkResult>(prior.Value.ResultJson)! with
                {
                    Status = "IDEMPOTENT_REPLAY"
                };
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        var sourceIdentity = await ReadWithinAsync(
            connection,
            transaction,
            new StateKey(sourceProject, EntityKinds.Project, ProjectIdentityFacts.EntityId),
            cancellationToken).ConfigureAwait(false);
        if (sourceIdentity is null || sourceIdentity.Lifecycle != StateLifecycle.Valid)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectForkResult("SOURCE_IDENTITY_NOT_ACTIVE", sourceProject, newProject, 0, 0, 0, 0);
        }

        if (await ProjectHasAnyEntityAsync(connection, transaction, newProject, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectForkResult("TARGET_IDENTITY_ALREADY_EXISTS", sourceProject, newProject, 0, 0, 0, 0);
        }

        var sourceEntities = await ReadProjectEntitiesAsync(connection, transaction, sourceProject, cancellationToken).ConfigureAwait(false);
        var inherited = sourceEntities
            .Where(state => request.InheritedKinds.Contains(state.Key.Kind) && state.Lifecycle is StateLifecycle.Valid or StateLifecycle.Stale)
            .ToArray();
        var identityMutation = new StateMutation(
            new StateKey(newProject, EntityKinds.Project, ProjectIdentityFacts.EntityId),
            0,
            0,
            StateLifecycle.Valid,
            ProjectIdentityFacts.Authority,
            SqliteSchema.CurrentVersion,
            request.SourceFingerprint,
            request.SourceFingerprint,
            request.IdentityPayloadJson);
        var representative = new TransactionRequest(
            request.OperationId,
            identityMutation,
            "PROJECT_FORKED",
            "PROJECT_IDENTITY_FORKED",
            JsonSerializer.Serialize(new { source_project_id = sourceProject, new_project_id = newProject }));
        await InsertPreparedOperationAsync(connection, transaction, representative, identityMutation.Key, fingerprint, cancellationToken).ConfigureAwait(false);
        var generation = await NextGenerationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await UpsertEntityAsync(connection, transaction, identityMutation, generation, now, cancellationToken).ConfigureAwait(false);
        foreach (var source in inherited)
        {
            await UpsertEntityAsync(
                connection,
                transaction,
                new StateMutation(
                    new StateKey(newProject, source.Key.Kind, source.Key.EntityId),
                    0,
                    source.GoalRevision,
                    source.Lifecycle,
                    "PROJECT_FORK",
                    source.SchemaVersion,
                    source.SourceFingerprint,
                    source.DependencyFingerprint,
                    source.PayloadJson,
                    source.Retention),
                generation,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        var inheritedKeys = inherited
            .Select(static state => $"{state.Key.Kind}\0{state.Key.EntityId}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var edge in await ReadProjectEdgesAsync(connection, transaction, sourceProject, cancellationToken).ConfigureAwait(false))
        {
            if (inheritedKeys.Contains($"{edge.FromKind}\0{edge.FromId}") && inheritedKeys.Contains($"{edge.ToKind}\0{edge.ToId}"))
            {
                await UpsertEdgeAsync(connection, transaction, edge with { ProjectId = newProject }, now, cancellationToken).ConfigureAwait(false);
            }
        }

        var archived = await ArchiveSourceEntitiesAsync(connection, transaction, sourceProject, generation, now, cancellationToken).ConfigureAwait(false);
        await ArchiveSourceEdgesAsync(connection, transaction, sourceProject, now, cancellationToken).ConfigureAwait(false);
        var reset = sourceEntities.Count(static state => IsOperationalKind(state.Key.Kind) && state.Lifecycle is StateLifecycle.Valid or StateLifecycle.Stale);
        await TransferArtifactsAsync(connection, transaction, sourceProject, newProject, cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            new EngineeringEvent(
                request.OperationId,
                newProject,
                "PROJECT_FORKED",
                EntityKinds.Project,
                ProjectIdentityFacts.EntityId,
                generation,
                "PROJECT_IDENTITY_FORKED",
                representative.EventPayloadJson,
                now),
            cancellationToken).ConfigureAwait(false);
        var result = new ProjectForkResult("COMMITTED", sourceProject, newProject, generation, inherited.Length, archived, reset);
        await CompleteOperationAsync(
            connection,
            transaction,
            request.OperationId,
            generation,
            now,
            cancellationToken,
            JsonSerializer.Serialize(result)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static ProjectIdentityInventory EmptyIdentityInventory(
        bool databaseExists,
        bool healthy = true,
        string? errorCode = null) =>
        new(
            databaseExists,
            healthy,
            errorCode,
            Array.Empty<StateSnapshot>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            false,
            false,
            false);

    private static async Task<bool> HasRequiredIdentityTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('entities','operations','engineering_events','dependency_edges','materialized_views','artifacts');";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 6;
    }

    private static async Task<List<StateSnapshot>> ReadIdentityRecordsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken)
    {
        var values = new List<StateSnapshot>();
        foreach (var projectId in projectIds)
        {
            var identity = await ReadWithinAsync(
                connection,
                transaction: null,
                new StateKey(projectId, EntityKinds.Project, ProjectIdentityFacts.EntityId),
                cancellationToken).ConfigureAwait(false);
            if (identity?.Lifecycle == StateLifecycle.Valid) values.Add(identity);
        }
        return values;
    }

    private static async Task<List<string>> ReadProjectIdsAsync(
        SqliteConnection connection,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? "SELECT project_id FROM entities WHERE lifecycle IN ('VALID','STALE') GROUP BY project_id ORDER BY project_id LIMIT 3;"
            : "SELECT DISTINCT project_id FROM entities ORDER BY project_id LIMIT 3;";
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<List<string>> ReadCompletedImportIdsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        foreach (var projectId in projectIds)
        {
            var marker = await ReadWithinAsync(
                connection,
                transaction: null,
                new StateKey(projectId, EntityKinds.Migration, "community-5.18"),
                cancellationToken).ConfigureAwait(false);
            if (marker?.Lifecycle == StateLifecycle.Valid) values.Add(projectId);
        }
        return values;
    }

    private static bool IdentityOriginMayImportLegacy(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var origin = document.RootElement.GetProperty("origin").GetString();
            return origin is "LEGACY_MIGRATION" or "RECOVERED_LEGACY";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return true;
        }
    }

    private static async Task<bool> ExistsAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<IReadOnlyList<StateSnapshot>> ReadProjectEntitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SnapshotColumns} FROM entities WHERE project_id=$project ORDER BY kind, entity_id;";
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<StateSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(SqliteMapping.ReadSnapshot(reader));
        return values;
    }

    private static async Task<bool> ProjectHasAnyEntityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM entities WHERE project_id=$project LIMIT 1);";
        command.Parameters.AddWithValue("$project", projectId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<IReadOnlyList<DependencyEdge>> ReadProjectEdgesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id, from_kind, from_id, to_kind, to_id, relation, fingerprint, lifecycle FROM dependency_edges WHERE project_id=$project AND lifecycle='VALID' ORDER BY from_kind, from_id, to_kind, to_id;";
        command.Parameters.AddWithValue("$project", projectId);
        var values = new List<DependencyEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(SqliteMapping.ReadEdge(reader));
        return values;
    }

    private static async Task<int> ArchiveSourceEntitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE entities SET lifecycle='ARCHIVED', generation=$generation, updated_at=$updated WHERE project_id=$project AND lifecycle <> 'ARCHIVED';";
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$project", projectId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TransferArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceProject,
        string newProject,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE artifacts SET project_id=$newProject WHERE project_id=$sourceProject;";
        command.Parameters.AddWithValue("$newProject", newProject);
        command.Parameters.AddWithValue("$sourceProject", sourceProject);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ArchiveSourceEdgesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE dependency_edges SET lifecycle='ARCHIVED', updated_at=$updated WHERE project_id=$project AND lifecycle <> 'ARCHIVED';";
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$project", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsOperationalKind(string kind) => kind is
        EntityKinds.Task or EntityKinds.TaskDependency or EntityKinds.Ownership or EntityKinds.Worktree or
        EntityKinds.RuntimeLease or EntityKinds.Agent or EntityKinds.Checkpoint or EntityKinds.ResourceLease;

    private static string ForkFingerprint(ProjectForkRequest request)
    {
        var value = string.Join('\n',
            request.SourceProjectId,
            request.NewProjectId,
            request.IdentityPayloadJson,
            request.SourceFingerprint,
            string.Join(',', request.InheritedKinds.OrderBy(static value => value, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

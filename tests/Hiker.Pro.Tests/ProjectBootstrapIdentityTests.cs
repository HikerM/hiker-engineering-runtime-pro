using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ProjectBootstrapIdentityTests
{
    [Fact]
    public async Task ReadOnlyPreflight_NewProjectDoesNotCreateAiOrIdentity()
    {
        await using var project = await RawProject.CreateAsync();

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.NewProject, result.State);
        Assert.Equal("BLOCKED", result.Status);
        Assert.False(Directory.Exists(project.Layout.AiRoot));
    }

    [Fact]
    public async Task Init_GeneratesIdentityOnceAndRepeatedInitReusesIt()
    {
        await using var project = await RawProject.CreateAsync();

        var first = await project.Bootstrap.InitializeAsync();
        var beforeRetry = await project.Store.GetMetricsAsync();
        var retry = await project.Bootstrap.InitializeAsync();
        var afterRetry = await project.Store.GetMetricsAsync();

        Assert.True(Guid.TryParseExact(first.ProjectId, "D", out _));
        Assert.Equal(first.ProjectId, retry.ProjectId);
        Assert.True(first.IdentityCreated);
        Assert.True(retry.IdentityReused);
        Assert.Equal(beforeRetry, afterRetry);
        var identity = await project.Store.ReadAsync(new StateKey(first.ProjectId, EntityKinds.Project, ProjectBootstrapService.IdentityEntityId));
        Assert.Equal(ProjectBootstrapService.IdentityAuthority, identity?.Authority);
    }

    [Fact]
    public async Task LegacyProject_InitCreatesOneIdentityImportsFactsAndRetainsSources()
    {
        await using var project = await RawProject.CreateAsync();
        var taskPath = Path.Combine(project.Layout.AiRoot, "tasks", "TASK-001.json");
        Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
        await File.WriteAllTextAsync(taskPath, "{\"task_id\":\"TASK-001\",\"status\":\"CREATED\"}");

        var result = await project.Bootstrap.InitializeAsync();
        var preflight = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal("IMPORTED", result.Status);
        Assert.True(result.IdentityCreated);
        Assert.True(File.Exists(taskPath));
        Assert.Equal(ProjectBootstrapState.ActiveExistingProject, preflight.State);
        Assert.Equal(result.ProjectId, preflight.ProjectId);
        Assert.NotNull(await project.Store.ReadAsync(new StateKey(result.ProjectId, EntityKinds.Migration, "community-5.18")));
    }

    [Fact]
    public async Task ImportedLegacyNamespace_ResolvesSameIdentityWithoutReadSideWrite()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        var path = Path.Combine(project.Layout.AiRoot, "tasks", "TASK-001.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"task_id\":\"TASK-001\"}");
        Assert.Equal("IMPORTED", (await new Community518Importer(project.Layout, project.State).ImportAsync("6ddb0adf-d101-4cdc-ab86-545ad29ca8be")).Status);
        var before = await project.Store.GetMetricsAsync();

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);
        var after = await project.Store.GetMetricsAsync();

        Assert.Equal(ProjectBootstrapState.ActiveExistingProject, result.State);
        Assert.Equal("6ddb0adf-d101-4cdc-ab86-545ad29ca8be", result.ProjectId);
        Assert.Equal("LEGACY_STATE_NAMESPACE", result.IdentityAuthority);
        Assert.Empty(result.Identity.ActiveIdentityRecords);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task MultipleActiveIdentities_AreBlockedWithoutChoosingFirst()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        await SeedIdentityAsync(project, "project-a");
        await SeedIdentityAsync(project, "project-b");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.IdentityConflict, result.State);
        Assert.False(result.CanProceed);
        Assert.Null(result.ProjectId);
    }

    [Fact]
    public async Task MalformedIdentityPayload_IsControlledStateDamage()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        await SeedAsync(
            project,
            "project-invalid",
            EntityKinds.Project,
            ProjectBootstrapService.IdentityEntityId,
            ProjectBootstrapService.IdentityAuthority,
            "{\"project_id\":\"project-invalid\"}");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.StateDamaged, result.State);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public async Task SuppliedProjectIdMismatch_IsBlocked()
    {
        await using var project = await RawProject.CreateAsync();
        _ = await project.Bootstrap.InitializeAsync("project-authoritative");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, "project-other");

        Assert.Equal(ProjectBootstrapState.IdentityMismatch, result.State);
        Assert.Equal("project-authoritative", result.ProjectId);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public async Task ImportedFactWithoutMarker_IsMigrationIncompleteAndRetainsOriginalProjectId()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        await SeedAsync(project, "legacy-project", EntityKinds.Task, "TASK-001", "COMMUNITY_5_18_IMPORT", "{\"task_id\":\"TASK-001\"}");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.MigrationIncomplete, result.State);
        Assert.Equal("legacy-project", result.ProjectId);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
    }

    [Fact]
    public async Task StateFactsWithoutIdentityOrMigrationProof_AreBlocked()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        await SeedAsync(project, "unbound-project", EntityKinds.Task, "TASK-001", "STATE_COORDINATOR", "{\"task_id\":\"TASK-001\"}");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.IdentityMissing, result.State);
        Assert.Equal("unbound-project", result.ProjectId);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public async Task CompletedLegacyImport_IsReportedAsAlreadyImportedForMigrationIntent()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        var path = Path.Combine(project.Layout.AiRoot, "tasks", "TASK-001.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"task_id\":\"TASK-001\"}");
        Assert.Equal("IMPORTED", (await new Community518Importer(project.Layout, project.State).ImportAsync("legacy-project")).Status);

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.MigrateCommunity);

        Assert.Equal(ProjectBootstrapState.LegacyAlreadyImported, result.State);
        Assert.Equal("legacy-project", result.ProjectId);
        Assert.True(result.CanProceed);
    }

    [Fact]
    public async Task MovedProject_RetainsIdentityAndDoesNotDeriveIdFromPath()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"hiker-identity-source-{Guid.NewGuid():N}");
        var movedRoot = Path.Combine(Path.GetTempPath(), $"hiker-identity-moved-{Guid.NewGuid():N}");
        string projectId;
        await using (var source = await RawProject.CreateAsync(sourceRoot))
        {
            projectId = (await source.Bootstrap.InitializeAsync()).ProjectId;
        }
        SqliteConnection.ClearAllPools();
        CopyDirectory(sourceRoot, movedRoot);
        try
        {
            await using var moved = await RawProject.CreateAsync(movedRoot);
            var result = await moved.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

            Assert.Equal(ProjectBootstrapState.ProjectMoved, result.State);
            Assert.Equal(projectId, result.ProjectId);
            Assert.True(result.CanProceed);
        }
        finally
        {
            DeleteDirectory(sourceRoot);
            DeleteDirectory(movedRoot);
        }
    }

    [Fact]
    public async Task ExplicitFork_CreatesNewIdentityInheritsFactsAndResetsOperationalState()
    {
        await using var project = await RawProject.CreateAsync();
        _ = await project.Bootstrap.InitializeAsync("source-project");
        await SeedAsync(project, "source-project", EntityKinds.Requirement, "REQ-001", "STATE_COORDINATOR", "{\"requirement_id\":\"REQ-001\"}");
        await SeedAsync(project, "source-project", EntityKinds.Evidence, "EVIDENCE-001", "STATE_COORDINATOR", "{\"evidence_id\":\"EVIDENCE-001\"}");
        await SeedAsync(project, "source-project", EntityKinds.Task, "TASK-001", "STATE_COORDINATOR", "{\"task_id\":\"TASK-001\",\"status\":\"RUNNING\"}");
        await SeedAsync(project, "source-project", EntityKinds.RuntimeLease, "TASK-001", "STATE_COORDINATOR", "{\"task_id\":\"TASK-001\"}");

        var fork = await project.Bootstrap.ForkAsync("fork-project");
        var preflight = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal("COMMITTED", fork.Fork.Status);
        Assert.Equal(ProjectBootstrapState.ProjectForked, preflight.State);
        Assert.Equal("fork-project", preflight.ProjectId);
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("fork-project", EntityKinds.Requirement, "REQ-001")));
        Assert.NotNull(await project.Store.ReadAsync(new StateKey("fork-project", EntityKinds.Evidence, "EVIDENCE-001")));
        Assert.Null(await project.Store.ReadAsync(new StateKey("fork-project", EntityKinds.Task, "TASK-001")));
        Assert.Null(await project.Store.ReadAsync(new StateKey("fork-project", EntityKinds.RuntimeLease, "TASK-001")));
        var sourceIdentity = await project.Store.ReadAsync(new StateKey("source-project", EntityKinds.Project, ProjectBootstrapService.IdentityEntityId));
        Assert.Equal(StateLifecycle.Archived, sourceIdentity?.Lifecycle);
        Assert.NotNull(fork.SafetyBackup);
        Assert.True(File.Exists(fork.SafetyBackup!.BackupPath));

        var retry = await project.Bootstrap.ForkAsync("fork-project");
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Fork.Status);
        Assert.Null(retry.SafetyBackup);
    }

    [Fact]
    public async Task DamagedStateDatabase_IsRecoveryRequiredAndNeverAutoInitialized()
    {
        await using var project = await RawProject.CreateAsync();
        Directory.CreateDirectory(project.Layout.StateDirectory);
        await File.WriteAllBytesAsync(project.Layout.DatabasePath, Encoding.UTF8.GetBytes("not-a-sqlite-database"));
        var before = await File.ReadAllBytesAsync(project.Layout.DatabasePath);

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Read);

        Assert.Equal(ProjectBootstrapState.StateDamaged, result.State);
        Assert.Equal("RECOVERY_REQUIRED", result.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(project.Layout.DatabasePath));
    }

    [Fact]
    public async Task SchemaOnlyDatabasePlusLegacyFacts_IsMixedLegacyProject()
    {
        await using var project = await RawProject.CreateInitializedAsync();
        var path = Path.Combine(project.Layout.AiRoot, "context", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"project_id\":\"legacy\"}");

        var result = await project.Bootstrap.PreflightAsync(ProjectBootstrapIntent.Initialize);

        Assert.Equal(ProjectBootstrapState.MixedLegacyProject, result.State);
        Assert.True(result.CanProceed);
    }

    private static async Task SeedIdentityAsync(RawProject project, string projectId)
    {
        var rootFingerprint = Hash(Path.GetFullPath(project.Root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "hiker-project-identity/v1",
            project_id = projectId,
            origin = "INITIALIZE",
            parent_project_id = (string?)null,
            root_fingerprint = rootFingerprint,
            created_at = DateTimeOffset.UtcNow,
            updated_at = DateTimeOffset.UtcNow
        });
        await SeedAsync(project, projectId, EntityKinds.Project, ProjectBootstrapService.IdentityEntityId, ProjectBootstrapService.IdentityAuthority, payload);
    }

    private static async Task SeedAsync(
        RawProject project,
        string projectId,
        string kind,
        string id,
        string authority,
        string payload)
    {
        var snapshot = await project.State.ReadForReasoningAsync(new StateKey(projectId, kind, id));
        var result = await project.State.CommitAsync(
            snapshot,
            $"seed-{projectId}-{kind}-{id}",
            new StateMutation(
                new StateKey(projectId, kind, id),
                snapshot.State?.Generation ?? 0,
                0,
                StateLifecycle.Valid,
                authority,
                "3.0.0",
                Hash(payload),
                Hash(id),
                payload),
            "TEST_SEED",
            "TEST_SEED",
            "{}");
        Assert.True(result.Committed);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class RawProject : IAsyncDisposable
    {
        private readonly bool _ownsRoot;

        private RawProject(string root, bool ownsRoot)
        {
            Root = root;
            _ownsRoot = ownsRoot;
            Layout = new ProjectLayout(root);
            Store = new SqliteStateStore(Layout);
            State = new StateCoordinator(Store);
            Bootstrap = new ProjectBootstrapService(Layout, Store, State);
        }

        public string Root { get; }
        public ProjectLayout Layout { get; }
        public SqliteStateStore Store { get; }
        public StateCoordinator State { get; }
        public ProjectBootstrapService Bootstrap { get; }

        public static Task<RawProject> CreateAsync(string? root = null)
        {
            var ownsRoot = root is null;
            var value = root ?? Path.Combine(Path.GetTempPath(), $"hiker-bootstrap-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(value);
            return Task.FromResult(new RawProject(value, ownsRoot));
        }

        public static async Task<RawProject> CreateInitializedAsync()
        {
            var project = await CreateAsync();
            await project.Store.InitializeAsync();
            return project;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (_ownsRoot) DeleteDirectory(Root);
        }
    }
}

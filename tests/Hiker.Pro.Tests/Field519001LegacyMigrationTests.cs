using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Context;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class Field519001LegacyMigrationTests
{
    private const string ProjectId = "field-5-19-001";

    [Fact]
    public async Task ProjectDoctor_ReportsLegacyAggregateOwnershipWithoutTreatingItAsActiveRuntimeState()
    {
        await using var project = await TestProject.CreateAsync();
        var sourcePath = Path.Combine(project.Layout.AiRoot, "governance", "ownership.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        const string legacyPayload = """
            {"schema_version":"3.0.0","rules":[]}
            """;
        await File.WriteAllTextAsync(sourcePath, legacyPayload);

        var import = await new Community518Importer(project.Layout, project.Coordinator).ImportAsync(ProjectId);
        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("IMPORTED", import.Status);
        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, static finding =>
            finding.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW" &&
            finding.Entity == "governance:ownership.json" &&
            !finding.Repairable);
        var imported = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Ownership, "governance:ownership.json"));
        Assert.NotNull(imported);
        Assert.Equal("COMMUNITY_5_18_IMPORT", imported.Authority);
        Assert.Equal(StateLifecycle.Incompatible, imported.Lifecycle);
        Assert.Equal(legacyPayload.Replace(" ", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty), imported.PayloadJson);
    }

    [Fact]
    public async Task ProjectDoctor_ReportsLegacyAggregateTaskMapWithoutTreatingItAsActiveWorktreeState()
    {
        await using var project = await TestProject.CreateAsync();
        var sourcePath = Path.Combine(project.Layout.AiRoot, "workspace", "task-map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        const string legacyPayload = """
            {
              "schema_version":"3.0.0",
              "status":"ACCEPTED",
              "lanes":[],
              "policy":{"max_active_writers":2}
            }
            """;
        await File.WriteAllTextAsync(sourcePath, legacyPayload);

        var import = await new Community518Importer(project.Layout, project.Coordinator).ImportAsync(ProjectId);
        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("IMPORTED", import.Status);
        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, static finding =>
            finding.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW" &&
            finding.Entity == "workspace:task-map.json" &&
            !finding.Repairable);
        var imported = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Worktree, "workspace:task-map.json"));
        Assert.NotNull(imported);
        Assert.Equal("COMMUNITY_5_18_IMPORT", imported.Authority);
        Assert.Equal(StateLifecycle.Incompatible, imported.Lifecycle);
    }

    [Theory]
    [InlineData("missing-task", "{\"writer_runtime_id\":\"runtime-1\",\"workspace_root\":\"/fixture\",\"workspace_fingerprint\":\"workspace-1\",\"updated_at\":\"2026-08-27T00:00:00Z\"}", "task_id")]
    [InlineData("null-task", "{\"task_id\":null,\"writer_runtime_id\":\"runtime-1\",\"workspace_root\":\"/fixture\",\"workspace_fingerprint\":\"workspace-1\",\"updated_at\":\"2026-08-27T00:00:00Z\"}", "task_id")]
    [InlineData("missing-writer", "{\"task_id\":\"task-1\",\"workspace_root\":\"/fixture\",\"workspace_fingerprint\":\"workspace-1\",\"updated_at\":\"2026-08-27T00:00:00Z\"}", "writer_runtime_id")]
    public async Task ProjectDoctor_ReportsInvalidActiveOwnershipRequiredFields(
        string entityId,
        string payload,
        string missingField)
    {
        await using var project = await TestProject.CreateAsync();
        await SeedAsync(project, EntityKinds.Ownership, entityId, payload);

        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("BLOCKED", health.Status);
        var finding = Assert.Single(health.Findings, static item => item.Code == "INVALID_OWNERSHIP_STATE");
        Assert.Equal(entityId, finding.Entity);
        Assert.Contains(missingField, finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectDoctor_ReportsInvalidActiveWorktreeWithoutTaskId()
    {
        await using var project = await TestProject.CreateAsync();
        const string payload = """
            {"worktree_id":"worktree-1","writer_runtime_id":"runtime-1","root":"/fixture","git_common_directory":"/fixture/.git","head":"head-1","branch":"main","predicted_affected_scopes":[],"status":"ACTIVE","updated_at":"2026-08-27T00:00:00Z"}
            """;
        await SeedAsync(project, EntityKinds.Worktree, "worktree-1", payload);

        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("BLOCKED", health.Status);
        var finding = Assert.Single(health.Findings, static item => item.Code == "INVALID_WORKTREE_STATE");
        Assert.Contains("task_id", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rc1LegacyStateStillMarkedValid_IsRecognizedWithoutMutation()
    {
        await using var project = await TestProject.CreateAsync();
        const string payload = "{\"schema_version\":\"3.0.0\",\"rules\":[]}";
        var seeded = await SeedAsync(
            project,
            EntityKinds.Ownership,
            "governance:ownership.json",
            payload,
            authority: "COMMUNITY_5_18_IMPORT",
            lifecycle: StateLifecycle.Valid);
        var legacyWorktree = await SeedAsync(
            project,
            EntityKinds.Worktree,
            "workspace:task-map.json",
            "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}",
            authority: "COMMUNITY_5_18_IMPORT",
            lifecycle: StateLifecycle.Valid);

        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);
        var after = await project.Store.ReadAsync(seeded.Key);
        var worktreeAfter = await project.Store.ReadAsync(legacyWorktree.Key);

        Assert.Equal("BLOCKED", health.Status);
        Assert.Equal(2, health.Findings.Count(static item => item.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW"));
        Assert.Equal(seeded.Generation, after?.Generation);
        Assert.Equal(StateLifecycle.Valid, after?.Lifecycle);
        Assert.Equal(payload, after?.PayloadJson);
        Assert.Equal(legacyWorktree.Generation, worktreeAfter?.Generation);
        Assert.Equal(StateLifecycle.Valid, worktreeAfter?.Lifecycle);
    }

    [Fact]
    public async Task LegacyAndActiveRuntimeStates_CoexistWithoutFalseRuntimeActivation()
    {
        await using var project = await TestProject.CreateAsync();
        var governance = Path.Combine(project.Layout.AiRoot, "governance");
        var workspace = Path.Combine(project.Layout.AiRoot, "workspace");
        Directory.CreateDirectory(governance);
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(governance, "ownership.json"), "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
        await File.WriteAllTextAsync(Path.Combine(workspace, "task-map.json"), "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}");
        Assert.Equal("IMPORTED", (await new Community518Importer(project.Layout, project.Coordinator).ImportAsync(ProjectId)).Status);
        var started = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("field-start", Start(project.Root), new TestRuntimeAdapter("field-adapter"));

        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("RUNNING", started.Status);
        Assert.Equal("BLOCKED", health.Status);
        Assert.Equal(2, health.Findings.Count(static item => item.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW"));
        Assert.DoesNotContain(health.Findings, static item => item.Code is "INVALID_OWNERSHIP_STATE" or "INVALID_WORKTREE_STATE" or "LEASE_OWNERSHIP_DRIFT" or "LEASE_WORKTREE_DRIFT");
        var activeOwner = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Ownership, "task-1"));
        var activeWorktree = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Worktree, "worktree-task-1"));
        Assert.Equal("RUNTIME_COORDINATOR", activeOwner?.Authority);
        Assert.Equal("RUNTIME_COORDINATOR", activeWorktree?.Authority);
    }

    [Fact]
    public async Task ValidActiveOwnershipAndWorktree_KeepProjectDoctorHealthy()
    {
        await using var project = await TestProject.CreateAsync(ProjectId);
        var started = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("valid-start", Start(project.Root), new TestRuntimeAdapter("valid-adapter"));

        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("RUNNING", started.Status);
        Assert.Equal("HEALTHY", health.Status);
        Assert.Empty(health.Findings);
    }

    [Fact]
    public async Task AggregateImportRetry_IsIdempotentAndDoesNotChangeGeneration()
    {
        await using var project = await TestProject.CreateAsync();
        var sourcePath = Path.Combine(project.Layout.AiRoot, "governance", "ownership.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
        var importer = new Community518Importer(project.Layout, project.Coordinator);

        var first = await importer.ImportAsync(ProjectId);
        var beforeRetry = await project.Store.GetMetricsAsync();
        var retry = await importer.ImportAsync(ProjectId);
        var afterRetry = await project.Store.GetMetricsAsync();

        Assert.Equal("IMPORTED", first.Status);
        Assert.Equal("ALREADY_IMPORTED", retry.Status);
        Assert.Equal(first.SourceFingerprint, retry.SourceFingerprint);
        Assert.Equal(beforeRetry.Generation, afterRetry.Generation);
        Assert.Equal(beforeRetry.Entities, afterRetry.Entities);
        Assert.Equal(beforeRetry.Events, afterRetry.Events);
    }

    [Fact]
    public async Task BackupRestore_PreservesLegacyFactAndStructuredProjectHealth()
    {
        await using var project = await TestProject.CreateAsync();
        var sourcePath = Path.Combine(project.Layout.AiRoot, "workspace", "task-map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        const string payload = "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}";
        await File.WriteAllTextAsync(sourcePath, payload);
        Assert.Equal("IMPORTED", (await new Community518Importer(project.Layout, project.Coordinator).ImportAsync(ProjectId)).Status);
        var backup = await new StateBackupService(project.Layout).CreateAsync();
        await SeedAsync(project, EntityKinds.Decision, "after-backup", "{\"decision_id\":\"after-backup\"}");

        var restore = await new StateBackupService(project.Layout).RestoreAsync(backup.BackupPath);
        var legacy = await project.Store.ReadAsync(new StateKey(ProjectId, EntityKinds.Worktree, "workspace:task-map.json"));
        var health = await new AdvancedDoctor(project.Store, new ProcessIdentityProbe()).CheckProjectAsync(ProjectId);

        Assert.Equal("RESTORED", restore.Status);
        Assert.Equal(payload, legacy?.PayloadJson);
        Assert.Equal(StateLifecycle.Incompatible, legacy?.Lifecycle);
        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, static item => item.Code == "LEGACY_IMPORTED_STATE_REQUIRES_REVIEW");
    }

    [Fact]
    public async Task ProjectResume_DoesNotExposeRc1LegacyWorktreeAsActive()
    {
        await using var project = await TestProject.CreateAsync();
        await SeedAsync(
            project,
            EntityKinds.Worktree,
            "workspace:task-map.json",
            "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}",
            authority: "COMMUNITY_5_18_IMPORT",
            lifecycle: StateLifecycle.Valid);

        var summary = await new ProjectResumeService(project.Store).ResumeAsync(ProjectId, "goal-1");

        Assert.Empty(summary.ActiveWorktrees);
    }

    [Fact]
    public async Task CliChain_MigrationAndProjectScopedHealthRemainStructuredAndIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-field-cli-{Guid.NewGuid():N}");
        try
        {
            var governance = Path.Combine(root, ".ai", "governance");
            var workspace = Path.Combine(root, ".ai", "workspace");
            Directory.CreateDirectory(governance);
            Directory.CreateDirectory(workspace);
            await File.WriteAllTextAsync(Path.Combine(governance, "ownership.json"), "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
            await File.WriteAllTextAsync(Path.Combine(workspace, "task-map.json"), "{\"schema_version\":\"3.0.0\",\"status\":\"ACCEPTED\",\"lanes\":[]}");

            var firstImport = await RunCliAsync(root, "migrate-community", "--project-id", ProjectId);
            var doctor = await RunCliAsync(root, "doctor");
            var status = await RunCliAsync(root, "status");
            var projectDoctor = await RunCliAsync(root, "doctor", "--project-id", ProjectId);
            var projectStatus = await RunCliAsync(root, "status", "--project-id", ProjectId);
            var retry = await RunCliAsync(root, "migrate-community", "--project-id", ProjectId);

            Assert.Equal(0, firstImport.ExitCode);
            Assert.Equal("IMPORTED", Json(firstImport.Output).GetProperty("import").GetProperty("status").GetString());
            Assert.Equal(2, doctor.ExitCode);
            Assert.Equal("BLOCKED", Json(doctor.Output).GetProperty("status").GetString());
            Assert.Equal(2, status.ExitCode);
            Assert.False(Json(status.Output).GetProperty("ok").GetBoolean());
            Assert.Equal(2, projectDoctor.ExitCode);
            Assert.Equal("BLOCKED", Json(projectDoctor.Output).GetProperty("status").GetString());
            Assert.Equal(2, Json(projectDoctor.Output).GetProperty("findings").GetArrayLength());
            Assert.Equal(2, projectStatus.ExitCode);
            Assert.False(Json(projectStatus.Output).GetProperty("ok").GetBoolean());
            Assert.Equal("BLOCKED", Json(projectStatus.Output).GetProperty("health").GetProperty("status").GetString());
            Assert.Equal(0, retry.ExitCode);
            Assert.Equal("ALREADY_IMPORTED", Json(retry.Output).GetProperty("import").GetProperty("status").GetString());
            Assert.DoesNotContain("ArgumentNullException", string.Concat(projectDoctor.Output, projectDoctor.Error, projectStatus.Output, projectStatus.Error), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RuntimeStartRequest Start(string root) => new(
        ProjectId,
        "task-1",
        "runtime-1",
        root,
        "artifacts/context",
        "artifacts/instruction",
        1,
        "source-1",
        WorktreeId: "worktree-task-1",
        PredictedAffectedScopes: new[] { "src/field" });

    private static async Task<StateSnapshot> SeedAsync(
        TestProject project,
        string kind,
        string entityId,
        string payload,
        string authority = "RUNTIME_COORDINATOR",
        StateLifecycle lifecycle = StateLifecycle.Valid)
    {
        var key = new StateKey(ProjectId, kind, entityId);
        var result = await project.Store.CompareAndSwapAsync(new TransactionRequest(
            $"seed-{kind.ToLowerInvariant()}-{entityId}",
            new StateMutation(key, 0, 0, lifecycle, authority, authority == "COMMUNITY_5_18_IMPORT" ? "2.0.0" : "3.0.0", "fixture-source", "fixture-dependency", payload),
            "FIELD_FIXTURE_CREATED",
            "FIELD_FIXTURE_CREATED",
            "{}"));
        Assert.True(result.Committed);
        return Assert.IsType<StateSnapshot>(result.Snapshot);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task<CliResult> RunCliAsync(string root, params string[] arguments)
    {
        var repository = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var cli = Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0", "hiker.dll");
        Assert.True(File.Exists(cli), $"CLI assembly was not built: {cli}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Hiker.Pro.Cli");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate Hiker.Pro repository root");
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

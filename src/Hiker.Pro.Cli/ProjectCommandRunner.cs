using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Release;
using Hiker.Pro.State;

internal sealed class ProjectCommandRunner(string root, JsonSerializerOptions json)
{
    public async Task<int> RunAsync(string[] args)
    {
        var layout = new ProjectLayout(root);
        await using var store = new SqliteStateStore(layout);
        var state = new StateCoordinator(store);
        var bootstrap = new ProjectBootstrapService(layout, store, state);
        var suppliedProjectId = ValueAfter(args, "--project-id");
        switch (args[0])
        {
            case "init":
                {
                    var result = await bootstrap.InitializeAsync(suppliedProjectId).ConfigureAwait(false);
                    Write(new
                    {
                        ok = true,
                        result.Status,
                        result.ProjectId,
                        result.ProjectState,
                        result.IdentityCreated,
                        result.IdentityReused,
                        result.CommunityImported,
                        result.Import,
                        schema_version = BuildVersionFacts.Current.StateSchema,
                        state_ref = ".ai/state/hiker-state.db"
                    });
                    return 0;
                }
            case "doctor":
                {
                    if (args.Contains("--repair", StringComparer.Ordinal))
                    {
                        var repairProject = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Recovery, suppliedProjectId).ConfigureAwait(false);
                        var repair = await state.RepairAsync().ConfigureAwait(false);
                        Write(new { project = repairProject, repair });
                        return repair.Status == "BLOCKED" ? 2 : 0;
                    }

                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!).ConfigureAwait(false);
                    Write(new { health.Status, health.Findings, project = active });
                    return health.Status == "BLOCKED" ? 2 : 0;
                }
            case "status":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!).ConfigureAwait(false);
                    var metrics = await store.GetMetricsAsync().ConfigureAwait(false);
                    Write(new { ok = health.Status != "BLOCKED", project = active, health, metrics });
                    return health.Status == "BLOCKED" ? 2 : 0;
                }
            case "backup":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Backup, suppliedProjectId).ConfigureAwait(false);
                    var backup = await new StateBackupService(layout).CreateAsync(ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, backup });
                    return 0;
                }
            case "resume":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var goal = ValueAfter(args, "--goal-id") ?? throw new ArgumentException("resume requires --goal-id <current goal id>");
                    var resume = await new ProjectResumeService(store).ResumeAsync(active.ProjectId!, goal).ConfigureAwait(false);
                    Write(new { project = active, resume });
                    return 0;
                }
            case "restore":
                {
                    var backupPath = ValueAfter(args, "--backup") ?? throw new ArgumentException("restore requires --backup <path>");
                    var expectedHash = ValueAfter(args, "--confirm-sha256") ?? throw new ArgumentException("restore requires --confirm-sha256 <backup hash>");
                    var actualHash = await FileSha256Async(backupPath).ConfigureAwait(false);
                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Restore confirmation hash does not match the backup");
                    }

                    var restore = await new StateBackupService(layout).RestoreAsync(backupPath).ConfigureAwait(false);
                    var project = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Recovery, suppliedProjectId).ConfigureAwait(false);
                    Write(new { restore, project });
                    return project.CanProceed ? 0 : 2;
                }
            case "export":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Export, suppliedProjectId).ConfigureAwait(false);
                    var export = await store.ExportAsync(active.ProjectId!, ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, export });
                    return 0;
                }
            case "migrate-community":
                {
                    var before = await bootstrap.PreflightAsync(ProjectBootstrapIntent.MigrateCommunity, suppliedProjectId).ConfigureAwait(false);
                    if (!before.CanProceed) throw new ProjectBootstrapException(before);
                    StateBackupResult? backup = null;
                    if (before.Identity.DatabaseExists && before.Identity.DatabaseHealthy)
                    {
                        backup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
                    }

                    var result = await bootstrap.MigrateCommunityAsync(suppliedProjectId).ConfigureAwait(false);
                    Write(new { import = result.Import, project = result, pre_import_backup = backup });
                    return result.Status is "IMPORTED" or "ALREADY_IMPORTED" or "REUSED" ? 0 : 2;
                }
            case "fork":
                {
                    var targetProjectId = suppliedProjectId ?? throw new ArgumentException("fork requires --project-id <new stable id>");
                    var result = await bootstrap.ForkAsync(targetProjectId).ConfigureAwait(false);
                    Write(result);
                    return 0;
                }
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                return 2;
        }
    }

    private void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, json));

    private static string? ValueAfter(string[] values, string name)
    {
        var index = Array.IndexOf(values, name);
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }
}

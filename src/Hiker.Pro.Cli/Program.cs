using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Context;
using Hiker.Pro.State;
using Hiker.Pro.Release;

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
};

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker <init|doctor|status|resume|version|backup|restore|export|migrate-community> [--root <project>]");
    return 0;
}

if (args[0] == "version")
{
    var version = BuildVersionFacts.Current;
    Console.WriteLine($"{version.Product} runtime/{version.Runtime} schema/{version.StateSchema}");
    return 0;
}

var root = ValueAfter(args, "--root") ?? Directory.GetCurrentDirectory();
var layout = new ProjectLayout(root);
await using var store = new SqliteStateStore(layout);
var state = new StateCoordinator(store);

try
{
    switch (args[0])
    {
        case "init":
            await store.InitializeAsync().ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                status = "INITIALIZED",
                schema_version = SqliteSchemaVersion.Value,
                state_ref = ".ai/state/hiker-state.db"
            }, options));
            return 0;
        case "doctor":
            await store.InitializeAsync().ConfigureAwait(false);
            if (args.Contains("--repair", StringComparer.Ordinal))
            {
                var repair = await store.RepairAsync().ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(repair, options));
                return repair.Status == "BLOCKED" ? 2 : 0;
            }

            var doctorProject = ValueAfter(args, "--project-id");
            var health = doctorProject is null
                ? await store.HealthCheckAsync().ConfigureAwait(false)
                : await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(doctorProject).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(health, options));
            return health.Status == "BLOCKED" ? 2 : 0;
        case "status":
            await store.InitializeAsync().ConfigureAwait(false);
            var metrics = await store.GetMetricsAsync().ConfigureAwait(false);
            var statusProject = ValueAfter(args, "--project-id");
            var statusHealth = statusProject is null
                ? await store.HealthCheckAsync().ConfigureAwait(false)
                : await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(statusProject).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = statusHealth.Status != "BLOCKED", health = statusHealth, metrics }, options));
            return statusHealth.Status == "BLOCKED" ? 2 : 0;
        case "backup":
            await store.InitializeAsync().ConfigureAwait(false);
            var backup = await new StateBackupService(layout).CreateAsync(ValueAfter(args, "--output")).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(backup, options));
            return 0;
        case "resume":
            await store.InitializeAsync().ConfigureAwait(false);
            var resumeProject = ValueAfter(args, "--project-id") ?? throw new ArgumentException("resume requires --project-id <stable id>");
            var resumeGoal = ValueAfter(args, "--goal-id") ?? throw new ArgumentException("resume requires --goal-id <current goal id>");
            var resume = await new ProjectResumeService(store).ResumeAsync(resumeProject, resumeGoal).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(resume, options));
            return 0;
        case "restore":
            await store.InitializeAsync().ConfigureAwait(false);
            var backupPath = ValueAfter(args, "--backup") ?? throw new ArgumentException("restore requires --backup <path>");
            var expectedHash = ValueAfter(args, "--confirm-sha256") ?? throw new ArgumentException("restore requires --confirm-sha256 <backup hash>");
            var actualHash = await FileSha256Async(backupPath).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Restore confirmation hash does not match the backup");
            }

            var restore = await new StateBackupService(layout).RestoreAsync(backupPath).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(restore, options));
            return 0;
        case "export":
            await store.InitializeAsync().ConfigureAwait(false);
            var exportProject = ValueAfter(args, "--project-id") ?? throw new ArgumentException("export requires --project-id <stable id>");
            var export = await store.ExportAsync(exportProject, ValueAfter(args, "--output")).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(export, options));
            return 0;
        case "migrate-community":
            await store.InitializeAsync().ConfigureAwait(false);
            var projectId = ValueAfter(args, "--project-id") ?? throw new ArgumentException("migrate-community requires --project-id <stable id>");
            var migrationBackup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
            var import = await new Community518Importer(layout, state).ImportAsync(projectId).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { import, pre_import_backup = migrationBackup }, options));
            return import.Status is "IMPORTED" or "ALREADY_IMPORTED" ? 0 : 2;
        default:
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            return 2;
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        status = "FAILED",
        error = exception.GetType().Name,
        message = exception.Message
    }, options));
    return 2;
}

static string? ValueAfter(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static async Task<string> FileSha256Async(string path)
{
    await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
}

internal static class SqliteSchemaVersion
{
    public static string Value => BuildVersionFacts.Current.StateSchema;
}

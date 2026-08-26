using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
};

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker <init|doctor|status|version|backup|restore|migrate-community> [--root <project>]");
    return 0;
}

if (args[0] == "version")
{
    Console.WriteLine("5.19.0-dev.1 runtime/0.1.0 schema/3.0.0");
    return 0;
}

var root = ValueAfter(args, "--root") ?? Directory.GetCurrentDirectory();
var layout = new ProjectLayout(root);
await using var store = new SqliteStateStore(layout);

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

            var health = await store.HealthCheckAsync().ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(health, options));
            return health.Status == "BLOCKED" ? 2 : 0;
        case "status":
            await store.InitializeAsync().ConfigureAwait(false);
            var metrics = await store.GetMetricsAsync().ConfigureAwait(false);
            var statusHealth = await store.HealthCheckAsync().ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = statusHealth.Status != "BLOCKED", health = statusHealth, metrics }, options));
            return statusHealth.Status == "BLOCKED" ? 2 : 0;
        case "backup":
            await store.InitializeAsync().ConfigureAwait(false);
            var backup = await new StateBackupService(layout).CreateAsync(ValueAfter(args, "--output")).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(backup, options));
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
        case "migrate-community":
            await store.InitializeAsync().ConfigureAwait(false);
            var projectId = ValueAfter(args, "--project-id") ?? throw new ArgumentException("migrate-community requires --project-id <stable id>");
            var migrationBackup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
            var import = await new Community518Importer(layout, store).ImportAsync(projectId).ConfigureAwait(false);
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
    public const string Value = "3.0.0";
}

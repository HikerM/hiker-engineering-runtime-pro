using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class LegacyInventoryCompatibilityTests
{
    [Fact]
    public async Task KnownLegacyInventory_MigratesPreservesFinalizesAndRemainsIdempotent()
    {
        var root = NewRoot();
        try
        {
            await WriteKnownLegacyFixtureAsync(root);
            var beforeHashes = HashLegacySources(root);

            var before = await RunCliAsync(root, "status");
            var migration = await RunCliAsync(root, "migrate-community");
            var migrationJson = Json(migration);
            var projectId = migrationJson.GetProperty("project").GetProperty("project_id").GetString();
            var import = migrationJson.GetProperty("import");
            var inventory = import.GetProperty("inventory");

            Assert.Equal(2, before.ExitCode);
            Assert.Equal("LEGACY_FACTS_DISCOVERED", Json(before).GetProperty("project").GetProperty("reason_code").GetString());
            Assert.Equal(0, migration.ExitCode);
            Assert.Equal("IMPORTED", import.GetProperty("status").GetString());
            Assert.True(Guid.TryParseExact(projectId, "D", out _));
            Assert.Equal(11, import.GetProperty("discovered").GetInt32());
            Assert.Equal(5, inventory.GetProperty("known_legacy_content").GetInt32());
            Assert.Equal(5, inventory.GetProperty("safe_preserved_content").GetInt32());
            Assert.Equal(10, inventory.GetProperty("migratable_legacy_content").GetInt32());
            Assert.Equal(0, inventory.GetProperty("unknown_requires_review").GetInt32());
            Assert.Equal(64, inventory.GetProperty("inventory_fingerprint").GetString()?.Length);
            Assert.Equal(beforeHashes, HashLegacySources(root));

            await using (var store = new SqliteStateStore(new ProjectLayout(root)))
            {
                var architecture = await store.ReadAsync(new StateKey(projectId!, EntityKinds.Decision, "architecture:module-registry.json"));
                var requirement = await store.ReadAsync(new StateKey(projectId!, EntityKinds.Requirement, "requirements:ledger.json"));
                var evidence = await store.ReadAsync(new StateKey(projectId!, EntityKinds.Evidence, "evidence:index.json"));
                var knowledge = await store.ReadAsync(new StateKey(projectId!, EntityKinds.RuntimeProfile, "knowledge:metadata.json"));
                var opaqueEvidence = await store.ReadAsync(new StateKey(projectId!, EntityKinds.Evidence, "evidence:raw:capture.bin"));

                Assert.Equal(StateLifecycle.Archived, architecture?.Lifecycle);
                Assert.Equal(RetentionTier.Warm, architecture?.Retention);
                Assert.Equal(StateLifecycle.Valid, requirement?.Lifecycle);
                Assert.Equal(StateLifecycle.Archived, evidence?.Lifecycle);
                Assert.Equal(StateLifecycle.Archived, knowledge?.Lifecycle);
                Assert.Equal(RetentionTier.Cold, knowledge?.Retention);
                Assert.Null(opaqueEvidence);
            }

            var finalize = await RunCliAsync(root, "finalize-legacy");
            var status = await RunCliAsync(root, "status");
            var doctor = await RunCliAsync(root, "doctor");
            var retryMigration = await RunCliAsync(root, "migrate-community");
            var retryFinalize = await RunCliAsync(root, "finalize-legacy");

            Assert.Equal(0, finalize.ExitCode);
            Assert.Equal("FINALIZED", Json(finalize).GetProperty("status").GetString());
            Assert.Equal(0, status.ExitCode);
            Assert.True(Json(status).GetProperty("ok").GetBoolean());
            Assert.Equal("ACTIVE_EXISTING_PROJECT", Json(status).GetProperty("project").GetProperty("state").GetString());
            Assert.Equal(
                0,
                Json(status).GetProperty("project").GetProperty("ai_content").GetProperty("legacy_inventory").GetProperty("migratable_legacy_content").GetInt32());
            Assert.Equal(0, doctor.ExitCode);
            Assert.Equal("HEALTHY", Json(doctor).GetProperty("status").GetString());
            Assert.Equal("ALREADY_IMPORTED", Json(retryMigration).GetProperty("import").GetProperty("status").GetString());
            Assert.Equal("ALREADY_FINALIZED", Json(retryFinalize).GetProperty("status").GetString());
            Assert.Equal(beforeHashes, HashLegacySources(root));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnsupportedStructuredLegacyPath_IsPreservedAndDoesNotBlockMigration()
    {
        var root = NewRoot();
        try
        {
            await WriteAsync(root, "governance/ownership.json", "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
            await WriteAsync(root, "architecture/custom.json", "{\"schema_version\":\"1.0.0\",\"items\":[]}");
            var customHash = HashFile(Path.Combine(root, ".ai", "architecture", "custom.json"));

            var migration = await RunCliAsync(root, "migrate-community");
            var result = Json(migration);
            var import = result.GetProperty("import");
            var projectId = import.GetProperty("project_id").GetString();

            Assert.Equal(0, migration.ExitCode);
            Assert.Equal("IMPORTED", import.GetProperty("status").GetString());
            Assert.Equal(0, import.GetProperty("inventory").GetProperty("unknown_requires_review").GetInt32());
            Assert.Contains(
                import.GetProperty("inventory").GetProperty("entries").EnumerateArray(),
                static item => item.GetProperty("path").GetString() == "architecture/custom.json" &&
                               item.GetProperty("handling").GetString() == "UNTRUSTED_LAZY_SOURCE_PRESERVE_ONLY");
            Assert.True(File.Exists(Path.Combine(root, ".ai", "state", "hiker-state.db")));
            Assert.Equal(customHash, HashFile(Path.Combine(root, ".ai", "architecture", "custom.json")));
            await using (var store = new SqliteStateStore(new ProjectLayout(root)))
            {
                Assert.Null(await store.ReadAsync(new StateKey(projectId!, EntityKinds.Decision, "architecture:custom.json")));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MalformedKnownRequirementFact_IsPreservedAndBlocksUnknownSchema()
    {
        var root = NewRoot();
        try
        {
            await WriteAsync(root, "governance/ownership.json", "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
            await WriteAsync(root, "requirements/ledger.json", "{\"schema_version\":\"1.0.0\",\"revision\":1,\"requirements\":{}}");
            var ledger = Path.Combine(root, ".ai", "requirements", "ledger.json");
            var beforeHash = HashFile(ledger);

            var migration = await RunCliAsync(root, "migrate-community");
            var result = Json(migration);

            Assert.Equal(2, migration.ExitCode);
            Assert.Equal("BLOCKED", result.GetProperty("status").GetString());
            Assert.Equal("UNKNOWN_SCHEMA", result.GetProperty("project").GetProperty("reason_code").GetString());
            Assert.Contains(
                result.GetProperty("project").GetProperty("ai_content").GetProperty("unknown_items").EnumerateArray(),
                static item => item.GetString()!.Contains("UNKNOWN_LEGACY_SCHEMA", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(root, ".ai", "state", "hiker-state.db")));
            Assert.Equal(beforeHash, HashFile(ledger));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task WriteKnownLegacyFixtureAsync(string root)
    {
        await WriteAsync(root, "governance/ownership.json", "{\"schema_version\":\"3.0.0\",\"rules\":[]}");
        await WriteAsync(root, "architecture/module-registry.json", "{\"schema_version\":\"1.0.0\",\"mode\":\"auto-discovery\",\"modules\":[]}");
        await WriteAsync(root, "architecture/dependency-rules.json", "{\"schema_version\":\"1.0.0\",\"mode\":\"advisory-until-configured\",\"rules\":[]}");
        await WriteAsync(root, "architecture/public-surface.json", "{\"schema_version\":\"1.0.0\",\"surfaces\":[]}");
        await WriteAsync(root, "requirements/ledger.json", "{\"schema_version\":\"1.0.0\",\"revision\":1,\"requirements\":[{\"id\":\"REQ-001\",\"statement\":\"sanitized requirement\"}]}");
        await WriteAsync(root, "requirements/source-baseline.json", "{\"schema_version\":\"1.0.0\",\"capabilities\":[{\"id\":\"CAP-001\",\"statement\":\"sanitized capability\"}]}");
        await WriteAsync(root, "requirements/reconciliation.json", "{\"schema_version\":\"1.0.0\",\"matrix\":[],\"status\":\"READY_FOR_PLANNING\"}");
        await WriteAsync(root, "evidence/index.json", "{\"schema_version\":\"1.0.0\",\"records\":[]}");
        await WriteAsync(root, "evidence/risk/latest.json", "{\"schema_version\":\"1.0.0\",\"risk\":{},\"remaining_risks\":[]}");
        await WriteAsync(root, "evidence/test-plan/latest.json", "{\"schema_version\":\"1.0.0\",\"must_run\":[],\"results\":{}}");
        await WriteAsync(root, "evidence/raw/capture.bin", "opaque evidence is preserved but never imported");
        await WriteAsync(root, "knowledge/metadata.json", "{\"schema_version\":1,\"last_indexed_commit\":null,\"node_count\":0,\"edge_count\":0}");
        await WriteBytesAsync(root, "knowledge/engineering.db", new byte[] { 0x53, 0x51, 0x4c, 0x69, 0x74, 0x65 });
        await WriteAsync(root, "logs/execution.jsonl", "{\"event\":\"historical\",\"status\":\"complete\"}\n");
    }

    private static Dictionary<string, string> HashLegacySources(string root)
    {
        var ai = Path.Combine(root, ".ai");
        return Directory.EnumerateFiles(ai, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(ai, path).StartsWith($"state{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !Path.GetRelativePath(ai, path).StartsWith($"cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !Path.GetRelativePath(ai, path).StartsWith($"archive{Path.DirectorySeparatorChar}state-backups", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(ai, path).Replace('\\', '/'),
                HashFile,
                StringComparer.Ordinal);
    }

    private static async Task WriteAsync(string root, string relative, string payload) =>
        await WriteBytesAsync(root, relative, Encoding.UTF8.GetBytes(payload));

    private static async Task WriteBytesAsync(string root, string relative, byte[] payload)
    {
        var path = Path.Combine(root, ".ai", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload);
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-legacy-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static JsonElement Json(CliResult result)
    {
        var payload = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

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
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
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
            if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate Hiker.Pro repository root");
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class PreservedUntrustedLegacyContentTests
{
    [Fact]
    public async Task UntrustedScriptsAndDocuments_ArePreservedInPlaceWithoutExecutionOrImport()
    {
        var root = NewRoot();
        try
        {
            await WriteLegacyOwnershipAsync(root);
            await WriteAsync(
                root,
                "tmp/model-node-word/create_model_node_docx.py",
                "from pathlib import Path\nPath(__file__).with_name('executed.marker').write_text('executed')\n");
            await WriteAsync(root, "tmp/model-node-word/notes.md", "preserved notes");
            await WriteAsync(root, "tmp/model-node-word/tool-settings.json", "{\"format\":\"docx\",\"enabled\":true}");
            await WriteBytesAsync(root, "tmp/model-node-word/template.docx", new byte[] { 0x50, 0x4b, 0x03, 0x04 });
            await WriteBytesAsync(root, "tmp/model-node-word/tool.exe", new byte[] { 0x4d, 0x5a, 0x00, 0x00 });
            var script = AiPath(root, "tmp/model-node-word/create_model_node_docx.py");
            var scriptHash = HashFile(script);
            var scriptWriteTime = File.GetLastWriteTimeUtc(script);

            var migration = await RunCliAsync(root, "migrate-community");
            var migrationJson = Json(migration);
            var import = migrationJson.GetProperty("import");
            var projectId = import.GetProperty("project_id").GetString();
            var tmpEntry = import.GetProperty("inventory").GetProperty("entries").EnumerateArray()
                .Single(entry => entry.GetProperty("path").GetString() == "tmp");

            Assert.Equal(0, migration.ExitCode);
            Assert.Equal("IMPORTED", import.GetProperty("status").GetString());
            Assert.True(Guid.TryParseExact(projectId, "D", out _));
            Assert.Equal(1, import.GetProperty("discovered").GetInt32());
            Assert.Equal("PRESERVED_UNTRUSTED_LEGACY_CONTENT", tmpEntry.GetProperty("semantic_type").GetString());
            Assert.Contains(
                "SAFE_PRESERVED_CONTENT",
                tmpEntry.GetProperty("classifications").EnumerateArray().Select(static item => item.GetString()));
            Assert.Equal("UNTRUSTED_LAZY_SOURCE_PRESERVE_ONLY", tmpEntry.GetProperty("handling").GetString());
            Assert.Equal(scriptHash, HashFile(script));
            Assert.Equal(scriptWriteTime, File.GetLastWriteTimeUtc(script));
            Assert.False(File.Exists(AiPath(root, "tmp/model-node-word/executed.marker")));
            Assert.True(File.Exists(AiPath(root, "tmp/model-node-word/notes.md")));
            Assert.True(File.Exists(AiPath(root, "tmp/model-node-word/tool-settings.json")));
            Assert.True(File.Exists(AiPath(root, "tmp/model-node-word/template.docx")));
            Assert.True(File.Exists(AiPath(root, "tmp/model-node-word/tool.exe")));

            var finalize = await RunCliAsync(root, "finalize-legacy");
            var status = await RunCliAsync(root, "status");
            var doctor = await RunCliAsync(root, "doctor");

            Assert.Equal(0, finalize.ExitCode);
            Assert.Equal("FINALIZED", Json(finalize).GetProperty("status").GetString());
            Assert.Equal(0, status.ExitCode);
            Assert.True(Json(status).GetProperty("ok").GetBoolean());
            Assert.Equal("ACTIVE_EXISTING_PROJECT", Json(status).GetProperty("project").GetProperty("state").GetString());
            Assert.Equal(0, doctor.ExitCode);
            Assert.Equal("HEALTHY", Json(doctor).GetProperty("status").GetString());
            Assert.Equal(scriptHash, HashFile(script));
            Assert.False(File.Exists(AiPath(root, "tmp/model-node-word/executed.marker")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AuthorityLikeJsonInsideUntrustedDirectory_RemainsBlockedWithoutPartialState()
    {
        var root = NewRoot();
        try
        {
            await WriteLegacyOwnershipAsync(root);
            await WriteAsync(
                root,
                "tmp/candidate.json",
                "{\"schema_version\":\"3.0.0\",\"project_id\":\"00000000-0000-0000-0000-000000000001\",\"kind\":\"OWNERSHIP\",\"entity_id\":\"owner\",\"authority\":\"COMMUNITY_5_18_IMPORT\"}");
            var candidate = AiPath(root, "tmp/candidate.json");
            var beforeHash = HashFile(candidate);

            var migration = await RunCliAsync(root, "migrate-community");
            var project = Json(migration).GetProperty("project");

            Assert.Equal(2, migration.ExitCode);
            Assert.Equal("UNKNOWN_AI_CONTENT", project.GetProperty("reason_code").GetString());
            Assert.Contains(
                project.GetProperty("ai_content").GetProperty("unknown_items").EnumerateArray().Select(static item => item.GetString()),
                static item => item is not null && item.Contains("POTENTIAL_STATE_AUTHORITY_PAYLOAD", StringComparison.Ordinal));
            Assert.False(File.Exists(AiPath(root, "state/hiker-state.db")));
            Assert.Equal(beforeHash, HashFile(candidate));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PreservedOnlyLegacyInventory_RecordsZeroFactMigrationAndBecomesHealthy()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(AiPath(root, "requirements"));
            await WriteBytesAsync(root, "archive/checkpoints/historical.zip", new byte[] { 0x50, 0x4b, 0x03, 0x04 });
            await WriteAsync(root, "tmp/model-node-word/create_model_node_docx.py", "raise SystemExit('must never execute')\n");
            var script = AiPath(root, "tmp/model-node-word/create_model_node_docx.py");
            var scriptHash = HashFile(script);

            var migration = await RunCliAsync(root, "migrate-community");
            var migrationJson = Json(migration);
            var import = migrationJson.GetProperty("import");
            var projectId = import.GetProperty("project_id").GetString();
            var finalize = await RunCliAsync(root, "finalize-legacy");
            var status = await RunCliAsync(root, "status");
            var doctor = await RunCliAsync(root, "doctor");

            Assert.Equal(0, migration.ExitCode);
            Assert.Equal("IMPORTED", import.GetProperty("status").GetString());
            Assert.Equal(0, import.GetProperty("discovered").GetInt32());
            Assert.Equal(0, import.GetProperty("imported").GetInt32());
            Assert.True(Guid.TryParseExact(projectId, "D", out _));
            Assert.Equal(0, finalize.ExitCode);
            Assert.Equal("ALREADY_FINALIZED", Json(finalize).GetProperty("status").GetString());
            Assert.Equal(0, status.ExitCode);
            Assert.True(Json(status).GetProperty("ok").GetBoolean());
            Assert.Equal("ACTIVE_EXISTING_PROJECT", Json(status).GetProperty("project").GetProperty("state").GetString());
            Assert.Equal(0, doctor.ExitCode);
            Assert.Equal("HEALTHY", Json(doctor).GetProperty("status").GetString());
            Assert.Equal(scriptHash, HashFile(script));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ReparsePointEscapingAi_RemainsBlocked()
    {
        var root = NewRoot();
        var outside = Path.Combine(Path.GetTempPath(), $"hiker-untrusted-outside-{Guid.NewGuid():N}");
        var link = AiPath(root, "tmp/escape");
        try
        {
            await WriteLegacyOwnershipAsync(root);
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), "outside");
            var tmp = AiPath(root, "tmp");
            Directory.CreateDirectory(tmp);
            await CreateDirectoryLinkAsync(link, outside);

            var migration = await RunCliAsync(root, "migrate-community");
            var project = Json(migration).GetProperty("project");

            Assert.Equal(2, migration.ExitCode);
            Assert.Contains(
                project.GetProperty("ai_content").GetProperty("unknown_items").EnumerateArray().Select(static item => item.GetString()),
                static item => item is not null && item.Contains("REPARSE_POINT", StringComparison.Ordinal));
            Assert.False(File.Exists(AiPath(root, "state/hiker-state.db")));
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            DeleteRoot(root);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ProjectLayout_PathTraversal_IsRejectedBeforeInventory()
    {
        var root = NewRoot();
        try
        {
            var layout = new Hiker.Pro.State.ProjectLayout(root);
            Assert.Throws<InvalidDataException>(() => layout.ResolveAiRelativePath("../outside"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnboundedUntrustedDirectory_IsStructurallyBlocked()
    {
        var root = NewRoot();
        try
        {
            await WriteLegacyOwnershipAsync(root);
            var bulk = AiPath(root, "tmp/bulk");
            Directory.CreateDirectory(bulk);
            for (var index = 0; index < 10_000; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(bulk, $"item-{index:D5}.bin"), Array.Empty<byte>());
            }

            var migration = await RunCliAsync(root, "migrate-community");
            var project = Json(migration).GetProperty("project");

            Assert.Equal(2, migration.ExitCode);
            Assert.Contains(
                project.GetProperty("ai_content").GetProperty("unknown_items").EnumerateArray().Select(static item => item.GetString()),
                static item => item is not null && item.Contains("INVENTORY_LIMIT_EXCEEDED", StringComparison.Ordinal));
            Assert.False(File.Exists(AiPath(root, "state/hiker-state.db")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static Task WriteLegacyOwnershipAsync(string root) =>
        WriteAsync(root, "governance/ownership.json", "{\"schema_version\":\"2.0.0\",\"owners\":[]}");

    private static Task WriteAsync(string root, string relative, string payload) =>
        WriteBytesAsync(root, relative, Encoding.UTF8.GetBytes(payload));

    private static async Task WriteBytesAsync(string root, string relative, byte[] payload)
    {
        var path = AiPath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload);
    }

    private static string AiPath(string root, string relative) =>
        Path.Combine(root, ".ai", relative.Replace('/', Path.DirectorySeparatorChar));

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to create junction fixture");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"Unable to create junction fixture: {output} {error}");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-preserved-untrusted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
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

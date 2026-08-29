using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hiker.Pro.Context;
using Hiker.Pro.Safety;
using Hiker.Pro.State;
using Hiker.Pro.Storage;
using Hiker.Pro.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StateIsolationTests
{
    [Fact]
    public async Task GitWorkspaceInspectorExcludesLargeAiState()
    {
        var root = TemporaryRoot();
        try
        {
            await GitAsync(root, "init", "-b", "main");
            await GitAsync(root, "config", "user.email", "hiker.invalid"); await GitAsync(root, "config", "user.name", "Hiker");
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "fixture");
            await GitAsync(root, "add", "README.md"); await GitAsync(root, "commit", "-m", "fixture");
            var ai = Path.Combine(root, ".ai", "archive", "events"); Directory.CreateDirectory(ai);
            for (var index = 0; index < 10_100; index++) File.Create(Path.Combine(ai, $"event-{index}.json")).Dispose();
            var timer = Stopwatch.StartNew(); var snapshot = await new GitWorkspaceInspector().InspectAsync(root); timer.Stop();
            Assert.False(snapshot.Dirty); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"elapsed={timer.Elapsed}");
            var layout = new ProjectLayout(root); await using (var store = new SqliteStateStore(layout))
            {
                await store.InitializeAsync(); var before = await store.GetMetricsAsync();
                var doctor = await store.StorageHealthCheckAsync("P0-LARGE-AI", StorageHealthMode.Fast);
                var context = await new ProjectResumeService(store).ResumeAsync("P0-LARGE-AI", "missing-goal", 4096);
                var after = await store.GetMetricsAsync();
                Assert.Equal("HEALTHY", doctor.Status); Assert.Equal(before.Generation, after.Generation); Assert.True(context.EncodedBytes <= 4096);
            }
            SqliteConnection.ClearAllPools();
            await File.WriteAllTextAsync(Path.Combine(root, "source.cs"), "class Source {}");
            Assert.True((await new GitWorkspaceInspector().InspectAsync(root)).Dirty);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void TraversalLimitAndReparsePolicyAreDeterministic()
    {
        var root = TemporaryRoot();
        try
        {
            for (var index = 0; index < 8; index++) File.WriteAllText(Path.Combine(root, $"f-{index}.txt"), "x");
            var error = Assert.Throws<TraversalLimitReachedException>(() => TraversalGuard.EnumerateFiles(root, new TraversalBudget(MaxEntries: 2)));
            Assert.Equal("TRAVERSAL_LIMIT_REACHED", error.Receipt.Status); Assert.Equal("entries", error.Limit);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ProjectLayoutRejectsEscapeAndExistingReparsePoint()
    {
        var root = TemporaryRoot();
        try
        {
            var layout = new ProjectLayout(root); layout.EnsureDirectories();
            Assert.Throws<InvalidDataException>(() => layout.ResolveAiRelativePath("../outside.json"));
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var link = Path.Combine(layout.AiRoot, "linked");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { return; }
            Assert.Throws<InvalidDataException>(() => layout.ResolveAiRelativePath("linked/value.json"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void MachineJsonResultFuseReturnsOneBoundedDocument()
    {
        var envelope = new JsonObject
        {
            ["contract_version"] = "hiker-cli/v1",
            ["command"] = "status",
            ["ok"] = true,
            ["status"] = "HEALTHY",
            ["pro_state"] = "PRO_ACTIVE",
            ["exit_code"] = 0,
            ["payload"] = new string('x', 1_000_000)
        };
        var receipt = MachineResultFuse.Apply(envelope, new JsonSerializerOptions { WriteIndented = true }, 64 * 1024);
        using var document = JsonDocument.Parse(receipt.Document);
        Assert.True(receipt.Truncated); Assert.True(receipt.Document.Length < 64 * 1024);
        Assert.True(document.RootElement.GetProperty("result_truncated").GetBoolean());
        Assert.Equal("RESULT_BUDGET_EXCEEDED", document.RootElement.GetProperty("reason_code").GetString());
    }

    [Fact]
    public async Task RuntimeSegmentsRotateAndRemainBounded()
    {
        var root = TemporaryRoot(); var path = Path.Combine(root, "events.jsonl");
        try
        {
            await using (var writer = new BoundedSegmentWriter(path, 64 * 1024, 2))
                for (var index = 0; index < 300; index++) await writer.WriteLineAsync(new string('x', 1024), CancellationToken.None);
            Assert.True(new FileInfo(path).Length <= 64 * 1024); Assert.True(File.Exists(path + ".1")); Assert.False(File.Exists(path + ".3"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task FastDoctorDoesNotWriteReceiptOrAdvanceStateGeneration()
    {
        await using var project = await TestProject.CreateAsync("P0-STATE-ISOLATION");
        var before = await project.Store.GetMetricsAsync();
        var report = await project.Store.StorageHealthCheckAsync(project.ProjectId!, StorageHealthMode.Fast);
        var after = await project.Store.GetMetricsAsync();
        Assert.Equal("HEALTHY", report.Status); Assert.Equal(before.Generation, after.Generation);
        Assert.False(File.Exists(project.Layout.FastDoctorReceiptPath));
    }

    [Fact]
    public async Task ResumePackageHasAHardFinalByteFuse()
    {
        await using var project = await TestProject.CreateAsync("P0-CONTEXT-BOUNDED");
        var summary = await new ProjectResumeService(project.Store).ResumeAsync(project.ProjectId!, "missing-goal", 4096);
        Assert.True(summary.EncodedBytes <= 4096, $"encoded={summary.EncodedBytes}");
    }

    [Fact]
    public async Task LegacyInventoryLimitAbortDoesNotCommitEarlierSegments()
    {
        await using var project = await TestProject.CreateAsync("P0-INVENTORY-ATOMIC");
        var good = Path.Combine(project.Layout.AiRoot, "a-good"); Directory.CreateDirectory(good);
        await File.WriteAllTextAsync(Path.Combine(good, "fact.json"), "{}");
        var bad = Path.Combine(project.Layout.AiRoot, "z-bad"); Directory.CreateDirectory(bad);
        for (var index = 0; index < 4_097; index++) File.Create(Path.Combine(bad, $"f-{index}.json")).Dispose();
        var error = await Assert.ThrowsAsync<TraversalLimitReachedException>(() => project.Store.InventoryLegacyStorageAsync(project.ProjectId!));
        Assert.Equal("entries_per_directory", error.Limit);
        await using var connection = new SqliteConnection($"Data Source={project.Layout.DatabasePath}"); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM migration_inventory_segments WHERE project_id=$project;"; command.Parameters.AddWithValue("$project", project.ProjectId!);
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string TemporaryRoot() { var root = Path.Combine(Path.GetTempPath(), $"hiker-state-isolation-{Guid.NewGuid():N}"); Directory.CreateDirectory(root); return root; }

    private static async Task GitAsync(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}

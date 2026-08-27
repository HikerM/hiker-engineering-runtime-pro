using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StorageCliTests
{
    [Fact]
    public async Task ExistingPreStorageLifecycleDatabase_IsAdditivelyUpgradedBeforeStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-storage-upgrade-cli-{Guid.NewGuid():N}");
        try
        {
            var init = await RunCliAsync(root, "init", "--project-id", "storage-upgrade-project");
            Assert.Equal(0, init.ExitCode);
            var database = Path.Combine(root, ".ai", "state", "hiker-state.db");
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE artifact_verifications;
                    DROP TABLE artifact_links;
                    DROP TABLE runtime_snapshots;
                    DROP TABLE state_deltas;
                    DROP TABLE event_segments;
                    DROP TABLE event_dedup;
                    DROP TABLE archived_operations;
                    DROP TABLE archived_dependency_edges;
                    DROP TABLE archived_entities;
                    DROP TABLE storage_maintenance;
                    DROP TABLE storage_counters;
                    DROP TABLE migration_inventory_segments;
                    DELETE FROM schema_migrations WHERE version='storage-lifecycle-v1';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var status = await RunCliAsync(root, "status");
            var payload = JsonDocument.Parse(status.Output).RootElement;

            Assert.Equal(0, status.ExitCode);
            Assert.True(payload.GetProperty("ok").GetBoolean());
            Assert.Equal("HEALTHY", payload.GetProperty("storage").GetProperty("status").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StorageCliTests
{
    [Theory]
    [InlineData("92732e5744dd104829a06af91f7c6d9b6190a7d624b7874c31896a372b0e2074", "757b9a2fa51c7c2ab968a59cc50d6adaa1332499fd414bac2d5717ac5cff4a52")]
    [InlineData("d570b9238357a30d86cf6c08af61a6e83f7e4211159f42f8e6e9b2dd4e4385b9", "9148a7f1074a81382d6cd9933ee03536c66f9e581940bbf5df9233bc93a780e4")]
    public async Task LineEndingSpecificSchemaChecksums_AreReadCompatibleWithoutRewrite(
        string coreChecksum,
        string storageChecksum)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-schema-line-endings-{Guid.NewGuid():N}");
        try
        {
            var init = await RunCliAsync(root, "init", "--project-id", "schema-line-endings-project");
            Assert.Equal(0, init.ExitCode);
            var database = Path.Combine(root, ".ai", "state", "hiker-state.db");
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using (var current = connection.CreateCommand())
                {
                    current.CommandText = "SELECT checksum FROM schema_migrations WHERE version=$version;";
                    current.Parameters.AddWithValue("$version", "3.0.0");
                    Assert.Equal("92732e5744dd104829a06af91f7c6d9b6190a7d624b7874c31896a372b0e2074", await current.ExecuteScalarAsync());
                    current.Parameters["$version"].Value = "storage-lifecycle-v1";
                    Assert.Equal("757b9a2fa51c7c2ab968a59cc50d6adaa1332499fd414bac2d5717ac5cff4a52", await current.ExecuteScalarAsync());
                }

                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE schema_migrations
                    SET checksum = CASE version
                        WHEN '3.0.0' THEN $core
                        WHEN 'storage-lifecycle-v1' THEN $storage
                        ELSE checksum
                    END
                    WHERE version IN ('3.0.0', 'storage-lifecycle-v1');
                    """;
                update.Parameters.AddWithValue("$core", coreChecksum);
                update.Parameters.AddWithValue("$storage", storageChecksum);
                Assert.Equal(2, await update.ExecuteNonQueryAsync());
            }

            var status = await RunCliAsync(root, "status");
            Assert.True(status.ExitCode == 0, $"stdout={status.Output}\nstderr={status.Error}");
            using (var payload = JsonDocument.Parse(status.Output))
            {
                Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("HEALTHY", payload.RootElement.GetProperty("storage").GetProperty("status").GetString());
            }

            var doctor = await RunCliAsync(root, "doctor");
            Assert.True(doctor.ExitCode == 0, $"stdout={doctor.Output}\nstderr={doctor.Error}");
            using (var payload = JsonDocument.Parse(doctor.Output))
            {
                Assert.Equal("HEALTHY", payload.RootElement.GetProperty("status").GetString());
            }

            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly"))
            {
                await connection.OpenAsync();
                await using var read = connection.CreateCommand();
                read.CommandText = "SELECT version, checksum FROM schema_migrations WHERE version IN ('3.0.0', 'storage-lifecycle-v1') ORDER BY version;";
                await using var reader = await read.ExecuteReaderAsync();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                while (await reader.ReadAsync())
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }

                Assert.Equal(coreChecksum, values["3.0.0"]);
                Assert.Equal(storageChecksum, values["storage-lifecycle-v1"]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

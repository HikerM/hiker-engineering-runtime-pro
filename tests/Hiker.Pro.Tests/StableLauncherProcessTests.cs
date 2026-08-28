using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Hiker.Pro.Activation;
using Hiker.Pro.Context;
using Hiker.Pro.Release;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class StableLauncherProcessTests
{
    [Fact]
    public async Task StableLauncherPropagatesRuntimeJsonExitCodeAndIdentityWithBoundedLatency()
    {
        if (!OperatingSystem.IsWindows()) return;
        var repository = FindRepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var home = Path.Combine(Path.GetTempPath(), $"hiker-launcher-field-{Guid.NewGuid():N}");
        var staging = Path.Combine(home, "incoming", ".staging-runtime");
        try
        {
            CopyDirectory(Path.Combine(repository, "src", "Hiker.Pro.Launcher", "bin", configuration, "net9.0"), Path.Combine(home, "bin"));
            CopyDirectory(Path.Combine(repository, "src", "Hiker.Pro.Cli", "bin", configuration, "net9.0"), staging);
            var identity = new RuntimeIdentity(
                BuildVersionFacts.Current.Product,
                BuildVersionFacts.Current.Runtime,
                BuildVersionFacts.Current.StateSchema,
                LiveAdoptionProtocol.CurrentVersion,
                "cccccccccccccccccccccccccccccccccccccccc",
                "process-field-runtime",
                "field");
            var manager = new RuntimeActivationManager(home, new ProcessRuntimePackageProbe());
            await manager.ActivateAsync(new VerifiedRuntimePackage(
                staging,
                identity,
                await HashesAsync(staging),
                "VERIFIED",
                "VERIFIED"));

            var executable = Path.Combine(home, "bin", "hiker.exe");
            var launcherDurations = new List<double>();
            var runtimeDurations = new List<double>();
            var runtimeExecutable = (await new RuntimeLauncherResolver(home).ResolveAsync()).Executable;
            ProcessResult? version = null;
            for (var index = 0; index < 8; index++)
            {
                runtimeDurations.Add((await RunAsync(runtimeExecutable, home, "version", "--json")).Duration.TotalMilliseconds);
                version = await RunAsync(executable, home, "version", "--json");
                launcherDurations.Add(version.Duration.TotalMilliseconds);
            }
            var invalid = await RunAsync(executable, home, "unknown-command", "--json");
            using var document = JsonDocument.Parse(version!.Output);
            var root = document.RootElement;

            Assert.Equal(0, version.ExitCode);
            Assert.Equal("process-field-runtime", root.GetProperty("runtime_identity").GetString());
            Assert.Equal("1.0.0", root.GetProperty("launcher_version").GetString());
            Assert.Equal(1, root.GetProperty("install_generation").GetInt64());
            Assert.Equal(64, invalid.ExitCode);
            Assert.Equal(64, JsonDocument.Parse(invalid.Output).RootElement.GetProperty("exit_code").GetInt32());
            var launcherP95 = Percentile(launcherDurations, 0.95);
            var runtimeP95 = Percentile(runtimeDurations, 0.95);
            Assert.True(launcherP95 < 2_000, $"launcher total P95={launcherP95:F2}ms");
            Assert.True(launcherP95 - runtimeP95 < 750, $"launcher incremental P95={launcherP95 - runtimeP95:F2}ms (launcher={launcherP95:F2}, runtime={runtimeP95:F2})");

            var projectRoot = Path.Combine(home, "field-project");
            Directory.CreateDirectory(projectRoot);
            var initialized = await RunAsync(executable, home, "init", "--root", projectRoot, "--json");
            Assert.Equal(0, initialized.ExitCode);
            var operationIdentity = await FirstOperationIdentityAsync(projectRoot);
            Assert.Equal("process-field-runtime", operationIdentity.RuntimeIdentity);
            Assert.Equal(64, operationIdentity.BuildFingerprint?.Length);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunAsync(string executable, string home, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["HIKER_HOME"] = home;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Launcher did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        watch.Stop();
        return new ProcessResult(process.ExitCode, await output, await error, watch.Elapsed);
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashesAsync(string root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            await using var stream = File.OpenRead(path);
            values[Path.GetRelativePath(root, path).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
        return values;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.OrderBy(static value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static async Task<(string? RuntimeIdentity, string? BuildFingerprint)> FirstOperationIdentityAsync(string projectRoot)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(projectRoot, ".ai", "state", "hiker-state.db"),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.payload_json FROM operations o JOIN engineering_events e ON e.event_id=o.operation_id ORDER BY o.created_at LIMIT 1;";
        var payload = Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("No operation identity was recorded");
        using var document = JsonDocument.Parse(payload);
        return (
            document.RootElement.GetProperty("runtime_identity").GetString(),
            document.RootElement.GetProperty("build_fingerprint").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hiker.Pro.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error, TimeSpan Duration);
}

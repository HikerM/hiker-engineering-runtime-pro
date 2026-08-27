using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Release;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class SelfContainedRuntimeTests
{
    [Fact]
    public async Task SelfContainedWinX64Publish_RunsWithoutMachineDotnet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = RepositoryRoot();
        var output = Path.Combine(Path.GetTempPath(), $"hiker-self-contained-{Guid.NewGuid():N}");
        var project = Path.Combine(Path.GetTempPath(), $"hiker-self-contained-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(project);
        try
        {
            var publish = await RunAsync(
                "dotnet",
                root,
                new[]
                {
                    "publish",
                    Path.Combine(root, "src", "Hiker.Pro.Cli", "Hiker.Pro.Cli.csproj"),
                    "--configuration", "Release",
                    "--runtime", "win-x64",
                    "--self-contained", "true",
                    "--output", output,
                    "-p:UseAppHost=true",
                    "-p:PublishSingleFile=false",
                    "-p:PublishReadyToRun=false",
                    "-p:DebugSymbols=false",
                    "-p:DebugType=None"
                },
                environment: null,
                timeout: TimeSpan.FromMinutes(3));
            Assert.Equal(0, publish.ExitCode);

            var required = new[] { "hiker.exe", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll" };
            Assert.DoesNotContain(required, file => !File.Exists(Path.Combine(output, file)));
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "hiker.runtimeconfig.json"))))
            {
                var runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
                Assert.False(runtimeOptions.TryGetProperty("framework", out _));
                Assert.False(runtimeOptions.TryGetProperty("frameworks", out _));
            }

            var isolated = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = string.Empty,
                ["DOTNET_ROOT"] = Path.Combine(project, "missing-dotnet"),
                ["DOTNET_ROOT_X64"] = null,
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
            };
            var version = await RunAsync(Path.Combine(output, "hiker.exe"), output, new[] { "version" }, isolated, TimeSpan.FromMinutes(1));
            var protocol = await RunAsync(Path.Combine(output, "hiker.exe"), output, new[] { "version", "--json" }, isolated, TimeSpan.FromMinutes(1));
            var init = await RunAsync(Path.Combine(output, "hiker.exe"), output, new[] { "init", "--root", project }, isolated, TimeSpan.FromMinutes(1));
            var status = await RunAsync(Path.Combine(output, "hiker.exe"), output, new[] { "status", "--root", project }, isolated, TimeSpan.FromMinutes(1));

            Assert.Equal(0, version.ExitCode);
            Assert.Equal($"{BuildVersionFacts.Current.Product} runtime/{BuildVersionFacts.Current.Runtime} schema/{BuildVersionFacts.Current.StateSchema}", version.StandardOutput.Trim());
            Assert.Equal(0, protocol.ExitCode);
            using (var protocolDocument = JsonDocument.Parse(protocol.StandardOutput))
            {
                var protocolRoot = protocolDocument.RootElement;
                Assert.Equal(BuildVersionFacts.Current.Product, protocolRoot.GetProperty("product_version").GetString());
                Assert.Equal(BuildVersionFacts.Current.Runtime, protocolRoot.GetProperty("runtime_api_version").GetString());
                Assert.Equal(BuildVersionFacts.Current.StateSchema, protocolRoot.GetProperty("state_schema_version").GetString());
                Assert.Equal(LiveAdoptionProtocol.CurrentVersion, protocolRoot.GetProperty("live_adoption_protocol").GetInt32());
            }
            Assert.Equal(0, init.ExitCode);
            Assert.Contains("INITIALIZED", init.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(0, status.ExitCode);
            Assert.Contains("\"ok\": true", status.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(output);
            DeleteDirectory(project);
        }
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hiker.Pro.slnx")) && File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the Hiker Pro repository root");
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                if (item.Value is null) process.StartInfo.Environment.Remove(item.Key);
                else process.StartInfo.Environment[item.Key] = item.Value;
            }
        }

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

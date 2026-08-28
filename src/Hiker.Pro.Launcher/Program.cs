using System.Diagnostics;
using System.Text.Json;
using Hiker.Pro.Activation;

const string launcherVersion = "1.0.0";
var jsonMode = args.Contains("--json", StringComparer.Ordinal);
try
{
    var configuredHome = Environment.GetEnvironmentVariable("HIKER_HOME");
    var home = string.IsNullOrWhiteSpace(configuredHome)
        ? Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
          ?? throw new InvalidOperationException("Stable launcher cannot resolve Hiker home")
        : Path.GetFullPath(configuredHome);
    var target = await new RuntimeLauncherResolver(home).ResolveAsync().ConfigureAwait(false);
    if (string.Equals(Path.GetFullPath(target.Executable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Stable launcher pointer resolves back to the launcher");

    var start = new ProcessStartInfo(target.Executable)
    {
        UseShellExecute = false,
        WorkingDirectory = Directory.GetCurrentDirectory()
    };
    foreach (var argument in args) start.ArgumentList.Add(argument);
    start.Environment["HIKER_RUNTIME_IDENTITY"] = target.Pointer.RuntimeIdentity;
    start.Environment["HIKER_RUNTIME_BUILD_COMMIT"] = target.Pointer.BuildCommit;
    start.Environment["HIKER_INSTALL_GENERATION"] = target.Pointer.InstallGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);
    start.Environment["HIKER_ACTIVATION_EPOCH"] = target.Pointer.ActivationEpoch;
    start.Environment["HIKER_LAUNCHER_VERSION"] = launcherVersion;
    start.Environment["HIKER_RUNTIME_CHANNEL"] = target.Pointer.RuntimeChannel;
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Active Hiker runtime could not start");
    await process.WaitForExitAsync().ConfigureAwait(false);
    return process.ExitCode;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    if (jsonMode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            contract_version = "hiker-cli/v1",
            command = args.FirstOrDefault() ?? "unknown",
            ok = false,
            status = "LAUNCHER_BLOCKED",
            pro_state = "PRO_REQUIRED_BLOCKED",
            exit_code = 70,
            launcher_version = launcherVersion,
            error = exception.GetType().Name,
            message = exception.Message
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.Error.WriteLine($"hiker-launcher: {exception.GetType().Name}: {exception.Message}");
    }
    else
    {
        Console.Error.WriteLine($"hiker-launcher: {exception.GetType().Name}: {exception.Message}");
    }
    return 70;
}

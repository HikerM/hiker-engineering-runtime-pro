using System.Diagnostics;
using System.Text.Json;

namespace Hiker.Pro.Activation;

public sealed class ProcessRuntimePackageProbe : IRuntimePackageProbe
{
    public async Task<RuntimeProbeResult> ProbeAsync(string executable, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("version");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Runtime version probe could not start");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidDataException($"Runtime version probe failed: {await error.ConfigureAwait(false)}");
        using var document = JsonDocument.Parse(await output.ConfigureAwait(false));
        var root = document.RootElement;
        return new RuntimeProbeResult(
            root.GetProperty("product_version").GetString() ?? string.Empty,
            root.GetProperty("runtime_api_version").GetString() ?? string.Empty,
            root.GetProperty("state_schema_version").GetString() ?? string.Empty,
            root.GetProperty("live_adoption_protocol").GetInt32());
    }
}

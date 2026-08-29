using System.Diagnostics;
using System.Text.Json;
using System.Text;

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
        var output = ReadBoundedAsync(process.StandardOutput, 256 * 1024, cancellationToken);
        var error = ReadBoundedAsync(process.StandardError, 64 * 1024, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
        if (stdout.Truncated || stderr.Truncated) throw new InvalidDataException("Runtime version probe exceeded its result budget");
        if (process.ExitCode != 0) throw new InvalidDataException($"Runtime version probe failed: {stderr.Text}");
        using var document = JsonDocument.Parse(stdout.Text);
        var root = document.RootElement;
        return new RuntimeProbeResult(
            root.GetProperty("product_version").GetString() ?? string.Empty,
            root.GetProperty("runtime_api_version").GetString() ?? string.Empty,
            root.GetProperty("state_schema_version").GetString() ?? string.Empty,
            root.GetProperty("live_adoption_protocol").GetInt32());
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var value = new StringBuilder(Math.Min(maximum, 8192)); var buffer = new char[4096]; var observed = 0;
        while (true) { var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false); if (count == 0) break; observed += count; var remaining = maximum - value.Length; if (remaining > 0) value.Append(buffer, 0, Math.Min(remaining, count)); }
        return (value.ToString(), observed > maximum);
    }
}

using System.Diagnostics;
using System.Text;

namespace Hiker.Pro.Safety;

public sealed record BoundedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    long ObservedOutputCharacters,
    long ObservedErrorCharacters,
    bool OutputTruncated,
    bool ErrorTruncated);

public static class BoundedProcess
{
    public static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo start,
        int maxOutputCharacters = 1_048_576,
        int maxErrorCharacters = 65_536,
        CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {start.FileName}");
        var output = CaptureAsync(process.StandardOutput, maxOutputCharacters, cancellationToken);
        var error = CaptureAsync(process.StandardError, maxErrorCharacters, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        return new BoundedProcessResult(process.ExitCode, stdout.Text, stderr.Text, stdout.Observed, stderr.Observed, stdout.Truncated, stderr.Truncated);
    }

    private static async Task<(string Text, long Observed, bool Truncated)> CaptureAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var value = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var buffer = new char[8 * 1024];
        long observed = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            observed += count;
            var remaining = maximumCharacters - value.Length;
            if (remaining > 0) value.Append(buffer, 0, Math.Min(remaining, count));
        }
        return (value.ToString(), observed, observed > maximumCharacters);
    }
}

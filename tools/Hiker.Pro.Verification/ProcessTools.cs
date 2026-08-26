using System.Diagnostics;

namespace Hiker.Pro.Verification;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

internal static class ProcessTools
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {executable}");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        stopwatch.Stop();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            stopwatch.Elapsed);
    }

    public static async Task<ProcessResult> RequireAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(executable, workingDirectory, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result;
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.OrderBy(static value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var position = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[position];
    }
}

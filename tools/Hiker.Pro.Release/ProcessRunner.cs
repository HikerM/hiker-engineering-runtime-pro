using System.Diagnostics;

namespace Hiker.Pro.ReleaseTool;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var started = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(executable)}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded {timeout}");
        }

        var result = new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), started.Elapsed);
        return result;
    }

    public static async Task<ProcessResult> RequireAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(executable, workingDirectory, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} failed ({result.ExitCode}): {Bounded(result.StandardError)}");
        }

        return result;
    }

    private static string Bounded(string value) => value.Length <= 2000 ? value.Trim() : value[..2000].Trim();
}

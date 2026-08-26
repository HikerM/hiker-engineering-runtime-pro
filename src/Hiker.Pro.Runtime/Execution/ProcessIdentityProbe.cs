using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.Execution;

public sealed class ProcessIdentityProbe
{
    private readonly string _hostFingerprint = Hash($"{Environment.MachineName}|{Environment.OSVersion}|{Environment.ProcessPath}");

    public async Task<RuntimeProcessIdentity> CurrentAsync(CancellationToken cancellationToken = default) =>
        await CaptureAsync(Environment.ProcessId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Current process identity is unavailable");

    public Task<RuntimeProcessIdentity?> CaptureAsync(int processId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return Task.FromResult<RuntimeProcessIdentity?>(null);
            }

            var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime());
            string executable;
            try
            {
                executable = process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                executable = string.Empty;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RuntimeProcessIdentity?>(new RuntimeProcessIdentity(processId, startTime, ExecutableFingerprint(executable), _hostFingerprint));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult<RuntimeProcessIdentity?>(null);
        }
    }

    public async Task<bool> IsSameLiveProcessAsync(RuntimeProcessIdentity expected, CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(expected.ProcessId, cancellationToken).ConfigureAwait(false);
        return current is not null &&
               current.StartTime == expected.StartTime &&
               string.Equals(current.ExecutablePathFingerprint, expected.ExecutablePathFingerprint, StringComparison.Ordinal) &&
               string.Equals(current.HostFingerprint, expected.HostFingerprint, StringComparison.Ordinal);
    }

    private static string ExecutableFingerprint(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "unavailable";
        }

        var path = Path.GetFullPath(executable);
        var info = new FileInfo(path);
        return Hash($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

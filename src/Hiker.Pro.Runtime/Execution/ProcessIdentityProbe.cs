using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.Execution;

public sealed class ProcessIdentityProbe
{
    private static readonly string[] CodexHostProcessNames = ["ChatGPT", "codex", "codex-code-mode-host"];
    private readonly string _hostFingerprint = Hash($"{System.Environment.MachineName}|{System.Environment.OSVersion}|{System.Environment.ProcessPath}");
    private readonly Func<string, DateTimeOffset, CancellationToken, Task<bool>>? _providerHostRestartProof;

    public ProcessIdentityProbe(
        Func<string, DateTimeOffset, CancellationToken, Task<bool>>? providerHostRestartProof = null)
    {
        _providerHostRestartProof = providerHostRestartProof;
    }

    public async Task<RuntimeProcessIdentity> CurrentAsync(CancellationToken cancellationToken = default) =>
        await CaptureAsync(System.Environment.ProcessId, cancellationToken).ConfigureAwait(false)
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

    public async Task<bool> CanProveProviderHostRestartedAsync(
        string provider,
        DateTimeOffset priorBindingAttachedAt,
        CancellationToken cancellationToken = default)
    {
        if (_providerHostRestartProof is not null)
        {
            return await _providerHostRestartProof(provider, priorBindingAttachedAt, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ??
                                      System.Environment.GetEnvironmentVariable("CODEX_SESSION_ID")))
        {
            return false;
        }

        var observedCurrentHost = false;
        foreach (var processName in CodexHostProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return false;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (process.HasExited)
                        {
                            continue;
                        }

                        observedCurrentHost = true;
                        var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime());
                        if (startTime <= priorBindingAttachedAt)
                        {
                            return false;
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        return false;
                    }
                }
            }
        }

        return observedCurrentHost;
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

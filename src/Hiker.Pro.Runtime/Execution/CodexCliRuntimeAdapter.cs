using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Execution;

public sealed record CodexCliAdapterOptions(
    string Executable = "codex",
    string Sandbox = "workspace-write",
    string? Model = null,
    TimeSpan? StartTimeout = null,
    int MaxInstructionBytes = 65_536);

public sealed record CodexCliProbeResult(bool Available, string Version, string Status);

public sealed class CodexCliRuntimeAdapter : IExecutionRuntimeAdapter, IAsyncDisposable
{
    private readonly CodexCliAdapterOptions _options;
    private readonly ProcessIdentityProbe _identityProbe;
    private readonly ConcurrentDictionary<string, RuntimeStartRequest> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveProcess> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeLifecycle> _lifecycles = new(StringComparer.Ordinal);

    public CodexCliRuntimeAdapter(CodexCliAdapterOptions? options = null, ProcessIdentityProbe? identityProbe = null)
    {
        _options = options ?? new CodexCliAdapterOptions();
        _identityProbe = identityProbe ?? new ProcessIdentityProbe();
    }

    public string AdapterId => "codex-cli";
    public RuntimeCapabilities Capabilities { get; } = new(true, true, true, true, true, true);

    public async Task<CodexCliProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(_options.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--version");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Codex CLI");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var version = (await output.ConfigureAwait(false)).Trim();
            return new CodexCliProbeResult(process.ExitCode == 0, version, process.ExitCode == 0 ? "AVAILABLE" : "FAILED");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CodexCliProbeResult(false, string.Empty, exception.GetType().Name);
        }
    }

    public Task<RuntimeHandle> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!_plans.TryAdd(request.RuntimeId, request))
        {
            throw new InvalidOperationException("Runtime ID already has a Codex execution plan");
        }

        _lifecycles[request.RuntimeId] = RuntimeLifecycle.Starting;
        return Task.FromResult(new RuntimeHandle(
            request.RuntimeId,
            AdapterId,
            RuntimeLifecycle.Starting,
            null,
            DateTimeOffset.UtcNow,
            $"pending:{request.RuntimeId}"));
    }

    public Task<RuntimeHandle> ResumeAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) =>
        LaunchAsync(handle, SessionId(handle.RecoveryToken), cancellationToken);

    public async Task<RuntimeHandle> PauseAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        if (_active.TryRemove(handle.RuntimeId, out var active))
        {
            await StopProcessAsync(active, cancellationToken).ConfigureAwait(false);
        }

        _lifecycles[handle.RuntimeId] = RuntimeLifecycle.Paused;
        return handle with { Lifecycle = RuntimeLifecycle.Paused };
    }

    public async Task<RuntimeHandle> InterruptAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        if (_active.TryRemove(handle.RuntimeId, out var active))
        {
            await StopProcessAsync(active, cancellationToken).ConfigureAwait(false);
        }

        _lifecycles[handle.RuntimeId] = RuntimeLifecycle.Interrupted;
        return handle with { Lifecycle = RuntimeLifecycle.Interrupted };
    }

    public async Task<RuntimeHandle> CancelAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        if (_active.TryRemove(handle.RuntimeId, out var active))
        {
            await StopProcessAsync(active, cancellationToken).ConfigureAwait(false);
        }

        _lifecycles[handle.RuntimeId] = RuntimeLifecycle.Cancelled;
        return handle with { Lifecycle = RuntimeLifecycle.Cancelled };
    }

    public Task<RuntimeStatus> StatusAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        if (_active.TryGetValue(handle.RuntimeId, out var active))
        {
            if (!active.Process.HasExited)
                return Task.FromResult(new RuntimeStatus(handle.RuntimeId, RuntimeLifecycle.Running, handle.ProcessIdentity, DateTimeOffset.UtcNow, "PROCESS_RUNNING"));
            var terminal = active.Process.ExitCode == 0 ? RuntimeLifecycle.Completed : RuntimeLifecycle.Failed;
            _lifecycles[handle.RuntimeId] = terminal;
            return Task.FromResult(new RuntimeStatus(handle.RuntimeId, terminal, handle.ProcessIdentity, DateTimeOffset.UtcNow, $"EXIT_{active.Process.ExitCode}"));
        }

        var lifecycle = _lifecycles.GetValueOrDefault(handle.RuntimeId, RuntimeLifecycle.Interrupted);
        return Task.FromResult(new RuntimeStatus(handle.RuntimeId, lifecycle, handle.ProcessIdentity, DateTimeOffset.UtcNow, "NO_ACTIVE_PROCESS"));
    }

    public Task<RuntimeCheckpoint> CheckpointAsync(RuntimeHandle handle, string taskId, CancellationToken cancellationToken = default)
    {
        var session = SessionId(handle.RecoveryToken) ?? throw new InvalidOperationException("Codex session ID is unavailable for checkpoint");
        var request = Plan(handle.RuntimeId);
        var eventPath = EventPath(request);
        var length = File.Exists(eventPath) ? new FileInfo(eventPath).Length : 0;
        var fingerprint = Hash($"{session}|{length}|{request.SourceFingerprint}");
        return Task.FromResult(new RuntimeCheckpoint(
            $"codex-{session}-{length}",
            handle.RuntimeId,
            taskId,
            $"codex-session:{session}",
            fingerprint,
            DateTimeOffset.UtcNow));
    }

    public Task<RuntimeCollectedOutput> CollectOutputAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        var request = Plan(handle.RuntimeId);
        var refs = ExistingRefs(request).ToArray();
        return Task.FromResult(new RuntimeCollectedOutput(handle.RuntimeId, "CODEX_JSONL_CAPTURED", refs, false));
    }

    public Task<IReadOnlyList<string>> CollectArtifactsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(ExistingRefs(Plan(handle.RuntimeId)).ToArray());

    public async Task<IReadOnlyList<RuntimeCollectedEvent>> CollectEventsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        var request = Plan(handle.RuntimeId);
        var path = EventPath(request);
        if (!File.Exists(path)) return Array.Empty<RuntimeCollectedEvent>();
        var values = new List<RuntimeCollectedEvent>();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (values.Count < 256 && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "unknown" : "unknown";
                values.Add(new RuntimeCollectedEvent(
                    Hash(line)[..24],
                    handle.RuntimeId,
                    type,
                    "CODEX_EVENT_METADATA",
                    DateTimeOffset.UtcNow));
            }
            catch (JsonException)
            {
                values.Add(new RuntimeCollectedEvent(Hash(line)[..24], handle.RuntimeId, "invalid_json", "CODEX_EVENT_PARSE_FAILED", DateTimeOffset.UtcNow));
            }
        }

        return values;
    }

    public Task<RuntimeHandle> RecoverAsync(RuntimeRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        const string prefix = "codex-session:";
        if (!request.Checkpoint.StateArtifactRef.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Checkpoint is not a Codex session reference");
        var sessionId = request.Checkpoint.StateArtifactRef[prefix.Length..];
        var plan = Plan(request.PriorHandle.RuntimeId);
        var isHandoff = plan.NonSecretOptions?.ContainsKey("handoff_format") == true;
        return LaunchAsync(request.PriorHandle with { RecoveryToken = isHandoff ? $"pending:{request.PriorHandle.RuntimeId}" : sessionId }, isHandoff ? null : sessionId, cancellationToken);
    }

    public async Task ShutdownAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        if (_active.TryRemove(handle.RuntimeId, out var active))
        {
            await StopProcessAsync(active, cancellationToken).ConfigureAwait(false);
        }

        _lifecycles[handle.RuntimeId] = RuntimeLifecycle.Completed;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var active in _active.Values)
        {
            await StopProcessAsync(active, CancellationToken.None).ConfigureAwait(false);
        }
        _active.Clear();
    }

    private async Task<RuntimeHandle> LaunchAsync(RuntimeHandle handle, string? sessionId, CancellationToken cancellationToken)
    {
        var request = Plan(handle.RuntimeId);
        if (_active.TryGetValue(handle.RuntimeId, out var existing))
        {
            if (!existing.Process.HasExited)
            {
                throw new InvalidOperationException("Codex runtime already has an active process");
            }

            if (_active.TryRemove(handle.RuntimeId, out var completed))
            {
                await StopProcessAsync(completed, CancellationToken.None).ConfigureAwait(false);
            }
        }
        var prompt = sessionId is null
            ? await ReadInputAsync(request, cancellationToken).ConfigureAwait(false)
            : "Continue from the latest durable Hiker checkpoint. Reconcile the current state generation before writing and do not replay completed operations.";
        var outputDirectory = OutputDirectory(request);
        Directory.CreateDirectory(outputDirectory);
        var start = new ProcessStartInfo(_options.Executable)
        {
            WorkingDirectory = request.WorkspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        if (sessionId is not null)
        {
            start.ArgumentList.Add("resume");
        }
        start.ArgumentList.Add("--json");
        if (sessionId is null)
        {
            start.ArgumentList.Add("--color");
            start.ArgumentList.Add("never");
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(_options.Sandbox);
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(request.WorkspacePath);
        }
        if (_options.Model is not null)
        {
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add(_options.Model);
        }
        if (sessionId is not null) start.ArgumentList.Add(sessionId);
        start.ArgumentList.Add("-");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Codex CLI");
        var discoveredSession = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new FileStream(EventPath(request), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var errors = new FileStream(ErrorPath(request), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var lifetime = new CancellationTokenSource();
        var outputPump = PumpOutputAsync(process.StandardOutput, events, discoveredSession, lifetime.Token);
        var errorPump = PumpTextAsync(process.StandardError, errors, lifetime.Token);
        var active = new ActiveProcess(process, events, errors, outputPump, errorPump, lifetime);
        _active[handle.RuntimeId] = active;
        await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        string recoveredSession;
        try
        {
            if (sessionId is not null)
            {
                recoveredSession = sessionId;
            }
            else
            {
                var timeout = _options.StartTimeout ?? TimeSpan.FromSeconds(30);
                recoveredSession = await discoveredSession.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _active.TryRemove(handle.RuntimeId, out _);
            await StopProcessAsync(active, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var identity = await _identityProbe.CaptureAsync(process.Id, cancellationToken).ConfigureAwait(false);
        _lifecycles[handle.RuntimeId] = RuntimeLifecycle.Running;
        return handle with { Lifecycle = RuntimeLifecycle.Running, ProcessIdentity = identity, RecoveryToken = recoveredSession };
    }

    private async Task<string> ReadInputAsync(RuntimeStartRequest request, CancellationToken cancellationToken)
    {
        var instruction = ResolveAiRef(request.WorkspacePath, request.InstructionArtifactRef);
        var context = ResolveAiRef(request.WorkspacePath, request.ContextArtifactRef);
        var instructionBytes = new FileInfo(instruction).Length;
        var contextBytes = string.Equals(instruction, context, StringComparison.OrdinalIgnoreCase) ? 0 : new FileInfo(context).Length;
        if (instructionBytes + contextBytes > _options.MaxInstructionBytes)
            throw new InvalidDataException("Codex input exceeds adapter byte budget");
        var prompt = await File.ReadAllTextAsync(instruction, cancellationToken).ConfigureAwait(false);
        if (contextBytes > 0)
            prompt = $"{prompt}\n\n<verified_context>\n{await File.ReadAllTextAsync(context, cancellationToken).ConfigureAwait(false)}\n</verified_context>";
        return prompt;
    }

    private static async Task PumpOutputAsync(StreamReader reader, Stream output, TaskCompletionSource<string> session, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "thread.started" &&
                    document.RootElement.TryGetProperty("thread_id", out var threadId) && threadId.GetString() is { Length: > 0 } value)
                    session.TrySetResult(value);
            }
            catch (JsonException) { }
        }

        session.TrySetException(new InvalidOperationException("Codex exited before reporting a session ID"));
    }

    private static async Task PumpTextAsync(StreamReader reader, Stream output, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StopProcessAsync(ActiveProcess active, CancellationToken cancellationToken)
    {
        try
        {
            if (!active.Process.HasExited) active.Process.Kill(entireProcessTree: true);
            await active.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            active.Lifetime.Cancel();
            try { await Task.WhenAll(active.OutputPump, active.ErrorPump).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await active.Events.DisposeAsync().ConfigureAwait(false);
            await active.Errors.DisposeAsync().ConfigureAwait(false);
            active.Process.Dispose();
            active.Lifetime.Dispose();
        }
    }

    private RuntimeStartRequest Plan(string runtimeId) => _plans.TryGetValue(runtimeId, out var request)
        ? request
        : throw new InvalidOperationException("Codex runtime plan is missing");

    private static string? SessionId(string recoveryToken) => recoveryToken.StartsWith("pending:", StringComparison.Ordinal) ? null : recoveryToken;
    private static string OutputDirectory(RuntimeStartRequest request) => Path.Combine(request.WorkspacePath, ".ai", "cache", "runtime", request.RuntimeId);
    private static string EventPath(RuntimeStartRequest request) => Path.Combine(OutputDirectory(request), "events.jsonl");
    private static string ErrorPath(RuntimeStartRequest request) => Path.Combine(OutputDirectory(request), "stderr.log");
    private static IEnumerable<string> ExistingRefs(RuntimeStartRequest request)
    {
        foreach (var path in new[] { EventPath(request), ErrorPath(request) })
            if (File.Exists(path)) yield return Path.GetRelativePath(Path.Combine(request.WorkspacePath, ".ai"), path).Replace('\\', '/');
    }

    private static string ResolveAiRef(string root, string reference)
    {
        if (Path.IsPathRooted(reference)) throw new InvalidDataException("Runtime input reference must be relative");
        var aiRoot = Path.GetFullPath(Path.Combine(root, ".ai"));
        var normalized = reference.StartsWith(".ai/", StringComparison.Ordinal) ? reference[4..] : reference;
        var path = Path.GetFullPath(Path.Combine(aiRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(aiRoot, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || !File.Exists(path))
            throw new InvalidDataException("Runtime input reference is missing or escapes .ai");
        return path;
    }

    private static void ValidateRequest(RuntimeStartRequest request)
    {
        if (!Directory.Exists(request.WorkspacePath) || string.IsNullOrWhiteSpace(request.RuntimeId))
            throw new ArgumentException("Codex runtime request is invalid", nameof(request));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ActiveProcess(
        Process Process,
        Stream Events,
        Stream Errors,
        Task OutputPump,
        Task ErrorPump,
        CancellationTokenSource Lifetime);
}

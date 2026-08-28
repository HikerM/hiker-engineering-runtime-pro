using System.Text.Json;

namespace Hiker.Pro.Activation;

public sealed class RuntimeLauncherResolver(string hikerHome)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RuntimePointerStore pointerStore = new(hikerHome);

    public async Task<RuntimeLaunchTarget> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var pointer = await pointerStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No verified active runtime is configured");
        var runtimesRoot = Path.Combine(pointerStore.Home, "runtimes");
        var runtimeRoot = RuntimePathPolicy.ResolveWithin(runtimesRoot, pointer.ActiveRuntime);
        RuntimePathPolicy.RejectReparsePath(pointerStore.Home, runtimeRoot);
        var identityPath = Path.Combine(runtimeRoot, "runtime-identity.json");
        if (!File.Exists(identityPath)) throw new InvalidDataException("Active runtime identity manifest is missing");
        var identityInfo = new FileInfo(identityPath);
        if (identityInfo.Length is <= 0 or > 64 * 1024 || (identityInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Active runtime identity manifest is invalid");
        var identity = JsonSerializer.Deserialize<RuntimeIdentity>(await File.ReadAllTextAsync(identityPath, cancellationToken).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("Active runtime identity manifest is empty");
        if (!string.Equals(identity.RuntimeIdentityValue, pointer.RuntimeIdentity, StringComparison.Ordinal) ||
            !string.Equals(identity.ProductVersion, pointer.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(identity.BuildCommit, pointer.BuildCommit, StringComparison.Ordinal))
            throw new InvalidDataException("Active runtime identity does not match the pointer");
        var executable = Path.Combine(runtimeRoot, "hiker.exe");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("Active runtime executable is missing", executable);
        return new RuntimeLaunchTarget(executable, pointer, 2);
    }
}

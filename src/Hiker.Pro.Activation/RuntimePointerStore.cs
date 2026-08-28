using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Activation;

public sealed class RuntimePointerStore(string hikerHome)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string Home { get; } = Path.GetFullPath(hikerHome);
    public string StateDirectory => Path.Combine(Home, "state");
    public string PointerPath => Path.Combine(StateDirectory, "current-runtime.json");

    public async Task<ActiveRuntimePointer?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PointerPath)) return null;
        var info = new FileInfo(PointerPath);
        if (info.Length is <= 0 or > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Active runtime pointer is invalid");
        await using var stream = new FileStream(PointerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var pointer = await JsonSerializer.DeserializeAsync<ActiveRuntimePointer>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Active runtime pointer is empty");
        Validate(pointer);
        return pointer;
    }

    public async Task WriteAtomicAsync(ActiveRuntimePointer pointer, CancellationToken cancellationToken = default)
    {
        Validate(pointer);
        Directory.CreateDirectory(StateDirectory);
        var temporary = Path.Combine(StateDirectory, $".current-runtime-{Guid.NewGuid():N}.tmp");
        var payload = JsonSerializer.Serialize(pointer, Json);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, PointerPath, overwrite: true);
    }

    public static void Validate(ActiveRuntimePointer pointer)
    {
        if (!string.Equals(pointer.Format, "hiker-active-runtime/v1", StringComparison.Ordinal) ||
            !RuntimePathPolicy.IsSafeToken(pointer.ActiveRuntime) ||
            pointer.PreviousRuntime is not null && !RuntimePathPolicy.IsSafeToken(pointer.PreviousRuntime) ||
            !RuntimePathPolicy.IsSafeToken(pointer.RuntimeIdentity) ||
            pointer.InstallGeneration <= 0 ||
            string.IsNullOrWhiteSpace(pointer.ActivationEpoch) ||
            pointer.LiveAdoptionProtocol <= 0)
        {
            throw new InvalidDataException("Active runtime pointer failed validation");
        }
    }
}

internal static class RuntimePathPolicy
{
    public static bool IsSafeToken(string value) =>
        value.Length is > 0 and <= 120 &&
        !value.StartsWith(".staging", StringComparison.OrdinalIgnoreCase) &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    public static string ResolveWithin(string root, string child)
    {
        var parent = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(parent, child));
        var relative = Path.GetRelativePath(parent, target);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Runtime path escapes the installation root");
        return target;
    }

    public static void RejectReparsePath(string root, string target)
    {
        var current = new DirectoryInfo(Path.GetFullPath(target));
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null && current.FullName.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime path contains a reparse point");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
    }
}

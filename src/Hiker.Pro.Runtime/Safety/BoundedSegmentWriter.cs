using System.Text;
using System.Text.Json;

namespace Hiker.Pro.Safety;

public sealed class BoundedSegmentWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly long _maxSegmentBytes;
    private readonly int _maxSegments;
    private FileStream? _stream;

    public BoundedSegmentWriter(string path, long maxSegmentBytes, int maxSegments)
    {
        _path = path;
        _maxSegmentBytes = Math.Max(64 * 1024, maxSegmentBytes);
        _maxSegments = Math.Clamp(maxSegments, 1, 16);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var raw = Encoding.UTF8.GetBytes(line + "\n");
        if (raw.Length > _maxSegmentBytes)
            raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hiker.output_truncated", observed_bytes = raw.Length, reason_code = "SINGLE_LINE_BUDGET_EXCEEDED" }) + "\n");
        await EnsureStreamAsync(raw.Length, cancellationToken).ConfigureAwait(false);
        await _stream!.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStreamAsync(int incomingBytes, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length + incomingBytes > _maxSegmentBytes) await RotateAsync().ConfigureAwait(false);
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        if (_stream.Length + incomingBytes <= _maxSegmentBytes) return;
        await _stream.DisposeAsync().ConfigureAwait(false); _stream = null;
        await RotateAsync().ConfigureAwait(false);
        _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private Task RotateAsync()
    {
        var oldest = $"{_path}.{_maxSegments}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _maxSegments - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}"; var target = $"{_path}.{index + 1}";
            if (File.Exists(source)) File.Move(source, target, overwrite: true);
        }
        if (File.Exists(_path)) File.Move(_path, $"{_path}.1", overwrite: true);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

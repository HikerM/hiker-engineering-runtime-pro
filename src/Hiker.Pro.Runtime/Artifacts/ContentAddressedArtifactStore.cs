using System.Security.Cryptography;
using Hiker.Pro.State;

namespace Hiker.Pro.Artifacts;

public sealed record ArtifactPutRequest(
    string ProjectId,
    string Type,
    Stream Content,
    string? TaskId,
    string? EvidenceId,
    string SourceFingerprint,
    RetentionTier Retention);

public sealed record ArtifactVerification(bool Valid, string Status, ArtifactRecord Record);

public sealed class ContentAddressedArtifactStore
{
    private readonly ProjectLayout _layout;
    private readonly StateCoordinator _state;

    public ContentAddressedArtifactStore(ProjectLayout layout, StateCoordinator state)
    {
        _layout = layout;
        _state = state;
    }

    public async Task<ArtifactRecord> PutAsync(ArtifactPutRequest request, CancellationToken cancellationToken = default)
    {
        _layout.EnsureDirectories();
        var temporary = Path.Combine(_layout.ArtifactsRoot, $".upload-{Guid.NewGuid():N}.tmp");
        long length = 0;
        string hash;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await request.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    length += read;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var directory = Path.Combine(_layout.ArtifactsRoot, hash[..2]);
            Directory.CreateDirectory(directory);
            var final = Path.Combine(directory, hash);
            if (!File.Exists(final))
            {
                File.Move(temporary, final);
            }
            else
            {
                File.Delete(temporary);
                await VerifyFileAsync(final, hash, cancellationToken).ConfigureAwait(false);
            }

            var relative = Path.GetRelativePath(_layout.AiRoot, final).Replace(Path.DirectorySeparatorChar, '/');
            var record = new ArtifactRecord(
                $"artifact-{hash}",
                request.ProjectId,
                request.Type,
                hash,
                length,
                relative,
                request.TaskId,
                request.EvidenceId,
                request.SourceFingerprint,
                request.Retention,
                DateTimeOffset.UtcNow);
            await _state.RegisterArtifactAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<ArtifactVerification> VerifyAsync(ArtifactRecord record, CancellationToken cancellationToken = default)
    {
        var path = Resolve(record.RelativeRef);
        if (!File.Exists(path))
        {
            return new ArtifactVerification(false, "MISSING", record);
        }

        var info = new FileInfo(path);
        if (info.Length != record.Bytes)
        {
            return new ArtifactVerification(false, "SIZE_MISMATCH", record);
        }

        try
        {
            await VerifyFileAsync(path, record.Sha256, cancellationToken).ConfigureAwait(false);
            return new ArtifactVerification(true, "VALID", record);
        }
        catch (InvalidDataException)
        {
            return new ArtifactVerification(false, "HASH_MISMATCH", record);
        }
    }

    public Stream OpenRead(ArtifactRecord record) => new FileStream(
        Resolve(record.RelativeRef),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.SequentialScan);

    private string Resolve(string relativeRef)
    {
        var normalized = relativeRef.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(_layout.AiRoot, normalized));
        if (!path.StartsWith(_layout.ArtifactsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Artifact reference escapes the content-addressed store");
        }

        return path;
    }

    private static async Task VerifyFileAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(hash),
                System.Text.Encoding.ASCII.GetBytes(expectedHash)))
        {
            throw new InvalidDataException("Artifact content hash does not match its address");
        }
    }
}

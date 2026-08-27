using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Storage;

public sealed class IncrementalArtifactBackupService(ProjectLayout layout, StateCoordinator state)
{
    public async Task<IncrementalArtifactBackupResult> CreateAsync(
        string projectId,
        string? destinationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(projectId, EntityKinds.Project, "artifact-backup").Normalize();
        layout.EnsureDirectories();
        var destination = Path.GetFullPath(destinationDirectory ?? layout.ArtifactBackupRoot);
        var aiRoot = Path.GetFullPath(layout.AiRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var artifactRoot = Path.GetFullPath(layout.ArtifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup destination cannot be inside active artifact storage");
        Directory.CreateDirectory(destination);
        var objects = Path.Combine(destination, "objects");
        var manifests = Path.Combine(destination, "manifests");
        var stateBackups = Path.Combine(destination, "state");
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(stateBackups);

        var reachability = await state.AnalyzeArtifactReachabilityAsync(projectId, DateTimeOffset.MinValue, cancellationToken: cancellationToken).ConfigureAwait(false);
        var artifacts = reachability
            .Where(static value => value.Reachability != StorageReachability.Unreachable)
            .Select(static value => value.Artifact)
            .GroupBy(static value => value.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static value => value.ArtifactId, StringComparer.Ordinal).First())
            .OrderBy(static value => value.Sha256, StringComparer.Ordinal)
            .ToArray();
        var copied = 0;
        var reused = 0;
        long copiedBytes = 0;
        var items = new List<BackupArtifactItem>(artifacts.Length);
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = layout.ResolveAiRelativePath(artifact.RelativeRef);
            if (!source.StartsWith(aiRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new InvalidDataException($"Artifact backup source is missing or escapes .ai: {artifact.ArtifactId}");
            var targetDirectory = Path.Combine(objects, artifact.Sha256[..2]);
            var target = Path.Combine(targetDirectory, artifact.Sha256);
            Directory.CreateDirectory(targetDirectory);
            if (File.Exists(target) && new FileInfo(target).Length == artifact.Bytes)
            {
                reused++;
            }
            else
            {
                await StoragePayloadPolicy.EnsureArchiveFileSafeAsync(source, cancellationToken).ConfigureAwait(false);
                var temp = $"{target}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    var actual = await HashFileAsync(temp, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Artifact backup hash mismatch: {artifact.ArtifactId}");
                    File.Move(temp, target, overwrite: false);
                    copied++;
                    copiedBytes += artifact.Bytes;
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
            items.Add(new BackupArtifactItem(artifact.Sha256, artifact.Bytes, Path.GetRelativePath(destination, target).Replace('\\', '/')));
        }

        var stateBackup = await new StateBackupService(layout).CreateAsync(stateBackups, cancellationToken).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        var manifestBody = JsonSerializer.Serialize(new
        {
            format = "hiker-incremental-artifact-backup/v1",
            project_id = projectId,
            state_backup = new
            {
                path = Path.GetRelativePath(destination, stateBackup.BackupPath).Replace('\\', '/'),
                stateBackup.Sha256,
                stateBackup.Bytes
            },
            artifacts = items,
            created_at = createdAt
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        StoragePayloadPolicy.EnsureArchiveSafe(manifestBody);
        var manifestHash = Hash(manifestBody);
        var setId = $"backup-{createdAt:yyyyMMddTHHmmssfffffffZ}-{manifestHash[..12]}";
        var manifestPath = Path.Combine(manifests, $"{setId}.json");
        await WriteDurableAsync(manifestPath, manifestBody, cancellationToken).ConfigureAwait(false);
        return new IncrementalArtifactBackupResult(
            "CREATED",
            setId,
            manifestPath,
            manifestHash,
            stateBackup,
            artifacts.Length,
            copied,
            reused,
            copiedBytes,
            createdAt);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task WriteDurableAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private sealed record BackupArtifactItem(string Sha256, long Bytes, string ObjectRef);
}

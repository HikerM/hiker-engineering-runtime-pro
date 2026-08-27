using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Storage;

namespace Hiker.Pro.State;

public sealed partial class SqliteStateStore
{
    private static readonly HashSet<string> CurrentStorageDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "state", "artifacts", "archive", "exports", "projections", "cache"
    };

    public async Task<IncrementalMigrationInventoryResult> InventoryLegacyStorageAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = new StateKey(projectId, EntityKinds.Project, "migration-inventory").Normalize().ProjectId;
        if (!Directory.Exists(_layout.AiRoot))
            return new IncrementalMigrationInventoryResult("NO_AI_CONTENT", 0, 0, 0, Array.Empty<MigrationInventorySegment>());
        var directories = Directory.EnumerateDirectories(_layout.AiRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !CurrentStorageDirectories.Contains(Path.GetFileName(path)))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Length > 256) throw new InvalidOperationException("Legacy inventory root budget is exhausted");
        var segments = new List<MigrationInventorySegment>();
        var scanned = 0;
        var reused = 0;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var directory in directories)
        {
            var info = new DirectoryInfo(directory);
            var relative = Path.GetRelativePath(_layout.AiRoot, directory).Replace(Path.DirectorySeparatorChar, '/');
            var observed = info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? IsolatedInventorySegment(project, relative, info.Attributes)
                : ScanInventorySegment(project, directory, relative);
            var identity = observed.Fingerprint;
            var cached = await ReadInventorySegmentAsync(connection, project, relative, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Value.RootIdentity == identity)
            {
                segments.Add(cached.Value.Segment);
                reused++;
                continue;
            }
            await UpsertInventorySegmentAsync(connection, observed, identity, cancellationToken).ConfigureAwait(false);
            segments.Add(observed);
            scanned++;
        }
        return new IncrementalMigrationInventoryResult(scanned == 0 ? "UNCHANGED_REUSED" : "INCREMENTAL_UPDATED", scanned, reused, scanned, ContractCollections.Freeze(segments));
    }

    private MigrationInventorySegment ScanInventorySegment(string projectId, string root, string relativeRoot)
    {
        var canonical = new StringBuilder();
        long files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);
                var relative = Path.GetRelativePath(root, path);
                if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    throw new InvalidDataException("Legacy inventory path escapes its segment");
                var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    canonical.Append("R|").Append(normalized).Append('|').Append((long)attributes).Append('\n');
                    continue;
                }
                if (Directory.Exists(path))
                {
                    canonical.Append("D|").Append(normalized).Append('|').Append(Directory.GetLastWriteTimeUtc(path).Ticks).Append('\n');
                    pending.Push(path);
                    continue;
                }
                files++;
                if (files > 100_000) throw new InvalidOperationException("Legacy inventory segment file budget is exhausted");
                var info = new FileInfo(path);
                bytes += info.Length;
                if (bytes > 100L * 1024 * 1024 * 1024) throw new InvalidOperationException("Legacy inventory segment byte budget is exhausted");
                canonical.Append("F|").Append(normalized).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return new MigrationInventorySegment(projectId, relativeRoot, fingerprint, files, bytes, "VERIFIED", DateTimeOffset.UtcNow);
    }

    private static MigrationInventorySegment IsolatedInventorySegment(string projectId, string relativeRoot, FileAttributes attributes)
    {
        var identity = $"REPARSE_POINT|{relativeRoot}|{(long)attributes}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new MigrationInventorySegment(projectId, relativeRoot, fingerprint, 0, 0, "ISOLATED_REPARSE_POINT", DateTimeOffset.UtcNow);
    }

    private static async Task<(string RootIdentity, MigrationInventorySegment Segment)?> ReadInventorySegmentAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string projectId,
        string relativeRoot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_identity,fingerprint,files,bytes,status,verified_at FROM migration_inventory_segments WHERE project_id=$project AND relative_root=$root;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$root", relativeRoot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), new MigrationInventorySegment(projectId, relativeRoot, reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)))
            : null;
    }

    private static async Task UpsertInventorySegmentAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        MigrationInventorySegment segment,
        string rootIdentity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO migration_inventory_segments(project_id,relative_root,root_identity,fingerprint,files,bytes,status,verified_at)
            VALUES($project,$root,$identity,$fingerprint,$files,$bytes,$status,$verified)
            ON CONFLICT(project_id,relative_root) DO UPDATE SET root_identity=excluded.root_identity,fingerprint=excluded.fingerprint,
                files=excluded.files,bytes=excluded.bytes,status=excluded.status,verified_at=excluded.verified_at;
            """;
        command.Parameters.AddWithValue("$project", segment.ProjectId);
        command.Parameters.AddWithValue("$root", segment.RelativeRoot);
        command.Parameters.AddWithValue("$identity", rootIdentity);
        command.Parameters.AddWithValue("$fingerprint", segment.Fingerprint);
        command.Parameters.AddWithValue("$files", segment.Files);
        command.Parameters.AddWithValue("$bytes", segment.Bytes);
        command.Parameters.AddWithValue("$status", segment.Status);
        command.Parameters.AddWithValue("$verified", segment.VerifiedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

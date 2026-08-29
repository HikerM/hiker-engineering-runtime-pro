using System.Diagnostics;

namespace Hiker.Pro.Safety;

public sealed record TraversalBudget(
    int MaxDepth = 12,
    int MaxDirectories = 10_000,
    int MaxEntries = 100_000,
    int MaxFiles = 100_000,
    long MaxObservedBytes = 100L * 1024 * 1024 * 1024,
    int MaxElapsedMilliseconds = 30_000,
    int MaxEntriesPerDirectory = 4_096);

public sealed record TraversalReceipt(
    string Status,
    string? Limit,
    int Directories,
    int Entries,
    int Files,
    long ObservedBytes,
    double ElapsedMilliseconds,
    int ReparsePoints);

public sealed class TraversalLimitReachedException : IOException
{
    public TraversalLimitReachedException(string limit, TraversalReceipt receipt)
        : base($"TRAVERSAL_LIMIT_REACHED:{limit}")
    {
        Limit = limit;
        Receipt = receipt with { Status = "TRAVERSAL_LIMIT_REACHED", Limit = limit };
    }

    public string Limit { get; }
    public TraversalReceipt Receipt { get; }
}

public static class TraversalGuard
{
    public static (IReadOnlyList<string> Files, TraversalReceipt Receipt) EnumerateFiles(
        string root,
        TraversalBudget? budget = null,
        Func<string, bool>? include = null)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var limits = budget ?? new TraversalBudget();
        var timer = Stopwatch.StartNew();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((canonicalRoot, 0));
        var files = new List<string>();
        var directories = 0;
        var entries = 0;
        var observedFiles = 0;
        long bytes = 0;
        var reparsePoints = 0;

        void Check(string? forced = null)
        {
            var receipt = new TraversalReceipt("COMPLETE", null, directories, entries, observedFiles, bytes, timer.Elapsed.TotalMilliseconds, reparsePoints);
            var limit = forced ??
                (directories > limits.MaxDirectories ? "directories" :
                 entries > limits.MaxEntries ? "entries" :
                 observedFiles > limits.MaxFiles ? "files" :
                 bytes > limits.MaxObservedBytes ? "observed_bytes" :
                 timer.ElapsedMilliseconds > limits.MaxElapsedMilliseconds ? "elapsed_ms" : null);
            if (limit is not null) throw new TraversalLimitReachedException(limit, receipt);
        }

        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();
            if (depth > limits.MaxDepth) continue;
            if (IsReparsePoint(current))
            {
                reparsePoints++;
                throw new InvalidDataException("REPARSE_POINT_BLOCKED:bounded traversal root");
            }
            directories++;
            Check();
            string[] children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                    .Take(limits.MaxEntriesPerDirectory + 1)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (children.Length > limits.MaxEntriesPerDirectory) Check("entries_per_directory");
            foreach (var path in children)
            {
                entries++;
                Check();
                EnsureContained(canonicalRoot, path, requireExisting: true);
                if (IsReparsePoint(path))
                {
                    reparsePoints++;
                    throw new InvalidDataException("REPARSE_POINT_BLOCKED:bounded traversal entry");
                }
                if (Directory.Exists(path))
                {
                    if (depth < limits.MaxDepth) pending.Enqueue((path, depth + 1));
                    continue;
                }
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                observedFiles++;
                bytes += info.Length;
                Check();
                if (include is null || include(path)) files.Add(path);
            }
        }
        return (files, new TraversalReceipt("COMPLETE", null, directories, entries, observedFiles, bytes, timer.Elapsed.TotalMilliseconds, reparsePoints));
    }

    public static string EnsureContained(string root, string path, bool requireExisting)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(canonicalRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Path escapes its bounded root");
        if (Directory.Exists(canonicalRoot) && IsReparsePoint(canonicalRoot))
            throw new InvalidDataException("Bounded root is a reparse point");
        var current = canonicalRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(part) || part == ".") continue;
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new InvalidDataException("Path crosses a symlink, junction, or reparse point");
        }
        if (requireExisting && !File.Exists(candidate) && !Directory.Exists(candidate))
            throw new FileNotFoundException("Bounded path is missing", candidate);
        return candidate;
    }

    public static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }
}

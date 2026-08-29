using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace Hiker.Pro.State;

internal sealed record LegacyAiInspection(
    LegacyInventorySummary Summary,
    IReadOnlyList<string> UnknownItems);

internal static class LegacyAiInventoryInspector
{
    private const int MaxFiles = 100_000;
    private const int MaxDirectories = 10_000;
    private const int MaxDepth = 32;
    private const int MaxEntriesPerDirectory = 4_096;
    private const long MaxBytes = 100L * 1024 * 1024 * 1024;
    private const int MaxElapsedMilliseconds = 30_000;

    private static readonly IReadOnlyDictionary<string, DirectoryPolicy> SemanticDirectories =
        new Dictionary<string, DirectoryPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["architecture"] = new("LEGACY_ENGINEERING_ARCHITECTURE", false),
            ["requirements"] = new("LEGACY_REQUIREMENT_FACT", false),
            ["evidence"] = new("LEGACY_EVIDENCE", true),
            ["knowledge"] = new("LEGACY_ENGINEERING_MEMORY", false),
            ["logs"] = new("HISTORICAL_LOG_ARCHIVE", true)
        };

    private static readonly IReadOnlySet<string> KnownKnowledgeFiles = new HashSet<string>(
        new[] { "knowledge/metadata.json", "knowledge/engineering.db", "knowledge/engineering.db-shm", "knowledge/engineering.db-wal" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> KnownTopLevelNames = new HashSet<string>(
        new[]
        {
            "state", "artifacts", "exports", "archive", "cache", "projections",
            "context", "runtime", "governance", "workspace", "quality", "tasks", "schema.json",
            "architecture", "requirements", "evidence", "knowledge", "logs"
        },
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> SemanticTopLevelNames { get; } = new HashSet<string>(
        SemanticDirectories.Keys,
        StringComparer.OrdinalIgnoreCase);

    public static LegacyAiInspection Inspect(ProjectLayout layout, bool inspectLegacyContents = true)
    {
        if (!Directory.Exists(layout.AiRoot))
        {
            return new LegacyAiInspection(EmptySummary(), Array.Empty<string>());
        }

        var entries = new List<LegacyInventoryEntry>();
        var unknown = new List<string>();
        var fingerprintItems = new List<string>();
        foreach (var directoryPolicy in SemanticDirectories)
        {
            string absolute;
            try { absolute = layout.ResolveAiRelativePath(directoryPolicy.Key); }
            catch (InvalidDataException)
            {
                AddUnknown(unknown, $"{directoryPolicy.Key}:REPARSE_OR_PATH_ESCAPE");
                entries.Add(UnknownEntry(directoryPolicy.Key, "REPARSE_OR_PATH_ESCAPE", "NO_FOLLOW_BLOCKED"));
                continue;
            }
            if (!Directory.Exists(absolute)) continue;

            fingerprintItems.Add($"D\0{directoryPolicy.Key}");
            var migratable = inspectLegacyContents
                ? InspectSemanticDirectory(
                    layout,
                    directoryPolicy.Key,
                    directoryPolicy.Value,
                    entries,
                    unknown,
                    fingerprintItems)
                : 0;
            var classifications = new List<string>
            {
                LegacyInventoryClassifications.KnownLegacyContent,
                LegacyInventoryClassifications.SafePreservedContent
            };
            if (migratable > 0)
            {
                classifications.Add(LegacyInventoryClassifications.MigratableLegacyContent);
            }

            entries.Add(new LegacyInventoryEntry(
                directoryPolicy.Key,
                directoryPolicy.Value.SemanticType,
                ContractCollections.Freeze(classifications),
                migratable > 0 ? "VALIDATED_FACT_IMPORT_AND_SOURCE_PRESERVE" : "SOURCE_PRESERVE_ONLY",
                migratable));
        }

        InspectTopLevel(layout, inspectLegacyContents, entries, unknown, fingerprintItems);
        var frozenEntries = ContractCollections.Freeze(entries);
        var summary = new LegacyInventorySummary(
            frozenEntries.Count(entry => entry.Classifications.Contains(LegacyInventoryClassifications.KnownLegacyContent, StringComparer.Ordinal)),
            frozenEntries.Count(entry => entry.Classifications.Contains(LegacyInventoryClassifications.SafePreservedContent, StringComparer.Ordinal)),
            frozenEntries.Sum(static entry => entry.MigratableFacts),
            unknown.Count,
            InventoryFingerprint(fingerprintItems),
            frozenEntries);
        return new LegacyAiInspection(summary, ContractCollections.Freeze(unknown));
    }

    private static int InspectSemanticDirectory(
        ProjectLayout layout,
        string relativeDirectory,
        DirectoryPolicy directoryPolicy,
        List<LegacyInventoryEntry> entries,
        List<string> unknown,
        List<string> fingerprintItems)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        if (IsReparsePoint(directory))
        {
            AddUnknown(unknown, $"{relativeDirectory}:REPARSE_POINT");
            entries.Add(UnknownEntry(relativeDirectory, "REPARSE_POINT", "NO_FOLLOW_BLOCKED"));
            return 0;
        }

        var count = 0;
        var scanned = 0; var directories = 0; long bytes = 0; var timer = Stopwatch.StartNew();
        try
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((directory, 0));
            while (pending.Count > 0 && scanned <= MaxFiles)
            {
                var (current, depth) = pending.Pop(); directories++;
                if (directories > MaxDirectories || depth > MaxDepth || timer.ElapsedMilliseconds > MaxElapsedMilliseconds)
                {
                    AddUnknown(unknown, $"{relativeDirectory}:TRAVERSAL_LIMIT_REACHED"); entries.Add(UnknownEntry(relativeDirectory, "TRAVERSAL_LIMIT_REACHED", "BOUNDED_SCAN_BLOCKED")); break;
                }
                var children = Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly).Take(MaxEntriesPerDirectory + 1).ToArray();
                if (children.Length > MaxEntriesPerDirectory)
                {
                    AddUnknown(unknown, $"{relativeDirectory}:INVENTORY_LIMIT_EXCEEDED:TRAVERSAL_LIMIT_REACHED:entries_per_directory:{MaxEntriesPerDirectory}"); entries.Add(UnknownEntry(relativeDirectory, "TRAVERSAL_LIMIT_REACHED", "BOUNDED_SCAN_BLOCKED")); break;
                }
                foreach (var item in children)
                {
                    scanned++;
                    if (scanned > MaxFiles)
                    {
                        AddUnknown(unknown, $"{relativeDirectory}:INVENTORY_LIMIT_EXCEEDED:{MaxFiles}");
                        entries.Add(UnknownEntry(relativeDirectory, "INVENTORY_LIMIT_EXCEEDED", "BOUNDED_SCAN_BLOCKED"));
                        break;
                    }

                    var relative = Path.GetRelativePath(layout.AiRoot, item).Replace('\\', '/');
                    if (IsReparsePoint(item))
                    {
                        AddUnknown(unknown, $"{relative}:REPARSE_POINT");
                        entries.Add(UnknownEntry(relative, "REPARSE_POINT", "NO_FOLLOW_BLOCKED"));
                        continue;
                    }

                    if (Directory.Exists(item))
                    {
                        fingerprintItems.Add($"D\0{relative}");
                        if (directoryPolicy.OpaquePreserved) pending.Push((item, depth + 1));
                        else PreserveUncatalogued(layout, item, relative, entries, unknown, fingerprintItems);
                        continue;
                    }

                    var file = new FileInfo(item);
                    bytes += file.Length;
                    if (bytes > MaxBytes) { AddUnknown(unknown, $"{relativeDirectory}:BYTE_LIMIT_EXCEEDED:{MaxBytes}"); entries.Add(UnknownEntry(relativeDirectory, "TRAVERSAL_LIMIT_REACHED", "BOUNDED_SCAN_BLOCKED")); break; }
                    fingerprintItems.Add($"F\0{relative}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}");
                    if (Community518SourceCatalog.TryResolve(relative, item, out var source))
                    {
                        if (Community518SourceCatalog.TryValidate(source, out var reason)) count++;
                        else if (Community518SourceCatalog.CanPreserveWithoutImport(source, reason))
                        {
                            entries.Add(new LegacyInventoryEntry(
                                relative,
                                $"{source.SemanticType}_OVERSIZED_SOURCE",
                                new[]
                                {
                                    LegacyInventoryClassifications.KnownLegacyContent,
                                    LegacyInventoryClassifications.SafePreservedContent
                                },
                                "BOUNDED_SOURCE_PRESERVE_ONLY",
                                0));
                        }
                        else
                        {
                            AddUnknown(unknown, $"{relative}:UNKNOWN_LEGACY_SCHEMA:{reason}");
                            entries.Add(UnknownEntry(relative, $"UNKNOWN_LEGACY_SCHEMA:{reason}", "INVALID_FACT_SOURCE_BLOCKED"));
                        }
                        continue;
                    }

                    if (relativeDirectory.Equals("knowledge", StringComparison.OrdinalIgnoreCase) &&
                        KnownKnowledgeFiles.Contains(relative))
                    {
                        continue;
                    }

                    if (!directoryPolicy.OpaquePreserved)
                    {
                        PreserveUncatalogued(layout, item, relative, entries, unknown, fingerprintItems);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddUnknown(unknown, $"{relativeDirectory}:INVENTORY_UNREADABLE:{exception.GetType().Name.ToUpperInvariant()}");
            entries.Add(UnknownEntry(
                relativeDirectory,
                $"INVENTORY_UNREADABLE:{exception.GetType().Name.ToUpperInvariant()}",
                "UNREADABLE_SOURCE_BLOCKED"));
        }

        return count;
    }

    private static void InspectTopLevel(
        ProjectLayout layout,
        bool inspectLegacyContents,
        List<LegacyInventoryEntry> entries,
        List<string> unknown,
        List<string> fingerprintItems)
    {
        var topLevel = Directory.EnumerateFileSystemEntries(layout.AiRoot, "*", SearchOption.TopDirectoryOnly).Take(258).ToArray();
        if (topLevel.Length > 257) { AddUnknown(unknown, ".ai:TOP_LEVEL_LIMIT_EXCEEDED:257"); entries.Add(UnknownEntry(".ai", "TRAVERSAL_LIMIT_REACHED", "BOUNDED_SCAN_BLOCKED")); }
        foreach (var item in topLevel.Take(257))
        {
            var name = Path.GetFileName(item);
            if (KnownTopLevelNames.Contains(name))
            {
                continue;
            }

            var temporary = name.StartsWith(".staging", StringComparison.Ordinal) ||
                            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            var inspection = inspectLegacyContents
                ? LegacyUntrustedContentInspector.Inspect(layout, item)
                : LegacyUntrustedContentInspector.InspectShallow(layout, item);
            fingerprintItems.AddRange(inspection.FingerprintItems);
            if (inspection.SafeToPreserve)
            {
                entries.Add(PreservedEntry(name, temporary));
                continue;
            }

            foreach (var finding in inspection.Findings) AddUnknown(unknown, finding);
            entries.Add(UnknownEntry(
                name,
                string.Join('|', inspection.Findings),
                "UNTRUSTED_AUTHORITY_OR_PATH_BLOCKED"));
        }
    }

    private static void PreserveUncatalogued(
        ProjectLayout layout,
        string item,
        string relative,
        List<LegacyInventoryEntry> entries,
        List<string> unknown,
        List<string> fingerprintItems)
    {
        var inspection = LegacyUntrustedContentInspector.Inspect(layout, item);
        fingerprintItems.AddRange(inspection.FingerprintItems);
        if (inspection.SafeToPreserve)
        {
            entries.Add(new LegacyInventoryEntry(
                relative,
                "PRESERVED_UNTRUSTED_LEGACY_CONTENT",
                new[] { LegacyInventoryClassifications.SafePreservedContent },
                "UNTRUSTED_LAZY_SOURCE_PRESERVE_ONLY",
                0));
        }
        else
        {
            foreach (var finding in inspection.Findings) AddUnknown(unknown, finding);
            entries.Add(UnknownEntry(
                relative,
                string.Join('|', inspection.Findings),
                "UNTRUSTED_AUTHORITY_OR_PATH_BLOCKED"));
        }
    }

    private static LegacyInventoryEntry UnknownEntry(string path, string reason, string handling) => new(
        path,
        $"ISOLATED_UNTRUSTED_LEGACY_CONTENT:{reason}",
        new[] { LegacyInventoryClassifications.UnknownRequiresReview },
        handling,
        0);

    private static LegacyInventoryEntry PreservedEntry(string path, bool temporary) => new(
        path,
        temporary ? "REBUILDABLE_TEMPORARY_CONTENT" : "PRESERVED_UNTRUSTED_LEGACY_CONTENT",
        new[]
        {
            temporary
                ? LegacyInventoryClassifications.TemporaryRebuildable
                : LegacyInventoryClassifications.SafePreservedContent
        },
        temporary ? "REBUILDABLE_NO_IMPORT" : "UNTRUSTED_LAZY_SOURCE_PRESERVE_ONLY",
        0);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void AddUnknown(List<string> values, string value)
    {
        if (values.Count < 32) values.Add(value);
    }

    private static string InventoryFingerprint(IEnumerable<string> values)
    {
        var payload = string.Join('\n', values.OrderBy(static value => value, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static LegacyInventorySummary EmptySummary() => new(
        0,
        0,
        0,
        0,
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(),
        Array.Empty<LegacyInventoryEntry>());

    private sealed record DirectoryPolicy(string SemanticType, bool OpaquePreserved);
}

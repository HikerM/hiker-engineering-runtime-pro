using System.Text.Json;

namespace Hiker.Pro.State;

internal sealed record UntrustedContentInspection(
    bool SafeToPreserve,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> FingerprintItems);

internal static class LegacyUntrustedContentInspector
{
    private const int MaxEntries = 10_000;
    private const long MaxJsonInspectionBytes = 1_048_576;

    private static readonly IReadOnlySet<string> AuthorityFileNames = new HashSet<string>(
        new[]
        {
            "hiker-state.db",
            "project-identity.json",
            "migration-marker.json",
            "ownership.json",
            "project-state.json",
            "task-map.json",
            "task.json",
            "control.json",
            "checkpoint-ledger.json",
            "goal-contract.json"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AuthorityKinds = new HashSet<string>(
        new[]
        {
            EntityKinds.Project,
            EntityKinds.Migration,
            EntityKinds.Ownership,
            EntityKinds.Worktree,
            EntityKinds.RuntimeProfile,
            EntityKinds.Task,
            EntityKinds.Checkpoint,
            EntityKinds.Goal,
            EntityKinds.GoalRevision
        },
        StringComparer.OrdinalIgnoreCase);

    public static UntrustedContentInspection Inspect(ProjectLayout layout, string topLevelItem)
    {
        var findings = new List<string>();
        var fingerprintItems = new List<string>();
        var pending = new Stack<string>();
        pending.Push(topLevelItem);
        var discovered = 1;
        var limitExceeded = false;

        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!TryRelative(layout, current, out var relative))
                {
                    AddFinding(findings, $"{Path.GetFileName(current)}:PATH_ESCAPE");
                    continue;
                }

                if (IsReparsePoint(current))
                {
                    AddFinding(findings, $"{relative}:REPARSE_POINT");
                    continue;
                }

                if (Directory.Exists(current))
                {
                    fingerprintItems.Add($"D\0{relative}");
                    foreach (var child in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                    {
                        discovered++;
                        if (discovered > MaxEntries)
                        {
                            AddFinding(findings, $"{RelativeOrName(layout, topLevelItem)}:INVENTORY_LIMIT_EXCEEDED:{MaxEntries}");
                            limitExceeded = true;
                            break;
                        }

                        pending.Push(child);
                    }

                    if (limitExceeded) break;
                    continue;
                }

                var file = new FileInfo(current);
                fingerprintItems.Add($"F\0{relative}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}");
                if (TryFindAuthorityConflict(current, file.Length, out var reason))
                {
                    AddFinding(findings, $"{relative}:{reason}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddFinding(findings, $"{RelativeOrName(layout, topLevelItem)}:INVENTORY_UNREADABLE:{exception.GetType().Name.ToUpperInvariant()}");
        }

        return new UntrustedContentInspection(
            findings.Count == 0,
            ContractCollections.Freeze(findings),
            ContractCollections.Freeze(fingerprintItems));
    }

    public static UntrustedContentInspection InspectShallow(ProjectLayout layout, string topLevelItem)
    {
        var findings = new List<string>();
        var fingerprintItems = new List<string>();
        if (!TryRelative(layout, topLevelItem, out var relative))
        {
            AddFinding(findings, $"{Path.GetFileName(topLevelItem)}:PATH_ESCAPE");
        }
        else if (IsReparsePoint(topLevelItem))
        {
            AddFinding(findings, $"{relative}:REPARSE_POINT");
        }
        else if (Directory.Exists(topLevelItem))
        {
            fingerprintItems.Add($"D\0{relative}\0SHALLOW");
        }
        else
        {
            var file = new FileInfo(topLevelItem);
            fingerprintItems.Add($"F\0{relative}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}");
            if (TryFindAuthorityConflict(topLevelItem, file.Length, out var reason))
            {
                AddFinding(findings, $"{relative}:{reason}");
            }
        }

        return new UntrustedContentInspection(
            findings.Count == 0,
            ContractCollections.Freeze(findings),
            ContractCollections.Freeze(fingerprintItems));
    }

    private static bool TryFindAuthorityConflict(string path, long length, out string reason)
    {
        var fileName = Path.GetFileName(path);
        if (AuthorityFileNames.Contains(fileName))
        {
            reason = "POTENTIAL_STATE_AUTHORITY_PATH";
            return true;
        }

        if (!string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase))
        {
            reason = string.Empty;
            return false;
        }

        if (length > MaxJsonInspectionBytes)
        {
            reason = $"UNTRUSTED_JSON_INSPECTION_LIMIT:{MaxJsonInspectionBytes}";
            return true;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            if (LooksLikeAuthorityState(document.RootElement))
            {
                reason = "POTENTIAL_STATE_AUTHORITY_PAYLOAD";
                return true;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON cannot be imported by the state catalog and remains inert source material.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = $"UNTRUSTED_JSON_UNREADABLE:{exception.GetType().Name.ToUpperInvariant()}";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool LooksLikeAuthorityState(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (TryString(root, "schema_version", out var schema) &&
            schema.Contains("hiker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryString(root, "kind", out var kind) && AuthorityKinds.Contains(kind)) return true;
        if (root.TryGetProperty("identity_authority", out _) ||
            root.TryGetProperty("migration_marker", out _) ||
            root.TryGetProperty("writer_runtime_id", out _))
        {
            return true;
        }

        var hasProject = root.TryGetProperty("project_id", out _);
        var hasAuthorityEnvelope = root.TryGetProperty("authority", out _) ||
                                   root.TryGetProperty("entity_id", out _) ||
                                   root.TryGetProperty("generation", out _) ||
                                   root.TryGetProperty("root_fingerprint", out _);
        if (hasProject && hasAuthorityEnvelope) return true;

        var hasOperation = root.TryGetProperty("operation_id", out _);
        var hasCommitFingerprint = root.TryGetProperty("before_fingerprint", out _) ||
                                   root.TryGetProperty("intended_after_fingerprint", out _) ||
                                   root.TryGetProperty("committed_after_fingerprint", out _);
        return hasOperation && hasCommitFingerprint;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryRelative(ProjectLayout layout, string path, out string relative)
    {
        try
        {
            var root = Path.GetFullPath(layout.AiRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            {
                relative = string.Empty;
                return false;
            }

            relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            return relative != ".." &&
                   !relative.StartsWith("../", StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            relative = string.Empty;
            return false;
        }
    }

    private static string RelativeOrName(ProjectLayout layout, string path) =>
        TryRelative(layout, path, out var relative) ? relative : Path.GetFileName(path);

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

    private static void AddFinding(List<string> findings, string finding)
    {
        if (findings.Count < 32) findings.Add(finding);
    }
}

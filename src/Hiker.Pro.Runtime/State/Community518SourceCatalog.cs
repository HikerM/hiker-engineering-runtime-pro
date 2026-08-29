using System.Text.Json;

namespace Hiker.Pro.State;

internal sealed record Community518Source(
    string Path,
    string Kind,
    StateLifecycle Lifecycle,
    RetentionTier Retention,
    string SemanticType,
    string ValidationProfile);

internal static class Community518SourceCatalog
{
    private const int MaxFiles = 100_000;
    private const long MaxSourceBytes = 16L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, SourcePolicy> ExactFiles =
        new Dictionary<string, SourcePolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["schema.json"] = Existing(EntityKinds.Project, "COMMUNITY_STATE_PROTOCOL"),
            ["context/project.json"] = Existing(EntityKinds.Project, "PROJECT_CONTEXT"),
            ["context/tech-stack.json"] = Existing(EntityKinds.RuntimeProfile, "TECHNOLOGY_PROFILE"),
            ["context/architecture.json"] = Existing(EntityKinds.Decision, "ARCHITECTURE_DECISION"),
            ["context/standards.json"] = Existing(EntityKinds.Decision, "ENGINEERING_STANDARD"),
            ["runtime/task.json"] = Existing(EntityKinds.Task, "TASK_RUNTIME"),
            ["runtime/control.json"] = Existing(EntityKinds.RuntimeProfile, "CONTROL_RUNTIME"),
            ["runtime/checkpoint-ledger.json"] = Existing(EntityKinds.Checkpoint, "CHECKPOINT_RUNTIME"),
            ["governance/project-state.json"] = Existing(EntityKinds.Project, "PROJECT_GOVERNANCE"),
            ["governance/context-retention.json"] = Existing(EntityKinds.RuntimeProfile, "CONTEXT_RETENTION"),
            ["governance/task-index.json"] = Existing(EntityKinds.Task, "TASK_INDEX"),
            ["governance/locked-decisions.json"] = Existing(EntityKinds.Decision, "LOCKED_DECISION"),
            ["governance/ownership.json"] = Existing(EntityKinds.Ownership, "LEGACY_OWNERSHIP"),
            ["governance/goal-contract.json"] = Existing(EntityKinds.Goal, "GOAL_CONTRACT"),
            ["workspace/task-map.json"] = Existing(EntityKinds.Worktree, "LEGACY_WORKTREE"),
            ["quality/policy.json"] = Existing(EntityKinds.Evidence, "QUALITY_POLICY"),
            ["quality/evidence/index.json"] = Existing(EntityKinds.Evidence, "QUALITY_EVIDENCE"),

            ["architecture/module-registry.json"] = Archived(
                EntityKinds.Decision,
                RetentionTier.Warm,
                "LEGACY_ENGINEERING_ARCHITECTURE",
                "architecture-modules"),
            ["architecture/dependency-rules.json"] = Archived(
                EntityKinds.Decision,
                RetentionTier.Warm,
                "LEGACY_ENGINEERING_ARCHITECTURE",
                "architecture-rules"),
            ["architecture/public-surface.json"] = Archived(
                EntityKinds.Decision,
                RetentionTier.Warm,
                "LEGACY_ENGINEERING_ARCHITECTURE",
                "architecture-surfaces"),
            ["requirements/ledger.json"] = Active(
                EntityKinds.Requirement,
                RetentionTier.Hot,
                "LEGACY_REQUIREMENT_FACT",
                "requirement-ledger"),
            ["requirements/source-baseline.json"] = Active(
                EntityKinds.Requirement,
                RetentionTier.Warm,
                "LEGACY_REQUIREMENT_FACT",
                "requirement-baseline"),
            ["requirements/reconciliation.json"] = Archived(
                EntityKinds.Requirement,
                RetentionTier.Warm,
                "LEGACY_REQUIREMENT_FACT",
                "requirement-reconciliation"),
            ["evidence/index.json"] = Archived(
                EntityKinds.Evidence,
                RetentionTier.Warm,
                "LEGACY_EVIDENCE",
                "evidence-index"),
            ["evidence/risk/latest.json"] = Archived(
                EntityKinds.Evidence,
                RetentionTier.Warm,
                "LEGACY_EVIDENCE",
                "risk-evidence"),
            ["evidence/test-plan/latest.json"] = Archived(
                EntityKinds.Evidence,
                RetentionTier.Warm,
                "LEGACY_EVIDENCE",
                "test-plan-evidence"),
            ["knowledge/metadata.json"] = Archived(
                EntityKinds.RuntimeProfile,
                RetentionTier.Cold,
                "LEGACY_ENGINEERING_MEMORY",
                "knowledge-metadata")
        };

    public static IReadOnlyList<Community518Source> Discover(ProjectLayout layout)
    {
        var values = new List<Community518Source>();
        foreach (var item in ExactFiles)
        {
            var path = layout.ResolveAiRelativePath(item.Key);
            if (File.Exists(path)) values.Add(ToSource(path, item.Value));
        }

        AddPattern(layout, values, "tasks", "*.json", EntityKinds.Task, "TASK_FACT");
        AddPattern(layout, values, Path.Combine("runtime", "checkpoints"), "*.json", EntityKinds.Checkpoint, "CHECKPOINT_RUNTIME");
        if (values.Count > MaxFiles)
        {
            throw new InvalidOperationException($"Community import exceeds the {MaxFiles} file safety budget");
        }

        return values.OrderBy(static value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool HasAny(ProjectLayout layout)
    {
        if (ExactFiles.Keys.Any(relative => File.Exists(layout.ResolveAiRelativePath(relative)))) return true;
        return HasPattern(layout, "tasks", "*.json") ||
               HasPattern(layout, Path.Combine("runtime", "checkpoints"), "*.json");
    }

    public static bool TryValidate(Community518Source source, out string reason)
    {
        try
        {
            var info = new FileInfo(source.Path);
            if (info.Length > MaxSourceBytes)
            {
                reason = "SOURCE_BYTE_BUDGET_EXCEEDED";
                return false;
            }
            var payload = File.ReadAllText(source.Path);
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "ROOT_NOT_OBJECT";
                return false;
            }

            return TryValidate(document.RootElement, source.ValidationProfile, out reason);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            reason = exception.GetType().Name.ToUpperInvariant();
            return false;
        }
    }

    public static bool CanPreserveWithoutImport(Community518Source source, string reason) =>
        string.Equals(reason, "SOURCE_BYTE_BUDGET_EXCEEDED", StringComparison.Ordinal) &&
        source.Kind == EntityKinds.Evidence &&
        source.Lifecycle == StateLifecycle.Archived;

    public static bool RequiresPreserveOnly(Community518Source source)
    {
        try
        {
            return new FileInfo(source.Path).Length > MaxSourceBytes &&
                   CanPreserveWithoutImport(source, "SOURCE_BYTE_BUDGET_EXCEEDED");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryValidate(JsonElement root, string profile, out string reason)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            reason = "ROOT_NOT_OBJECT";
            return false;
        }

        return ValidateProfile(root, profile, out reason);
    }

    public static bool TryResolve(string relativePath, string absolutePath, out Community518Source source)
    {
        if (ExactFiles.TryGetValue(relativePath, out var policy))
        {
            source = ToSource(absolutePath, policy);
            return true;
        }

        source = default!;
        return false;
    }

    private static bool ValidateProfile(JsonElement root, string profile, out string reason)
    {
        var valid = profile switch
        {
            "legacy-object" => true,
            "architecture-modules" => HasSchema(root) && HasArray(root, "modules"),
            "architecture-rules" => HasSchema(root) && HasArray(root, "rules"),
            "architecture-surfaces" => HasSchema(root) && HasArray(root, "surfaces"),
            "requirement-ledger" => HasSchema(root) && HasArray(root, "requirements") && HasInteger(root, "revision"),
            "requirement-baseline" => HasSchema(root) && HasArray(root, "capabilities"),
            "requirement-reconciliation" => HasSchema(root) && HasArray(root, "matrix"),
            "evidence-index" => HasSchema(root) && HasArray(root, "records"),
            "risk-evidence" => HasSchema(root) && root.TryGetProperty("risk", out var risk) && risk.ValueKind == JsonValueKind.Object,
            "test-plan-evidence" => HasSchema(root) && HasArray(root, "must_run"),
            "knowledge-metadata" => HasSchema(root),
            _ => false
        };
        reason = valid ? "VALID" : $"SCHEMA_MISMATCH:{profile}";
        return valid;
    }

    private static bool HasSchema(JsonElement root) =>
        root.TryGetProperty("schema_version", out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number;

    private static bool HasArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array;

    private static bool HasInteger(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out _);

    private static void AddPattern(
        ProjectLayout layout,
        List<Community518Source> values,
        string relativeDirectory,
        string pattern,
        string kind,
        string semanticType)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        if (!Directory.Exists(directory)) return;
        var remaining = MaxFiles - values.Count;
        if (remaining <= 0) throw new InvalidOperationException($"Community import exceeds the {MaxFiles} file safety budget");
        var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(remaining + 1).ToArray();
        if (files.Length > remaining) throw new InvalidOperationException($"Community import exceeds the {MaxFiles} file safety budget");
        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(layout.AiRoot, path);
            var safe = layout.ResolveAiRelativePath(relative);
            values.Add(new Community518Source(safe, kind, StateLifecycle.Valid, RetentionTier.Hot, semanticType, "legacy-object"));
        }
    }

    private static bool HasPattern(ProjectLayout layout, string relativeDirectory, string pattern)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(1).Any();
    }

    private static Community518Source ToSource(string path, SourcePolicy policy) =>
        new(path, policy.Kind, policy.Lifecycle, policy.Retention, policy.SemanticType, policy.ValidationProfile);

    private static SourcePolicy Existing(string kind, string semanticType) => new(
        kind,
        kind is EntityKinds.Ownership or EntityKinds.Worktree ? StateLifecycle.Incompatible : StateLifecycle.Valid,
        kind is EntityKinds.Checkpoint or EntityKinds.Evidence ? RetentionTier.Warm : RetentionTier.Hot,
        semanticType,
        "legacy-object");

    private static SourcePolicy Active(string kind, RetentionTier retention, string semanticType, string profile) =>
        new(kind, StateLifecycle.Valid, retention, semanticType, profile);

    private static SourcePolicy Archived(string kind, RetentionTier retention, string semanticType, string profile) =>
        new(kind, StateLifecycle.Archived, retention, semanticType, profile);

    private sealed record SourcePolicy(
        string Kind,
        StateLifecycle Lifecycle,
        RetentionTier Retention,
        string SemanticType,
        string ValidationProfile);
}

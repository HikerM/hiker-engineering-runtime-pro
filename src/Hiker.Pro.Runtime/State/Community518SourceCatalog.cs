namespace Hiker.Pro.State;

internal static class Community518SourceCatalog
{
    private const int MaxFiles = 100_000;

    public static readonly IReadOnlyDictionary<string, string> ExactFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["schema.json"] = EntityKinds.Project,
        ["context/project.json"] = EntityKinds.Project,
        ["context/tech-stack.json"] = EntityKinds.RuntimeProfile,
        ["context/architecture.json"] = EntityKinds.Decision,
        ["context/standards.json"] = EntityKinds.Decision,
        ["runtime/task.json"] = EntityKinds.Task,
        ["runtime/control.json"] = EntityKinds.RuntimeProfile,
        ["runtime/checkpoint-ledger.json"] = EntityKinds.Checkpoint,
        ["governance/project-state.json"] = EntityKinds.Project,
        ["governance/context-retention.json"] = EntityKinds.RuntimeProfile,
        ["governance/task-index.json"] = EntityKinds.Task,
        ["governance/locked-decisions.json"] = EntityKinds.Decision,
        ["governance/ownership.json"] = EntityKinds.Ownership,
        ["governance/goal-contract.json"] = EntityKinds.Goal,
        ["workspace/task-map.json"] = EntityKinds.Worktree,
        ["quality/policy.json"] = EntityKinds.Evidence,
        ["quality/evidence/index.json"] = EntityKinds.Evidence
    };

    public static IReadOnlyList<(string Path, string Kind)> Discover(ProjectLayout layout)
    {
        var values = new List<(string Path, string Kind)>();
        foreach (var item in ExactFiles)
        {
            var path = layout.ResolveAiRelativePath(item.Key);
            if (File.Exists(path)) values.Add((path, item.Value));
        }

        AddPattern(layout, values, "tasks", "*.json", EntityKinds.Task);
        AddPattern(layout, values, Path.Combine("runtime", "checkpoints"), "*.json", EntityKinds.Checkpoint);
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

    private static void AddPattern(
        ProjectLayout layout,
        List<(string Path, string Kind)> values,
        string relativeDirectory,
        string pattern,
        string kind)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        if (!Directory.Exists(directory)) return;
        values.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(path => (path, kind)));
    }

    private static bool HasPattern(ProjectLayout layout, string relativeDirectory, string pattern)
    {
        var directory = layout.ResolveAiRelativePath(relativeDirectory);
        return Directory.Exists(directory) && Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(1).Any();
    }
}

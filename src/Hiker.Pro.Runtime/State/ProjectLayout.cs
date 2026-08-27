namespace Hiker.Pro.State;

public sealed class ProjectLayout
{
    public ProjectLayout(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        AiRoot = Path.Combine(ProjectRoot, ".ai");
        StateDirectory = Path.Combine(AiRoot, "state");
        DatabasePath = Path.Combine(StateDirectory, "hiker-state.db");
        ArtifactsRoot = Path.Combine(AiRoot, "artifacts", "sha256");
        ArchiveRoot = Path.Combine(AiRoot, "archive");
        EventArchiveRoot = Path.Combine(ArchiveRoot, "events");
        ArtifactBackupRoot = Path.Combine(ArchiveRoot, "artifact-backups");
        ExportsRoot = Path.Combine(AiRoot, "exports");
        ProjectionsRoot = Path.Combine(AiRoot, "projections");
        CacheRoot = Path.Combine(AiRoot, "cache");
        FastDoctorReceiptPath = Path.Combine(CacheRoot, "storage-fast-doctor-receipt.json");
    }

    public string ProjectRoot { get; }
    public string AiRoot { get; }
    public string StateDirectory { get; }
    public string DatabasePath { get; }
    public string ArtifactsRoot { get; }
    public string ArchiveRoot { get; }
    public string EventArchiveRoot { get; }
    public string ArtifactBackupRoot { get; }
    public string ExportsRoot { get; }
    public string ProjectionsRoot { get; }
    public string CacheRoot { get; }
    public string FastDoctorReceiptPath { get; }

    public string ResolveAiRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("State references must be relative to .ai");
        }

        var resolved = Path.GetFullPath(Path.Combine(AiRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(AiRoot, resolved);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("State reference escapes .ai");
        }

        return resolved;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(ArtifactsRoot);
        Directory.CreateDirectory(ArchiveRoot);
        Directory.CreateDirectory(EventArchiveRoot);
        Directory.CreateDirectory(ArtifactBackupRoot);
        Directory.CreateDirectory(ExportsRoot);
        Directory.CreateDirectory(ProjectionsRoot);
        Directory.CreateDirectory(CacheRoot);
    }
}

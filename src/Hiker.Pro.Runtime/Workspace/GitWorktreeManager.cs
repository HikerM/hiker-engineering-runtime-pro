using System.Diagnostics;

namespace Hiker.Pro.Workspace;

public sealed record WorktreeCreateRequest(
    string RepositoryRoot,
    string WorktreePath,
    string BranchName,
    string StartPoint);

public sealed record WorktreeCreateResult(string Status, WorkspaceSnapshot Snapshot);

public sealed class GitWorktreeManager(IWorkspaceInspector inspector)
{
    public async Task<WorktreeCreateResult> CreateAsync(WorktreeCreateRequest request, CancellationToken cancellationToken = default)
    {
        var repository = Path.GetFullPath(request.RepositoryRoot);
        var target = Path.GetFullPath(request.WorktreePath);
        if (!Directory.Exists(repository) || Directory.Exists(target) || File.Exists(target) ||
            string.IsNullOrWhiteSpace(request.BranchName) || string.IsNullOrWhiteSpace(request.StartPoint))
        {
            throw new ArgumentException("Worktree creation request is invalid", nameof(request));
        }

        var source = await inspector.InspectAsync(repository, cancellationToken).ConfigureAwait(false);
        var workspaceParent = Path.GetDirectoryName(repository) ?? throw new InvalidOperationException("Repository has no workspace parent");
        var relativeTarget = Path.GetRelativePath(workspaceParent, target);
        if (relativeTarget == ".." || relativeTarget.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Worktree target must stay within the repository workspace parent");
        }

        await GitAsync(repository, cancellationToken, "check-ref-format", "--branch", request.BranchName).ConfigureAwait(false);
        await GitAsync(repository, cancellationToken, "rev-parse", "--verify", $"{request.StartPoint}^{{commit}}").ConfigureAwait(false);
        await GitAsync(repository, cancellationToken, "worktree", "add", "-b", request.BranchName, target, request.StartPoint).ConfigureAwait(false);
        var snapshot = await inspector.InspectAsync(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(snapshot.GitCommonDirectory, source.GitCommonDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Created worktree does not share the expected Git common directory");
        }

        return new WorktreeCreateResult("CREATED", snapshot);
    }

    public async Task<string> RemoveIfCleanAsync(string repositoryRoot, string worktreePath, CancellationToken cancellationToken = default)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        var target = Path.GetFullPath(worktreePath);
        var snapshot = await inspector.InspectAsync(target, cancellationToken).ConfigureAwait(false);
        if (snapshot.Dirty)
        {
            return "BLOCKED_DIRTY_WORKTREE";
        }

        await GitAsync(repository, cancellationToken, "worktree", "remove", target).ConfigureAwait(false);
        return "REMOVED";
    }

    private static async Task<string> GitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start git");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {arguments[0]} failed with exit {process.ExitCode}: {(await stderr.ConfigureAwait(false)).Trim()}");
        return (await stdout.ConfigureAwait(false)).Trim();
    }
}

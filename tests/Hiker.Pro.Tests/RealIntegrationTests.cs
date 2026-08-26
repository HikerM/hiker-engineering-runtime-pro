using System.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Workspace;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class RealIntegrationTests
{
    [Fact]
    public async Task GitWorktreeManager_CreatesValidatesAndRefusesDirtyRemoval()
    {
        await using var project = await TestProject.CreateAsync();
        var repository = Path.Combine(project.Root, "repository");
        var worktree = Path.Combine(project.Root, "worktree-task-1");
        Directory.CreateDirectory(repository);
        await GitAsync(repository, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "fixture");
        await GitAsync(repository, "add", "README.md");
        await GitAsync(repository, "-c", "user.name=Hiker Test", "-c", "user.email=hiker-test@example.invalid", "commit", "-m", "fixture");

        var inspector = new GitWorkspaceInspector();
        var manager = new GitWorktreeManager(inspector);
        var created = await manager.CreateAsync(new WorktreeCreateRequest(repository, worktree, "task/task-1", "HEAD"));
        Assert.Equal("CREATED", created.Status);
        Assert.Equal("task/task-1", created.Snapshot.Branch);
        Assert.False(created.Snapshot.Dirty);
        var untracked = Path.Combine(worktree, "uncommitted.txt");
        await File.WriteAllTextAsync(untracked, "do not destroy");
        Assert.Equal("BLOCKED_DIRTY_WORKTREE", await manager.RemoveIfCleanAsync(repository, worktree));
        Assert.True(File.Exists(untracked));
        File.Delete(untracked);
        Assert.Equal("REMOVED", await manager.RemoveIfCleanAsync(repository, worktree));
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task CodexAdapter_ProbeIsRealButDoesNotInvokeModel()
    {
        await using var adapter = new CodexCliRuntimeAdapter();
        var probe = await adapter.ProbeAsync();
        Assert.True(probe.Available);
        Assert.Matches("^codex-cli [0-9]", probe.Version);
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
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
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}

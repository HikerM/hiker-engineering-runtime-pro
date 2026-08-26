using System.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Workspace;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class SchedulingMergeQueueTests
{
    [Fact]
    public async Task Scheduler_AdmitsDisjointWorkDefersConflictsAndRequiresIndependentAssurance()
    {
        await using var project = await TestProject.CreateAsync();
        var scheduler = new ConflictAwareScheduler(project.Coordinator);
        var disjoint = await scheduler.EvaluateAsync("project-1", new[]
        {
            Candidate("task-a", "lane-a", EngineeringRisk.Low, "src/api"),
            Candidate("task-b", "lane-b", EngineeringRisk.Medium, "src/worker"),
            Candidate("task-c", "lane-c", EngineeringRisk.Low, "docs")
        });
        Assert.Equal(3, disjoint.Dispositions.Count(static item => item.Status == "ADMITTED"));

        var conflict = await scheduler.EvaluateAsync("project-1", new[]
        {
            Candidate("task-a", "lane-a", EngineeringRisk.Low, "src/api"),
            Candidate("task-b", "lane-b", EngineeringRisk.Low, "src/api/contracts")
        });
        Assert.Single(conflict.Dispositions, static item => item.Status == "ADMITTED");
        Assert.Single(conflict.Dispositions, static item => item.ReasonCode == "AFFECTED_SCOPE_CONFLICT");

        var unassured = await scheduler.EvaluateAsync("project-1", new[]
        {
            Candidate("task-critical", "writer", EngineeringRisk.Critical, "src/security")
        });
        Assert.Equal("INDEPENDENT_ASSURANCE_REQUIRED", Assert.Single(unassured.Dispositions).ReasonCode);

        var assured = await scheduler.EvaluateAsync("project-1", new[]
        {
            Candidate("task-critical", "writer", EngineeringRisk.Critical, "src/security"),
            new SchedulingCandidate("review-critical", "reviewer", AgentLaneRole.Assurance, EngineeringRisk.Critical, new[] { "src/security" }, "task-critical")
        });
        Assert.Equal(1, assured.WriterCapacity);
        Assert.Equal(2, assured.Dispositions.Count(static item => item.Status == "ADMITTED"));
    }

    [Fact]
    public async Task MergeQueue_RequiresAssuranceSerializesAuthorizationAndObservesRealGitMerge()
    {
        await using var project = await TestProject.CreateAsync();
        var parent = Path.Combine(Path.GetTempPath(), $"hiker-merge-queue-{Guid.NewGuid():N}");
        var repository = Path.Combine(parent, "repository");
        var worktree = Path.Combine(parent, "task-worktree");
        Directory.CreateDirectory(repository);
        try
        {
            await GitAsync(repository, "init", "-b", "main");
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n");
            await GitAsync(repository, "add", "README.md");
            await GitAsync(repository, "-c", "user.name=Hiker Test", "-c", "user.email=test@example.invalid", "commit", "-m", "base");
            var baseCommit = await GitAsync(repository, "rev-parse", "HEAD");
            var inspector = new GitWorkspaceInspector();
            var worktrees = new GitWorktreeManager(inspector);
            _ = await worktrees.CreateAsync(new WorktreeCreateRequest(repository, worktree, "task/merge-queue", "HEAD"));
            await File.WriteAllTextAsync(Path.Combine(worktree, "change.txt"), "candidate\n");
            await GitAsync(worktree, "add", "change.txt");
            await GitAsync(worktree, "-c", "user.name=Hiker Test", "-c", "user.email=test@example.invalid", "commit", "-m", "candidate");
            var candidateCommit = await GitAsync(worktree, "rev-parse", "HEAD");
            var queue = new MergeQueueCoordinator(project.Coordinator, inspector, new GitAncestryVerifier());

            var invalid = Request(repository, worktree, baseCommit, candidateCommit) with
            {
                AssuranceRuntimeId = "writer-1"
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(invalid));

            var request = Request(repository, worktree, baseCommit, candidateCommit);
            var enqueued = await queue.EnqueueAsync(request);
            Assert.Equal(MergeQueueStatus.Queued, enqueued.Entry?.Status);
            var ready = await queue.MarkReadyAsync("merge-ready", "project-1", "candidate-1", "source");
            Assert.Equal(MergeQueueStatus.Ready, ready.Entry?.Status);
            var authorized = await queue.AuthorizeNextAsync("merge-authorize", "project-1", repository, "main", "source");
            Assert.Equal(MergeQueueStatus.Authorized, authorized.Entry?.Status);
            var duplicateAuthorization = await queue.AuthorizeNextAsync("merge-authorize-other", "project-1", repository, "main", "source");
            Assert.Equal("MERGE_ALREADY_AUTHORIZED", duplicateAuthorization.ReasonCode);

            await GitAsync(repository, "merge", "--ff-only", candidateCommit);
            var completed = await queue.CompleteAsync("merge-complete", "project-1", "candidate-1", repository, "source");
            Assert.Equal(MergeQueueStatus.Merged, completed.Entry?.Status);
            var replay = await queue.CompleteAsync("merge-complete", "project-1", "candidate-1", repository, "source");
            Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
            Assert.Equal("REMOVED", await worktrees.RemoveIfCleanAsync(repository, worktree));
        }
        finally
        {
            DeleteTree(worktree);
            DeleteTree(repository);
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false);
        }
    }

    private static SchedulingCandidate Candidate(string taskId, string laneId, EngineeringRisk risk, string scope) =>
        new(taskId, laneId, AgentLaneRole.Writer, risk, new[] { scope });

    private static MergeCandidateRequest Request(string repository, string worktree, string baseCommit, string candidateCommit) => new(
        "merge-enqueue",
        "project-1",
        "candidate-1",
        "task-1",
        worktree,
        "task/merge-queue",
        "main",
        baseCommit,
        candidateCommit,
        "writer-1",
        "reviewer-1",
        1,
        EngineeringRisk.High,
        new[] { "src/security" },
        new[] { "evidence:test" },
        new[] { "evidence:assurance" },
        "source");

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
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
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments[0]} failed: {(await error).Trim()}");
        }

        return (await output).Trim();
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}

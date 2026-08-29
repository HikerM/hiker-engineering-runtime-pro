using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hiker.Pro.Safety;

namespace Hiker.Pro.Workspace;

public sealed record WorkspaceSnapshot(
    string Root,
    string GitCommonDirectory,
    string Head,
    string Branch,
    bool Dirty,
    string StatusFingerprint,
    DateTimeOffset ObservedAt);

public interface IWorkspaceInspector
{
    Task<WorkspaceSnapshot> InspectAsync(string workspacePath, CancellationToken cancellationToken = default);
}

public sealed class GitWorkspaceInspector : IWorkspaceInspector
{
    public async Task<WorkspaceSnapshot> InspectAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var requested = Path.GetFullPath(workspacePath);
        var root = await GitAsync(requested, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        var common = await GitAsync(requested, cancellationToken, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        var head = await GitAsync(requested, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        var branch = await GitAsync(requested, cancellationToken, "branch", "--show-current").ConfigureAwait(false);
        var status = await GitAsync(requested, cancellationToken, "status", "--porcelain=v2", "--untracked-files=all", "--", ".", ":(exclude).ai/**").ConfigureAwait(false);
        var canonicalRoot = Path.GetFullPath(root);
        var commonPath = Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(canonicalRoot, common));
        return new WorkspaceSnapshot(
            canonicalRoot,
            commonPath,
            head,
            branch,
            status.Length > 0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(status))).ToLowerInvariant(),
            DateTimeOffset.UtcNow);
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
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var result = await BoundedProcess.RunAsync(start, maxOutputCharacters: 2 * 1024 * 1024, maxErrorCharacters: 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (result.OutputTruncated)
            throw new TraversalLimitReachedException("git_output_bytes", new TraversalReceipt("TRAVERSAL_LIMIT_REACHED", "git_output_bytes", 0, 0, 0, result.ObservedOutputCharacters, 0, 0));
        var output = result.StandardOutput.Trim();
        var error = result.StandardError.Trim();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments[0]} failed with exit {result.ExitCode}: {error}");
        }

        return output;
    }
}

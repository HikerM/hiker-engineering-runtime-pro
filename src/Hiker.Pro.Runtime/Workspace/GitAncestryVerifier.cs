using System.Diagnostics;
using Hiker.Pro.Safety;

namespace Hiker.Pro.Workspace;

public interface IGitAncestryVerifier
{
    Task<bool> IsAncestorAsync(string repositoryPath, string ancestorCommit, string descendantRef, CancellationToken cancellationToken = default);
}

public sealed class GitAncestryVerifier : IGitAncestryVerifier
{
    public async Task<bool> IsAncestorAsync(
        string repositoryPath,
        string ancestorCommit,
        string descendantRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ancestorCommit) || string.IsNullOrWhiteSpace(descendantRef))
        {
            throw new ArgumentException("Git ancestry requires two non-empty revisions");
        }

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetFullPath(repositoryPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("merge-base");
        start.ArgumentList.Add("--is-ancestor");
        start.ArgumentList.Add(ancestorCommit);
        start.ArgumentList.Add(descendantRef);
        var result = await BoundedProcess.RunAsync(start, 16 * 1024, 64 * 1024, cancellationToken).ConfigureAwait(false);
        var error = result.StandardError.Trim();
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException($"git merge-base failed with exit {result.ExitCode}: {error}")
        };
    }
}

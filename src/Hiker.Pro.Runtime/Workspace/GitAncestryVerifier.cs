using System.Diagnostics;

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
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start git ancestry verification");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        var error = (await stderr.ConfigureAwait(false)).Trim();
        return process.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException($"git merge-base failed with exit {process.ExitCode}: {error}")
        };
    }
}

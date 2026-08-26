using System.Security.Cryptography;

namespace Hiker.Pro.Trust;

public sealed record PackageIntegrityResult(string Status, int Verified, IReadOnlyList<string> Failures);

public sealed class PackageIntegrityVerifier(SignedEnvelopeVerifier verifier)
{
    public async Task<PackageIntegrityResult> VerifyUpdateAsync(
        SignedEnvelope envelope,
        string packageRoot,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? allowedUnlistedRelativePaths = null)
    {
        var signature = verifier.Verify<SignedUpdateManifest>(envelope, TrustPurposes.Update);
        if (!signature.Valid || signature.Payload is null)
        {
            return new PackageIntegrityResult(signature.Status, 0, new[] { "MANIFEST_SIGNATURE" });
        }

        var root = Path.GetFullPath(packageRoot);
        var failures = new List<string>();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expectedPaths = new HashSet<string>(pathComparer);
        var verified = 0;
        foreach (var artifact in signature.Payload.Artifacts)
        {
            var path = Resolve(root, artifact.RelativePath);
            if (!expectedPaths.Add(path))
            {
                failures.Add($"{artifact.RelativePath}:DUPLICATE_PATH");
                continue;
            }
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.Bytes)
            {
                failures.Add($"{artifact.RelativePath}:MISSING_OR_SIZE");
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!FixedEquals(hash, artifact.Sha256))
            {
                failures.Add($"{artifact.RelativePath}:HASH_MISMATCH");
                continue;
            }

            verified++;
        }

        var allowed = new HashSet<string>(pathComparer);
        if (allowedUnlistedRelativePaths is not null)
        {
            foreach (var relative in allowedUnlistedRelativePaths)
            {
                allowed.Add(Resolve(root, relative));
            }
        }
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(path);
            if (!expectedPaths.Contains(full) && !allowed.Contains(full))
            {
                failures.Add($"{Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/')}:UNLISTED_FILE");
            }
        }

        return new PackageIntegrityResult(failures.Count == 0 ? "VERIFIED" : "BLOCKED", verified, failures);
    }

    public async Task<PackageIntegrityResult> VerifyModuleAsync(
        SignedEnvelope envelope,
        string assemblyPath,
        IReadOnlySet<string> enabledFeatures,
        string productVersion,
        CancellationToken cancellationToken = default)
    {
        var signature = verifier.Verify<SignedModuleManifest>(envelope, TrustPurposes.Module);
        if (!signature.Valid || signature.Payload is null)
            return new PackageIntegrityResult(signature.Status, 0, new[] { "MODULE_SIGNATURE" });
        var module = signature.Payload;
        if (SemanticVersion.Compare(productVersion, module.MinimumProductVersion) < 0 || module.RequiredFeatures.Any(feature => !enabledFeatures.Contains(feature)))
            return new PackageIntegrityResult("NOT_ENTITLED", 0, new[] { "MODULE_POLICY" });
        await using var stream = new FileStream(Path.GetFullPath(assemblyPath), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        return FixedEquals(hash, module.AssemblySha256)
            ? new PackageIntegrityResult("VERIFIED", 1, Array.Empty<string>())
            : new PackageIntegrityResult("BLOCKED", 0, new[] { "MODULE_HASH_MISMATCH" });
    }

    private static string Resolve(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Package paths must be relative");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("Package artifact escapes package root");
        return path;
    }

    private static bool FixedEquals(string left, string right) => left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));
}

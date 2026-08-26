using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Trust;

namespace Hiker.Pro.ReleaseTool;

internal sealed class ReleasePackageBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ReleaseBuildResult> BuildAsync(
        ReleaseBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RepositoryRoot);
        var outputRoot = Path.GetFullPath(options.OutputRoot);
        var privateKeyPem = await ResolvePrivateKeyAsync(root, outputRoot, options, cancellationToken).ConfigureAwait(false);
        var versions = await RepositoryVersionReader.ReadAndValidateAsync(root, cancellationToken).ConfigureAwait(false);
        var status = await ProcessRunner.RequireAsync("git", root, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new InvalidOperationException("Release packages require a clean Git worktree");
        }

        var branch = (await ProcessRunner.RequireAsync("git", root, new[] { "branch", "--show-current" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(branch, "main", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Release packages must be built from main");
        }

        var commit = (await ProcessRunner.RequireAsync("git", root, new[] { "rev-parse", "HEAD" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var publishedAtText = (await ProcessRunner.RequireAsync("git", root, new[] { "show", "-s", "--format=%cI", "HEAD" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var publishedAt = DateTimeOffset.Parse(publishedAtText, System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outputRoot);
        var safeVersion = SafeToken(versions.Product);
        var safeRid = SafeToken(options.RuntimeIdentifier);
        var packageBase = $"hiker-pro-{safeVersion}-{safeRid}";
        var packagePath = Path.Combine(outputRoot, $"{packageBase}.zip");
        var checksumPath = Path.Combine(outputRoot, $"{packageBase}.sha256");
        var publicKeyPath = Path.Combine(outputRoot, $"{packageBase}.public.pem");
        var factsPath = Path.Combine(outputRoot, $"{packageBase}.release.json");
        foreach (var target in new[] { packagePath, checksumPath, publicKeyPath, factsPath })
        {
            if (File.Exists(target)) throw new IOException($"Release output already exists: {Path.GetFileName(target)}");
        }

        var stage = Path.Combine(outputRoot, $".staging-{Guid.NewGuid():N}");
        var payloadRoot = Path.Combine(stage, "payload");
        Directory.CreateDirectory(payloadRoot);
        try
        {
            await PublishAsync(root, payloadRoot, versions.Product, options, cancellationToken).ConfigureAwait(false);
            RemoveBuildOnlyFiles(payloadRoot);
            File.Copy(Path.Combine(root, "VERSION"), Path.Combine(payloadRoot, "VERSION"), overwrite: false);
            File.Copy(Path.Combine(root, "release-versions.json"), Path.Combine(payloadRoot, "release-versions.json"), overwrite: false);
            await File.WriteAllTextAsync(
                Path.Combine(payloadRoot, "PACKAGE-NOTICE.txt"),
                "Hiker 5.19 Pro commercial runtime. Verify manifest.json with the independently trusted release public key before installation.\n",
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var artifactFiles = Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            var artifacts = new List<UpdateArtifact>(artifactFiles.Length);
            foreach (var path in artifactFiles)
            {
                var relative = $"payload/{Path.GetRelativePath(payloadRoot, path).Replace(Path.DirectorySeparatorChar, '/')}";
                artifacts.Add(new UpdateArtifact(
                    relative,
                    await FileSha256Async(path, cancellationToken).ConfigureAwait(false),
                    new FileInfo(path).Length,
                    Platform(options.RuntimeIdentifier),
                    Architecture(options.RuntimeIdentifier)));
            }

            var manifest = new SignedUpdateManifest(
                "Hiker Pro",
                versions.Product,
                options.Channel,
                versions.Runtime,
                publishedAt,
                artifacts,
                null,
                commit);
            var manifestPath = Path.Combine(stage, "manifest.json");
            string publicPem;
            string publicFingerprint;
            using (var key = ECDsa.Create())
            {
                key.ImportFromPem(privateKeyPem);
                if (key.KeySize != 256) throw new CryptographicException("Release signing key must use ECDSA P-256");
                var payload = CanonicalJson.Serialize(manifest);
                var envelope = new SignedEnvelope(
                    "hiker-signed-envelope/v1",
                    SignedEnvelopeVerifier.Algorithm,
                    options.KeyId,
                    TrustPurposes.Update,
                    Encode(payload),
                    Encode(key.SignData(payload, HashAlgorithmName.SHA256)));
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(envelope, Json), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var publicBytes = key.ExportSubjectPublicKeyInfo();
                publicPem = key.ExportSubjectPublicKeyInfoPem();
                publicFingerprint = Convert.ToHexString(SHA256.HashData(publicBytes)).ToLowerInvariant();
            }

            await File.WriteAllTextAsync(publicKeyPath, publicPem, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            CreateArchive(stage, packagePath, publishedAt);
            var packageSha = await FileSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                checksumPath,
                $"{packageSha}  {Path.GetFileName(packagePath)}\n",
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            var result = new ReleaseBuildResult(
                "BUILT",
                versions.Product,
                versions.Runtime,
                versions.StateSchema,
                commit,
                Path.GetFileName(packagePath),
                packageSha,
                "manifest.json",
                await FileSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
                Path.GetFileName(checksumPath),
                Path.GetFileName(publicKeyPath),
                publicFingerprint,
                artifacts.Count,
                new FileInfo(packagePath).Length);
            await File.WriteAllTextAsync(factsPath, JsonSerializer.Serialize(result, Json), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task PublishAsync(
        string repositoryRoot,
        string payloadRoot,
        string version,
        ReleaseBuildOptions options,
        CancellationToken cancellationToken)
    {
        var project = Path.Combine(repositoryRoot, "src", "Hiker.Pro.Cli", "Hiker.Pro.Cli.csproj");
        await ProcessRunner.RequireAsync(
            "dotnet",
            repositoryRoot,
            new[]
            {
                "publish", project,
                "--configuration", options.Configuration,
                "--runtime", options.RuntimeIdentifier,
                "--self-contained", "false",
                "--output", payloadRoot,
                $"-p:Version={version}",
                "-p:ContinuousIntegrationBuild=true",
                "-p:DebugSymbols=false",
                "-p:DebugType=None"
            },
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
    }

    private static void RemoveBuildOnlyFiles(string payloadRoot)
    {
        foreach (var path in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
                     .Where(static path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(path);
        }
    }

    private static void CreateArchive(string stage, string packagePath, DateTimeOffset publishedAt)
    {
        var timestamp = publishedAt < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : publishedAt;
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var path in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(stage, path).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.LastWriteTime = timestamp;
            using var source = File.OpenRead(path);
            using var target = entry.Open();
            source.CopyTo(target);
        }
    }

    private static async Task<string> ResolvePrivateKeyAsync(
        string repositoryRoot,
        string outputRoot,
        ReleaseBuildOptions options,
        CancellationToken cancellationToken)
    {
        if ((options.PrivateKeyPath is null) == (options.PrivateKeyPem is null))
            throw new InvalidOperationException("Provide exactly one external private-key source: path or standard input");
        if (options.PrivateKeyPem is { Length: > 0 } standardInput)
            return standardInput;
        var keyPath = Path.GetFullPath(options.PrivateKeyPath!);
        if (!File.Exists(keyPath)) throw new FileNotFoundException("Release signing private key was not found", keyPath);
        if (IsWithin(repositoryRoot, keyPath) || IsWithin(outputRoot, keyPath))
        {
            throw new InvalidOperationException("Release signing private key must remain outside the repository and release output");
        }
        return await File.ReadAllTextAsync(keyPath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string Platform(string runtimeIdentifier) => runtimeIdentifier.Split('-', 2)[0];
    private static string Architecture(string runtimeIdentifier) => runtimeIdentifier.Split('-', 2).ElementAtOrDefault(1) ?? "unknown";

    private static string SafeToken(string value)
    {
        if (value.Length is 0 or > 80 || value.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            throw new InvalidDataException("Release version or runtime identifier contains unsafe characters");
        return value;
    }

    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }
}

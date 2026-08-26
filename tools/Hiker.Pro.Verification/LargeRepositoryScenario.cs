using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.Verification;

internal sealed class LargeRepositoryScenario(VerificationOptions options)
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hiker-pro-large-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await ProcessTools.RequireAsync("git", root, new[] { "init", "-b", "main" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var checkpoints = new SortedSet<int>(new[] { 10_000, 50_000, 100_000 }.Where(value => value <= options.FileScale));
            checkpoints.Add(options.FileScale);
            var creation = new Dictionary<int, double>();
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < options.FileScale; index++)
            {
                var directory = Path.Combine(root, "services", $"service-{index % 50:D2}", $"bucket-{index / 1000:D4}");
                Directory.CreateDirectory(directory);
                var content = $"{{\"component\":\"component-{index:D6}\",\"service\":\"service-{index % 50:D2}\",\"contract\":\"api-{index % 25:D2}\",\"sequence\":{index}}}";
                await File.WriteAllTextAsync(Path.Combine(directory, $"component-{index:D6}.json"), content).ConfigureAwait(false);
                if (checkpoints.Contains(index + 1))
                {
                    creation[index + 1] = stopwatch.Elapsed.TotalMilliseconds;
                }
            }

            stopwatch.Restart();
            var fingerprint = await FingerprintAsync(root).ConfigureAwait(false);
            stopwatch.Stop();
            var scanMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            await ProcessTools.RequireAsync("git", root, new[] { "add", "--all" }, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            await ProcessTools.RequireAsync("git", root, new[]
            {
                "-c", "user.name=Hiker Verification", "-c", "user.email=verification@example.invalid",
                "commit", "-m", "large repository fixture"
            }, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            stopwatch.Stop();
            var gitIndexMs = stopwatch.Elapsed.TotalMilliseconds;
            for (var index = 0; index < 100; index++)
            {
                var path = Path.Combine(root, "services", $"service-{index % 50:D2}", $"bucket-{index / 1000:D4}", $"component-{index:D6}.json");
                await File.AppendAllTextAsync(path, Environment.NewLine + "{\"changed\":true}").ConfigureAwait(false);
            }

            stopwatch.Restart();
            var status = await ProcessTools.RequireAsync("git", root, new[] { "status", "--short" }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            stopwatch.Stop();
            var deltaMs = stopwatch.Elapsed.TotalMilliseconds;
            var changed = status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var count = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).LongCount();
            var requiredScalePoints = new[] { 10_000, 50_000, 100_000 };
            var passed = count == options.FileScale &&
                         options.FileScale >= 100_000 &&
                         requiredScalePoints.All(creation.ContainsKey) &&
                         changed == 100 &&
                         fingerprint.Length == 64;
            return new[]
            {
                new VerificationCheck(
                    "LARGE-REPOSITORY",
                    passed ? "PASS" : "BLOCKED",
                    "SIMULATION_PROVEN",
                    "Synthetic structural and semantic file fixture exercised real NTFS and Git; it is not a customer repository",
                    new Dictionary<string, object?>
                    {
                        ["files"] = count,
                        ["services"] = 50,
                        ["contracts"] = 25,
                        ["creation_checkpoints_ms"] = creation,
                        ["required_scale_points"] = requiredScalePoints,
                        ["full_fingerprint_ms"] = scanMs,
                        ["git_add_commit_ms"] = gitIndexMs,
                        ["affected_files"] = changed,
                        ["git_delta_status_ms"] = deltaMs,
                        ["fingerprint"] = fingerprint,
                        ["field_proven"] = "NOT_YET_PROVEN"
                    })
            };
        }
        finally
        {
            NormalizeAndDelete(root);
        }
    }

    private static async Task<string> FingerprintAsync(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var contentHash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            hash.AppendData(contentHash);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void NormalizeAndDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}

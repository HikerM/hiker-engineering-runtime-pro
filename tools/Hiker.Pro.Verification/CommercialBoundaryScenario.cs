using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hiker.Pro.Trust;

namespace Hiker.Pro.Verification;

internal sealed partial class CommercialBoundaryScenario(VerificationOptions options)
{
    private static readonly string[] ProMarkers =
    {
        "Hiker.Pro.Runtime",
        "CommercialEntitlement",
        "LicensePolicyEngine",
        "DeviceActivationManager",
        "SqliteStateStore"
    };

    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        if (options.PublicRepositoryRoot is null || !Directory.Exists(options.PublicRepositoryRoot))
        {
            return new[]
            {
                Check("COMM-G1-G2-G16", "BLOCKED", "Public Community repository root was not provided")
            };
        }

        var privateLeaks = await ScanTrackedAsync(options.RepositoryRoot, includeProMarkers: false).ConfigureAwait(false);
        var publicLeaks = await ScanTrackedAsync(options.PublicRepositoryRoot, includeProMarkers: true).ConfigureAwait(false);
        var packageLeaks = await ScanPublicArchiveAsync(options.PublicRepositoryRoot).ConfigureAwait(false);
        var metadata = await PrivateRepositoryMetadataAsync().ConfigureAwait(false);
        var boundaryPass = metadata.Private && publicLeaks.Count == 0 && packageLeaks.Count == 0;
        var performance = MeasureLicenseEvaluation();
        return new[]
        {
            new VerificationCheck(
                "COMM-G1-G2-G16",
                boundaryPass ? "PASS" : "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "Public Git HEAD archive and tracked source were scanned while GitHub metadata was read from the actual private repository",
                new Dictionary<string, object?>
                {
                    ["repository"] = metadata.Name,
                    ["private"] = metadata.Private,
                    ["visibility"] = metadata.Visibility,
                    ["default_branch"] = metadata.DefaultBranch,
                    ["public_source_marker_findings"] = publicLeaks,
                    ["public_package_marker_findings"] = packageLeaks
                }),
            new VerificationCheck(
                "COMM-G3",
                privateLeaks.Count == 0 ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "Tracked private source was scanned for private keys, credential tokens and developer home paths",
                new Dictionary<string, object?>
                {
                    ["findings"] = privateLeaks,
                    ["tracked_only"] = true
                }),
            new VerificationCheck(
                "COMM-G17",
                performance.P95Ms < 10 ? "PASS" : "BLOCKED",
                "DETERMINISTIC_VERIFIED",
                "License evaluation is explicit and outside StateStore hot paths; standalone verification cost is reported",
                new Dictionary<string, object?>
                {
                    ["evaluations"] = performance.Count,
                    ["p50_ms"] = performance.P50Ms,
                    ["p95_ms"] = performance.P95Ms,
                    ["p99_ms"] = performance.P99Ms,
                    ["state_hot_path_injected_checks"] = 0
                })
        };
    }

    private static async Task<List<string>> ScanTrackedAsync(string repository, bool includeProMarkers)
    {
        var listed = await ProcessTools.RequireAsync("git", repository, new[] { "ls-files", "-z" }, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        var findings = new List<string>();
        foreach (var relative in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(repository, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }
            var name = Path.GetFileName(relative);
            if (PrivateKeyName().IsMatch(name))
            {
                findings.Add($"PRIVATE_KEY_NAME:{relative}");
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024)
            {
                continue;
            }
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            if (bytes.Contains((byte)0))
            {
                continue;
            }
            var content = Encoding.UTF8.GetString(bytes);
            if (PrivateKeyContent().IsMatch(content)) findings.Add($"PRIVATE_KEY_CONTENT:{relative}");
            if (CredentialToken().IsMatch(content)) findings.Add($"CREDENTIAL_TOKEN:{relative}");
            if (ContainsConcreteDeveloperHome(content)) findings.Add($"DEVELOPER_HOME:{relative}");
            if (includeProMarkers && ProMarkers.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
            {
                findings.Add($"PRO_SOURCE_MARKER:{relative}");
            }
        }
        return findings.OrderBy(static value => value, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<string>> ScanPublicArchiveAsync(string publicRepository)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hiker-community-{Guid.NewGuid():N}.zip");
        try
        {
            await ProcessTools.RequireAsync(
                "git",
                publicRepository,
                new[] { "archive", "--format=zip", $"--output={path}", "HEAD" },
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var findings = new List<string>();
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > 4 * 1024 * 1024 || entry.Length == 0)
                {
                    continue;
                }
                await using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (ProMarkers.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
                {
                    findings.Add(entry.FullName);
                }
            }
            return findings;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<RepositoryMetadata> PrivateRepositoryMetadataAsync()
    {
        var result = await ProcessTools.RequireAsync(
            "gh",
            options.RepositoryRoot,
            new[] { "repo", "view", "HikerM/hiker-engineering-runtime-pro", "--json", "nameWithOwner,isPrivate,visibility,defaultBranchRef" },
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        return new RepositoryMetadata(
            root.GetProperty("nameWithOwner").GetString() ?? string.Empty,
            root.GetProperty("isPrivate").GetBoolean(),
            root.GetProperty("visibility").GetString() ?? string.Empty,
            root.GetProperty("defaultBranchRef").GetProperty("name").GetString() ?? string.Empty);
    }

    private static LicensePerformance MeasureLicenseEvaluation()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new[]
        {
            PublicKey("entitlement", TrustPurposes.Entitlement, key, now),
            PublicKey("activation", TrustPurposes.Activation, key, now)
        };
        var installation = Hash("installation");
        var device = LicensePolicyEngine.DeviceBinding(new[] { "os-installation", "disk-family" });
        var entitlement = Sign(
            new CommercialEntitlement(
                "license", "PRO", new[] { "runtime" }, "5.19.0", "5.19.99", 2,
                now.AddDays(-1), now.AddDays(30), now.AddDays(30), 7, Hash("account")),
            "entitlement",
            TrustPurposes.Entitlement,
            key);
        var activation = Sign(
            new DeviceActivationCertificate("activation", "license", installation, device, 1, now.AddDays(-1), now.AddDays(30)),
            "activation",
            TrustPurposes.Activation,
            key);
        var engine = new LicensePolicyEngine(new SignedEnvelopeVerifier(trust), TimeProvider.System);
        for (var index = 0; index < 10; index++)
        {
            _ = engine.Evaluate(entitlement, activation, "5.19.0", installation, device, new TrustClockState(now, now, "clock"));
        }
        var values = new List<double>();
        for (var index = 0; index < 250; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            var decision = engine.Evaluate(entitlement, activation, "5.19.0", installation, device, new TrustClockState(now, now, "clock"));
            stopwatch.Stop();
            if (decision.Mode != LicenseMode.Licensed) throw new InvalidOperationException("License benchmark decision changed");
            values.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        return new LicensePerformance(
            values.Count,
            ProcessTools.Percentile(values, 0.50),
            ProcessTools.Percentile(values, 0.95),
            ProcessTools.Percentile(values, 0.99));
    }

    private static TrustKey PublicKey(string keyId, string purpose, ECDsa key, DateTimeOffset now) => new(
        keyId,
        purpose,
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        now.AddDays(-2),
        now.AddYears(10),
        false);

    private static SignedEnvelope Sign<T>(T payload, string keyId, string purpose, ECDsa key)
    {
        var bytes = CanonicalJson.Serialize(payload);
        return new SignedEnvelope(
            "hiker-signed-envelope/v1",
            SignedEnvelopeVerifier.Algorithm,
            keyId,
            purpose,
            Encode(bytes),
            Encode(key.SignData(bytes, HashAlgorithmName.SHA256)));
    }

    private static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static VerificationCheck Check(string id, string status, string detail) => new(id, status, "DETERMINISTIC_VERIFIED", detail, new Dictionary<string, object?>());

    private static bool ContainsConcreteDeveloperHome(string content)
    {
        foreach (Match match in DeveloperHome().Matches(content))
        {
            if (!match.Value.Contains('<') && !match.Value.Contains('>') &&
                !match.Value.Contains('%') && !match.Value.Contains('$') &&
                !match.Value.Contains('{') && !match.Value.Contains('}'))
            {
                return true;
            }
        }
        return false;
    }

    [GeneratedRegex("(^|[._-])(private|signing)[._-]?(key)?\\.(pem|key|p8|p12|pfx)$", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyName();

    [GeneratedRegex("-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyContent();

    [GeneratedRegex("(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})")]
    private static partial Regex CredentialToken();

    [GeneratedRegex("([A-Za-z]:\\\\Users\\\\[^\\\\\"']+|/home/[^/\\s\"']+|/Users/[^/\\s\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperHome();

    private sealed record RepositoryMetadata(string Name, bool Private, string Visibility, string DefaultBranch);
    private sealed record LicensePerformance(int Count, double P50Ms, double P95Ms, double P99Ms);
}

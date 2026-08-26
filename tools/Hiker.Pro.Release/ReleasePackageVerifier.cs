using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.Trust;

namespace Hiker.Pro.ReleaseTool;

internal sealed class ReleasePackageVerifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ReleaseVerificationResult> VerifyAsync(
        ReleaseVerifyOptions options,
        CancellationToken cancellationToken = default)
    {
        var package = Path.GetFullPath(options.PackagePath);
        var publicKey = Path.GetFullPath(options.PublicKeyPath);
        if (!File.Exists(package) || !File.Exists(publicKey))
        {
            throw new FileNotFoundException("Release package or trusted public key is missing");
        }

        var checks = new List<ReleaseGateCheck>();
        var packageSha = await FileSha256Async(package, cancellationToken).ConfigureAwait(false);
        checks.Add(await VerifyChecksumAsync(package, packageSha, cancellationToken).ConfigureAwait(false));
        var extraction = Path.Combine(Path.GetTempPath(), $"hiker-pro-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        string productVersion = "UNKNOWN";
        string gitCommit = "UNKNOWN";
        try
        {
            ExtractSafely(package, extraction);
            var manifestPath = Path.Combine(extraction, "manifest.json");
            var envelope = JsonSerializer.Deserialize<SignedEnvelope>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                Json) ?? throw new InvalidDataException("Release manifest envelope is empty");
            using var key = ECDsa.Create();
            key.ImportFromPem(await File.ReadAllTextAsync(publicKey, cancellationToken).ConfigureAwait(false));
            var actualPublicKeyFingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            var trustRootMatches = FixedEquals(options.ExpectedPublicKeyFingerprint, actualPublicKeyFingerprint);
            checks.Add(new ReleaseGateCheck(
                "TRUST_ROOT",
                trustRootMatches ? "PASS" : "BLOCKED",
                "The independently supplied release public key matches the out-of-band pinned SHA-256 fingerprint",
                new Dictionary<string, object?> { ["public_key_sha256"] = actualPublicKeyFingerprint }));
            var now = DateTimeOffset.UtcNow;
            var trust = new TrustKey(
                options.KeyId,
                TrustPurposes.Update,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                now.AddDays(-1),
                now.AddYears(20),
                false);
            var signedEnvelopeVerifier = new SignedEnvelopeVerifier(new[] { trust });
            var signedManifest = signedEnvelopeVerifier.Verify<SignedUpdateManifest>(envelope, TrustPurposes.Update);
            if (!signedManifest.Valid || signedManifest.Payload is null)
            {
                throw new InvalidDataException($"Release manifest signature is invalid: {signedManifest.Status}");
            }
            gitCommit = signedManifest.Payload.SourceCommit ?? "UNKNOWN";
            var verifier = new PackageIntegrityVerifier(signedEnvelopeVerifier);
            var allowedUnlisted = new HashSet<string>(new[] { "manifest.json" }, StringComparer.Ordinal);
            var integrity = await verifier.VerifyUpdateAsync(envelope, extraction, cancellationToken, allowedUnlisted).ConfigureAwait(false);
            checks.Add(new ReleaseGateCheck(
                "PACKAGE_SIGNATURE_AND_HASHES",
                integrity.Status == "VERIFIED" ? "PASS" : "BLOCKED",
                "Signed update manifest and every packaged artifact were verified with an independently supplied public key",
                new Dictionary<string, object?> { ["verified_artifacts"] = integrity.Verified, ["failures"] = integrity.Failures }));

            var packageVersions = ReadPackageVersions(extraction);
            productVersion = packageVersions.Product;
            var signedVersionPolicy = string.Equals(signedManifest.Payload.Product, "Hiker Pro", StringComparison.Ordinal) &&
                                      string.Equals(signedManifest.Payload.Version, productVersion, StringComparison.Ordinal) &&
                                      string.Equals(signedManifest.Payload.MinimumRuntimeVersion, packageVersions.Runtime, StringComparison.Ordinal) &&
                                      signedManifest.Payload.Artifacts.Count > 0 &&
                                      gitCommit.Length == 40 && gitCommit.All(Uri.IsHexDigit);
            checks.Add(new ReleaseGateCheck(
                "VERSION_FACTS",
                productVersion != "UNKNOWN" && signedVersionPolicy ? "PASS" : "BLOCKED",
                "VERSION, release-versions.json and the signed manifest agree and bind the package to a Git commit",
                new Dictionary<string, object?>
                {
                    ["product_version"] = productVersion,
                    ["runtime_version"] = packageVersions.Runtime,
                    ["state_schema_version"] = packageVersions.StateSchema,
                    ["source_commit"] = gitCommit
                }));
            checks.Add(ScanPayload(extraction));
            checks.Add(await RunCleanInstallAsync(extraction, packageVersions, cancellationToken).ConfigureAwait(false));
            checks.Add(await VerifyTamperRejectionAsync(envelope, extraction, verifier, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(extraction)) Directory.Delete(extraction, recursive: true);
        }

        var report = new ReleaseVerificationResult(
            "hiker-pro-release-verification/v1",
            checks.All(static check => check.Status == "PASS") ? "PASS" : "BLOCKED",
            Path.GetFileName(package),
            packageSha,
            productVersion,
            gitCommit,
            checks);
        if (options.ReportPath is not null)
        {
            var reportPath = Path.GetFullPath(options.ReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    private static async Task<ReleaseGateCheck> VerifyChecksumAsync(string package, string actual, CancellationToken cancellationToken)
    {
        var checksumPath = Path.ChangeExtension(package, ".sha256");
        var expected = File.Exists(checksumPath)
            ? (await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        var pass = expected is not null && FixedEquals(expected, actual);
        return new ReleaseGateCheck(
            "PACKAGE_CHECKSUM",
            pass ? "PASS" : "BLOCKED",
            "The release archive matches its external SHA-256 sidecar",
            new Dictionary<string, object?> { ["sha256"] = actual, ["sidecar_present"] = expected is not null });
    }

    private static void ExtractSafely(string package, string extraction)
    {
        using var archive = ZipFile.OpenRead(package);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (Path.IsPathRooted(entry.FullName)) throw new InvalidDataException("Release archive contains a rooted entry");
            var target = Path.GetFullPath(Path.Combine(extraction, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(extraction, target);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                throw new InvalidDataException("Release archive entry escapes the installation root");
        }

        archive.ExtractToDirectory(extraction, overwriteFiles: false);
    }

    private static PackageVersions ReadPackageVersions(string extraction)
    {
        var payload = Path.Combine(extraction, "payload");
        var version = File.ReadAllText(Path.Combine(payload, "VERSION")).Trim();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(payload, "release-versions.json")));
        var product = document.RootElement.GetProperty("product").GetString();
        return string.Equals(version, product, StringComparison.Ordinal)
            ? new PackageVersions(
                version,
                document.RootElement.GetProperty("runtime").GetString() ?? "UNKNOWN",
                document.RootElement.GetProperty("state_schema").GetString() ?? "UNKNOWN")
            : new PackageVersions("UNKNOWN", "UNKNOWN", "UNKNOWN");
    }

    private static ReleaseGateCheck ScanPayload(string extraction)
    {
        var findings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(extraction, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extraction, path).Replace(Path.DirectorySeparatorChar, '/');
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".cs" or ".csproj" or ".sln" or ".slnx" or ".pdb" or ".pfx" or ".p12" or ".p8" or ".key" or ".pem")
            {
                findings.Add($"FORBIDDEN_FILE:{relative}");
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length > 64 * 1024 * 1024) continue;
            var content = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            if (SensitiveContentPolicy.ContainsPrivateKey(content)) findings.Add($"PRIVATE_KEY:{relative}");
            if (SensitiveContentPolicy.ContainsCredentialToken(content)) findings.Add($"TOKEN:{relative}");
            if (SensitiveContentPolicy.ContainsConcreteDeveloperHome(content)) findings.Add($"DEVELOPER_HOME:{relative}");
        }

        return new ReleaseGateCheck(
            "PACKAGE_PRIVACY_AND_SOURCE_BOUNDARY",
            findings.Count == 0 ? "PASS" : "BLOCKED",
            "The release archive excludes source, debug symbols, private keys, credential tokens and developer home paths",
            new Dictionary<string, object?> { ["findings"] = findings });
    }

    private static async Task<ReleaseGateCheck> RunCleanInstallAsync(
        string extraction,
        PackageVersions versions,
        CancellationToken cancellationToken)
    {
        var payload = Path.Combine(extraction, "payload");
        var executable = OperatingSystem.IsWindows() && File.Exists(Path.Combine(payload, "hiker.exe"))
            ? Path.Combine(payload, "hiker.exe")
            : "dotnet";
        var prefix = executable == "dotnet" ? new[] { Path.Combine(payload, "hiker.dll") } : Array.Empty<string>();
        var project = Path.Combine(Path.GetTempPath(), $"hiker-pro-clean-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(project);
        try
        {
            var version = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "version" }), cancellationToken).ConfigureAwait(false);
            var init = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "init", "--root", project }), cancellationToken).ConfigureAwait(false);
            var doctor = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "doctor", "--root", project }), cancellationToken).ConfigureAwait(false);
            var status = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "status", "--root", project }), cancellationToken).ConfigureAwait(false);
            var backup = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "backup", "--root", project }), cancellationToken).ConfigureAwait(false);
            var export = await RequireCliAsync(executable, payload, prefix.Concat(new[] { "export", "--root", project, "--project-id", "clean-install" }), cancellationToken).ConfigureAwait(false);
            var exportCount = Directory.Exists(Path.Combine(project, ".ai", "exports"))
                ? Directory.EnumerateFiles(Path.Combine(project, ".ai", "exports"), "*.json").Count()
                : 0;
            var expectedVersion = $"{versions.Product} runtime/{versions.Runtime} schema/{versions.StateSchema}";
            var pass = string.Equals(version.StandardOutput.Trim(), expectedVersion, StringComparison.Ordinal) &&
                       init.StandardOutput.Contains("INITIALIZED", StringComparison.Ordinal) &&
                       doctor.StandardOutput.Contains("HEALTHY", StringComparison.Ordinal) &&
                       status.StandardOutput.Contains("\"ok\": true", StringComparison.Ordinal) &&
                       backup.StandardOutput.Contains("CREATED", StringComparison.Ordinal) &&
                       export.StandardOutput.Contains("EXPORTED", StringComparison.Ordinal) &&
                       exportCount == 1;
            return new ReleaseGateCheck(
                "CLEAN_INSTALL",
                pass ? "PASS" : "BLOCKED",
                "An isolated extracted package executed version, init, doctor, status, backup and state export",
                new Dictionary<string, object?>
                {
                    ["commands"] = 6,
                    ["export_files"] = exportCount,
                    ["duration_ms"] = new[] { version, init, doctor, status, backup, export }.Sum(static item => item.Duration.TotalMilliseconds)
                });
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    private static async Task<ReleaseGateCheck> VerifyTamperRejectionAsync(
        SignedEnvelope envelope,
        string extraction,
        PackageIntegrityVerifier verifier,
        CancellationToken cancellationToken)
    {
        var payloadPath = Directory.EnumerateFiles(Path.Combine(extraction, "payload"), "*.dll", SearchOption.TopDirectoryOnly).OrderBy(static value => value, StringComparer.Ordinal).First();
        await using (var stream = new FileStream(payloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var original = stream.ReadByte();
            if (original < 0) throw new InvalidDataException("Cannot tamper an empty release artifact");
            stream.Position = 0;
            stream.WriteByte((byte)(original ^ 0x01));
            stream.Flush(flushToDisk: true);
        }

        var result = await verifier.VerifyUpdateAsync(
            envelope,
            extraction,
            cancellationToken,
            new HashSet<string>(new[] { "manifest.json" }, StringComparer.Ordinal)).ConfigureAwait(false);
        return new ReleaseGateCheck(
            "PACKAGE_TAMPER_REJECTION",
            result.Status == "BLOCKED" ? "PASS" : "BLOCKED",
            "A byte-level mutation of a packaged assembly was rejected by the signed artifact manifest",
            new Dictionary<string, object?> { ["verification_status"] = result.Status, ["failures"] = result.Failures });
    }

    private static Task<ProcessResult> RequireCliAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) =>
        ProcessRunner.RequireAsync(executable, workingDirectory, arguments, TimeSpan.FromMinutes(1), cancellationToken);

    private static async Task<string> FileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool FixedEquals(string left, string right) => left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private sealed record PackageVersions(string Product, string Runtime, string StateSchema);
}

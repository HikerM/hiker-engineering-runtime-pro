using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Hiker.Pro.ReleaseTool;

internal sealed class CommercialReleaseGate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] RequiredTestEvidence =
    {
        "FaultBeforeEventInsert_RollsBackDomainAndJournalThenAllowsSafeRetry",
        "BatchCompareAndSwap_RejectsWholeBatchWhenOneMemberIsStale",
        "OperationRetry_IsIdempotentAndPayloadConflictIsRejected",
        "PartialImpactPreservesUnaffectedFactsAndInvalidatesOnlyBoundEvidence",
        "ApiContractChangeInvalidatesConsumerEvidenceButNotDatabaseEvidence",
        "RejectedDecisionRetainsReasonAndCurrentDecisionSupersedesAtomically",
        "Start_RejectsSecondWriterWhileLeaseIsActiveWithoutStartingAdapter",
        "Recovery_UsesValidatedCheckpointAndDoesNotReplayCompletedRecovery",
        "Completion_DrainsThenReleasesLeaseOwnershipAndWorktreeExactlyOnce",
        "Scheduler_AdmitsDisjointWorkDefersConflictsAndRequiresIndependentAssurance",
        "MergeQueue_RequiresAssuranceSerializesAuthorizationAndObservesRealGitMerge",
        "ProductionDomainWrites_AreRoutedThroughStateCoordinator",
        "DoctorDetectsArtifactCorruptionAndBrokenDependency",
        "DoctorRepairDropsOnlyInvalidProjectionAndGcRemovesAgedOrphanArtifact",
        "ContextResolver_CostDependsOnTaskEdgesNotColdHistorySize",
        "SqlitePerformance_UsesRealQueryPlansConcurrencyAndNPlusOneEvidence",
        "SensitiveContentPolicy_DetectsDirectAndEscapedHomesWithoutFlaggingProductIdentifiers"
    };
    private static readonly IReadOnlyDictionary<string, string[]> RequiredReports = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["state-aging.json"] = new[] { "ACCELERATED-AGING", "HISTORY-HOT-PATH", "MULTI-WORKER-CAS", "MULTI-RUNTIME-WORKERS" },
        ["large-repository.json"] = new[] { "LARGE-REPOSITORY" },
        ["real-services.json"] = new[] { "REAL-PROCESS-PORT-HTTP", "REAL-DB" },
        ["commercial.json"] = new[] { "COMM-G1-G2-G16", "COMM-G3", "COMM-G17" },
        ["commercial-security.json"] = Enumerable.Range(4, 12).Select(static value => $"COMM-G{value}").ToArray(),
        ["codex-runtime.json"] = new[] { "REAL-CODEX-RUNTIME" }
    };

    public async Task<CommercialReleaseGateReport> RunAsync(
        CommercialGateOptions options,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RepositoryRoot);
        var publicRoot = Path.GetFullPath(options.PublicRepositoryRoot);
        var evidenceRoot = Path.GetFullPath(options.EvidenceRoot);
        var releaseRoot = Path.GetFullPath(options.ReleaseRoot);
        var checks = new List<ReleaseGateCheck>();
        var versions = await RepositoryVersionReader.ReadAndValidateAsync(root, cancellationToken).ConfigureAwait(false);
        var head = (await ProcessRunner.RequireAsync("git", root, new[] { "rev-parse", "HEAD" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var branch = (await ProcessRunner.RequireAsync("git", root, new[] { "branch", "--show-current" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var worktree = (await ProcessRunner.RequireAsync("git", root, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput;
        var remoteLine = (await ProcessRunner.RequireAsync("git", root, new[] { "ls-remote", "origin", "refs/heads/main" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var remoteHead = remoteLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        checks.Add(Check(
            "SOURCE_AND_VERSION",
            branch == "main" && string.IsNullOrWhiteSpace(worktree) && string.Equals(head, remoteHead, StringComparison.Ordinal) && versions.Product.Contains("5.19.0", StringComparison.Ordinal),
            "Version facts agree, the release source is clean main, and local HEAD equals origin/main",
            new Dictionary<string, object?> { ["branch"] = branch, ["head"] = head, ["remote_head"] = remoteHead }));

        var repository = await ReadRepositoryMetadataAsync(root, cancellationToken).ConfigureAwait(false);
        checks.Add(Check(
            "PRIVATE_REPOSITORY",
            repository.Private && repository.DefaultBranch == "main" && repository.Name == "HikerM/hiker-engineering-runtime-pro",
            "GitHub repository metadata confirms the commercial source repository is private",
            new Dictionary<string, object?> { ["repository"] = repository.Name, ["private"] = repository.Private, ["visibility"] = repository.Visibility, ["default_branch"] = repository.DefaultBranch }));

        checks.Add(await VerifyPublicBaselineAsync(publicRoot, versions, cancellationToken).ConfigureAwait(false));
        checks.Add(VerifyDocumentation(root));
        checks.Add(VerifyTests(options.TestResultsPath));
        checks.AddRange(VerifyScenarioReports(evidenceRoot, versions.Product, head));
        var release = VerifyReleasePackage(evidenceRoot, releaseRoot, versions.Product, head);
        checks.Add(release.Check);
        checks.Add(VerifyCommunityRegression(evidenceRoot, versions));

        var pass = checks.All(static check => check.Status == "PASS");
        var report = new CommercialReleaseGateReport(
            "hiker-pro-commercial-gate/v1",
            pass ? "PASS" : "BLOCKED",
            pass ? "PASS_5_19_COMMERCIAL" : "BLOCKED_5_19",
            versions.Product,
            versions.Runtime,
            versions.StateSchema,
            repository.Name,
            repository.Private,
            repository.DefaultBranch,
            head,
            release.PackageFile,
            checks);
        var output = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static IEnumerable<ReleaseGateCheck> VerifyScenarioReports(string evidenceRoot, string productVersion, string head)
    {
        foreach (var required in RequiredReports)
        {
            var path = Path.Combine(evidenceRoot, required.Key);
            if (!File.Exists(path))
            {
                yield return Check($"REPORT_{Path.GetFileNameWithoutExtension(required.Key).ToUpperInvariant()}", false, "Required release evidence report is missing", new Dictionary<string, object?> { ["report"] = required.Key });
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var ids = root.GetProperty("checks").EnumerateArray()
                .Where(static item => item.GetProperty("status").GetString() == "PASS")
                .Select(static item => item.GetProperty("id").GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            var pass = root.GetProperty("status").GetString() == "PASS" &&
                       root.GetProperty("productVersion").GetString() == productVersion &&
                       root.GetProperty("gitCommit").GetString() == head &&
                       !root.GetProperty("gitDirty").GetBoolean() &&
                       required.Value.All(ids.Contains);
            yield return Check(
                $"REPORT_{Path.GetFileNameWithoutExtension(required.Key).ToUpperInvariant()}",
                pass,
                "The required scenario report is PASS, clean, version-matched, commit-bound and contains every required check",
                new Dictionary<string, object?> { ["report"] = required.Key, ["required_checks"] = required.Value, ["passing_checks"] = ids.OrderBy(static value => value, StringComparer.Ordinal).ToArray() });
        }
    }

    private static (ReleaseGateCheck Check, string PackageFile) VerifyReleasePackage(
        string evidenceRoot,
        string releaseRoot,
        string productVersion,
        string head)
    {
        var reportPath = Path.Combine(evidenceRoot, "release-package.json");
        if (!File.Exists(reportPath))
        {
            return (Check("RELEASE_PACKAGE", false, "Release package verification report is missing"), string.Empty);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = document.RootElement;
        var packageFile = root.GetProperty("packageFile").GetString() ?? string.Empty;
        var statuses = root.GetProperty("checks").EnumerateArray().ToDictionary(
            static item => item.GetProperty("id").GetString() ?? string.Empty,
            static item => item.GetProperty("status").GetString() ?? string.Empty,
            StringComparer.Ordinal);
        var required = new[] { "PACKAGE_CHECKSUM", "TRUST_ROOT", "PACKAGE_SIGNATURE_AND_HASHES", "VERSION_FACTS", "PACKAGE_PRIVACY_AND_SOURCE_BOUNDARY", "CLEAN_INSTALL", "PACKAGE_TAMPER_REJECTION" };
        var pass = root.GetProperty("status").GetString() == "PASS" &&
                   root.GetProperty("productVersion").GetString() == productVersion &&
                   root.GetProperty("gitCommit").GetString() == head &&
                   required.All(id => statuses.TryGetValue(id, out var status) && status == "PASS") &&
                   File.Exists(Path.Combine(releaseRoot, packageFile)) &&
                   File.Exists(Path.ChangeExtension(Path.Combine(releaseRoot, packageFile), ".sha256"));
        return (Check(
            "RELEASE_PACKAGE",
            pass,
            "The commit-bound signed package passed checksum, trust root, integrity, privacy, clean install and tamper gates",
            new Dictionary<string, object?> { ["package"] = packageFile, ["required_checks"] = required }), packageFile);
    }

    private static ReleaseGateCheck VerifyTests(string testResultsPath)
    {
        var path = Path.GetFullPath(testResultsPath);
        if (!File.Exists(path)) return Check("UNIT_INTEGRATION", false, "Current-commit TRX test evidence is missing");
        var document = XDocument.Load(path, LoadOptions.None);
        var counters = document.Descendants().SingleOrDefault(static element => element.Name.LocalName == "Counters");
        var total = AttributeInt(counters, "total");
        var passed = AttributeInt(counters, "passed");
        var failed = AttributeInt(counters, "failed") + AttributeInt(counters, "error") + AttributeInt(counters, "aborted");
        var passingNames = document.Descendants()
            .Where(static element => element.Name.LocalName == "UnitTestResult" && element.Attribute("outcome")?.Value == "Passed")
            .Select(static element => element.Attribute("testName")?.Value ?? string.Empty)
            .ToArray();
        var missing = RequiredTestEvidence
            .Where(required => !passingNames.Any(name => name.Contains(required, StringComparison.Ordinal)))
            .ToArray();
        return Check(
            "UNIT_INTEGRATION",
            total >= 50 && passed == total && failed == 0 && missing.Length == 0,
            "The current release candidate suite passed and contains every named critical architecture, atomicity, recovery, scope, performance and privacy proof",
            new Dictionary<string, object?>
            {
                ["total"] = total,
                ["passed"] = passed,
                ["failed"] = failed,
                ["required_named_tests"] = RequiredTestEvidence,
                ["missing_named_tests"] = missing
            });
    }

    private static ReleaseGateCheck VerifyDocumentation(string root)
    {
        var required = new[]
        {
            "README.md",
            "ARCHITECTURE.md",
            "PROJECT_STATE.md",
            "CHANGELOG.md",
            "docs/verification/5.19-verification.md",
            "docs/release/5.19-release-process.md"
        };
        var missing = required.Where(path => !File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        return Check("RELEASE_DOCUMENTATION", missing.Length == 0, "Release state, architecture, change log, process and verification documentation are present", new Dictionary<string, object?> { ["missing"] = missing });
    }

    private static async Task<ReleaseGateCheck> VerifyPublicBaselineAsync(
        string publicRoot,
        RepositoryVersionFacts versions,
        CancellationToken cancellationToken)
    {
        var head = (await ProcessRunner.RequireAsync("git", publicRoot, new[] { "rev-parse", "HEAD" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        var status = (await ProcessRunner.RequireAsync("git", publicRoot, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false)).StandardOutput;
        using var publicVersions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(publicRoot, "release-versions.json"), cancellationToken).ConfigureAwait(false));
        var version = publicVersions.RootElement.GetProperty("engineering").GetString() ?? "UNKNOWN";
        return Check(
            "PUBLIC_5_18_BASELINE",
            head == versions.CommunityCommit && string.IsNullOrWhiteSpace(status) && version == versions.CommunityBaseline,
            "The Community 5.18 baseline remains unchanged and clean",
            new Dictionary<string, object?> { ["head"] = head, ["version"] = version, ["clean"] = string.IsNullOrWhiteSpace(status) });
    }

    private static ReleaseGateCheck VerifyCommunityRegression(string evidenceRoot, RepositoryVersionFacts versions)
    {
        var regressionPath = Path.Combine(evidenceRoot, "community-5.18-live-regression.json");
        var installPath = Path.Combine(evidenceRoot, "community-5.18-clean-install.json");
        if (!File.Exists(regressionPath) || !File.Exists(installPath))
            return Check("COMMUNITY_REGRESSION", false, "Community regression or clean-install evidence is missing");
        using var regression = JsonDocument.Parse(File.ReadAllText(regressionPath));
        using var install = JsonDocument.Parse(File.ReadAllText(installPath));
        var regressionRoot = regression.RootElement;
        var installRoot = install.RootElement;
        var pass = regressionRoot.GetProperty("ok").GetBoolean() &&
                   regressionRoot.GetProperty("head").GetString() == versions.CommunityCommit &&
                   regressionRoot.GetProperty("test_count").GetDouble() >= 297 &&
                   installRoot.GetProperty("ok").GetBoolean() &&
                   installRoot.GetProperty("version").GetString() == versions.CommunityBaseline &&
                   installRoot.GetProperty("single_active_version").GetBoolean();
        return Check(
            "COMMUNITY_REGRESSION",
            pass,
            "Community 5.17/5.18 behavior passed 297 tests and isolated clean-install verification",
            new Dictionary<string, object?> { ["tests"] = regressionRoot.GetProperty("test_count").GetDouble(), ["single_active_version"] = installRoot.GetProperty("single_active_version").GetBoolean() });
    }

    private static async Task<RepositoryMetadata> ReadRepositoryMetadataAsync(string root, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RequireAsync(
            "gh",
            root,
            new[] { "repo", "view", "HikerM/hiker-engineering-runtime-pro", "--json", "nameWithOwner,isPrivate,visibility,defaultBranchRef" },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var value = document.RootElement;
        return new RepositoryMetadata(
            value.GetProperty("nameWithOwner").GetString() ?? string.Empty,
            value.GetProperty("isPrivate").GetBoolean(),
            value.GetProperty("visibility").GetString() ?? string.Empty,
            value.GetProperty("defaultBranchRef").GetProperty("name").GetString() ?? string.Empty);
    }

    private static ReleaseGateCheck Check(string id, bool pass, string detail, IReadOnlyDictionary<string, object?>? metrics = null) =>
        new(id, pass ? "PASS" : "BLOCKED", detail, metrics ?? new Dictionary<string, object?>());

    private static int AttributeInt(XElement? element, string name) =>
        int.TryParse(element?.Attribute(name)?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private sealed record RepositoryMetadata(string Name, bool Private, string Visibility, string DefaultBranch);
}

namespace Hiker.Pro.ReleaseTool;

internal sealed record RepositoryVersionFacts(
    string Product,
    string Runtime,
    string StateSchema,
    string CommunityBaseline,
    string CommunityCommit);

internal sealed record ReleaseBuildOptions(
    string RepositoryRoot,
    string OutputRoot,
    string PrivateKeyPath,
    string KeyId,
    string RuntimeIdentifier,
    string Channel,
    string Configuration);

internal sealed record ReleaseBuildResult(
    string Status,
    string ProductVersion,
    string RuntimeVersion,
    string StateSchemaVersion,
    string GitCommit,
    string PackageFile,
    string PackageSha256,
    string ManifestFile,
    string ManifestSha256,
    string ChecksumFile,
    string PublicKeyFile,
    string PublicKeyFingerprint,
    int ArtifactCount,
    long PackageBytes);

internal sealed record ReleaseVerifyOptions(
    string PackagePath,
    string PublicKeyPath,
    string KeyId,
    string ExpectedPublicKeyFingerprint,
    string? ReportPath);

internal sealed record ReleaseGateCheck(string Id, string Status, string Detail, IReadOnlyDictionary<string, object?> Metrics);

internal sealed record ReleaseVerificationResult(
    string SchemaVersion,
    string Status,
    string PackageFile,
    string PackageSha256,
    string ProductVersion,
    string GitCommit,
    IReadOnlyList<ReleaseGateCheck> Checks);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

internal sealed record CommercialGateOptions(
    string RepositoryRoot,
    string PublicRepositoryRoot,
    string EvidenceRoot,
    string ReleaseRoot,
    string TestResultsPath,
    string OutputPath);

internal sealed record CommercialReleaseGateReport(
    string SchemaVersion,
    string Status,
    string Verdict,
    string ProductVersion,
    string RuntimeVersion,
    string StateSchemaVersion,
    string Repository,
    bool Private,
    string DefaultBranch,
    string GitCommit,
    string ReleaseCandidate,
    IReadOnlyList<ReleaseGateCheck> Checks);

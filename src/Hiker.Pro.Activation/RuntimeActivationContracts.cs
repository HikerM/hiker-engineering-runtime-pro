namespace Hiker.Pro.Activation;

public sealed record RuntimeIdentity(
    string ProductVersion,
    string RuntimeApiVersion,
    string StateSchemaVersion,
    int LiveAdoptionProtocol,
    string BuildCommit,
    string RuntimeIdentityValue,
    string Channel);

public sealed record ActiveRuntimePointer(
    string Format,
    string ActiveRuntime,
    string? PreviousRuntime,
    string RuntimeIdentity,
    string ProductVersion,
    string RuntimeApiVersion,
    string StateSchemaVersion,
    int LiveAdoptionProtocol,
    string BuildCommit,
    long InstallGeneration,
    string ActivationEpoch,
    DateTimeOffset VerifiedAt,
    string RuntimeChannel);

public sealed record SchemaCompatibilityReceipt(
    string SourceSchema,
    string TargetSchema,
    string BackupSha256,
    bool Verified);

public sealed record VerifiedRuntimePackage(
    string StagingDirectory,
    RuntimeIdentity Identity,
    IReadOnlyDictionary<string, string> FileSha256,
    string PackageVerificationStatus,
    string EntitlementVerificationStatus,
    SchemaCompatibilityReceipt? SchemaCompatibility = null);

public sealed record RuntimeProbeResult(
    string ProductVersion,
    string RuntimeApiVersion,
    string StateSchemaVersion,
    int LiveAdoptionProtocol);

public sealed record RuntimeActivationResult(
    string Status,
    ActiveRuntimePointer Pointer,
    string RuntimeDirectory,
    int VerifiedFiles,
    bool PointerChanged);

public sealed record FlatRuntimeBootstrapResult(
    string Status,
    ActiveRuntimePointer Pointer,
    string RuntimeDirectory,
    int VerifiedFiles,
    bool PointerChanged);

public sealed record RuntimeLaunchTarget(
    string Executable,
    ActiveRuntimePointer Pointer,
    int PointerFilesRead);

public interface IRuntimePackageProbe
{
    Task<RuntimeProbeResult> ProbeAsync(string executable, CancellationToken cancellationToken = default);
}

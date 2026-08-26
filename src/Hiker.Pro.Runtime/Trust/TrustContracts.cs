namespace Hiker.Pro.Trust;

public static class TrustPurposes
{
    public const string Entitlement = "ENTITLEMENT";
    public const string Activation = "ACTIVATION";
    public const string Update = "UPDATE";
    public const string Module = "MODULE";
}

public sealed record SignedEnvelope(
    string Format,
    string Algorithm,
    string KeyId,
    string Purpose,
    string Payload,
    string Signature);

public sealed record TrustKey(
    string KeyId,
    string Purpose,
    string SubjectPublicKeyInfoBase64,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool Revoked);

public sealed record SignatureVerification<T>(bool Valid, string Status, T? Payload, string KeyId);

public sealed record CommercialEntitlement(
    string LicenseId,
    string Edition,
    IReadOnlyList<string> Features,
    string MinimumVersion,
    string MaximumVersion,
    int DeviceLimit,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatesUntil,
    int OfflineGraceDays,
    string AccountSubjectHash);

public sealed record DeviceActivationCertificate(
    string ActivationId,
    string LicenseId,
    string InstallationPublicKeyHash,
    string DeviceBindingHash,
    int DeviceSlot,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record UpdateArtifact(
    string RelativePath,
    string Sha256,
    long Bytes,
    string Platform,
    string Architecture);

public sealed record SignedUpdateManifest(
    string Product,
    string Version,
    string Channel,
    string MinimumRuntimeVersion,
    DateTimeOffset PublishedAt,
    IReadOnlyList<UpdateArtifact> Artifacts,
    IReadOnlyList<string>? RollbackFromVersions = null);

public sealed record UpdatePolicyDecision(bool Allowed, string Status);

public sealed record SignedModuleManifest(
    string ModuleId,
    string Version,
    string MinimumProductVersion,
    string AssemblySha256,
    IReadOnlyList<string> RequiredFeatures);

public sealed record TrustClockState(DateTimeOffset LastSeenUtc, DateTimeOffset? LastServerUtc, string Fingerprint);

public enum LicenseMode
{
    Licensed,
    OfflineGrace,
    RecoveryOnly,
    RequiresOnlineValidation,
    Invalid
}

public sealed record LicenseDecision(
    LicenseMode Mode,
    bool ProFeaturesAllowed,
    bool DataRecoveryAllowed,
    bool StateExportAllowed,
    IReadOnlySet<string> Features,
    string Status,
    DateTimeOffset EvaluatedAt);

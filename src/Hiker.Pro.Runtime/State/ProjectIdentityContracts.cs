namespace Hiker.Pro.State;

public static class ProjectIdentityFacts
{
    public const string EntityId = "hiker-project-identity";
    public const string Authority = "PROJECT_BOOTSTRAP";
}

public enum ProjectBootstrapState
{
    NewProject,
    LegacyProject,
    ActiveExistingProject,
    MixedLegacyProject,
    IdentityMissing,
    IdentityConflict,
    IdentityMismatch,
    ProjectMoved,
    ProjectForked,
    StateDamaged,
    MigrationIncomplete,
    LegacyAlreadyImported,
    RequiresReview
}

public enum ProjectBootstrapIntent
{
    Read,
    Initialize,
    MigrateCommunity,
    RuntimeStart,
    Backup,
    Export,
    Fork,
    Recovery
}

public sealed record ProjectIdentityInventory(
    bool DatabaseExists,
    bool DatabaseHealthy,
    string? ErrorCode,
    IReadOnlyList<StateSnapshot> ActiveIdentityRecords,
    IReadOnlyList<string> ActiveProjectIds,
    IReadOnlyList<string> HistoricalProjectIds,
    IReadOnlyList<string> CompletedCommunityImportProjectIds,
    bool HasImportedCommunityFacts,
    bool HasAnyEntity,
    bool HasAnyOperation);

public static class LegacyInventoryClassifications
{
    public const string KnownLegacyContent = "KNOWN_LEGACY_CONTENT";
    public const string SafePreservedContent = "SAFE_PRESERVED_CONTENT";
    public const string TemporaryRebuildable = "TEMPORARY_REBUILDABLE";
    public const string MigratableLegacyFact = "MIGRATABLE_LEGACY_FACT";
    public const string MigratableLegacyContent = MigratableLegacyFact;
    public const string UnknownRequiresReview = "UNKNOWN_REQUIRES_REVIEW";
}

public sealed record LegacyInventoryEntry(
    string Path,
    string SemanticType,
    IReadOnlyList<string> Classifications,
    string Handling,
    int MigratableFacts);

public sealed record LegacyInventorySummary(
    int KnownLegacyContent,
    int SafePreservedContent,
    int MigratableLegacyContent,
    int UnknownRequiresReview,
    string InventoryFingerprint,
    IReadOnlyList<LegacyInventoryEntry> Entries);

public sealed record AiContentInventory(
    bool AiExists,
    int AuthoritativeFacts,
    int MigratableLegacyFacts,
    int ColdArchiveItems,
    int RebuildableCacheItems,
    int TemporaryItems,
    LegacyInventorySummary LegacyInventory,
    IReadOnlyList<string> UnknownItems);

public sealed record ProjectPreflightResult(
    ProjectBootstrapState State,
    string Status,
    string? ProjectId,
    string IdentityAuthority,
    bool CanProceed,
    bool ReadOnly,
    bool IdentityWriteRequired,
    bool CommunityImportRequired,
    string ReasonCode,
    AiContentInventory AiContent,
    ProjectIdentityInventory Identity);

public sealed record ProjectBootstrapResult(
    string Status,
    string ProjectId,
    ProjectBootstrapState ProjectState,
    bool IdentityCreated,
    bool IdentityReused,
    bool CommunityImported,
    CommunityImportReport? Import);

public sealed record ProjectForkRequest(
    string OperationId,
    string SourceProjectId,
    string NewProjectId,
    string IdentityPayloadJson,
    string SourceFingerprint,
    IReadOnlySet<string> InheritedKinds);

public sealed record ProjectForkResult(
    string Status,
    string SourceProjectId,
    string NewProjectId,
    long Generation,
    int InheritedEntities,
    int ArchivedEntities,
    int ResetOperationalEntities);

public sealed record ProjectForkOperationResult(ProjectForkResult Fork, StateBackupResult? SafetyBackup);

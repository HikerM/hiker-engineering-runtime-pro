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

public sealed record AiContentInventory(
    bool AiExists,
    int AuthoritativeFacts,
    int MigratableLegacyFacts,
    int ColdArchiveItems,
    int RebuildableCacheItems,
    int TemporaryItems,
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

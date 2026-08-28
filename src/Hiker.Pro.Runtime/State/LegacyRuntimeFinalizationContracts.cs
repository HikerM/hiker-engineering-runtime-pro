using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.State;

public static class LegacyRuntimeFinalizationFacts
{
    public const string Authority = "COMMUNITY_5_18_IMPORT";
    public const string SchemaVersion = "2.0.0";

    public static IReadOnlySet<string> RuntimeKinds { get; } = new HashSet<string>(
        new[]
        {
            EntityKinds.Task,
            EntityKinds.TaskDependency,
            EntityKinds.Ownership,
            EntityKinds.Worktree,
            EntityKinds.RuntimeLease,
            EntityKinds.ResourceLease,
            EntityKinds.Agent,
            EntityKinds.Checkpoint
        },
        StringComparer.Ordinal);

    public static string OperationId(string projectId, string sourceFingerprint, long migrationGeneration) =>
        LegacyOperationIdentity.Create("RUNTIME_FINALIZE", projectId, sourceFingerprint, migrationGeneration);
}

public sealed record LegacyRuntimeInspection(
    string ProjectId,
    int Finalizable,
    int Archived,
    int RequiresReview,
    IReadOnlyDictionary<string, int> FinalizableByKind,
    IReadOnlyList<LegacyRuntimeFactReference> FinalizableFacts,
    IReadOnlyList<LegacyRuntimeFactReference> ReviewFacts,
    bool CompletionRecorded);

public sealed record LegacyRuntimeFactReference(
    string Kind,
    string EntityId,
    string SchemaVersion,
    StateLifecycle Lifecycle)
{
    public string Description => $"{Kind}/{EntityId}:schema={SchemaVersion}:lifecycle={Lifecycle}";
}

public sealed record LegacyRuntimeFinalizeRequest(
    string OperationId,
    string ProjectId,
    string SourceFingerprint,
    long MigrationGeneration);

public sealed record LegacyRuntimeFinalizeResult(
    string Status,
    string ProjectId,
    long Generation,
    int Finalized,
    IReadOnlyDictionary<string, int> FinalizedByKind,
    IReadOnlyList<string> ReviewEntities);

public sealed record LegacyRuntimeFinalizationOutcome(
    string Status,
    string ProjectId,
    string ReasonCode,
    LegacyRuntimeInspection Inspection,
    StateBackupResult? Backup,
    LegacyRuntimeFinalizeResult Finalization,
    HealthReport Health);

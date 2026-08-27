using Hiker.Pro.Environment;
using Hiker.Pro.Graph;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

public static class ChangeRiskClasses
{
    public const string Local = "LOCAL";
    public const string Shared = "SHARED";
    public const string CrossCutting = "CROSS_CUTTING";
    public const string Systemic = "SYSTEMIC";
}

public static class BlastDimensions
{
    public const string Structural = "STRUCTURAL";
    public const string Contract = "CONTRACT";
    public const string DataState = "DATA_STATE";
    public const string Runtime = "RUNTIME";
    public const string Configuration = "CONFIGURATION";
    public const string Resource = "RESOURCE";
    public const string Verification = "VERIFICATION";
    public const string Temporal = "TEMPORAL";
    public const string Security = "SECURITY";
    public const string Performance = "PERFORMANCE";
    public const string Delivery = "DELIVERY";
    public const string Historical = "HISTORICAL";
}

public static class ChangeControlStatuses
{
    public const string FastPath = "FAST_PATH";
    public const string AnalysisRequired = "FULL_BLAST_ANALYSIS_REQUIRED";
    public const string Ready = "CHANGE_READY";
    public const string ContractBreached = "CHANGE_CONTRACT_BREACHED";
    public const string FixLoopSuspected = "FIX_LOOP_SUSPECTED";
    public const string FixLoopConfirmed = "FIX_LOOP_CONFIRMED";
    public const string PatchingDisabled = "PATCHING_DISABLED";
    public const string RootCauseRequired = "ROOT_CAUSE_MODE_REQUIRED";
    public const string InvariantGuardFailed = "INVARIANT_GUARD_FAIL";
}

public sealed record ChangeRiskSignals(
    bool SharedState = false,
    bool DatabaseSchema = false,
    bool CrossServiceBoundary = false,
    bool CriticalPath = false,
    bool Irreversible = false,
    bool SecurityBoundary = false,
    bool PerformanceCritical = false,
    bool DeliveryCritical = false,
    bool TemporalCompatibilityWindow = false,
    string Uncertainty = "LOW",
    int RecentFailedAttempts = 0,
    int FallbackAdditions = 0,
    int SpecialCaseAdditions = 0,
    IReadOnlyList<string>? DeclaredDimensions = null);

public sealed record ChangeProbeRequest(
    string ProjectId,
    string TaskId,
    string ChangeId,
    IReadOnlyList<ImpactSeed> Seeds,
    ChangeRiskSignals Signals,
    IReadOnlyList<ResourceRequirement>? ResourceRequirements,
    string SourceFingerprint);

public sealed record ChangeCentrality(
    int DirectConsumerCount,
    int TransitiveConsumerCount,
    int ContractConsumerCount,
    int ServiceFanout,
    int EvidenceFanout,
    int CriticalPathCount,
    int RuntimeFanout,
    int ResourceFanout,
    int HistoricalCoChangeDegree);

public sealed record ChangeRiskProbeResult(
    string Classification,
    bool RequiresFullAnalysis,
    bool PatchingAllowed,
    IReadOnlyList<string> Reasons,
    ChangeCentrality Centrality,
    int GraphQueries,
    int EdgesInspected,
    double LatencyMs,
    string Status);

public sealed record ChangeContractDefinition(
    string ChangeContractId,
    string ChangeId,
    string TaskId,
    long GoalRevision,
    string ProblemCode,
    string IntendedInvariant,
    IReadOnlyList<string> AllowedAffectedScopes,
    IReadOnlyList<string> ExpectedBehaviorChanges,
    IReadOnlyList<string> ExpectedUnaffectedBehaviors,
    IReadOnlyList<string> ForbiddenSideEffects,
    IReadOnlyList<string> AcceptanceCriteria,
    string RollbackBoundary,
    string SourceFingerprint,
    string Status = "ACTIVE");

public sealed record ChangeExecutionPolicy(
    string Classification,
    string? ChangeContractId,
    bool RequiresSingleWriter,
    bool RequiresIndependentAssurance,
    bool RequiresExclusiveMutation,
    bool RequiresRollbackBoundary,
    bool PatchingAllowed,
    bool RootCauseModeRequired);

public sealed record ChangeRelationAssessment(
    StateKey Key,
    string RelationType,
    string? RelationFingerprint,
    string Source,
    string Confidence,
    string Freshness,
    string Authority,
    bool AuthoritativeDependency,
    DateTimeOffset ObservedAt);

public sealed record ChangeRegressionSet(
    IReadOnlyList<string> PrimaryAcceptance,
    IReadOnlyList<string> InvariantRegression,
    IReadOnlyList<StateKey> BlastDerivedRegression);

public sealed record ChangeBlastAnalysis(
    string Classification,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<ImpactedNode> Nodes,
    ChangeCentrality Centrality,
    IReadOnlyList<ChangeRelationAssessment> Relations,
    ChangeRegressionSet Regression,
    string Severity,
    string Confidence,
    int GraphQueries,
    int NodesVisited,
    bool Truncated,
    double LatencyMs,
    string Status);

public sealed record ChangePreflightRequest(
    string OperationId,
    ChangeProbeRequest Probe,
    ChangeContractDefinition? Contract = null,
    int MaxNodes = 4096,
    int MaxDepth = 16);

public sealed record ChangePreflightResult(
    string Status,
    ChangeRiskProbeResult Probe,
    ChangeBlastAnalysis? Blast,
    ChangeExecutionPolicy Policy,
    long Generation,
    int ContextDeltaBytes,
    bool FullAnalysisExecuted,
    bool StateWritten);

public sealed record ChangeAttempt(
    string AttemptId,
    string FailureSignature,
    IReadOnlyList<string> AffectedScopes,
    bool AcceptancePassed,
    int FallbackAdditions,
    int SpecialCaseAdditions,
    int EvidenceStaleCount,
    IReadOnlyList<string> RejectedSolutionCodes,
    DateTimeOffset ObservedAt);

public sealed record ChangeLoopState(
    string ChangeId,
    int TotalAttempts,
    int ConsecutiveFailures,
    int ScopeExpansionCount,
    int FallbackAdditions,
    int SpecialCaseAdditions,
    int EvidenceStaleCount,
    IReadOnlyList<ChangeAttempt> RecentAttempts,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record ChangeAttemptResult(
    string Status,
    ChangeLoopState State,
    long Generation,
    bool PatchingAllowed,
    bool RootCauseRequired);

public sealed record RootCausePlan(
    string ChangeId,
    string ObservedSymptomCode,
    string RootCauseHypothesisCode,
    string SharedInvariant,
    IReadOnlyList<string> ConflictingConstraints,
    IReadOnlyList<string> PriorFailedAttempts,
    IReadOnlyList<string> RejectedAlternatives,
    IReadOnlyList<StateKey> AffectedDependents,
    IReadOnlyList<string> VerificationPlan,
    string RollbackBoundary,
    string Status);

public sealed record PatchDebtFact(
    string DebtId,
    string ChangeId,
    string IntroducedBy,
    long GoalRevision,
    string Mechanism,
    string ReasonCode,
    IReadOnlyList<string> Scope,
    string? AffectedContract,
    string RemovalCondition,
    string ReviewCondition,
    IReadOnlyList<string> EvidenceIds,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record ChangeScopeEvaluationRequest(
    string OperationId,
    string ProjectId,
    string ChangeContractId,
    IReadOnlyList<string> ActualAffectedScopes,
    IReadOnlyList<StateKey> NewlyAffectedSharedFacts,
    IReadOnlyList<string> PatchMechanisms,
    IReadOnlyList<string> RegisteredPatchDebtIds,
    string SourceFingerprint);

public sealed record ChangeScopeEvaluation(
    string Status,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<StateKey> UndeclaredSharedFacts,
    IReadOnlyList<string> UntrackedPatchMechanisms,
    long Generation);

public sealed record ChangeVerificationOutcome(string EvidenceId, string Status, string FailureKind);

public sealed record ChangeVerificationRequest(
    string ProjectId,
    string ChangeId,
    string ChangeContractId,
    IReadOnlyList<ChangeVerificationOutcome> PrimaryAcceptance,
    IReadOnlyList<ChangeVerificationOutcome> InvariantRegression,
    IReadOnlyList<ChangeVerificationOutcome> BlastDerivedRegression);

public sealed record ChangeVerificationResult(
    string Status,
    bool Accepted,
    IReadOnlyList<string> Regressions,
    IReadOnlyList<string> EnvironmentalFailures,
    IReadOnlyList<string> MissingEvidence);

public sealed record ChangeOutcomeRequest(
    string OperationId,
    string ProjectId,
    string ChangeId,
    string ChangeContractId,
    ChangeBlastAnalysis Predicted,
    string ObservedClassification,
    IReadOnlyList<StateKey> ObservedAffectedNodes,
    IReadOnlyList<string> ObservedDimensions,
    int FixAttempts,
    int PatchDebtCreated,
    int PatchDebtRemoved,
    int RegressionsFound,
    bool RollbackUsed,
    bool AcceptancePassed,
    long TimeToAcceptedChangeMs,
    string SourceFingerprint);

public sealed record ChangeOutcomeTelemetry(
    string ChangeId,
    string RequestFingerprint,
    string PredictedClass,
    string ObservedClass,
    int AffectedNodes,
    int UnknownNodes,
    int FixAttempts,
    int PatchDebtCreated,
    int PatchDebtRemoved,
    int RegressionsFound,
    int FalsePositiveEdges,
    int MissingEdges,
    bool RollbackUsed,
    bool AcceptancePassed,
    long TimeToAcceptedChangeMs,
    IReadOnlyList<string> CalibrationFindings,
    DateTimeOffset ObservedAt);

public sealed record ChangeOutcomeResult(string Status, ChangeOutcomeTelemetry Telemetry, long Generation);

internal sealed record PersistedChangeImpact(ChangeBlastAnalysis Analysis, int TotalNodes, DateTimeOffset RecordedAt);

internal sealed record ChangePreflightReceipt(string RequestFingerprint, string ContractId, string ImpactId, string Classification);

internal sealed record ChangeScopeReceipt(
    string RequestFingerprint,
    string Status,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<StateKey> UndeclaredSharedFacts,
    IReadOnlyList<string> UntrackedPatchMechanisms);

using Hiker.Pro.State;

namespace Hiker.Pro.Execution;

public enum RuntimeLifecycle
{
    Queued,
    Starting,
    Running,
    Waiting,
    Paused,
    Completed,
    Failed,
    Interrupted,
    Cancelled
}

public sealed record RuntimeCapabilities(
    bool Pause,
    bool Resume,
    bool Checkpoint,
    bool Recover,
    bool ArtifactCollection,
    bool EventCollection);

public sealed record RuntimeStartRequest(
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string WorkspacePath,
    string ContextArtifactRef,
    string InstructionArtifactRef,
    long GoalRevision,
    string SourceFingerprint,
    IReadOnlyDictionary<string, string>? NonSecretOptions = null,
    string WorktreeId = "",
    IReadOnlyList<string>? PredictedAffectedScopes = null);

public sealed record RuntimeHandle(
    string RuntimeId,
    string AdapterId,
    RuntimeLifecycle Lifecycle,
    RuntimeProcessIdentity? ProcessIdentity,
    DateTimeOffset StartedAt,
    string RecoveryToken);

public sealed record RuntimeStatus(
    string RuntimeId,
    RuntimeLifecycle Lifecycle,
    RuntimeProcessIdentity? ProcessIdentity,
    DateTimeOffset ObservedAt,
    string DetailCode);

public sealed record RuntimeCheckpoint(
    string CheckpointId,
    string RuntimeId,
    string TaskId,
    string StateArtifactRef,
    string SourceFingerprint,
    DateTimeOffset CreatedAt);

public sealed record RuntimeCollectedOutput(
    string RuntimeId,
    string SummaryCode,
    IReadOnlyList<string> ArtifactRefs,
    bool Truncated);

public sealed record RuntimeCollectedEvent(
    string EventId,
    string RuntimeId,
    string Type,
    string SummaryCode,
    DateTimeOffset OccurredAt);

public sealed record RuntimeRecoveryRequest(
    RuntimeHandle PriorHandle,
    RuntimeCheckpoint Checkpoint,
    string HandoffArtifactRef,
    string WorkspaceFingerprint);

public interface IExecutionRuntimeAdapter
{
    string AdapterId { get; }
    RuntimeCapabilities Capabilities { get; }
    Task<RuntimeHandle> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeHandle> ResumeAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeHandle> PauseAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeHandle> InterruptAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeHandle> CancelAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeStatus> StatusAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeCheckpoint> CheckpointAsync(RuntimeHandle handle, string taskId, CancellationToken cancellationToken = default);
    Task<RuntimeCollectedOutput> CollectOutputAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> CollectArtifactsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeCollectedEvent>> CollectEventsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimeHandle> RecoverAsync(RuntimeRecoveryRequest request, CancellationToken cancellationToken = default);
    Task ShutdownAsync(RuntimeHandle handle, CancellationToken cancellationToken = default);
}

public sealed record RuntimeProcessIdentity(
    int ProcessId,
    DateTimeOffset StartTime,
    string ExecutablePathFingerprint,
    string HostFingerprint);

public sealed record RuntimeLease(
    string ProjectId,
    string TaskId,
    string RuntimeId,
    string AdapterId,
    RuntimeProcessIdentity? ProcessIdentity,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    string Status,
    long Epoch);

public sealed record RuntimeStartResult(string Status, RuntimeHandle Handle, long Generation);

public sealed record HandoffRequest(
    string OperationId,
    string ProjectId,
    string TaskId,
    RuntimeHandle CurrentHandle,
    IExecutionRuntimeAdapter CurrentAdapter,
    IExecutionRuntimeAdapter TargetAdapter,
    string TargetRuntimeId,
    string WorktreeId,
    string WorkspacePath,
    long GoalRevision,
    string SourceFingerprint,
    IReadOnlyList<string> RelevantStateRefs,
    IReadOnlyList<string> AcceptanceIds);

public sealed record HandoffResult(
    string Status,
    RuntimeHandle? TargetHandle,
    RuntimeCheckpoint? Checkpoint,
    ArtifactRecord? HandoffArtifact,
    long Generation);

public sealed record TaskRecoveryPlan(
    string OperationId,
    string ProjectId,
    string TaskId,
    RuntimeHandle PriorHandle,
    IExecutionRuntimeAdapter PriorAdapter,
    IExecutionRuntimeAdapter RecoveryAdapter,
    string RecoveryRuntimeId,
    string WorktreeId,
    RuntimeCheckpoint Checkpoint,
    string HandoffArtifactRef,
    string WorkspacePath,
    string ExpectedWorkspaceFingerprint,
    long GoalRevision,
    string SourceFingerprint);

public sealed record TaskRecoveryResult(string Status, RuntimeHandle? Handle, long Generation, string DetailCode);

public sealed record RuntimeCompletionRequest(
    string OperationId,
    string ProjectId,
    string TaskId,
    string WorktreeId,
    RuntimeHandle Handle,
    IExecutionRuntimeAdapter Adapter,
    RuntimeLifecycle Outcome,
    long GoalRevision,
    string SourceFingerprint);

public sealed record RuntimeCompletionResult(
    string Status,
    long Generation,
    RuntimeCollectedOutput? Output,
    IReadOnlyList<string> ArtifactRefs,
    IReadOnlyList<RuntimeCollectedEvent> Events);

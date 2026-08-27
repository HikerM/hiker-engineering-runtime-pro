using Hiker.Pro.Execution;
using Hiker.Pro.Artifacts;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.State;
using Hiker.Pro.Workspace;
using System.Text.Json;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class RuntimeCoordinatorTests
{
    [Fact]
    public async Task Start_ReservesThenActivatesOneWriterAcrossTaskLeaseAndOwnership()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var adapter = new TestRuntimeAdapter("test-runtime");
        var coordinator = new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap);
        var result = await coordinator.StartAsync("start-1", Start(project.Root, "runtime-1"), adapter);
        Assert.Equal("RUNNING", result.Status);
        Assert.Equal(RuntimeLifecycle.Running, result.Handle.Lifecycle);
        var task = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Task, "task-1"));
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        var owner = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Ownership, "task-1"));
        Assert.NotNull(task);
        Assert.Equal(task?.Generation, lease?.Generation);
        Assert.Equal(task?.Generation, owner?.Generation);
        Assert.Equal(1, adapter.StartCalls);
        Assert.Equal(1, adapter.ResumeCalls);
    }

    [Fact]
    public async Task Start_RejectsSecondWriterWhileLeaseIsActiveWithoutStartingAdapter()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var coordinator = new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap);
        Assert.Equal("RUNNING", (await coordinator.StartAsync("start-owner", Start(project.Root, "runtime-owner"), new TestRuntimeAdapter("adapter-owner"))).Status);
        var contender = new TestRuntimeAdapter("adapter-contender");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("start-contender", Start(project.Root, "runtime-contender"), contender));
        Assert.Equal(0, contender.StartCalls);
    }

    [Fact]
    public async Task Heartbeat_IsOwnedAndIdempotent()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var coordinator = new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap);
        var started = await coordinator.StartAsync("start-heartbeat", Start(project.Root, "runtime-1"), new TestRuntimeAdapter("adapter"));
        var heartbeatAt = DateTimeOffset.UtcNow;
        var first = await coordinator.HeartbeatAsync("heartbeat-1", "project-1", "task-1", "runtime-1", "source-1", started.Generation, heartbeatAt);
        var retry = await coordinator.HeartbeatAsync("heartbeat-1", "project-1", "task-1", "runtime-1", "source-1", started.Generation, heartbeatAt);
        Assert.True(first.Committed);
        Assert.Equal("IDEMPOTENT_REPLAY", retry.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HeartbeatAsync("heartbeat-2", "project-1", "task-1", "other-runtime", "source-1", first.CurrentGeneration, heartbeatAt));
    }

    [Fact]
    public async Task Handoff_PausesCheckpointsAndTransfersAllWriterFactsAtomically()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var inspector = new TestWorkspaceInspector(project.Root);
        var sourceAdapter = new TestRuntimeAdapter("source-adapter");
        var source = await new RuntimeCoordinator(project.Coordinator, inspector, new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("handoff-source", Start(project.Root, "runtime-source"), sourceAdapter);
        var targetAdapter = new TestRuntimeAdapter("target-adapter");
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var coordinator = new HandoffCoordinator(project.Coordinator, artifacts, inspector);
        var request = new HandoffRequest(
            "handoff-1",
            "project-1",
            "task-1",
            source.Handle,
            sourceAdapter,
            targetAdapter,
            "runtime-target",
            "worktree-task-1",
            project.Root,
            1,
            "source-1",
            new[] { "state://goal/current", "state://decision/approved" },
            new[] { "AC-1", "AC-2" });

        var result = await coordinator.HandoffAsync(request);
        Assert.Equal("RUNNING", result.Status);
        Assert.Equal("runtime-target", result.TargetHandle?.RuntimeId);
        Assert.Equal(1, sourceAdapter.PauseCalls);
        Assert.Equal(1, sourceAdapter.ShutdownCalls);
        Assert.Equal(1, targetAdapter.RecoverCalls);
        var task = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Task, "task-1"));
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        var owner = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Ownership, "task-1"));
        Assert.NotNull(task);
        Assert.Equal(task?.Generation, lease?.Generation);
        Assert.Equal(task?.Generation, owner?.Generation);
        Assert.Contains("runtime-target", task?.PayloadJson);
        Assert.NotNull(result.HandoffArtifact);
        using var reader = new StreamReader(artifacts.OpenRead(result.HandoffArtifact!));
        var handoff = await reader.ReadToEndAsync();
        Assert.Contains("hiker-minimal-handoff/v1", handoff);
        Assert.DoesNotContain("prompt", handoff, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chat", handoff, StringComparison.OrdinalIgnoreCase);

        var replay = await coordinator.HandoffAsync(request);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(1, sourceAdapter.PauseCalls);
        Assert.Equal(1, targetAdapter.RecoverCalls);
    }

    [Fact]
    public async Task Recovery_UsesValidatedCheckpointAndDoesNotReplayCompletedRecovery()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var inspector = new TestWorkspaceInspector(project.Root);
        var failedAdapter = new TestRuntimeAdapter("failed-adapter");
        var started = await new RuntimeCoordinator(project.Coordinator, inspector, new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("recover-source", Start(project.Root, "runtime-failed"), failedAdapter);
        var checkpoint = await failedAdapter.CheckpointAsync(started.Handle, "task-1");
        var checkpointPayload = $$"""
            {"checkpoint_id":"{{checkpoint.CheckpointId}}","runtime_id":"{{checkpoint.RuntimeId}}","task_id":"{{checkpoint.TaskId}}","state_artifact_ref":"{{checkpoint.StateArtifactRef}}","source_fingerprint":"{{checkpoint.SourceFingerprint}}","created_at":"{{checkpoint.CreatedAt:O}}"}
            """;
        Assert.True((await project.Store.CompareAndSwapAsync(new TransactionRequest(
            "recover-checkpoint",
            new StateMutation(
                new StateKey("project-1", EntityKinds.Checkpoint, checkpoint.CheckpointId),
                0,
                1,
                StateLifecycle.Valid,
                "RUNTIME_COORDINATOR",
                "3.0.0",
                checkpoint.SourceFingerprint,
                "deps-1",
                checkpointPayload,
                RetentionTier.Warm),
            "CHECKPOINT_CREATED",
            "CHECKPOINT_CREATED",
            "{}"))).Committed);
        _ = await failedAdapter.InterruptAsync(started.Handle);
        var recoveryAdapter = new TestRuntimeAdapter("recovery-adapter");
        var coordinator = new RecoveryCoordinator(project.Coordinator, inspector, new ProcessIdentityProbe());
        var plan = new TaskRecoveryPlan(
            "recover-1",
            "project-1",
            "task-1",
            started.Handle,
            failedAdapter,
            recoveryAdapter,
            "runtime-recovered",
            "worktree-task-1",
            checkpoint,
            "artifacts/handoff",
            project.Root,
            "workspace-1",
            1,
            "source-1");

        var result = await coordinator.RecoverAsync(plan);
        Assert.Equal("RUNNING", result.Status);
        Assert.Equal("runtime-recovered", result.Handle?.RuntimeId);
        Assert.Equal(1, recoveryAdapter.RecoverCalls);
        var replay = await coordinator.RecoverAsync(plan);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(1, recoveryAdapter.RecoverCalls);
    }

    [Fact]
    public async Task Recovery_BlocksOnWorkspaceDriftBeforeStartingSubstitute()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var normalInspector = new TestWorkspaceInspector(project.Root);
        var failedAdapter = new TestRuntimeAdapter("failed-adapter");
        var started = await new RuntimeCoordinator(project.Coordinator, normalInspector, new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("drift-source", Start(project.Root, "runtime-failed"), failedAdapter);
        var checkpoint = await failedAdapter.CheckpointAsync(started.Handle, "task-1");
        var payload = $$"""{"checkpoint_id":"{{checkpoint.CheckpointId}}","runtime_id":"{{checkpoint.RuntimeId}}","task_id":"{{checkpoint.TaskId}}","state_artifact_ref":"{{checkpoint.StateArtifactRef}}","source_fingerprint":"{{checkpoint.SourceFingerprint}}","created_at":"{{checkpoint.CreatedAt:O}}"}""";
        _ = await project.Store.CompareAndSwapAsync(new TransactionRequest(
            "drift-checkpoint",
            new StateMutation(new StateKey("project-1", EntityKinds.Checkpoint, checkpoint.CheckpointId), 0, 1, StateLifecycle.Valid, "RUNTIME_COORDINATOR", "3.0.0", checkpoint.SourceFingerprint, "deps", payload),
            "CHECKPOINT_CREATED", "CHECKPOINT_CREATED", "{}"));
        _ = await failedAdapter.InterruptAsync(started.Handle);
        var substitute = new TestRuntimeAdapter("substitute");
        var result = await new RecoveryCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root, "changed-workspace"), new ProcessIdentityProbe())
            .RecoverAsync(new TaskRecoveryPlan(
                "drift-recovery", "project-1", "task-1", started.Handle, failedAdapter, substitute, "runtime-new", "worktree-task-1", checkpoint,
                "artifacts/handoff", project.Root, "workspace-1", 1, "source-1"));
        Assert.Equal("REQUIRES_REVIEW", result.Status);
        Assert.Equal("WORKSPACE_DRIFT", result.DetailCode);
        Assert.Equal(0, substitute.StartCalls);
    }

    [Fact]
    public async Task WorktreeGate_AllowsDisjointScopesButRejectsOverlappingWriterScope()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var firstRoot = Path.Combine(project.Root, "worktree-a");
        var secondRoot = Path.Combine(project.Root, "worktree-b");
        var thirdRoot = Path.Combine(project.Root, "worktree-c");
        _ = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(firstRoot), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("scope-a", Start(firstRoot, "runtime-a", "task-a", "worktree-a", "modules/a"), new TestRuntimeAdapter("adapter-a"));
        var disjoint = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(secondRoot), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("scope-b", Start(secondRoot, "runtime-b", "task-b", "worktree-b", "modules/b"), new TestRuntimeAdapter("adapter-b"));
        Assert.Equal("RUNNING", disjoint.Status);
        var conflicting = new TestRuntimeAdapter("adapter-c");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(thirdRoot), new ProcessIdentityProbe(), project.Bootstrap)
                .StartAsync("scope-c", Start(thirdRoot, "runtime-c", "task-c", "worktree-c", "modules/a/api"), conflicting));
        Assert.Equal(0, conflicting.StartCalls);
    }

    [Fact]
    public async Task Completion_DrainsThenReleasesLeaseOwnershipAndWorktreeExactlyOnce()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var adapter = new TestRuntimeAdapter("completion-adapter");
        var started = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("completion-source", Start(project.Root, "runtime-complete"), adapter);
        var coordinator = new RuntimeLifecycleCoordinator(project.Coordinator);
        var request = new RuntimeCompletionRequest(
            "complete-1", "project-1", "task-1", "worktree-task-1", started.Handle, adapter,
            RuntimeLifecycle.Completed, 1, "source-1");
        var result = await coordinator.CompleteAsync(request);
        Assert.Equal("COMPLETED", result.Status);
        Assert.Equal(1, adapter.ShutdownCalls);
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        var ownership = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Ownership, "task-1"));
        var worktree = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Worktree, "worktree-task-1"));
        Assert.Equal(StateLifecycle.Archived, lease?.Lifecycle);
        Assert.Equal(StateLifecycle.Archived, ownership?.Lifecycle);
        Assert.Equal(StateLifecycle.Archived, worktree?.Lifecycle);
        var replay = await coordinator.CompleteAsync(request);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(1, adapter.ShutdownCalls);
    }

    [Fact]
    public async Task AdvancedDoctor_FailsClosedForExpiredDeadLeaseWithoutStealingIt()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var startClock = new TestTimeProvider(startTime);
        _ = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap, startClock)
            .StartAsync("doctor-source", Start(project.Root, "runtime-dead"), new TestRuntimeAdapter("doctor-adapter"));
        var doctor = new AdvancedDoctor(project.Store, new ProcessIdentityProbe(), new TestTimeProvider(startTime.AddMinutes(10)));
        var health = await doctor.CheckProjectAsync("project-1");
        Assert.Equal("BLOCKED", health.Status);
        Assert.Contains(health.Findings, static finding => finding.Code == "ORPHAN_RUNTIME_LEASE" && !finding.Repairable);
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        Assert.Equal(StateLifecycle.Valid, lease?.Lifecycle);
    }

    [Fact]
    public async Task ExpiredLease_WithSameLiveProcessIdentityCannotBeStolen()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var probe = new ProcessIdentityProbe();
        var identity = await probe.CurrentAsync();
        Assert.True(await probe.IsSameLiveProcessAsync(identity));
        Assert.False(await probe.IsSameLiveProcessAsync(identity with { StartTime = identity.StartTime.AddSeconds(-1) }));
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ownerAdapter = new TestRuntimeAdapter("live-owner") { ProcessIdentity = identity };
        _ = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), probe, project.Bootstrap, new TestTimeProvider(startTime))
            .StartAsync("live-owner-start", Start(project.Root, "runtime-live"), ownerAdapter);
        var contender = new TestRuntimeAdapter("contender");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), probe, project.Bootstrap, new TestTimeProvider(startTime.AddHours(1)))
                .StartAsync("live-owner-contender", Start(project.Root, "runtime-other"), contender));
        Assert.Equal(0, contender.StartCalls);
        var health = await new AdvancedDoctor(project.Store, probe, new TestTimeProvider(startTime.AddHours(1))).CheckProjectAsync("project-1");
        Assert.Contains(health.Findings, static finding => finding.Code == "EXPIRED_LIVE_OWNER");
    }

    [Fact]
    public async Task CodexCliAdapter_UsesRealInstalledCliAndStartsSuspendedWithoutModelCall()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var inputDirectory = Path.Combine(project.Layout.AiRoot, "runtime-input");
        Directory.CreateDirectory(inputDirectory);
        await File.WriteAllTextAsync(Path.Combine(inputDirectory, "instruction.txt"), "Return a bounded integration marker.");
        await using var adapter = new CodexCliRuntimeAdapter();
        var probe = await adapter.ProbeAsync();
        Assert.True(probe.Available);
        Assert.StartsWith("codex-cli ", probe.Version, StringComparison.Ordinal);
        var handle = await adapter.StartAsync(new RuntimeStartRequest(
            "project-1", "task-codex", "runtime-codex", project.Root,
            "runtime-input/instruction.txt", "runtime-input/instruction.txt", 1, "source-codex",
            WorktreeId: "worktree-codex", PredictedAffectedScopes: new[] { "src/codex" }));
        Assert.Equal(RuntimeLifecycle.Starting, handle.Lifecycle);
        Assert.Equal(RuntimeLifecycle.Starting, (await adapter.StatusAsync(handle)).Lifecycle);
        await adapter.ShutdownAsync(handle);
    }

    [Fact]
    public async Task FaultAfterLeaseReservation_LeavesSingleRecoverableWriterFact()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var adapter = new TestRuntimeAdapter("fault-after-reserve") { FailResume = true };
        var coordinator = new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("fault-reserve", Start(project.Root, "runtime-fault"), adapter));
        var leaseState = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        var ownershipState = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Ownership, "task-1"));
        Assert.NotNull(leaseState);
        Assert.Equal(leaseState?.Generation, ownershipState?.Generation);
        Assert.Equal("RESERVED", PayloadValue(leaseState!, "status"));
        Assert.Equal("runtime-fault", PayloadValue(ownershipState!, "writer_runtime_id"));
        var contender = new TestRuntimeAdapter("contender");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync("fault-contender", Start(project.Root, "runtime-contender"), contender));
        Assert.Equal(0, contender.StartCalls);
    }

    [Fact]
    public async Task FaultDuringHandoffRecovery_DoesNotCreateSecondWriterOrReplayTransfer()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var inspector = new TestWorkspaceInspector(project.Root);
        var sourceAdapter = new TestRuntimeAdapter("source");
        var started = await new RuntimeCoordinator(project.Coordinator, inspector, new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("fault-handoff-source", Start(project.Root, "runtime-source"), sourceAdapter);
        var targetAdapter = new TestRuntimeAdapter("target") { FailRecover = true };
        var coordinator = new HandoffCoordinator(project.Coordinator, new ContentAddressedArtifactStore(project.Layout, project.Coordinator), inspector);
        var request = new HandoffRequest(
            "fault-handoff", "project-1", "task-1", started.Handle, sourceAdapter, targetAdapter,
            "runtime-target", "worktree-task-1", project.Root, 1, "source-1",
            new[] { "state://goal/current" }, new[] { "AC-1" });
        var failed = await coordinator.HandoffAsync(request);
        Assert.Equal("RECOVERY_REQUIRED", failed.Status);
        var ownership = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.Ownership, "task-1"));
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        Assert.Equal("runtime-target", PayloadValue(ownership!, "writer_runtime_id"));
        Assert.Equal("runtime-target", PayloadValue(lease!, "runtime_id"));
        Assert.Equal(1, targetAdapter.RecoverCalls);
        var replay = await coordinator.HandoffAsync(request);
        Assert.Equal("RECOVERY_REQUIRED", replay.Status);
        Assert.Equal(1, targetAdapter.RecoverCalls);
    }

    [Fact]
    public async Task FaultDuringFinalRelease_StaysDrainingAndCannotDuplicateCompletion()
    {
        await using var project = await TestProject.CreateAsync("project-1");
        var adapter = new TestRuntimeAdapter("release-fault") { FailShutdown = true };
        var started = await new RuntimeCoordinator(project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap)
            .StartAsync("fault-release-source", Start(project.Root, "runtime-release"), adapter);
        var coordinator = new RuntimeLifecycleCoordinator(project.Coordinator);
        var request = new RuntimeCompletionRequest(
            "fault-release", "project-1", "task-1", "worktree-task-1", started.Handle,
            adapter, RuntimeLifecycle.Completed, 1, "source-1");
        var failed = await coordinator.CompleteAsync(request);
        Assert.Equal("RECOVERY_REQUIRED", failed.Status);
        var lease = await project.Store.ReadAsync(new StateKey("project-1", EntityKinds.RuntimeLease, "task-1"));
        Assert.Equal("DRAINING", PayloadValue(lease!, "status"));
        var retry = await coordinator.CompleteAsync(request);
        Assert.Equal("RECOVERY_REQUIRED", retry.Status);
        Assert.Equal(1, adapter.ShutdownCalls);
        Assert.Null(await project.Store.ReadOperationReceiptAsync("fault-release:release"));
    }

    private static RuntimeStartRequest Start(
        string root,
        string runtimeId,
        string taskId = "task-1",
        string worktreeId = "worktree-task-1",
        string scope = "src/task-1") => new(
        "project-1",
        taskId,
        runtimeId,
        root,
        "artifacts/context",
        "artifacts/instruction",
        1,
        "source-1",
        WorktreeId: worktreeId,
        PredictedAffectedScopes: new[] { scope });

    private static string? PayloadValue(StateSnapshot snapshot, string name)
    {
        using var document = JsonDocument.Parse(snapshot.PayloadJson);
        return document.RootElement.GetProperty(name).GetString();
    }
}

internal sealed class TestWorkspaceInspector(string root, string fingerprint = "workspace-1") : IWorkspaceInspector
{
    public Task<WorkspaceSnapshot> InspectAsync(string workspacePath, CancellationToken cancellationToken = default) => Task.FromResult(
        new WorkspaceSnapshot(Path.GetFullPath(root), Path.Combine(Path.GetFullPath(root), ".git"), "head-1", "main", false, fingerprint, DateTimeOffset.UtcNow));
}

internal sealed class TestRuntimeAdapter(string adapterId) : IExecutionRuntimeAdapter
{
    private readonly Dictionary<string, RuntimeLifecycle> _states = new(StringComparer.Ordinal);

    public string AdapterId { get; } = adapterId;
    public RuntimeCapabilities Capabilities { get; } = new(true, true, true, true, true, true);
    public int StartCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    public int PauseCalls { get; private set; }
    public int RecoverCalls { get; private set; }
    public int ShutdownCalls { get; private set; }
    public RuntimeProcessIdentity? ProcessIdentity { get; init; }
    public bool FailResume { get; init; }
    public bool FailRecover { get; init; }
    public bool FailShutdown { get; init; }

    public Task<RuntimeHandle> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken = default)
    {
        StartCalls++;
        _states[request.RuntimeId] = RuntimeLifecycle.Starting;
        return Task.FromResult(Handle(request.RuntimeId, RuntimeLifecycle.Starting));
    }

    public Task<RuntimeHandle> ResumeAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        ResumeCalls++;
        if (FailResume) throw new InvalidOperationException("Injected resume failure");
        _states[handle.RuntimeId] = RuntimeLifecycle.Running;
        return Task.FromResult(handle with { Lifecycle = RuntimeLifecycle.Running });
    }

    public Task<RuntimeHandle> PauseAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        PauseCalls++;
        _states[handle.RuntimeId] = RuntimeLifecycle.Paused;
        return Task.FromResult(handle with { Lifecycle = RuntimeLifecycle.Paused });
    }

    public Task<RuntimeHandle> InterruptAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        _states[handle.RuntimeId] = RuntimeLifecycle.Interrupted;
        return Task.FromResult(handle with { Lifecycle = RuntimeLifecycle.Interrupted });
    }

    public Task<RuntimeHandle> CancelAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        _states[handle.RuntimeId] = RuntimeLifecycle.Cancelled;
        return Task.FromResult(handle with { Lifecycle = RuntimeLifecycle.Cancelled });
    }

    public Task<RuntimeStatus> StatusAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeStatus(handle.RuntimeId, _states.GetValueOrDefault(handle.RuntimeId, RuntimeLifecycle.Interrupted), handle.ProcessIdentity, DateTimeOffset.UtcNow, "TEST"));

    public Task<RuntimeCheckpoint> CheckpointAsync(RuntimeHandle handle, string taskId, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeCheckpoint($"checkpoint-{handle.RuntimeId}", handle.RuntimeId, taskId, "artifacts/checkpoint", "checkpoint-fingerprint", DateTimeOffset.UtcNow));

    public Task<RuntimeCollectedOutput> CollectOutputAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult(
        new RuntimeCollectedOutput(handle.RuntimeId, "TEST_OUTPUT", Array.Empty<string>(), false));

    public Task<IReadOnlyList<string>> CollectArtifactsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<RuntimeCollectedEvent>> CollectEventsAsync(RuntimeHandle handle, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RuntimeCollectedEvent>>(Array.Empty<RuntimeCollectedEvent>());

    public Task<RuntimeHandle> RecoverAsync(RuntimeRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        RecoverCalls++;
        if (FailRecover) throw new InvalidOperationException("Injected recovery failure");
        _states[request.PriorHandle.RuntimeId] = RuntimeLifecycle.Running;
        return Task.FromResult(request.PriorHandle with { Lifecycle = RuntimeLifecycle.Running });
    }

    public Task ShutdownAsync(RuntimeHandle handle, CancellationToken cancellationToken = default)
    {
        ShutdownCalls++;
        if (FailShutdown) throw new InvalidOperationException("Injected shutdown failure");
        _states[handle.RuntimeId] = RuntimeLifecycle.Completed;
        return Task.CompletedTask;
    }

    private RuntimeHandle Handle(string runtimeId, RuntimeLifecycle lifecycle) => new(runtimeId, AdapterId, lifecycle, ProcessIdentity, DateTimeOffset.UtcNow, $"recovery-{runtimeId}");
}

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

using Hiker.Pro.Context;
using Hiker.Pro.Artifacts;
using Hiker.Pro.Environment;
using Hiker.Pro.Execution;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class EnvironmentControlPlaneTests
{
    [Fact]
    public async Task TaskEnvironmentCannotActivateWithoutExistingGoalAndTaskBinding()
    {
        await using var project = await TestProject.CreateAsync("environment-binding-required");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var registry = new EnvironmentProviderRegistry();
        registry.Register(provider);
        var selected = await new EnvironmentProfileCoordinator(project.Coordinator, registry).ResolveAndSelectAsync(
            "environment-binding-profile",
            project.ProjectId!,
            1,
            new EnvironmentDetectionRequest(project.Root, "development"));
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());

        var result = await new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources).ActivateAsync(
            InstanceRequest(project, selected.Profile!.ProfileId, "environment-binding-activate", Requirement("future-provider")));

        Assert.Equal(EnvironmentStatuses.ConfigurationIncompatible, result.Status);
        Assert.Contains("TASK_GOAL_BINDING_NOT_ACTIVE", result.Findings);
        Assert.Empty(await project.Store.ReadAsync(project.ProjectId!, EntityKinds.EnvironmentInstance, lifecycle: null, limit: 4));
    }

    [Fact]
    public async Task TaskEnvironmentAdoptsExistingInstanceAndReplaysWithoutMoreWrites()
    {
        await using var project = await TestProject.CreateAsync("environment-adopt");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var request = InstanceRequest(project, system.Profile.ProfileId, "activate-adopt", Requirement("future-provider"));

        var activated = await system.Instances.ActivateAsync(request);
        var generation = (await project.Store.GetMetricsAsync()).Generation;
        var replay = await system.Instances.ActivateAsync(request);

        Assert.Equal(EnvironmentStatuses.Active, activated.Status);
        Assert.True(activated.Instance?.AdoptedExisting);
        Assert.Equal(0, provider.ExecuteCalls);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        Assert.Equal(generation, (await project.Store.GetMetricsAsync()).Generation);
        Assert.Single(activated.Leases);
        Assert.Equal("task-1", activated.Instance?.TaskId);
        var taskEdges = await project.Store.QueryDependenciesAsync("environment-adopt", EntityKinds.Task, "task-1", outgoing: true);
        var goalEdges = await project.Store.QueryDependenciesAsync("environment-adopt", EntityKinds.Goal, "goal-1", outgoing: true);
        Assert.Contains(taskEdges, static edge => edge.ToKind == EntityKinds.EnvironmentInstance);
        Assert.DoesNotContain(goalEdges, static edge => edge.ToKind == EntityKinds.EnvironmentInstance);
    }

    [Fact]
    public async Task ProviderProvisioningRequiresExplicitApprovalForDestructiveAction()
    {
        await using var project = await TestProject.CreateAsync("environment-action");
        var provider = new TestEnvironmentProvider("future-provider")
        {
            CanAdopt = false,
            PlannedActions = new[] { Action("future-provider", destructive: true) }
        };
        var system = await CreateAsync(project, provider);
        var blocked = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "activate-blocked", Requirement("future-provider")));
        var approved = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "activate-approved", Requirement("future-provider")) with
            {
                ApprovedActionIds = new HashSet<string>(new[] { "action-start" }, StringComparer.Ordinal)
            });

        Assert.Equal("RECOVERY_REQUIRED", blocked.Status);
        Assert.Contains("DESTRUCTIVE_ACTION_REQUIRES_APPROVAL:action-start", blocked.Findings);
        Assert.Equal(EnvironmentStatuses.Active, approved.Status);
        Assert.Equal(1, provider.ExecuteCalls);
    }

    [Fact]
    public async Task SchedulerCombinesCodeDependencyEnvironmentResourceCapacityAndRisk()
    {
        await using var project = await TestProject.CreateAsync();
        var leases = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        _ = await leases.AcquireAsync(new EnvironmentResourceLeaseRequest(
            "seed-resource", "scheduler-project", "task-active", "runtime-active", "worktree-active", "instance-active",
            new[] { new ResourceRequirement("compute", "shared-pool", "future-provider", "host", ResourceSharingMode.Capacity, 2, 4) },
            null, TimeSpan.FromMinutes(5), "source"));
        var scheduler = new ConflictAwareScheduler(project.Coordinator);
        var plan = await scheduler.EvaluateAsync("scheduler-project", new[]
        {
            Candidate("task-capacity", new ResourceRequirement("compute", "shared-pool", "future-provider", "host", ResourceSharingMode.Capacity, 2, 4)),
            Candidate("task-exhausted", new ResourceRequirement("compute", "shared-pool", "future-provider", "host", ResourceSharingMode.Capacity, 1, 4)),
            Candidate("task-isolate", new ResourceRequirement("compute", "shared-pool", "future-provider", "host", ResourceSharingMode.Isolated, 1, 4, true))
        }, maximumWriterLanes: 3);
        var risk = await scheduler.EvaluateAsync("scheduler-project", new[]
        {
            new SchedulingCandidate("task-risk", "lane-risk", AgentLaneRole.Writer, EngineeringRisk.High, new[] { "security" },
                DependencyScopes: new[] { "contract:identity" }, ResourceRequirements: Array.Empty<ResourceRequirement>(),
                EnvironmentProfileId: "profile", EnvironmentInstanceId: "instance")
        });

        Assert.Contains(plan.Dispositions, item => item.TaskId == "task-capacity" && item.Status == "ADMITTED");
        Assert.Contains(plan.Dispositions, item => item.TaskId == "task-exhausted" && item.Status == "CAPACITY_EXHAUSTED");
        Assert.Contains(plan.Dispositions, item => item.TaskId == "task-isolate" && item.Status == "AUTO_ISOLATED");
        Assert.Contains(risk.Dispositions, item => item.TaskId == "task-risk" && item.Status == "RISK_ASSURANCE_REQUIRED");
    }

    [Fact]
    public void ResourcePolicyDistinguishesWaitIsolationCapacityAndHardConflictWithoutProviderBranches()
    {
        var activeExclusive = new[]
        {
            new EnvironmentResourceLease(
                "lease", "project", "task-a", "runtime-a", "worktree-a", "instance-a",
                new ResourceRequirement("database", "orders", "stateful-provider", "project", ResourceSharingMode.Exclusive),
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "ACTIVE")
        };
        var waiting = ResourceConflictPolicy.Evaluate(
            new ResourceRequirement("database", "orders", "stateful-provider", "project", ResourceSharingMode.Exclusive), activeExclusive);
        var isolated = ResourceConflictPolicy.Evaluate(
            new ResourceRequirement("database", "orders", "stateful-provider", "project", ResourceSharingMode.Isolated, 1, 1, true), activeExclusive);
        var activeShared = new[]
        {
            activeExclusive[0] with { Requirement = activeExclusive[0].Requirement with { SharingMode = ResourceSharingMode.Shared } }
        };
        var hard = ResourceConflictPolicy.Evaluate(
            new ResourceRequirement("database", "orders", "stateful-provider", "project", ResourceSharingMode.Shared, Destructive: true), activeShared);

        Assert.Equal(EnvironmentStatuses.WaitingResource, waiting.Status);
        Assert.Equal(EnvironmentStatuses.AutoIsolated, isolated.Status);
        Assert.Equal(EnvironmentStatuses.ResourceConflict, hard.Status);
    }

    [Fact]
    public async Task ProcessRegistryDeniesCrossTaskTerminationAndAdoptedProcessTermination()
    {
        await using var project = await TestProject.CreateAsync();
        var probe = new ProcessIdentityProbe();
        var registry = new EnvironmentProcessRegistry(project.Coordinator, probe);
        var identity = await probe.CurrentAsync();
        var registration = Registration(identity, adopted: true);
        Assert.True((await registry.RegisterAsync("process-register", registration, "source")).Committed);

        var crossTask = await registry.AssessTerminationAsync("process-project", "process-1", "task-other", "runtime-1");
        var adopted = await registry.AssessTerminationAsync("process-project", "process-1", "task-1", "runtime-1");

        Assert.False(crossTask.MayTerminate);
        Assert.Equal("CROSS_TASK_PROCESS_TERMINATION_DENIED", crossTask.ReasonCode);
        Assert.False(adopted.MayTerminate);
        Assert.Equal("ADOPTED_PROCESS_MUST_NOT_BE_TERMINATED", adopted.ReasonCode);
    }

    [Fact]
    public async Task ProcessRegistryNeverTerminatesReusedOrStaleProcessIdentity()
    {
        await using var project = await TestProject.CreateAsync();
        var probe = new ProcessIdentityProbe();
        var registry = new EnvironmentProcessRegistry(project.Coordinator, probe);
        var current = await probe.CurrentAsync();
        var stale = current with { StartTime = current.StartTime.AddTicks(-1) };
        var registration = Registration(stale, adopted: false);
        Assert.True((await registry.RegisterAsync("process-stale-register", registration, "source")).Committed);

        var decision = await registry.AssessTerminationAsync("process-project", "process-1", "task-1", "runtime-1");

        Assert.False(decision.MayTerminate);
        Assert.Equal("PROCESS_IDENTITY_NOT_LIVE", decision.ReasonCode);
    }

    [Fact]
    public async Task EnvironmentDoctorUsesCapabilitiesAndReportsHealthyBoundedState()
    {
        await using var project = await TestProject.CreateAsync("environment-doctor");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        _ = await system.Instances.ActivateAsync(InstanceRequest(project, system.Profile.ProfileId, "doctor-activate", Requirement("future-provider")));

        var report = await new EnvironmentDoctor(project.Coordinator, system.Registry, new ProcessIdentityProbe())
            .CheckAsync(project.Root, "environment-doctor");

        Assert.Equal(EnvironmentStatuses.Healthy, report.Status);
        Assert.Equal(1, report.ProviderCalls);
        Assert.False(report.FullSystemScan);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task RecoveryRevalidatesResourcesThenTransfersExistingInstanceWithoutProvisioningAgain()
    {
        await using var project = await TestProject.CreateAsync("environment-recovery");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true, RecoveryAllowed = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "recovery-activate", Requirement("future-provider")));
        var control = new EnvironmentRuntimeControl(project.Coordinator, system.Registry, system.Processes);

        var validated = await control.RevalidateAsync(
            project.Root, "environment-recovery", "task-1", "runtime-1", activated.Instance!.EnvironmentInstanceId);
        var transferred = await control.TransferRuntimeAsync(
            "environment-transfer", "environment-recovery", "task-1", "runtime-1", "runtime-2",
            activated.Instance.EnvironmentInstanceId, 1, "source");
        var current = await control.ValidateCurrentAsync(
            "environment-recovery", "task-1", "runtime-2", activated.Instance.EnvironmentInstanceId);

        Assert.True(validated.Allowed);
        Assert.Equal(1, provider.RecoverCalls);
        Assert.True(transferred.Committed);
        Assert.True(current.Allowed);
        Assert.Equal("runtime-2", current.Instance?.RuntimeId);
        Assert.Equal("runtime-2", Assert.Single(current.Leases).RuntimeId);
        Assert.Equal(0, provider.ExecuteCalls);
    }

    [Fact]
    public async Task AdoptedResourcesReleaseLocalStateWithoutStoppingExternalEnvironment()
    {
        await using var project = await TestProject.CreateAsync("environment-release");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "release-activate", Requirement("future-provider")));
        var release = new EnvironmentReleaseCoordinator(project.Coordinator, system.Registry, system.Resources, system.Processes);

        var result = await release.ReleaseAsync(new EnvironmentReleaseRequest(
            "release-environment", "environment-release", "task-1", "runtime-1", activated.Instance!.EnvironmentInstanceId, "source"));
        var replay = await release.ReleaseAsync(new EnvironmentReleaseRequest(
            "release-environment", "environment-release", "task-1", "runtime-1", activated.Instance.EnvironmentInstanceId, "source"));

        Assert.Equal("RELEASED", result.Status);
        Assert.True(result.AdoptedResourcesPreserved);
        Assert.Equal(0, provider.ReleaseCalls);
        Assert.Equal("ALREADY_RELEASED", replay.Status);
        Assert.Empty(await system.Resources.ActiveAsync("environment-release"));
    }

    [Fact]
    public async Task ContextIncludesOnlyTaskBoundEnvironmentFactsAndIgnoresColdHistory()
    {
        await using var project = await TestProject.CreateAsync("environment-context");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "context-activate", Requirement("future-provider")));
        var resolver = new ContextResolver(project.Store);
        var before = await resolver.ResolveAsync(new ContextRequest("environment-context", "task-1", "goal-1"));
        await SeedColdEnvironmentHistoryAsync(project, 96);
        var after = await resolver.ResolveAsync(new ContextRequest("environment-context", "task-1", "goal-1"));

        Assert.Contains(after.Items, item => item.Kind == EntityKinds.EnvironmentProfile && item.EntityId == system.Profile.ProfileId);
        Assert.Contains(after.Items, item => item.Kind == EntityKinds.EnvironmentInstance && item.EntityId == activated.Instance!.EnvironmentInstanceId);
        Assert.Contains(after.Items, item => item.Kind == EntityKinds.ResourceLease);
        Assert.Equal(before.StateReads, after.StateReads);
        Assert.Equal(before.EncodedBytes, after.EncodedBytes);
        Assert.DoesNotContain(after.Items, item => item.EntityId.StartsWith("cold-environment-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyntheticFutureProviderNeedsNoSchedulerOrStateEngineChange()
    {
        await using var project = await TestProject.CreateAsync("future-provider-project");
        var provider = new TestEnvironmentProvider("provider-contract-v99") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "future-activate", Requirement("provider-contract-v99")));

        Assert.Equal(EnvironmentStatuses.Active, activated.Status);
        Assert.Equal("provider-contract-v99", Assert.Single(activated.Instance!.Bindings).ProviderId);
        Assert.Equal("provider-contract/v99", provider.Descriptor.ContractVersion);
    }

    [Fact]
    public async Task RuntimeStartUsesActiveTaskEnvironmentAndBlocksMismatchedRuntimeBeforeAdapterStart()
    {
        await using var project = await TestProject.CreateAsync("environment-runtime-start");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "runtime-environment", Requirement("future-provider")));
        var control = new EnvironmentRuntimeControl(project.Coordinator, system.Registry, system.Processes);
        var runtime = new RuntimeCoordinator(
            project.Coordinator,
            new TestWorkspaceInspector(project.Root),
            new ProcessIdentityProbe(),
            project.Bootstrap,
            environmentGate: control);
        var rejectedAdapter = new TestRuntimeAdapter("rejected");
        var rejected = StartRequest(project.ProjectId!, project.Root, "runtime-other", system.Profile.ProfileId, activated.Instance!.EnvironmentInstanceId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync("runtime-rejected", rejected, rejectedAdapter));
        Assert.Equal(0, rejectedAdapter.StartCalls);
        var acceptedAdapter = new TestRuntimeAdapter("accepted");
        var accepted = await runtime.StartAsync(
            "runtime-accepted",
            StartRequest(project.ProjectId!, project.Root, "runtime-1", system.Profile.ProfileId, activated.Instance.EnvironmentInstanceId),
            acceptedAdapter);
        Assert.Equal("RUNNING", accepted.Status);
        Assert.Equal(1, acceptedAdapter.StartCalls);
    }

    [Fact]
    public async Task EnvironmentBoundHandoffTransfersSameInstanceAndLeasesToTargetRuntime()
    {
        await using var project = await TestProject.CreateAsync("environment-handoff");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var environmentRequest = InstanceRequest(project, system.Profile.ProfileId, "handoff-environment", Requirement("future-provider")) with
        {
            RuntimeId = "runtime-source"
        };
        var activated = await system.Instances.ActivateAsync(environmentRequest);
        var control = new EnvironmentRuntimeControl(project.Coordinator, system.Registry, system.Processes);
        var inspector = new TestWorkspaceInspector(project.Root);
        var sourceAdapter = new TestRuntimeAdapter("source-adapter");
        var source = await new RuntimeCoordinator(
            project.Coordinator, inspector, new ProcessIdentityProbe(), project.Bootstrap, environmentGate: control)
            .StartAsync("handoff-runtime-source", StartRequest(
                project.ProjectId!, project.Root, "runtime-source", system.Profile.ProfileId, activated.Instance!.EnvironmentInstanceId), sourceAdapter);
        var targetAdapter = new TestRuntimeAdapter("target-adapter");
        var artifacts = new ContentAddressedArtifactStore(project.Layout, project.Coordinator);
        var handoff = await new HandoffCoordinator(project.Coordinator, artifacts, inspector, environmentGate: control).HandoffAsync(new HandoffRequest(
            "environment-handoff",
            project.ProjectId!,
            "task-1",
            source.Handle,
            sourceAdapter,
            targetAdapter,
            "runtime-target",
            "worktree-1",
            project.Root,
            1,
            "source",
            Array.Empty<string>(),
            Array.Empty<string>(),
            system.Profile.ProfileId,
            activated.Instance.EnvironmentInstanceId));
        var current = await control.ValidateCurrentAsync(
            project.ProjectId!, "task-1", "runtime-target", activated.Instance.EnvironmentInstanceId);

        Assert.Equal("RUNNING", handoff.Status);
        Assert.True(current.Allowed);
        Assert.Equal("runtime-target", current.Instance?.RuntimeId);
        Assert.All(current.Leases, static lease => Assert.Equal("runtime-target", lease.RuntimeId));
        Assert.Equal(1, targetAdapter.RecoverCalls);
    }

    [Fact]
    public async Task CrashRecoveryRevalidatesProviderResourcesBeforeRebindingEnvironment()
    {
        await using var project = await TestProject.CreateAsync("environment-crash-recovery");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true, RecoveryAllowed = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(InstanceRequest(
            project, system.Profile.ProfileId, "crash-environment", Requirement("future-provider")) with
        {
            RuntimeId = "runtime-failed"
        });
        var control = new EnvironmentRuntimeControl(project.Coordinator, system.Registry, system.Processes);
        var inspector = new TestWorkspaceInspector(project.Root);
        var failedAdapter = new TestRuntimeAdapter("failed-adapter");
        var started = await new RuntimeCoordinator(
            project.Coordinator, inspector, new ProcessIdentityProbe(), project.Bootstrap, environmentGate: control)
            .StartAsync("crash-runtime-source", StartRequest(
                project.ProjectId!, project.Root, "runtime-failed", system.Profile.ProfileId, activated.Instance!.EnvironmentInstanceId), failedAdapter);
        var checkpoint = await failedAdapter.CheckpointAsync(started.Handle, "task-1");
        var checkpointPayload = $$"""
            {"checkpoint_id":"{{checkpoint.CheckpointId}}","runtime_id":"{{checkpoint.RuntimeId}}","task_id":"{{checkpoint.TaskId}}","state_artifact_ref":"{{checkpoint.StateArtifactRef}}","source_fingerprint":"{{checkpoint.SourceFingerprint}}","created_at":"{{checkpoint.CreatedAt:O}}"}
            """;
        Assert.True((await project.Store.CompareAndSwapAsync(new TransactionRequest(
            "environment-crash-checkpoint",
            new StateMutation(
                new StateKey(project.ProjectId!, EntityKinds.Checkpoint, checkpoint.CheckpointId),
                0, 1, StateLifecycle.Valid, "RUNTIME_COORDINATOR", "3.0.0", checkpoint.SourceFingerprint, "deps", checkpointPayload, RetentionTier.Warm),
            "CHECKPOINT_CREATED", "CHECKPOINT_CREATED", "{}"))).Committed);
        _ = await failedAdapter.InterruptAsync(started.Handle);
        var recoveredAdapter = new TestRuntimeAdapter("recovered-adapter");
        var recovered = await new RecoveryCoordinator(
            project.Coordinator, inspector, new ProcessIdentityProbe(), environmentGate: control).RecoverAsync(new TaskRecoveryPlan(
                "environment-crash-recover",
                project.ProjectId!,
                "task-1",
                started.Handle,
                failedAdapter,
                recoveredAdapter,
                "runtime-recovered",
                "worktree-1",
                checkpoint,
                "artifacts/handoff",
                project.Root,
                "workspace-1",
                1,
                "source",
                activated.Instance.EnvironmentInstanceId));
        var environment = await control.ValidateCurrentAsync(
            project.ProjectId!, "task-1", "runtime-recovered", activated.Instance.EnvironmentInstanceId);

        Assert.Equal("RUNNING", recovered.Status);
        Assert.Equal(1, provider.RecoverCalls);
        Assert.True(environment.Allowed);
        Assert.Equal("runtime-recovered", environment.Instance?.RuntimeId);
    }

    [Fact]
    public async Task RuntimeCompletionReleasesTaskEnvironmentBeforeWriterFactsBecomeTerminal()
    {
        await using var project = await TestProject.CreateAsync("environment-completion");
        var provider = new TestEnvironmentProvider("future-provider") { CanAdopt = true };
        var system = await CreateAsync(project, provider);
        var activated = await system.Instances.ActivateAsync(
            InstanceRequest(project, system.Profile.ProfileId, "completion-environment", Requirement("future-provider")));
        var control = new EnvironmentRuntimeControl(project.Coordinator, system.Registry, system.Processes);
        var adapter = new TestRuntimeAdapter("completion-adapter");
        var started = await new RuntimeCoordinator(
            project.Coordinator, new TestWorkspaceInspector(project.Root), new ProcessIdentityProbe(), project.Bootstrap, environmentGate: control)
            .StartAsync("completion-runtime", StartRequest(
                project.ProjectId!, project.Root, "runtime-1", system.Profile.ProfileId, activated.Instance!.EnvironmentInstanceId), adapter);
        var release = new EnvironmentReleaseCoordinator(project.Coordinator, system.Registry, system.Resources, system.Processes);

        var completed = await new RuntimeLifecycleCoordinator(project.Coordinator, environmentRelease: release).CompleteAsync(new RuntimeCompletionRequest(
            "environment-complete", project.ProjectId!, "task-1", "worktree-1", started.Handle, adapter,
            RuntimeLifecycle.Completed, 1, "source", activated.Instance.EnvironmentInstanceId));

        Assert.Equal("COMPLETED", completed.Status);
        Assert.Empty(await system.Resources.ActiveAsync(project.ProjectId!));
        var instance = await project.Store.ReadAsync(new StateKey(project.ProjectId!, EntityKinds.EnvironmentInstance, activated.Instance.EnvironmentInstanceId));
        Assert.Equal(StateLifecycle.Archived, instance?.Lifecycle);
    }

    private static SchedulingCandidate Candidate(string taskId, ResourceRequirement requirement) => new(
        taskId,
        $"lane-{taskId}",
        AgentLaneRole.Writer,
        EngineeringRisk.Medium,
        new[] { $"src/{taskId}" },
        DependencyScopes: new[] { $"dependency/{taskId}" },
        ResourceRequirements: new[] { requirement },
        EnvironmentProfileId: "profile",
        EnvironmentInstanceId: "instance");

    private static RuntimeStartRequest StartRequest(
        string projectId,
        string root,
        string runtimeId,
        string profileId,
        string instanceId) => new(
        projectId,
        "task-1",
        runtimeId,
        root,
        "artifacts/context",
        "artifacts/instruction",
        1,
        "source",
        WorktreeId: "worktree-1",
        PredictedAffectedScopes: new[] { "src/task" },
        EnvironmentProfileId: profileId,
        EnvironmentInstanceId: instanceId);

    private static async Task<EnvironmentSystem> CreateAsync(TestProject project, params TestEnvironmentProvider[] providers)
    {
        await SeedGoalTaskAsync(project);
        var registry = new EnvironmentProviderRegistry();
        foreach (var provider in providers) registry.Register(provider);
        var profiles = new EnvironmentProfileCoordinator(project.Coordinator, registry);
        var selected = await profiles.ResolveAndSelectAsync(
            $"select-{providers[0].Descriptor.ProviderId}",
            project.ProjectId!,
            1,
            new EnvironmentDetectionRequest(project.Root, "development"));
        Assert.Equal(EnvironmentStatuses.Selected, selected.Profile?.Status);
        var resources = new EnvironmentResourceLeaseCoordinator(project.Coordinator, new ProcessIdentityProbe());
        var processes = new EnvironmentProcessRegistry(project.Coordinator, new ProcessIdentityProbe());
        return new EnvironmentSystem(
            registry,
            selected.Profile!,
            resources,
            processes,
            new EnvironmentInstanceCoordinator(project.Coordinator, registry, resources));
    }

    private static EnvironmentInstanceRequest InstanceRequest(
        TestProject project,
        string profileId,
        string operationId,
        params ResourceRequirement[] requirements) => new(
        operationId,
        project.Root,
        project.ProjectId!,
        "goal-1",
        1,
        "task-1",
        "worktree-1",
        "runtime-1",
        profileId,
        requirements,
        new HashSet<string>(StringComparer.Ordinal),
        null,
        TimeSpan.FromMinutes(5),
        "source");

    private static ResourceRequirement Requirement(string providerId) => new(
        "future-resource", "resource-main", providerId, "project", ResourceSharingMode.Shared, 1, 4, true);

    private static EnvironmentAction Action(string providerId, bool destructive) => new(
        "action-start", providerId, "node-main", "start", new Dictionary<string, string> { ["purpose"] = "test" }, destructive, destructive ? "HIGH" : "LOW");

    private static ServiceProcessRegistration Registration(RuntimeProcessIdentity identity, bool adopted) => new(
        "process-1", "process-project", "goal-1", "task-1", "runtime-1", "instance-1", "worktree-1", "future-provider",
        Array.Empty<string>(), identity, !adopted, adopted, EnvironmentStatuses.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static async Task SeedGoalTaskAsync(TestProject project)
    {
        var generation = (await project.Store.GetMetricsAsync()).Generation;
        Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            "environment-goal-task",
            new[]
            {
                Mutation(project.ProjectId!, EntityKinds.Goal, "goal-1", "{\"status\":\"ACTIVE\"}"),
                Mutation(project.ProjectId!, EntityKinds.Task, "task-1", "{\"status\":\"RUNNING\"}")
            },
            "CONTEXT_SEEDED",
            "GOAL_TASK_SEEDED",
            $"{{\"before_generation\":{generation}}}"))).Committed);
    }

    private static async Task SeedColdEnvironmentHistoryAsync(TestProject project, int count)
    {
        for (var batch = 0; batch < count / 32; batch++)
        {
            var mutations = Enumerable.Range(batch * 32, 32).Select(index =>
                Mutation(project.ProjectId!, EntityKinds.EnvironmentInstance, $"cold-environment-{index:D3}", "{\"status\":\"RELEASED\"}", StateLifecycle.Archived, RetentionTier.Cold)).ToArray();
            Assert.True((await project.Store.CompareAndSwapBatchAsync(new BatchTransactionRequest(
                $"environment-cold-{batch}", mutations, "ENVIRONMENT_ARCHIVED", "COLD_HISTORY", "{}"))).Committed);
        }
    }

    private static StateMutation Mutation(
        string projectId,
        string kind,
        string id,
        string payload,
        StateLifecycle lifecycle = StateLifecycle.Valid,
        RetentionTier retention = RetentionTier.Hot) => new(
        new StateKey(projectId, kind, id), 0, 1, lifecycle, "TEST", "3.0.0", "source", "dependency", payload, retention);

    private sealed record EnvironmentSystem(
        EnvironmentProviderRegistry Registry,
        EnvironmentProfile Profile,
        EnvironmentResourceLeaseCoordinator Resources,
        EnvironmentProcessRegistry Processes,
        EnvironmentInstanceCoordinator Instances);

    private sealed class TestEnvironmentProvider(string providerId) : IEnvironmentProvider
    {
        public EnvironmentProviderDescriptor Descriptor { get; } = new(
            providerId,
            "provider-contract/v99",
            new[] { "detect", "inspect", "validate", "plan", "attach", "allocate", "provision", "execute", "observe", "health", "recover", "release" },
            new[] { "start", "release-resource" },
            new[] { "future-resource", "compute" },
            Array.Empty<string>(),
            new[] { "start" });

        public bool CanAdopt { get; set; }
        public bool RecoveryAllowed { get; set; } = true;
        public IReadOnlyList<EnvironmentAction> PlannedActions { get; set; } = Array.Empty<EnvironmentAction>();
        public int ExecuteCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<IReadOnlyList<EnvironmentCandidate>> DetectAsync(EnvironmentDetectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnvironmentCandidate>>(new[] { Candidate(request.Purpose) });

        public Task<EnvironmentValidationResult> ValidateAsync(EnvironmentValidationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnvironmentValidationResult(EnvironmentStatuses.Verified, true, EnvironmentFingerprint.Calculate(request.Candidate), request.Candidate.EvidenceRefs, Array.Empty<string>()));

        public Task<IReadOnlyList<EnvironmentObservation>> InspectAsync(EnvironmentInspectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EnvironmentObservation>>(new[]
            {
                new EnvironmentObservation(EnvironmentStatuses.Healthy, providerId, "node-main", "observation", new Dictionary<string, string> { ["state"] = "observed" }, new[] { "resource-main" }, CanAdopt, DateTimeOffset.UtcNow)
            });

        public Task<EnvironmentPlan> PlanAsync(EnvironmentPlanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnvironmentPlan("PLANNED", PlannedActions, request.Requirements, CanAdopt, Array.Empty<string>()));

        public Task<EnvironmentActionResult> ExecuteActionAsync(EnvironmentAction action, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            return Task.FromResult(new EnvironmentActionResult("COMPLETED", action.ActionId, new Dictionary<string, string> { ["status"] = "started" }, new[] { "resource-main" }, "evidence-action"));
        }

        public Task<EnvironmentHealthResult> HealthAsync(EnvironmentHealthRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EnvironmentHealthResult(EnvironmentStatuses.Healthy, Array.Empty<string>(), request.NodeIds, "health-observation"));

        public Task<EnvironmentRecoveryResult> RecoverAsync(EnvironmentRecoveryRequest request, CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            return Task.FromResult(new EnvironmentRecoveryResult(
                RecoveryAllowed ? EnvironmentStatuses.Healthy : "RECOVERY_REQUIRED",
                RecoveryAllowed,
                RecoveryAllowed,
                RecoveryAllowed ? Array.Empty<string>() : new[] { "RESOURCE_REVALIDATION_FAILED" }));
        }

        public Task<EnvironmentActionResult> ReleaseAsync(EnvironmentAction action, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Task.FromResult(new EnvironmentActionResult("RELEASED", action.ActionId, new Dictionary<string, string> { ["status"] = "released" }, Array.Empty<string>(), "evidence-release"));
        }

        private EnvironmentCandidate Candidate(string purpose) => new(
            $"candidate-{providerId}",
            providerId,
            purpose,
            new EnvironmentGraph(
                new[]
                {
                    new EnvironmentNode(
                        "node-main", providerId, purpose, Descriptor.SupportedCapabilities, Descriptor.SupportedActions,
                        new[] { new EnvironmentResourceDescriptor("future-resource", "resource-main", providerId, "project", new Dictionary<string, string> { ["class"] = "synthetic" }, ResourceSharingMode.Shared, 4, true, false) },
                        new Dictionary<string, string> { ["provider_contract"] = Descriptor.ContractVersion },
                        Array.Empty<SecretReference>())
                },
                Array.Empty<EnvironmentEdge>()),
            new Dictionary<string, string> { ["runtime"] = "future-99" },
            new[] { "health" },
            new[] { "attach" },
            new[] { "evidence:synthetic" },
            $"source-{providerId}");
    }
}

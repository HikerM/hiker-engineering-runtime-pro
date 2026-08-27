using Hiker.Pro.ChangeIntelligence;
using Hiker.Pro.Graph;
using Hiker.Pro.State;
using Xunit;

namespace Hiker.Pro.Tests;

public sealed class ChangeGovernanceTests
{
    [Fact]
    public async Task ThreeFailedAttemptsWithFallbackDisablePatchingAndRequireRootCauseMode()
    {
        await using var project = await TestProject.CreateAsync("change-loop");
        var preflight = await PrepareAsync(project, "loop-change", "loop-contract");
        var coordinator = new ChangeLoopCoordinator(project.Coordinator);
        _ = await coordinator.RecordAttemptAsync("attempt-op-1", project.ProjectId!, "loop-change", 1, Attempt("attempt-1"), "source");
        _ = await coordinator.RecordAttemptAsync("attempt-op-2", project.ProjectId!, "loop-change", 1, Attempt("attempt-2"), "source");
        var third = await coordinator.RecordAttemptAsync("attempt-op-3", project.ProjectId!, "loop-change", 1, Attempt("attempt-3", fallback: 1), "source");

        Assert.Equal(ChangeControlStatuses.FixLoopConfirmed, third.Status);
        Assert.False(third.PatchingAllowed);
        Assert.True(third.RootCauseRequired);
        var probe = await new ChangeRiskProbe(project.Coordinator).ProbeAsync(ChangeRiskProbeTests.Probe(project, "loop-change", EntityKinds.Contract, "shared-contract"));
        Assert.Equal(ChangeRiskClasses.Systemic, probe.Classification);
        Assert.False(probe.PatchingAllowed);
        var plan = await coordinator.BuildRootCausePlanAsync(
            project.ProjectId!, "loop-change", "loop-contract", "SYMPTOM_REPEATS", "SHARED_INVARIANT_BROKEN",
            new[] { "must-preserve-contract" },
            preflight.Blast!.Nodes.Where(static node => node.Distance > 0).Select(static node => node.Key).ToArray(),
            new[] { "acceptance-primary", "existing-behavior" });
        Assert.Equal(ChangeControlStatuses.RootCauseRequired, plan.Status);
        Assert.Equal(3, plan.PriorFailedAttempts.Count);
        Assert.Contains("fallback-rejected", plan.RejectedAlternatives);
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.BuildRootCausePlanAsync(
            project.ProjectId!, "loop-change", "loop-contract", "SYMPTOM_REPEATS", "SHARED_INVARIANT_BROKEN",
            Array.Empty<string>(), new[] { new StateKey("another-project", EntityKinds.Service, "consumer") }, new[] { "acceptance-primary" }));

        var reanalysis = await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "loop-root-cause-preflight",
            ChangeRiskProbeTests.Probe(project, "loop-change", EntityKinds.Contract, "shared-contract"),
            ChangeRiskProbeTests.Contract(project, "loop-contract", "loop-change", "contract/shared-contract")));
        Assert.Equal(ChangeControlStatuses.Ready, reanalysis.Status);
        Assert.Equal(ChangeRiskClasses.Systemic, reanalysis.Blast?.Classification);
        Assert.False(reanalysis.Policy.PatchingAllowed);
        Assert.True(reanalysis.Policy.RootCauseModeRequired);
    }

    [Fact]
    public async Task FallbackRequiresExplicitPatchDebtAndOpenDebtBlocksAcceptance()
    {
        await using var project = await TestProject.CreateAsync("change-debt");
        _ = await PrepareAsync(project, "debt-change", "debt-contract");
        var debts = new PatchDebtCoordinator(project.Coordinator);
        var recorded = await debts.RecordAsync(
            "debt-record",
            project.ProjectId!,
            new PatchDebtFact(
                "debt-1", "debt-change", "attempt-2", 1, "fallback", "TEMPORARY_COMPATIBILITY",
                new[] { "contract/shared-contract" }, "shared-contract", "remove-after-consumer-upgrade", "review-before-release",
                Array.Empty<string>(), "OPEN", default),
            "debt-contract",
            "source");
        Assert.True(recorded.Committed);
        var replay = await debts.RecordAsync(
            "debt-record",
            project.ProjectId!,
            new PatchDebtFact(
                "debt-1", "debt-change", "attempt-2", 1, "fallback", "TEMPORARY_COMPATIBILITY",
                new[] { "contract/shared-contract" }, "shared-contract", "remove-after-consumer-upgrade", "review-before-release",
                Array.Empty<string>(), "OPEN", default),
            "debt-contract",
            "source");
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        var guard = new ChangeExecutionGuard(project.Coordinator, debts);
        var verification = await guard.VerifyAsync(Verification(project, "debt-change", "debt-contract"));
        Assert.Equal("PATCH_DEBT_REVIEW_REQUIRED", verification.Status);
        Assert.False(verification.Accepted);

        var resolved = await debts.ResolveAsync("debt-accept", project.ProjectId!, "debt-1", 1, "ACCEPTED_DEBT", new[] { "debt-review-evidence" }, "source");
        Assert.True(resolved.Committed);
        var accepted = await guard.VerifyAsync(Verification(project, "debt-change", "debt-contract"));
        Assert.Equal("ACCEPTED", accepted.Status);
        Assert.True(accepted.Accepted);
    }

    [Fact]
    public async Task ScopeExpansionAndUntrackedTemporaryMechanismBreachContract()
    {
        await using var project = await TestProject.CreateAsync("change-scope");
        _ = await PrepareAsync(project, "scope-change", "scope-contract", "src/auth");
        var result = await new ChangeExecutionGuard(project.Coordinator).EvaluateScopeAsync(new ChangeScopeEvaluationRequest(
            "scope-breach",
            project.ProjectId!,
            "scope-contract",
            new[] { "src/auth", "database/session-schema" },
            new[] { new StateKey(project.ProjectId!, EntityKinds.Contract, "new-shared-contract") },
            new[] { "temporary-bypass" },
            Array.Empty<string>(),
            "source"));

        Assert.Equal(ChangeControlStatuses.ContractBreached, result.Status);
        Assert.Contains("database/session-schema", result.OutOfScope);
        Assert.Contains(result.UndeclaredSharedFacts, static key => key.EntityId == "new-shared-contract");
        Assert.Contains("temporary-bypass", result.UntrackedPatchMechanisms);
        var replay = await new ChangeExecutionGuard(project.Coordinator).EvaluateScopeAsync(new ChangeScopeEvaluationRequest(
            "scope-breach",
            project.ProjectId!,
            "scope-contract",
            new[] { "src/auth", "database/session-schema" },
            new[] { new StateKey(project.ProjectId!, EntityKinds.Contract, "new-shared-contract") },
            new[] { "temporary-bypass" },
            Array.Empty<string>(),
            "source"));
        Assert.Equal(ChangeControlStatuses.ContractBreached, replay.Status);
        var contract = await project.Store.ReadAsync(new StateKey(project.ProjectId!, EntityKinds.ChangeContract, "scope-contract"));
        Assert.Equal(StateLifecycle.Stale, contract?.Lifecycle);
    }

    [Fact]
    public async Task PrimaryFixPassingCannotHideInvariantRegression()
    {
        await using var project = await TestProject.CreateAsync("change-invariant");
        _ = await PrepareAsync(project, "invariant-change", "invariant-contract");
        var request = Verification(project, "invariant-change", "invariant-contract") with
        {
            InvariantRegression = new[] { new ChangeVerificationOutcome("existing-behavior", "FAIL", "REGRESSION") }
        };

        var result = await new ChangeExecutionGuard(project.Coordinator).VerifyAsync(request);

        Assert.Equal(ChangeControlStatuses.InvariantGuardFailed, result.Status);
        Assert.False(result.Accepted);
        Assert.Contains("existing-behavior", result.Regressions);
    }

    [Fact]
    public async Task ObservedRuntimeImpactMissingFromGraphCreatesCalibrationFindingWithoutAuthorityMutation()
    {
        await using var project = await TestProject.CreateAsync("change-observe");
        var preflight = await PrepareAsync(project, "observe-change", "observe-contract");
        var generationBefore = (await project.Store.GetMetricsAsync()).Generation;
        var result = await new ChangeOutcomeCoordinator(project.Coordinator).RecordAsync(new ChangeOutcomeRequest(
            "outcome-record",
            project.ProjectId!,
            "observe-change",
            "observe-contract",
            preflight.Blast!,
            ChangeRiskClasses.CrossCutting,
            preflight.Blast!.Nodes.Where(static node => node.Disposition == ImpactDisposition.Affected).Select(static node => node.Key)
                .Append(new StateKey(project.ProjectId!, EntityKinds.ServiceProcess, "runtime-worker")).ToArray(),
            preflight.Blast.Dimensions.Append(BlastDimensions.Runtime).Distinct(StringComparer.Ordinal).ToArray(),
            1, 0, 0, 0, false, true, 1200, "source"));

        Assert.Equal("RECORDED", result.Status);
        Assert.Equal(1, result.Telemetry.MissingEdges);
        Assert.Contains("MISSING_DEPENDENCY_EDGE", result.Telemetry.CalibrationFindings);
        Assert.True((await project.Store.GetMetricsAsync()).Generation > generationBefore);
        Assert.Empty(await project.Store.QueryDependenciesAsync(project.ProjectId!, EntityKinds.ServiceProcess, "runtime-worker", outgoing: true));
        var replay = await new ChangeOutcomeCoordinator(project.Coordinator).RecordAsync(new ChangeOutcomeRequest(
            "outcome-record",
            project.ProjectId!,
            "observe-change",
            "observe-contract",
            preflight.Blast!,
            ChangeRiskClasses.CrossCutting,
            preflight.Blast!.Nodes.Where(static node => node.Disposition == ImpactDisposition.Affected).Select(static node => node.Key)
                .Append(new StateKey(project.ProjectId!, EntityKinds.ServiceProcess, "runtime-worker")).ToArray(),
            preflight.Blast.Dimensions.Append(BlastDimensions.Runtime).Distinct(StringComparer.Ordinal).ToArray(),
            1, 0, 0, 0, false, true, 1200, "source"));
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
    }

    [Fact]
    public async Task OutcomeRejectsCallerPredictionReplacementAndCrossProjectObservation()
    {
        await using var project = await TestProject.CreateAsync("change-outcome-authority");
        var preflight = await PrepareAsync(project, "authority-change", "authority-contract");
        var coordinator = new ChangeOutcomeCoordinator(project.Coordinator);
        var request = new ChangeOutcomeRequest(
            "authority-outcome",
            project.ProjectId!,
            "authority-change",
            "authority-contract",
            preflight.Blast! with { Classification = ChangeRiskClasses.Local },
            ChangeRiskClasses.Local,
            preflight.Blast!.Nodes.Select(static node => node.Key).ToArray(),
            preflight.Blast.Dimensions,
            1, 0, 0, 0, false, true, 100, "source");

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecordAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RecordAsync(request with
        {
            Predicted = preflight.Blast,
            ObservedAffectedNodes = new[] { new StateKey("another-project", EntityKinds.Service, "consumer") }
        }));
        Assert.Null(await project.Store.ReadAsync(new StateKey(project.ProjectId!, EntityKinds.ChangeOutcome, "authority-change")));
    }

    [Fact]
    public async Task PatchDebtCannotBindAnotherChangeContract()
    {
        await using var project = await TestProject.CreateAsync("change-debt-binding");
        _ = await PrepareAsync(project, "bound-change", "bound-contract");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PatchDebtCoordinator(project.Coordinator).RecordAsync(
            "wrong-change-debt",
            project.ProjectId!,
            new PatchDebtFact(
                "debt-wrong", "different-change", "attempt-1", 1, "fallback", "TEMPORARY_COMPATIBILITY",
                new[] { "contract/shared-contract" }, "shared-contract", "remove", "review", Array.Empty<string>(), "OPEN", default),
            "bound-contract",
            "source"));
    }

    [Fact]
    public async Task HighRiskPreflightIsIdempotentAndRejectsOperationReuse()
    {
        await using var project = await TestProject.CreateAsync("change-idempotency");
        await ChangeRiskProbeTests.SeedAsync(project,
            new[]
            {
                ChangeRiskProbeTests.State(project, EntityKinds.Task, "task-1"),
                ChangeRiskProbeTests.State(project, EntityKinds.Contract, "shared-contract"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-a"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-b")
            },
            new[]
            {
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-a", EntityKinds.Contract, "shared-contract", "CONSUMES"),
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-b", EntityKinds.Contract, "shared-contract", "CONSUMES")
            });
        var request = new ChangePreflightRequest(
            "idempotent-preflight",
            ChangeRiskProbeTests.Probe(project, "idempotent-change", EntityKinds.Contract, "shared-contract"),
            ChangeRiskProbeTests.Contract(project, "idempotent-contract", "idempotent-change", "contract/shared-contract"));
        var plane = new ChangeIntelligencePlane(project.Coordinator);

        var first = await plane.PreflightAsync(request);
        var replay = await plane.PreflightAsync(request);

        Assert.Equal(ChangeControlStatuses.Ready, first.Status);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Status);
        var conflicting = request with { Contract = request.Contract! with { ProblemCode = "DIFFERENT_PROBLEM" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => plane.PreflightAsync(conflicting));
        await Assert.ThrowsAsync<ArgumentException>(() => plane.PreflightAsync(request with
        {
            OperationId = "mismatched-change-contract",
            Contract = request.Contract! with { ChangeId = "another-change" }
        }));
    }

    [Fact]
    public async Task SecretLikeContractContentIsRejectedBeforeStateWrite()
    {
        await using var project = await TestProject.CreateAsync("change-secret-boundary");
        await ChangeRiskProbeTests.SeedAsync(project,
            new[]
            {
                ChangeRiskProbeTests.State(project, EntityKinds.Task, "task-1"),
                ChangeRiskProbeTests.State(project, EntityKinds.Contract, "shared-contract"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-a"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-b")
            },
            new[]
            {
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-a", EntityKinds.Contract, "shared-contract", "CONSUMES"),
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-b", EntityKinds.Contract, "shared-contract", "CONSUMES")
            });
        var contract = ChangeRiskProbeTests.Contract(project, "secret-contract", "secret-change", "contract/shared-contract") with
        {
            IntendedInvariant = "Bearer should-never-enter-state"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            "secret-preflight",
            ChangeRiskProbeTests.Probe(project, "secret-change", EntityKinds.Contract, "shared-contract"),
            contract)));
        Assert.Null(await project.Store.ReadAsync(new StateKey(project.ProjectId!, EntityKinds.ChangeContract, "secret-contract")));
    }

    private static async Task<ChangePreflightResult> PrepareAsync(
        TestProject project,
        string changeId,
        string contractId,
        params string[] scopes)
    {
        await ChangeRiskProbeTests.SeedAsync(project,
            new[]
            {
                ChangeRiskProbeTests.State(project, EntityKinds.Task, "task-1"),
                ChangeRiskProbeTests.State(project, EntityKinds.Contract, "shared-contract"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-a"),
                ChangeRiskProbeTests.State(project, EntityKinds.Service, "consumer-b"),
                ChangeRiskProbeTests.State(project, EntityKinds.Evidence, "consumer-test")
            },
            new[]
            {
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-a", EntityKinds.Contract, "shared-contract", "CONSUMES"),
                ChangeRiskProbeTests.Edge(project, EntityKinds.Service, "consumer-b", EntityKinds.Contract, "shared-contract", "CONSUMES"),
                ChangeRiskProbeTests.Edge(project, EntityKinds.Evidence, "consumer-test", EntityKinds.Contract, "shared-contract", "VALIDATES")
            });
        var allowed = scopes.Length == 0 ? new[] { "contract/shared-contract" } : scopes;
        return await new ChangeIntelligencePlane(project.Coordinator).PreflightAsync(new ChangePreflightRequest(
            $"prepare-{changeId}",
            ChangeRiskProbeTests.Probe(project, changeId, EntityKinds.Contract, "shared-contract"),
            ChangeRiskProbeTests.Contract(project, contractId, changeId, allowed)));
    }

    private static ChangeAttempt Attempt(string id, int fallback = 0) => new(
        id,
        $"failure-{id}",
        new[] { "contract/shared-contract" },
        false,
        fallback,
        0,
        0,
        new[] { fallback > 0 ? "fallback-rejected" : $"solution-{id}-rejected" },
        default);

    private static ChangeVerificationRequest Verification(TestProject project, string changeId, string contractId) => new(
        project.ProjectId!,
        changeId,
        contractId,
        new[] { new ChangeVerificationOutcome("acceptance-primary", "PASS", "NONE") },
        new[] { new ChangeVerificationOutcome("existing-behavior", "PASS", "NONE") },
        new[] { new ChangeVerificationOutcome("consumer-test", "PASS", "NONE") });
}

using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Diagnostics;

public sealed class LegacyRuntimeFinalizationService(
    ProjectLayout layout,
    IStateStore store,
    StateCoordinator state,
    ProjectBootstrapService bootstrap,
    ProcessIdentityProbe processProbe)
{
    public async Task<LegacyRuntimeFinalizationOutcome> FinalizeAsync(
        string? suppliedProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var project = await bootstrap.RequireActiveAsync(
            ProjectBootstrapIntent.Recovery,
            suppliedProjectId,
            cancellationToken).ConfigureAwait(false);
        var projectId = project.ProjectId!;
        var inspection = await store.InspectLegacyRuntimeAsync(projectId, cancellationToken).ConfigureAwait(false);
        var marker = await state.ReadAsync(
            new StateKey(projectId, EntityKinds.Migration, "community-5.18"),
            cancellationToken).ConfigureAwait(false);
        var migrationComplete = marker is not null &&
                                project.Identity.CompletedCommunityImportProjectIds.Contains(projectId, StringComparer.Ordinal);
        if (!migrationComplete || inspection.RequiresReview > 0)
        {
            var health = await CheckHealthAsync(projectId, cancellationToken).ConfigureAwait(false);
            return new LegacyRuntimeFinalizationOutcome(
                "REQUIRES_REVIEW",
                projectId,
                migrationComplete ? "UNKNOWN_LEGACY_RUNTIME_STATE" : "COMMUNITY_MIGRATION_NOT_COMPLETE",
                inspection,
                null,
                new LegacyRuntimeFinalizeResult(
                    "REQUIRES_REVIEW",
                    projectId,
                    (await store.GetMetricsAsync(cancellationToken).ConfigureAwait(false)).Generation,
                    0,
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    inspection.ReviewFacts.Select(static fact => fact.Description).ToArray()),
                health);
        }

        StateBackupResult? backup = null;
        if (inspection.Finalizable > 0)
        {
            backup = await new StateBackupService(layout).CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var result = await state.FinalizeLegacyRuntimeStateAsync(
            new LegacyRuntimeFinalizeRequest(
                LegacyRuntimeFinalizationFacts.OperationId(projectId, marker!.SourceFingerprint, marker.Generation),
                projectId,
                marker.SourceFingerprint,
                marker.Generation),
            cancellationToken).ConfigureAwait(false);
        var after = await CheckHealthAsync(projectId, cancellationToken).ConfigureAwait(false);
        var reason = after.Status == "HEALTHY"
            ? result.Status == "FINALIZED" ? "LEGACY_RUNTIME_ARCHIVED" : "LEGACY_RUNTIME_ALREADY_ARCHIVED"
            : "DOCTOR_RECHECK_BLOCKED";
        return new LegacyRuntimeFinalizationOutcome(
            result.Status,
            projectId,
            reason,
            inspection,
            backup,
            result,
            after);
    }

    private Task<HealthReport> CheckHealthAsync(string projectId, CancellationToken cancellationToken) =>
        new AdvancedDoctor(store, processProbe).CheckProjectAsync(projectId, cancellationToken);
}

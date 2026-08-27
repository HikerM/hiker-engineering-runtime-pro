using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.State;

namespace Hiker.Pro.Context;

internal sealed class LiveAdoptionProjectBootstrap(
    ProjectLayout layout,
    IStateStore store,
    StateCoordinator coordinator,
    ProjectBootstrapService bootstrap,
    ProcessIdentityProbe processProbe)
{
    public async Task<ProjectPreflightResult> EnsureReadyAsync(
        ProjectPreflightResult initial,
        CancellationToken cancellationToken)
    {
        if (initial.State is ProjectBootstrapState.IdentityConflict or
            ProjectBootstrapState.IdentityMismatch or
            ProjectBootstrapState.StateDamaged or
            ProjectBootstrapState.RequiresReview)
        {
            return initial;
        }

        ProjectBootstrapResult? bootstrapResult = null;
        if (initial.State == ProjectBootstrapState.NewProject)
        {
            bootstrapResult = await bootstrap.InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (initial.State is ProjectBootstrapState.LegacyProject or
                 ProjectBootstrapState.MixedLegacyProject or
                 ProjectBootstrapState.MigrationIncomplete or
                 ProjectBootstrapState.IdentityMissing)
        {
            if (initial.Identity.DatabaseExists && initial.Identity.DatabaseHealthy)
            {
                _ = await new StateBackupService(layout).CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            bootstrapResult = await bootstrap.MigrateCommunityAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var active = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, cancellationToken: cancellationToken).ConfigureAwait(false);
        var projectId = active.ProjectId ?? bootstrapResult?.ProjectId;
        if (!active.CanProceed || projectId is null)
        {
            return active;
        }

        var finalizationOperation = LegacyRuntimeFinalizationFacts.OperationId(projectId);
        var finalizationReceipt = await store.ReadOperationReceiptAsync(finalizationOperation, cancellationToken).ConfigureAwait(false);
        if (finalizationReceipt is null)
        {
            var inspection = await store.InspectLegacyRuntimeAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (inspection.RequiresReview > 0)
            {
                return active with
                {
                    State = ProjectBootstrapState.RequiresReview,
                    Status = "BLOCKED",
                    CanProceed = false,
                    ReasonCode = "UNKNOWN_LEGACY_RUNTIME_AUTHORITY"
                };
            }

            if (inspection.Finalizable > 0)
            {
                var finalized = await new LegacyRuntimeFinalizationService(
                    layout,
                    store,
                    coordinator,
                    bootstrap,
                    processProbe).FinalizeAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (finalized.Status is not "FINALIZED" and not "ALREADY_FINALIZED")
                {
                    return active with
                    {
                        State = ProjectBootstrapState.RequiresReview,
                        Status = "BLOCKED",
                        CanProceed = false,
                        ReasonCode = finalized.ReasonCode
                    };
                }
            }
        }

        return await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

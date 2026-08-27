using Hiker.Pro.Execution;

namespace Hiker.Pro.Environment;

public sealed record TaskEnvironmentRequest(
    string OperationId,
    string ProjectRoot,
    string ProjectId,
    string GoalId,
    long GoalRevision,
    string TaskId,
    string WorktreeId,
    string RuntimeId,
    string Purpose,
    IReadOnlyList<ResourceRequirement> Requirements,
    IReadOnlySet<string>? ApprovedActionIds,
    RuntimeProcessIdentity? ProcessIdentity,
    TimeSpan LeaseTtl,
    string SourceFingerprint);

public sealed record TaskEnvironmentResult(
    string Status,
    EnvironmentProfile? Profile,
    EnvironmentInstance? Instance,
    IReadOnlyList<EnvironmentResourceLease> Leases,
    long Generation,
    IReadOnlyList<string> Findings);

public sealed class EnvironmentControlPlane(
    EnvironmentProfileCoordinator profiles,
    EnvironmentInstanceCoordinator instances,
    EnvironmentReleaseCoordinator release,
    EnvironmentDoctor doctor)
{
    public async Task<TaskEnvironmentResult> PrepareTaskAsync(
        TaskEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Requirements.Count is < 1 or > 128 || string.IsNullOrWhiteSpace(request.Purpose) ||
            string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 80)
            throw new ArgumentException("Task environment request is invalid or unbounded", nameof(request));
        var profile = await profiles.ResolveAndSelectAsync(
            $"{request.OperationId}:profile",
            request.ProjectId,
            request.GoalRevision,
            new EnvironmentDetectionRequest(request.ProjectRoot, request.Purpose),
            cancellationToken).ConfigureAwait(false);
        if (profile.Profile is null || profile.Profile.Status != EnvironmentStatuses.Selected)
        {
            return new TaskEnvironmentResult(profile.Status, profile.Profile, null, Array.Empty<EnvironmentResourceLease>(), profile.Generation, profile.Findings);
        }
        var instance = await instances.ActivateAsync(new EnvironmentInstanceRequest(
            $"{request.OperationId}:instance",
            request.ProjectRoot,
            request.ProjectId,
            request.GoalId,
            request.GoalRevision,
            request.TaskId,
            request.WorktreeId,
            request.RuntimeId,
            profile.Profile.ProfileId,
            request.Requirements,
            request.ApprovedActionIds ?? new HashSet<string>(StringComparer.Ordinal),
            request.ProcessIdentity,
            request.LeaseTtl,
            request.SourceFingerprint), cancellationToken).ConfigureAwait(false);
        return new TaskEnvironmentResult(instance.Status, profile.Profile, instance.Instance, instance.Leases, instance.Generation, instance.Findings);
    }

    public Task<EnvironmentReleaseResult> ReleaseTaskAsync(
        EnvironmentReleaseRequest request,
        CancellationToken cancellationToken = default) =>
        release.ReleaseAsync(request, cancellationToken);

    public Task<EnvironmentDoctorReport> CheckAsync(
        string projectRoot,
        string projectId,
        CancellationToken cancellationToken = default) =>
        doctor.CheckAsync(projectRoot, projectId, cancellationToken);
}

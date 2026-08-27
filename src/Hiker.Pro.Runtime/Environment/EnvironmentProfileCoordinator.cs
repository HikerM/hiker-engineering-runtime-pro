using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

public sealed record EnvironmentDiscoveryReport(
    string Status,
    IReadOnlyList<EnvironmentCandidate> Candidates,
    IReadOnlyList<string> Findings,
    int ProviderCalls,
    bool FullSystemScan);

public sealed record EnvironmentProfileResult(
    string Status,
    EnvironmentProfile? Profile,
    bool Committed,
    bool Reused,
    long Generation,
    IReadOnlyList<string> Findings);

public sealed class EnvironmentProfileCoordinator(
    StateCoordinator state,
    EnvironmentProviderRegistry providers,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EnvironmentDiscoveryReport> DiscoverAsync(
        EnvironmentDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateDetection(request);
        var candidates = new List<EnvironmentCandidate>();
        var findings = new List<string>();
        var calls = 0;
        foreach (var descriptor in providers.Descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = providers.Require(descriptor.ProviderId);
            var discovered = await provider.DetectAsync(request, cancellationToken).ConfigureAwait(false);
            calls++;
            foreach (var candidate in discovered.Take(request.MaximumCandidates))
            {
                if (!string.Equals(candidate.ProviderId, descriptor.ProviderId, StringComparison.Ordinal))
                {
                    findings.Add($"PROVIDER_CANDIDATE_IDENTITY_MISMATCH:{descriptor.ProviderId}");
                    continue;
                }

                try
                {
                    EnvironmentContractValidation.Candidate(candidate);
                    candidates.Add(candidate);
                }
                catch (ArgumentException)
                {
                    findings.Add($"PROVIDER_CANDIDATE_INVALID:{descriptor.ProviderId}");
                }
            }

            if (candidates.Count > request.MaximumCandidates)
            {
                return new EnvironmentDiscoveryReport(
                    EnvironmentStatuses.Ambiguous,
                    EnvironmentCollections.Freeze(candidates.Take(request.MaximumCandidates)),
                    new[] { "ENVIRONMENT_CANDIDATE_BUDGET_EXCEEDED" },
                    calls,
                    false);
            }
        }

        var status = candidates.Count switch
        {
            0 => "NO_ENVIRONMENT_CANDIDATE",
            1 => EnvironmentStatuses.Discovered,
            _ => EnvironmentStatuses.Ambiguous
        };
        return new EnvironmentDiscoveryReport(
            status,
            EnvironmentCollections.Freeze(candidates),
            EnvironmentCollections.Freeze(findings),
            calls,
            false);
    }

    public async Task<EnvironmentProfileResult> ResolveAndSelectAsync(
        string operationId,
        string projectId,
        long goalRevision,
        EnvironmentDetectionRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = new StateKey(projectId, EntityKinds.Project, "project").Normalize();
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Environment operation is required", nameof(operationId));
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE" && receipt.EntityKind == EntityKinds.EnvironmentProfile)
        {
            var stored = await state.ReadAsync(new StateKey(projectId, EntityKinds.EnvironmentProfile, receipt.EntityId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed environment profile operation has no state");
            return new EnvironmentProfileResult("IDEMPOTENT_REPLAY", EnvironmentStateCodec.Parse<EnvironmentProfile>(stored), false, true, stored.Generation, Array.Empty<string>());
        }

        var discovery = await DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
        if (discovery.Candidates.Count == 0)
        {
            return new EnvironmentProfileResult(discovery.Status, null, false, false, 0, discovery.Findings);
        }

        var verified = new List<(EnvironmentCandidate Candidate, EnvironmentValidationResult Validation)>();
        foreach (var candidate in discovery.Candidates)
        {
            var validation = await providers.Require(candidate.ProviderId)
                .ValidateAsync(new EnvironmentValidationRequest(request.ProjectRoot, candidate), cancellationToken)
                .ConfigureAwait(false);
            if (validation.Verified && validation.Status == EnvironmentStatuses.Verified)
            {
                var computed = EnvironmentStateCodec.Fingerprint(candidate);
                if (!string.Equals(computed, validation.EnvironmentFingerprint, StringComparison.Ordinal))
                {
                    return new EnvironmentProfileResult(
                        EnvironmentStatuses.ConfigurationIncompatible,
                        null,
                        false,
                        false,
                        0,
                        new[] { "PROVIDER_ENVIRONMENT_FINGERPRINT_MISMATCH" });
                }
                verified.Add((candidate, validation));
            }
        }

        if (verified.Count != 1)
        {
            return new EnvironmentProfileResult(
                verified.Count == 0 ? EnvironmentStatuses.ConfigurationIncompatible : EnvironmentStatuses.Ambiguous,
                null,
                false,
                false,
                0,
                verified.Count == 0 ? new[] { "NO_VERIFIED_ENVIRONMENT" } : new[] { "MULTIPLE_VERIFIED_ENVIRONMENTS" });
        }

        var selected = verified[0];
        var active = await state.ReadAsync(projectId, EntityKinds.EnvironmentProfile, StateLifecycle.Valid, 9, cancellationToken).ConfigureAwait(false);
        if (active.Count > 8)
        {
            return new EnvironmentProfileResult(EnvironmentStatuses.Ambiguous, null, false, false, 0, new[] { "ACTIVE_PROFILE_BUDGET_EXCEEDED" });
        }
        if (active.Any(snapshot => !string.Equals(snapshot.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal)))
        {
            return new EnvironmentProfileResult(EnvironmentStatuses.ConfigurationIncompatible, null, false, false, 0, new[] { "UNKNOWN_ACTIVE_PROFILE_AUTHORITY" });
        }
        var samePurpose = active
            .Select(snapshot => (Snapshot: snapshot, Profile: EnvironmentStateCodec.Parse<EnvironmentProfile>(snapshot)))
            .Where(item => string.Equals(item.Profile.Purpose, request.Purpose, StringComparison.Ordinal))
            .ToArray();
        if (samePurpose.Length > 1)
        {
            return new EnvironmentProfileResult(EnvironmentStatuses.Ambiguous, null, false, false, 0, new[] { "MULTIPLE_SELECTED_PROFILES" });
        }

        if (samePurpose.Length == 1)
        {
            var current = samePurpose[0];
            if (string.Equals(current.Profile.EnvironmentFingerprint, selected.Validation.EnvironmentFingerprint, StringComparison.Ordinal))
            {
                return new EnvironmentProfileResult(EnvironmentStatuses.Selected, current.Profile, false, true, current.Snapshot.Generation, Array.Empty<string>());
            }

            var stale = current.Profile with { Status = EnvironmentStatuses.Stale };
            var drift = await state.CommitBatchAsync(new BatchTransactionRequest(
                operationId,
                new[]
                {
                    EnvironmentStateCodec.Mutation(
                        current.Snapshot.Key,
                        current.Snapshot,
                        goalRevision,
                        StateLifecycle.Stale,
                        selected.Candidate.SourceFingerprint,
                        stale,
                        RetentionTier.Warm)
                },
                "ENVIRONMENT_DRIFT_DETECTED",
                EnvironmentStatuses.Drift,
                EnvironmentStateCodec.Json(new { current.Profile.ProfileId, request.Purpose })), cancellationToken).ConfigureAwait(false);
            return new EnvironmentProfileResult(EnvironmentStatuses.Drift, stale, drift.Committed, false, drift.CurrentGeneration, new[] { "REVALIDATION_REQUIRED" });
        }

        var profileId = $"env-{EnvironmentStateCodec.Hash($"{projectId}\0{request.Purpose}")[..24]}";
        var key = new StateKey(projectId, EntityKinds.EnvironmentProfile, profileId);
        var currentProfile = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (currentProfile is not null && !string.Equals(currentProfile.Authority, EnvironmentStateCodec.Authority, StringComparison.Ordinal))
        {
            return new EnvironmentProfileResult(EnvironmentStatuses.ConfigurationIncompatible, null, false, false, currentProfile.Generation, new[] { "UNKNOWN_PROFILE_AUTHORITY" });
        }
        var nextVersion = currentProfile is null ? 1 : EnvironmentStateCodec.Parse<EnvironmentProfile>(currentProfile).Version + 1;
        var profile = new EnvironmentProfile(
            profileId,
            nextVersion,
            projectId,
            request.Purpose,
            selected.Candidate.Graph,
            selected.Candidate.ToolchainFacts,
            selected.Candidate.HealthProbes,
            selected.Candidate.StartOrAttachActions,
            EnvironmentCollections.Freeze(selected.Validation.EvidenceRefs.Concat(selected.Candidate.EvidenceRefs).Distinct(StringComparer.Ordinal)),
            selected.Validation.EnvironmentFingerprint,
            selected.Candidate.SourceFingerprint,
            EnvironmentStatuses.Selected,
            _clock.GetUtcNow());
        var commit = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[] { EnvironmentStateCodec.Mutation(key, currentProfile, goalRevision, StateLifecycle.Valid, profile.EnvironmentFingerprint, profile) },
            "ENVIRONMENT_PROFILE_SELECTED",
            "VERIFIED_PROFILE_SELECTED",
            EnvironmentStateCodec.Json(new { profile.ProfileId, profile.Purpose }),
            new[] { new DependencyEdge(projectId, EntityKinds.Project, ProjectIdentityFacts.EntityId, EntityKinds.EnvironmentProfile, profileId, "USES_PROFILE", profile.EnvironmentFingerprint) },
            EnvironmentStateCodec.Json(new { profile_id = profileId })), cancellationToken).ConfigureAwait(false);
        return new EnvironmentProfileResult(commit.Status, profile, commit.Committed, false, commit.CurrentGeneration, Array.Empty<string>());
    }

    private static void ValidateDetection(EnvironmentDetectionRequest request)
    {
        if (!Directory.Exists(request.ProjectRoot) || string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Length > 160 ||
            request.MaximumCandidates is < 1 or > 128 || request.MaximumEvidenceItems is < 1 or > 1024)
        {
            throw new ArgumentException("Environment detection request is invalid or unbounded", nameof(request));
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hiker.Pro.State;

public sealed class ProjectBootstrapService(
    ProjectLayout layout,
    IStateStore store,
    StateCoordinator state,
    TimeProvider? timeProvider = null)
{
    public const string IdentityEntityId = ProjectIdentityFacts.EntityId;
    public const string IdentityAuthority = ProjectIdentityFacts.Authority;

    private static readonly IReadOnlySet<string> ForkInheritedKinds = new HashSet<string>(
        new[]
        {
            EntityKinds.Goal,
            EntityKinds.GoalRevision,
            EntityKinds.Requirement,
            EntityKinds.Decision,
            EntityKinds.RuntimeProfile,
            EntityKinds.Contract,
            EntityKinds.Database,
            EntityKinds.Code,
            EntityKinds.Ui,
            EntityKinds.Test,
            EntityKinds.Script,
            EntityKinds.Middleware,
            EntityKinds.Service,
            EntityKinds.Evidence
        },
        StringComparer.Ordinal);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ProjectPreflightResult> PreflightAsync(
        ProjectBootstrapIntent intent,
        string? suppliedProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var supplied = suppliedProjectId is null
            ? null
            : new StateKey(suppliedProjectId, EntityKinds.Project, IdentityEntityId).Normalize().ProjectId;
        var identity = await store.InspectProjectIdentityAsync(cancellationToken).ConfigureAwait(false);
        var inspectLegacyContents = identity.DatabaseHealthy &&
                                    (intent is ProjectBootstrapIntent.Initialize or ProjectBootstrapIntent.MigrateCommunity or ProjectBootstrapIntent.Recovery ||
                                     identity.ActiveIdentityRecords.Count == 0 && identity.CompletedCommunityImportProjectIds.Count == 0);
        var content = InspectAiContent(inspectLegacyContents);
        if (!identity.DatabaseHealthy)
        {
            return Result(
                ProjectBootstrapState.StateDamaged,
                null,
                "NONE",
                intent,
                content,
                identity,
                "STATE_DATABASE_DAMAGED");
        }

        if (identity.ActiveIdentityRecords.Count > 1)
        {
            return Result(ProjectBootstrapState.IdentityConflict, null, "CONFLICT", intent, content, identity, "MULTIPLE_ACTIVE_IDENTITIES");
        }

        if (identity.ActiveIdentityRecords.Count == 1)
        {
            var record = identity.ActiveIdentityRecords[0];
            if (!TryReadIdentity(record, out var payload) ||
                !string.Equals(record.Key.ProjectId, payload.ProjectId, StringComparison.Ordinal))
            {
                return Result(ProjectBootstrapState.StateDamaged, null, "INVALID", intent, content, identity, "IDENTITY_RECORD_INVALID");
            }

            if (identity.ActiveProjectIds.Any(id => !string.Equals(id, payload.ProjectId, StringComparison.Ordinal)))
            {
                return Result(ProjectBootstrapState.IdentityConflict, null, "CONFLICT", intent, content, identity, "ACTIVE_NAMESPACE_CONFLICT");
            }

            if (supplied is not null && !string.Equals(supplied, payload.ProjectId, StringComparison.Ordinal))
            {
                return Result(ProjectBootstrapState.IdentityMismatch, payload.ProjectId, IdentityAuthority, intent, content, identity, "SUPPLIED_ID_MISMATCH");
            }

            var importComplete = identity.CompletedCommunityImportProjectIds.Contains(payload.ProjectId, StringComparer.Ordinal);
            ProjectBootstrapState stateValue;
            string reason;
            if (identity.HasImportedCommunityFacts && !importComplete)
            {
                stateValue = ProjectBootstrapState.MigrationIncomplete;
                reason = "COMMUNITY_IMPORT_INCOMPLETE";
            }
            else if (content.MigratableLegacyFacts > 0 && !importComplete)
            {
                stateValue = ProjectBootstrapState.MixedLegacyProject;
                reason = "LEGACY_FACTS_NOT_IMPORTED";
            }
            else if (intent == ProjectBootstrapIntent.MigrateCommunity && importComplete)
            {
                stateValue = ProjectBootstrapState.LegacyAlreadyImported;
                reason = "COMMUNITY_IMPORT_ALREADY_COMPLETE";
            }
            else if (!string.Equals(payload.RootFingerprint, RootFingerprint(layout.ProjectRoot), StringComparison.Ordinal))
            {
                stateValue = ProjectBootstrapState.ProjectMoved;
                reason = "ROOT_CHANGED_IDENTITY_RETAINED";
            }
            else if (string.Equals(payload.Origin, "FORK", StringComparison.Ordinal))
            {
                stateValue = ProjectBootstrapState.ProjectForked;
                reason = "EXPLICIT_FORK_IDENTITY";
            }
            else
            {
                stateValue = ProjectBootstrapState.ActiveExistingProject;
                reason = "EXPLICIT_IDENTITY_ACTIVE";
            }

            return Result(stateValue, payload.ProjectId, IdentityAuthority, intent, content, identity, reason);
        }

        if (identity.ActiveProjectIds.Count > 1 || identity.CompletedCommunityImportProjectIds.Count > 1)
        {
            return Result(ProjectBootstrapState.IdentityConflict, null, "CONFLICT", intent, content, identity, "MULTIPLE_PROJECT_NAMESPACES");
        }

        var recoveredId = identity.ActiveProjectIds.SingleOrDefault()
            ?? identity.CompletedCommunityImportProjectIds.SingleOrDefault();
        if (supplied is not null && recoveredId is not null && !string.Equals(supplied, recoveredId, StringComparison.Ordinal))
        {
            return Result(ProjectBootstrapState.IdentityMismatch, recoveredId, "LEGACY_STATE_NAMESPACE", intent, content, identity, "SUPPLIED_ID_MISMATCH");
        }

        if (recoveredId is not null)
        {
            var imported = identity.CompletedCommunityImportProjectIds.Contains(recoveredId, StringComparer.Ordinal);
            if (identity.HasImportedCommunityFacts && !imported)
            {
                return Result(ProjectBootstrapState.MigrationIncomplete, recoveredId, "LEGACY_STATE_NAMESPACE", intent, content, identity, "COMMUNITY_IMPORT_INCOMPLETE");
            }

            if (imported)
            {
                var stateValue = intent == ProjectBootstrapIntent.MigrateCommunity
                    ? ProjectBootstrapState.LegacyAlreadyImported
                    : ProjectBootstrapState.ActiveExistingProject;
                return Result(stateValue, recoveredId, "LEGACY_STATE_NAMESPACE", intent, content, identity, "UNIQUE_IMPORTED_NAMESPACE");
            }

            return Result(ProjectBootstrapState.IdentityMissing, recoveredId, "UNRESOLVED", intent, content, identity, "STATE_EXISTS_WITHOUT_IDENTITY");
        }

        if (content.UnknownItems.Count > 0)
        {
            return Result(ProjectBootstrapState.RequiresReview, null, "NONE", intent, content, identity, "UNKNOWN_AI_CONTENT");
        }

        if (content.MigratableLegacyFacts > 0)
        {
            var legacyState = identity.DatabaseExists ? ProjectBootstrapState.MixedLegacyProject : ProjectBootstrapState.LegacyProject;
            return Result(legacyState, supplied, "NONE", intent, content, identity, "LEGACY_FACTS_DISCOVERED");
        }

        if (identity.DatabaseExists)
        {
            return Result(ProjectBootstrapState.IdentityMissing, supplied, "NONE", intent, content, identity, "EMPTY_STATE_DATABASE_WITHOUT_IDENTITY");
        }

        return Result(ProjectBootstrapState.NewProject, supplied, "NONE", intent, content, identity, "NO_PROJECT_STATE");
    }

    public async Task<ProjectBootstrapResult> InitializeAsync(
        string? suppliedProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var preflight = await PreflightAsync(ProjectBootstrapIntent.Initialize, suppliedProjectId, cancellationToken).ConfigureAwait(false);
        if (!preflight.CanProceed)
        {
            throw new ProjectBootstrapException(preflight);
        }

        var projectId = preflight.ProjectId ?? suppliedProjectId ?? Guid.NewGuid().ToString("D");
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var identityCreated = false;
        if (preflight.IdentityWriteRequired)
        {
            var origin = preflight.CommunityImportRequired ? "LEGACY_MIGRATION" : "INITIALIZE";
            await EnsureIdentityAsync(projectId, origin, parentProjectId: null, cancellationToken).ConfigureAwait(false);
            identityCreated = true;
        }
        else if (preflight.State == ProjectBootstrapState.ProjectMoved)
        {
            await EnsureIdentityAsync(projectId, "MOVE", parentProjectId: null, cancellationToken).ConfigureAwait(false);
        }

        CommunityImportReport? import = null;
        if (preflight.CommunityImportRequired)
        {
            import = await new Community518Importer(layout, state).ImportAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (import.Status == "REQUIRES_REVIEW")
            {
                throw new ProjectBootstrapException(
                    await PreflightAsync(ProjectBootstrapIntent.Recovery, projectId, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ProjectBootstrapResult(
            import is null ? (identityCreated ? "INITIALIZED" : "REUSED") : import.Status,
            projectId,
            preflight.State,
            identityCreated,
            !identityCreated,
            import?.Status is "IMPORTED" or "ALREADY_IMPORTED",
            import);
    }

    public async Task<ProjectBootstrapResult> MigrateCommunityAsync(
        string? suppliedProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var preflight = await PreflightAsync(ProjectBootstrapIntent.MigrateCommunity, suppliedProjectId, cancellationToken).ConfigureAwait(false);
        if (!preflight.CanProceed) throw new ProjectBootstrapException(preflight);
        if (preflight.State == ProjectBootstrapState.LegacyAlreadyImported)
        {
            var import = await new Community518Importer(layout, state).ImportAsync(preflight.ProjectId!, cancellationToken).ConfigureAwait(false);
            return new ProjectBootstrapResult(
                "ALREADY_IMPORTED",
                preflight.ProjectId!,
                preflight.State,
                false,
                true,
                false,
                import);
        }

        return await InitializeAsync(suppliedProjectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectPreflightResult> RequireActiveAsync(
        ProjectBootstrapIntent intent,
        string? suppliedProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var preflight = await PreflightAsync(intent, suppliedProjectId, cancellationToken).ConfigureAwait(false);
        if (!preflight.CanProceed || preflight.ProjectId is null)
        {
            throw new ProjectBootstrapException(preflight);
        }

        return preflight;
    }

    public async Task<ProjectForkOperationResult> ForkAsync(
        string? newProjectId = null,
        CancellationToken cancellationToken = default)
    {
        var preflight = await RequireActiveAsync(ProjectBootstrapIntent.Fork, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (newProjectId is not null &&
            string.Equals(newProjectId, preflight.ProjectId, StringComparison.Ordinal) &&
            preflight.State == ProjectBootstrapState.ProjectForked &&
            preflight.Identity.ActiveIdentityRecords.Count == 1 &&
            TryReadIdentity(preflight.Identity.ActiveIdentityRecords[0], out var forkedIdentity) &&
            forkedIdentity.ParentProjectId is { Length: > 0 } parentProjectId)
        {
            var operationId = $"project-fork-{parentProjectId}-{preflight.ProjectId}";
            var receipt = await store.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (receipt?.ResultJson is { Length: > 0 } resultJson)
            {
                var replay = JsonSerializer.Deserialize<ProjectForkResult>(resultJson)
                    ?? throw new InvalidDataException("Fork operation receipt is invalid");
                return new ProjectForkOperationResult(replay with { Status = "IDEMPOTENT_REPLAY" }, null);
            }
        }

        var sourceProjectId = preflight.ProjectId!;
        var targetProjectId = newProjectId ?? Guid.NewGuid().ToString("D");
        _ = new StateKey(targetProjectId, EntityKinds.Project, IdentityEntityId).Normalize();
        if (string.Equals(sourceProjectId, targetProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Fork requires a different project id", nameof(newProjectId));
        }

        var backup = await new StateBackupService(layout).CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (preflight.Identity.ActiveIdentityRecords.Count == 0)
        {
            await EnsureIdentityAsync(sourceProjectId, "RECOVERED_LEGACY", parentProjectId: null, cancellationToken).ConfigureAwait(false);
        }

        var payload = IdentityPayload(targetProjectId, "FORK", sourceProjectId, _clock.GetUtcNow(), null);
        var fingerprint = Sha256(payload);
        var result = await state.ForkProjectAsync(
            new ProjectForkRequest(
                $"project-fork-{sourceProjectId}-{targetProjectId}",
                sourceProjectId,
                targetProjectId,
                payload,
                fingerprint,
                ForkInheritedKinds),
            cancellationToken).ConfigureAwait(false);
        if (result.Status is not "COMMITTED" and not "IDEMPOTENT_REPLAY")
        {
            throw new InvalidOperationException($"Project fork was blocked: {result.Status}");
        }

        return new ProjectForkOperationResult(result, backup);
    }

    private async Task EnsureIdentityAsync(
        string projectId,
        string origin,
        string? parentProjectId,
        CancellationToken cancellationToken)
    {
        var key = new StateKey(projectId, EntityKinds.Project, IdentityEntityId).Normalize();
        var current = await state.ReadForReasoningAsync(key, cancellationToken).ConfigureAwait(false);
        var currentState = current.State;
        var createdAt = currentState is not null && TryReadIdentity(currentState, out var existing)
            ? existing.CreatedAt
            : _clock.GetUtcNow();
        var effectiveOrigin = currentState is not null && TryReadIdentity(currentState, out existing)
            ? existing.Origin
            : origin;
        var effectiveParent = currentState is not null && TryReadIdentity(currentState, out existing)
            ? existing.ParentProjectId
            : parentProjectId;
        var payload = IdentityPayload(projectId, effectiveOrigin, effectiveParent, createdAt, _clock.GetUtcNow());
        var fingerprint = Sha256(payload);
        var operation = $"project-identity-{projectId}-{RootFingerprint(layout.ProjectRoot)[..24]}";
        var result = await state.CommitAsync(
            current,
            operation,
            new StateMutation(
                key,
                currentState?.Generation ?? 0,
                0,
                StateLifecycle.Valid,
                IdentityAuthority,
                SqliteSchema.CurrentVersion,
                fingerprint,
                RootFingerprint(layout.ProjectRoot),
                payload),
            "PROJECT_IDENTITY_BOUND",
            "PROJECT_IDENTITY_AUTHORITY",
            JsonSerializer.Serialize(new { project_id = projectId, origin = effectiveOrigin }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Committed)
        {
            throw new InvalidOperationException($"Project identity commit failed: {result.Status}");
        }
    }

    private ProjectPreflightResult Result(
        ProjectBootstrapState stateValue,
        string? projectId,
        string authority,
        ProjectBootstrapIntent intent,
        AiContentInventory content,
        ProjectIdentityInventory identity,
        string reason)
    {
        var writableBootstrap = intent is ProjectBootstrapIntent.Initialize or ProjectBootstrapIntent.MigrateCommunity or ProjectBootstrapIntent.Recovery;
        var active = stateValue is
            ProjectBootstrapState.ActiveExistingProject or
            ProjectBootstrapState.ProjectMoved or
            ProjectBootstrapState.ProjectForked or
            ProjectBootstrapState.LegacyAlreadyImported;
        var recoverableBootstrap = stateValue is ProjectBootstrapState.NewProject or ProjectBootstrapState.LegacyProject or ProjectBootstrapState.MixedLegacyProject or ProjectBootstrapState.MigrationIncomplete;
        var emptyIdentity = stateValue == ProjectBootstrapState.IdentityMissing && !identity.HasAnyEntity && !identity.HasAnyOperation;
        var canProceed = active || (writableBootstrap && (recoverableBootstrap || emptyIdentity));
        var status = canProceed ? "READY" : stateValue is ProjectBootstrapState.MigrationIncomplete or ProjectBootstrapState.StateDamaged ? "RECOVERY_REQUIRED" : "BLOCKED";
        return new ProjectPreflightResult(
            stateValue,
            status,
            projectId,
            authority,
            canProceed,
            intent is ProjectBootstrapIntent.Read or ProjectBootstrapIntent.RuntimeStart or ProjectBootstrapIntent.Backup or ProjectBootstrapIntent.Export,
            writableBootstrap && identity.ActiveIdentityRecords.Count == 0 && (recoverableBootstrap || active || emptyIdentity),
            stateValue is ProjectBootstrapState.LegacyProject or ProjectBootstrapState.MixedLegacyProject or ProjectBootstrapState.MigrationIncomplete,
            reason,
            content,
            identity);
    }

    private AiContentInventory InspectAiContent(bool inspectLegacyContents)
    {
        var legacyInventory = LegacyAiInventoryInspector.Inspect(layout, inspectLegacyContents);
        if (!Directory.Exists(layout.AiRoot))
        {
            return new AiContentInventory(false, 0, 0, 0, 0, 0, legacyInventory.Summary, Array.Empty<string>());
        }

        var legacy = Community518SourceCatalog.HasAny(layout) ? 1 : 0;
        var authoritative = 0;
        var archive = 0;
        var cache = 0;
        var temporary = 0;
        var unknown = new List<string>(legacyInventory.UnknownItems);
        foreach (var item in Directory.EnumerateFileSystemEntries(layout.AiRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(item);
            if (name is "state" or "artifacts" or "exports") authoritative++;
            else if (name == "archive") archive++;
            else if (name is "cache" or "projections") cache++;
            else if (name.StartsWith(".staging", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) temporary++;
            else if (name is "context" or "runtime" or "governance" or "workspace" or "quality" or "tasks" or "schema.json")
            {
                // Counted by the allowlisted legacy catalog; unknown nested data is never auto-imported.
            }
            else if (LegacyAiInventoryInspector.SemanticTopLevelNames.Contains(name))
            {
                // Classified by bounded semantic inventory; only validated facts enter the importer.
            }
            else
            {
                // Unknown top-level entries are reported by the single inventory classifier.
            }
        }

        var inventorySummary = legacyInventory.Summary with { UnknownRequiresReview = unknown.Count };
        return new AiContentInventory(
            true,
            authoritative,
            legacy,
            archive,
            cache,
            temporary,
            inventorySummary,
            ContractCollections.Freeze(unknown));
    }

    private static bool TryReadIdentity(StateSnapshot state, out IdentityPayloadValue payload)
    {
        try
        {
            using var document = JsonDocument.Parse(state.PayloadJson);
            var root = document.RootElement;
            payload = new IdentityPayloadValue(
                root.GetProperty("project_id").GetString() ?? string.Empty,
                root.GetProperty("origin").GetString() ?? string.Empty,
                root.TryGetProperty("parent_project_id", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
                root.GetProperty("root_fingerprint").GetString() ?? string.Empty,
                root.GetProperty("created_at").GetDateTimeOffset());
            return payload.ProjectId.Length > 0 && payload.Origin.Length > 0 && payload.RootFingerprint.Length == 64;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            payload = default!;
            return false;
        }
    }

    private string IdentityPayload(
        string projectId,
        string origin,
        string? parentProjectId,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt) => JsonSerializer.Serialize(new
        {
            schema_version = "hiker-project-identity/v1",
            project_id = projectId,
            origin,
            parent_project_id = parentProjectId,
            root_fingerprint = RootFingerprint(layout.ProjectRoot),
            created_at = createdAt,
            updated_at = updatedAt ?? createdAt
        });

    private static string RootFingerprint(string root) => Sha256(
        Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant());

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record IdentityPayloadValue(
        string ProjectId,
        string Origin,
        string? ParentProjectId,
        string RootFingerprint,
        DateTimeOffset CreatedAt);
}

public sealed class ProjectBootstrapException(ProjectPreflightResult preflight)
    : InvalidOperationException($"Project bootstrap blocked: {preflight.State} ({preflight.ReasonCode})")
{
    public ProjectPreflightResult Preflight { get; } = preflight;
}

using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Environment;
using Hiker.Pro.Execution;
using Hiker.Pro.Release;
using Hiker.Pro.State;
using Hiker.Pro.Storage;

internal sealed class ProjectCommandRunner(string root, JsonSerializerOptions json)
{
    private string command = "unknown";

    public async Task<int> RunAsync(string[] args)
    {
        command = args[0];
        var layout = new ProjectLayout(root);
        await using var store = new SqliteStateStore(layout);
        var state = new StateCoordinator(store);
        var bootstrap = new ProjectBootstrapService(layout, store, state);
        var suppliedProjectId = ValueAfter(args, "--project-id");
        switch (args[0])
        {
            case "init":
                {
                    var result = await bootstrap.InitializeAsync(suppliedProjectId).ConfigureAwait(false);
                    Write(new
                    {
                        ok = true,
                        result.Status,
                        result.ProjectId,
                        result.ProjectState,
                        result.IdentityCreated,
                        result.IdentityReused,
                        result.CommunityImported,
                        result.Import,
                        schema_version = BuildVersionFacts.Current.StateSchema,
                        state_ref = ".ai/state/hiker-state.db"
                    });
                    return 0;
                }
            case "doctor":
                {
                    if (args.Contains("--repair", StringComparer.Ordinal))
                    {
                        var repairProject = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Recovery, suppliedProjectId).ConfigureAwait(false);
                        var repair = await state.RepairAsync().ConfigureAwait(false);
                        Write(new { project = repairProject, repair });
                        return repair.Status == "BLOCKED" ? 2 : 0;
                    }

                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var mode = args.Contains("--deep", StringComparer.Ordinal) ? StorageHealthMode.Deep : StorageHealthMode.Fast;
                    var storage = await state.StorageHealthCheckAsync(active.ProjectId!, mode).ConfigureAwait(false);
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!, storage).ConfigureAwait(false);
                    var environment = await CheckEnvironmentAsync(state, active.ProjectId!).ConfigureAwait(false);
                    var status = health.Status == "BLOCKED" || environment?.Status == "BLOCKED" ? "BLOCKED" : health.Status;
                    Write(new { status, health.Findings, storage, environment, project = active });
                    return status == "BLOCKED" ? 2 : 0;
                }
            case "status":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var storage = await state.StorageHealthCheckAsync(active.ProjectId!, StorageHealthMode.Fast).ConfigureAwait(false);
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!, storage).ConfigureAwait(false);
                    var environment = await CheckEnvironmentAsync(state, active.ProjectId!).ConfigureAwait(false);
                    var metrics = await store.GetMetricsAsync().ConfigureAwait(false);
                    var ok = health.Status != "BLOCKED" && environment?.Status != "BLOCKED";
                    Write(new { ok, project = active, health, storage, environment, metrics });
                    return ok ? 0 : 2;
                }
            case "project-facts":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var facts = await new ProjectRoutingFactsService(store).ReadAsync(active).ConfigureAwait(false);
                    Write(new { ok = true, status = facts.Status, facts });
                    return 0;
                }
            case "backup":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Backup, suppliedProjectId).ConfigureAwait(false);
                    var backup = await new StateBackupService(layout).CreateAsync(ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, backup });
                    return 0;
                }
            case "artifact-backup":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Backup, suppliedProjectId).ConfigureAwait(false);
                    var backup = await new IncrementalArtifactBackupService(layout, state)
                        .CreateAsync(active.ProjectId!, ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, backup });
                    return 0;
                }
            case "storage-status":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var metrics = await state.GetStorageMetricsAsync(active.ProjectId!).ConfigureAwait(false);
                    var health = await state.StorageHealthCheckAsync(active.ProjectId!, StorageHealthMode.Fast).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, metrics, health });
                    return health.Status == "BLOCKED" ? 2 : 0;
                }
            case "storage-maintain":
                {
                    var operationId = ValueAfter(args, "--operation-id")
                        ?? throw new ArgumentException("storage-maintain requires --operation-id <stable id>");
                    var historicalBefore = ParseTimestamp(args, "--historical-before");
                    var artifactBefore = ParseTimestamp(args, "--artifact-before");
                    var keepEvents = ParseInteger(args, "--keep-events", 1_000);
                    var keepOperations = ParseInteger(args, "--keep-operations", 10_000);
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Backup, suppliedProjectId).ConfigureAwait(false);
                    var result = await new StorageLifecycleMaintenanceCoordinator(state).RunAsync(
                        operationId,
                        active.ProjectId!,
                        new StorageMaintenancePolicy(
                            historicalBefore,
                            artifactBefore,
                            keepEvents,
                            keepOperations,
                            args.Contains("--compact", StringComparer.Ordinal))).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, result });
                    return result.Status == "COMPLETE" ? 0 : 2;
                }
            case "attach":
                {
                    var providerSession = ResolveProviderSession(args);
                    var boundaryProof = ValueAfter(args, "--boundary-proof") ??
                        System.Environment.GetEnvironmentVariable("HIKER_SAFE_BOUNDARY") ??
                        throw new ArgumentException("attach requires a host-confirmed safe boundary");
                    var adoption = await new LiveSessionAdoptionService(
                        layout,
                        store,
                        state,
                        bootstrap,
                        new ProcessIdentityProbe()).AdoptAsync(new LiveAdoptionRequest(
                            providerSession.Provider,
                            providerSession.SessionId,
                            boundaryProof,
                            ValueAfter(args, "--goal-id"),
                            ValueAfter(args, "--task-id"))).ConfigureAwait(false);
                    Write(new
                    {
                        ok = adoption.Status is LiveAdoptionStatuses.Adopted or LiveAdoptionStatuses.AlreadyAdopted,
                        adoption.Status,
                        adoption.ProjectId,
                        adoption.GoalId,
                        adoption.TaskId,
                        adoption.CheckpointId,
                        adoption.Provider,
                        adoption.ProviderSessionFingerprint,
                        adoption.Committed,
                        adoption.Idempotent,
                        adoption.NextAction,
                        adoption.StateReads,
                        adoption.StateWrites,
                        adoption.ContextBytes,
                        context_fingerprint = adoption.Context?.Fingerprint,
                        adoption.Reasons,
                        project = adoption.Project
                    });
                    return adoption.Status is LiveAdoptionStatuses.Adopted or LiveAdoptionStatuses.AlreadyAdopted ? 0 : 2;
                }
            case "checkpoint":
                {
                    var providerSession = ResolveProviderSession(args);
                    var checkpoint = await new LiveTurnLifecycleService(
                        store,
                        state,
                        bootstrap).CheckpointAsync(new LiveTurnCheckpointRequest(
                            providerSession.Provider,
                            providerSession.SessionId,
                            ValueAfter(args, "--outcome") ?? "TURN_TERMINAL",
                            ValuesAfter(args, "--changed-surface"),
                            ValuesAfter(args, "--evidence-ref"),
                            ValuesAfter(args, "--test-ref"))).ConfigureAwait(false);
                    Write(new
                    {
                        ok = checkpoint.Status is LiveTurnCheckpointStatuses.Checkpointed or LiveTurnCheckpointStatuses.AlreadyCheckpointed,
                        checkpoint.Status,
                        checkpoint.ProjectId,
                        checkpoint.GoalId,
                        checkpoint.TaskId,
                        checkpoint.CheckpointId,
                        checkpoint.ProviderSessionFingerprint,
                        checkpoint.Committed,
                        checkpoint.Idempotent,
                        checkpoint.OwnershipReleased,
                        checkpoint.StateReads,
                        checkpoint.StateWrites,
                        checkpoint.ContextBytes,
                        checkpoint.ContextFingerprint,
                        checkpoint.Reasons
                    });
                    return checkpoint.Status is LiveTurnCheckpointStatuses.Checkpointed or LiveTurnCheckpointStatuses.AlreadyCheckpointed ? 0 : 2;
                }
            case "resume":
                {
                    var goal = ValueAfter(args, "--goal-id");
                    if (goal is not null && ValueAfter(args, "--provider-session-id") is null &&
                        System.Environment.GetEnvironmentVariable("HIKER_PROVIDER_SESSION_ID") is null &&
                        System.Environment.GetEnvironmentVariable("CODEX_THREAD_ID") is null &&
                        System.Environment.GetEnvironmentVariable("CODEX_SESSION_ID") is null)
                    {
                        var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                        var resume = await new ProjectResumeService(store).ResumeAsync(active.ProjectId!, goal).ConfigureAwait(false);
                        Write(new { project = active, resume });
                        return 0;
                    }

                    var providerSession = ResolveProviderSession(args);
                    var boundaryProof = ValueAfter(args, "--boundary-proof") ??
                        System.Environment.GetEnvironmentVariable("HIKER_SAFE_BOUNDARY") ??
                        throw new ArgumentException("resume requires a host-confirmed safe boundary");
                    var adoption = await new LiveSessionAdoptionService(
                        layout,
                        store,
                        state,
                        bootstrap,
                        new ProcessIdentityProbe()).AdoptAsync(new LiveAdoptionRequest(
                            providerSession.Provider,
                            providerSession.SessionId,
                            boundaryProof,
                            goal,
                            ValueAfter(args, "--task-id"))).ConfigureAwait(false);
                    Write(new
                    {
                        ok = adoption.Status is LiveAdoptionStatuses.Adopted or LiveAdoptionStatuses.AlreadyAdopted,
                        adoption.Status,
                        adoption.ProjectId,
                        adoption.GoalId,
                        adoption.TaskId,
                        adoption.CheckpointId,
                        adoption.Provider,
                        adoption.ProviderSessionFingerprint,
                        adoption.Committed,
                        adoption.Idempotent,
                        adoption.NextAction,
                        adoption.StateReads,
                        adoption.StateWrites,
                        adoption.ContextBytes,
                        context_fingerprint = adoption.Context?.Fingerprint,
                        adoption.Reasons,
                        project = adoption.Project
                    });
                    return adoption.Status is LiveAdoptionStatuses.Adopted or LiveAdoptionStatuses.AlreadyAdopted ? 0 : 2;
                }
            case "restore":
                {
                    var backupPath = ValueAfter(args, "--backup") ?? throw new ArgumentException("restore requires --backup <path>");
                    var expectedHash = ValueAfter(args, "--confirm-sha256") ?? throw new ArgumentException("restore requires --confirm-sha256 <backup hash>");
                    var actualHash = await FileSha256Async(backupPath).ConfigureAwait(false);
                    if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Restore confirmation hash does not match the backup");
                    }

                    var restore = await new StateBackupService(layout).RestoreAsync(backupPath).ConfigureAwait(false);
                    var project = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Recovery, suppliedProjectId).ConfigureAwait(false);
                    Write(new { restore, project });
                    return project.CanProceed ? 0 : 2;
                }
            case "export":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Export, suppliedProjectId).ConfigureAwait(false);
                    var export = await store.ExportAsync(active.ProjectId!, ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, export });
                    return 0;
                }
            case "migrate-community":
                {
                    var generationBefore = File.Exists(layout.DatabasePath)
                        ? (await store.GetMetricsAsync().ConfigureAwait(false)).Generation
                        : 0;
                    var before = await bootstrap.PreflightAsync(ProjectBootstrapIntent.MigrateCommunity, suppliedProjectId).ConfigureAwait(false);
                    if (!before.CanProceed) throw new ProjectBootstrapException(before);
                    StateBackupResult? backup = null;
                    if (before.Identity.DatabaseExists && before.Identity.DatabaseHealthy)
                    {
                        backup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
                    }

                    var result = await bootstrap.MigrateCommunityAsync(suppliedProjectId).ConfigureAwait(false);
                    var metricsAfter = await store.GetMetricsAsync().ConfigureAwait(false);
                    Write(new
                    {
                        import = result.Import,
                        project = result,
                        pre_import_backup = backup,
                        operation_status = result.Status == "ALREADY_IMPORTED" ? "ALREADY_COMPLETED" : "COMPLETE",
                        state_writes = Math.Max(0, metricsAfter.Generation - generationBefore)
                    });
                    return result.Status is "IMPORTED" or "ALREADY_IMPORTED" or "REUSED" ? 0 : 2;
                }
            case "finalize-legacy":
                {
                    var metricsBefore = await store.GetMetricsAsync().ConfigureAwait(false);
                    var result = await new LegacyRuntimeFinalizationService(
                        layout,
                        store,
                        state,
                        bootstrap,
                        new ProcessIdentityProbe()).FinalizeAsync(suppliedProjectId).ConfigureAwait(false);
                    var project = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var metricsAfter = await store.GetMetricsAsync().ConfigureAwait(false);
                    var ok = result.Status is "FINALIZED" or "ALREADY_FINALIZED" &&
                             result.Health.Status == "HEALTHY" &&
                             project.CanProceed;
                    Write(new
                    {
                        ok,
                        result.Status,
                        result.ProjectId,
                        result.ReasonCode,
                        result.Inspection,
                        result.Backup,
                        result.Finalization,
                        result.Health,
                        operation_status = result.Status == "ALREADY_FINALIZED" ? "ALREADY_COMPLETED" : "COMPLETE",
                        state_writes = Math.Max(0, metricsAfter.Generation - metricsBefore.Generation),
                        project
                    });
                    return ok ? 0 : 2;
                }
            case "fork":
                {
                    var targetProjectId = suppliedProjectId ?? throw new ArgumentException("fork requires --project-id <new stable id>");
                    var result = await bootstrap.ForkAsync(targetProjectId).ConfigureAwait(false);
                    Write(result);
                    return 0;
                }
            default:
                Write(new { ok = false, status = "USAGE_ERROR", message = $"Unknown command: {args[0]}" }, CliExitCodes.Usage);
                return CliExitCodes.Usage;
        }
    }

    private void Write<T>(T value, int? exitCode = null) =>
        CliMachineContract.WritePayload(Console.Out, json, command, value, exitCode);

    private static string? ValueAfter(string[] values, string name)
    {
        var index = Array.IndexOf(values, name);
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }

    private static IReadOnlyList<string> ValuesAfter(string[] values, string name)
    {
        var result = new List<string>();
        for (var index = 0; index < values.Length - 1; index++)
        {
            if (string.Equals(values[index], name, StringComparison.Ordinal))
            {
                result.Add(values[index + 1]);
                index++;
            }
        }

        return result;
    }

    private static DateTimeOffset ParseTimestamp(string[] values, string name)
    {
        var value = ValueAfter(values, name) ?? throw new ArgumentException($"storage-maintain requires {name} <ISO-8601>");
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
            throw new ArgumentException($"{name} must be an ISO-8601 timestamp");
        return timestamp.ToUniversalTime();
    }

    private static int ParseInteger(string[] values, string name, int fallback)
    {
        var value = ValueAfter(values, name);
        if (value is null) return fallback;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{name} must be an integer");
        return parsed;
    }

    private static ProviderSession ResolveProviderSession(string[] args)
    {
        var explicitProvider = ValueAfter(args, "--provider") ?? System.Environment.GetEnvironmentVariable("HIKER_PROVIDER");
        var explicitSession = ValueAfter(args, "--provider-session-id") ?? System.Environment.GetEnvironmentVariable("HIKER_PROVIDER_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(explicitSession))
        {
            return new ProviderSession(explicitProvider ?? "external", explicitSession);
        }

        var codexSession = System.Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ??
            System.Environment.GetEnvironmentVariable("CODEX_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(codexSession))
        {
            return new ProviderSession("codex", codexSession);
        }

        throw new ArgumentException("No provider session identity is available from the host environment");
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }

    private async Task<EnvironmentDoctorReport?> CheckEnvironmentAsync(StateCoordinator state, string projectId)
    {
        var profiles = await state.ReadAsync(projectId, EntityKinds.EnvironmentProfile, lifecycle: null, 1).ConfigureAwait(false);
        if (profiles.Count == 0) return null;
        var providers = new EnvironmentProviderRegistry();
        providers.Register(new LocalProjectEnvironmentProvider(root));
        providers.Register(new WorkspaceIsolationEnvironmentProvider(root));
        return await new EnvironmentDoctor(state, providers, new ProcessIdentityProbe()).CheckAsync(root, projectId).ConfigureAwait(false);
    }

    private sealed record ProviderSession(string Provider, string SessionId);
}

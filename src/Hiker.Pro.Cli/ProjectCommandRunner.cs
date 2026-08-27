using System.Text.Json;
using Hiker.Pro.Context;
using Hiker.Pro.Diagnostics;
using Hiker.Pro.Execution;
using Hiker.Pro.Release;
using Hiker.Pro.State;

internal sealed class ProjectCommandRunner(string root, JsonSerializerOptions json)
{
    public async Task<int> RunAsync(string[] args)
    {
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
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!).ConfigureAwait(false);
                    Write(new { health.Status, health.Findings, project = active });
                    return health.Status == "BLOCKED" ? 2 : 0;
                }
            case "status":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                    var health = await new AdvancedDoctor(store, new ProcessIdentityProbe()).CheckProjectAsync(active.ProjectId!).ConfigureAwait(false);
                    var metrics = await store.GetMetricsAsync().ConfigureAwait(false);
                    Write(new { ok = health.Status != "BLOCKED", project = active, health, metrics });
                    return health.Status == "BLOCKED" ? 2 : 0;
                }
            case "backup":
                {
                    var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Backup, suppliedProjectId).ConfigureAwait(false);
                    var backup = await new StateBackupService(layout).CreateAsync(ValueAfter(args, "--output")).ConfigureAwait(false);
                    Write(new { project_id = active.ProjectId, backup });
                    return 0;
                }
            case "attach":
                {
                    var providerSession = ResolveProviderSession(args);
                    var boundaryProof = ValueAfter(args, "--boundary-proof") ??
                        Environment.GetEnvironmentVariable("HIKER_SAFE_BOUNDARY") ??
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
                        Environment.GetEnvironmentVariable("HIKER_PROVIDER_SESSION_ID") is null &&
                        Environment.GetEnvironmentVariable("CODEX_THREAD_ID") is null &&
                        Environment.GetEnvironmentVariable("CODEX_SESSION_ID") is null)
                    {
                        var active = await bootstrap.RequireActiveAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
                        var resume = await new ProjectResumeService(store).ResumeAsync(active.ProjectId!, goal).ConfigureAwait(false);
                        Write(new { project = active, resume });
                        return 0;
                    }

                    var providerSession = ResolveProviderSession(args);
                    var boundaryProof = ValueAfter(args, "--boundary-proof") ??
                        Environment.GetEnvironmentVariable("HIKER_SAFE_BOUNDARY") ??
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
                    var before = await bootstrap.PreflightAsync(ProjectBootstrapIntent.MigrateCommunity, suppliedProjectId).ConfigureAwait(false);
                    if (!before.CanProceed) throw new ProjectBootstrapException(before);
                    StateBackupResult? backup = null;
                    if (before.Identity.DatabaseExists && before.Identity.DatabaseHealthy)
                    {
                        backup = await new StateBackupService(layout).CreateAsync().ConfigureAwait(false);
                    }

                    var result = await bootstrap.MigrateCommunityAsync(suppliedProjectId).ConfigureAwait(false);
                    Write(new { import = result.Import, project = result, pre_import_backup = backup });
                    return result.Status is "IMPORTED" or "ALREADY_IMPORTED" or "REUSED" ? 0 : 2;
                }
            case "finalize-legacy":
                {
                    var result = await new LegacyRuntimeFinalizationService(
                        layout,
                        store,
                        state,
                        bootstrap,
                        new ProcessIdentityProbe()).FinalizeAsync(suppliedProjectId).ConfigureAwait(false);
                    var project = await bootstrap.PreflightAsync(ProjectBootstrapIntent.Read, suppliedProjectId).ConfigureAwait(false);
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
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                return 2;
        }
    }

    private void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, json));

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

    private static ProviderSession ResolveProviderSession(string[] args)
    {
        var explicitProvider = ValueAfter(args, "--provider") ?? Environment.GetEnvironmentVariable("HIKER_PROVIDER");
        var explicitSession = ValueAfter(args, "--provider-session-id") ?? Environment.GetEnvironmentVariable("HIKER_PROVIDER_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(explicitSession))
        {
            return new ProviderSession(explicitProvider ?? "external", explicitSession);
        }

        var codexSession = Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ??
            Environment.GetEnvironmentVariable("CODEX_SESSION_ID");
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

    private sealed record ProviderSession(string Provider, string SessionId);
}

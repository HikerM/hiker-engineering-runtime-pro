using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.Services;

public sealed record RuntimeProfileDescriptor(
    string ProfileId,
    string ServiceId,
    string Language,
    string RuntimeVersion,
    string Framework,
    string PackageManager,
    string BuildCommand,
    string TestCommand,
    string StartCommand,
    string DebugCommand,
    string Healthcheck,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Dependencies,
    string SourceFingerprint);

public sealed record EngineeringDependencyRef(string Kind, string EntityId);

public sealed record ScriptDescriptor(
    string ScriptId,
    string Purpose,
    string Runtime,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<EngineeringDependencyRef> Dependencies,
    string DestructiveRisk,
    string Rollback,
    string SourceFingerprint);

public sealed record MiddlewareDescriptor(
    string MiddlewareId,
    string Category,
    string Version,
    IReadOnlyList<string> Consumers,
    string Criticality,
    string ConfigurationSource,
    string Healthcheck,
    string FailureBehavior,
    string SourceFingerprint);

public sealed record EngineeringAssetRegistration(string Status, string Kind, string EntityId, long Generation);

public sealed class EngineeringAssetRegistry(StateCoordinator state)
{
    public Task<EngineeringAssetRegistration> RegisterRuntimeProfileAsync(
        string operationId,
        string projectId,
        long goalRevision,
        RuntimeProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ValidateRuntimeProfile(profile);
        var edges = new[]
        {
            new DependencyEdge(projectId, EntityKinds.Service, profile.ServiceId, EntityKinds.RuntimeProfile, profile.ProfileId, "RUNS_ON", profile.SourceFingerprint)
        };
        return RegisterAsync(operationId, projectId, goalRevision, EntityKinds.RuntimeProfile, profile.ProfileId, profile.SourceFingerprint, profile, edges, cancellationToken);
    }

    public Task<EngineeringAssetRegistration> RegisterScriptAsync(
        string operationId,
        string projectId,
        long goalRevision,
        ScriptDescriptor script,
        CancellationToken cancellationToken = default)
    {
        ValidateScript(script);
        var edges = script.Dependencies.Select(dependency => new DependencyEdge(
            projectId,
            EntityKinds.Script,
            script.ScriptId,
            dependency.Kind,
            dependency.EntityId,
            "DEPENDS_ON",
            script.SourceFingerprint)).ToArray();
        return RegisterAsync(operationId, projectId, goalRevision, EntityKinds.Script, script.ScriptId, script.SourceFingerprint, script, edges, cancellationToken);
    }

    public Task<EngineeringAssetRegistration> RegisterMiddlewareAsync(
        string operationId,
        string projectId,
        long goalRevision,
        MiddlewareDescriptor middleware,
        CancellationToken cancellationToken = default)
    {
        ValidateMiddleware(middleware);
        var edges = middleware.Consumers.Select(consumer => new DependencyEdge(
            projectId,
            EntityKinds.Service,
            consumer,
            EntityKinds.Middleware,
            middleware.MiddlewareId,
            "USES",
            middleware.SourceFingerprint)).ToArray();
        return RegisterAsync(operationId, projectId, goalRevision, EntityKinds.Middleware, middleware.MiddlewareId, middleware.SourceFingerprint, middleware, edges, cancellationToken);
    }

    private async Task<EngineeringAssetRegistration> RegisterAsync<T>(
        string operationId,
        string projectId,
        long goalRevision,
        string kind,
        string entityId,
        string sourceFingerprint,
        T payload,
        IReadOnlyList<DependencyEdge> edges,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(sourceFingerprint))
            throw new ArgumentException("Engineering asset operation and source fingerprint are required");
        var key = new StateKey(projectId, kind, entityId).Normalize();
        var current = await state.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var result = await state.CommitBatchAsync(new BatchTransactionRequest(
            operationId,
            new[]
            {
                new StateMutation(key, current?.Generation ?? 0, goalRevision, StateLifecycle.Valid, "ENGINEERING_ASSET_REGISTRY", SqliteSchema.CurrentVersion,
                    sourceFingerprint, sourceFingerprint, JsonSerializer.Serialize(payload), RetentionTier.Warm)
            },
            "ENGINEERING_ASSET_REGISTERED",
            "ENGINEERING_ASSET_REGISTERED",
            JsonSerializer.Serialize(new { kind, entityId }),
            edges), cancellationToken).ConfigureAwait(false);
        return new EngineeringAssetRegistration(result.Status, kind, entityId, result.CurrentGeneration);
    }

    private static void ValidateRuntimeProfile(RuntimeProfileDescriptor value)
    {
        _ = new StateKey("project", EntityKinds.RuntimeProfile, value.ProfileId).Normalize();
        _ = new StateKey("project", EntityKinds.Service, value.ServiceId).Normalize();
        if (new[] { value.Language, value.RuntimeVersion, value.Framework, value.PackageManager, value.BuildCommand, value.TestCommand, value.StartCommand, value.Healthcheck, value.SourceFingerprint }
            .Any(string.IsNullOrWhiteSpace) || value.Environments.Count > 64 || value.Dependencies.Count > 256)
            throw new ArgumentException("Runtime profile is incomplete or unbounded", nameof(value));
    }

    private static void ValidateScript(ScriptDescriptor value)
    {
        _ = new StateKey("project", EntityKinds.Script, value.ScriptId).Normalize();
        if (new[] { value.Purpose, value.Runtime, value.DestructiveRisk, value.Rollback, value.SourceFingerprint }.Any(string.IsNullOrWhiteSpace) ||
            value.Inputs.Count > 256 || value.Outputs.Count > 256 || value.SideEffects.Count > 256 || value.Dependencies.Count > 256)
            throw new ArgumentException("Script descriptor is incomplete or unbounded", nameof(value));
        foreach (var dependency in value.Dependencies) _ = new StateKey("project", dependency.Kind, dependency.EntityId).Normalize();
    }

    private static void ValidateMiddleware(MiddlewareDescriptor value)
    {
        _ = new StateKey("project", EntityKinds.Middleware, value.MiddlewareId).Normalize();
        if (new[] { value.Category, value.Version, value.Criticality, value.ConfigurationSource, value.Healthcheck, value.FailureBehavior, value.SourceFingerprint }
            .Any(string.IsNullOrWhiteSpace) || value.Consumers.Count > 256)
            throw new ArgumentException("Middleware descriptor is incomplete or unbounded", nameof(value));
        foreach (var consumer in value.Consumers) _ = new StateKey("project", EntityKinds.Service, consumer).Normalize();
    }
}

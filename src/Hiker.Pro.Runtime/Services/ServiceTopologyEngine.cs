using Hiker.Pro.State;

namespace Hiker.Pro.Services;

public sealed record ServiceContractDescriptor(
    string ContractId,
    string Type,
    string Version,
    string Fingerprint,
    IReadOnlyList<string> Consumers);

public sealed record ServiceDescriptor(
    string ServiceId,
    string Runtime,
    string RuntimeVersion,
    string BuildSystem,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Datastores,
    IReadOnlyList<string> Queues,
    IReadOnlyList<ServiceContractDescriptor> Contracts,
    IReadOnlyList<string> Dependencies,
    string SourceFingerprint);

public sealed record ServiceRegistrationResult(string Status, long Generation, int Contracts, int Dependencies);

public sealed class ServiceTopologyEngine(IStateStore stateStore)
{
    public async Task<ServiceRegistrationResult> RegisterAsync(
        string operationId,
        string projectId,
        long goalRevision,
        ServiceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        Validate(projectId, descriptor);
        var receipt = await stateStore.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            return new ServiceRegistrationResult("IDEMPOTENT_REPLAY", receipt.Generation ?? 0, descriptor.Contracts.Count, descriptor.Dependencies.Count);
        }

        var serviceKey = new StateKey(projectId, EntityKinds.Service, descriptor.ServiceId);
        var currentService = await stateStore.ReadAsync(serviceKey, cancellationToken).ConfigureAwait(false);
        var mutations = new List<StateMutation>
        {
            Mutation(serviceKey, currentService, goalRevision, descriptor.SourceFingerprint, descriptor)
        };
        foreach (var contract in descriptor.Contracts.OrderBy(static item => item.ContractId, StringComparer.Ordinal))
        {
            var key = new StateKey(projectId, EntityKinds.Contract, contract.ContractId);
            mutations.Add(Mutation(key, await stateStore.ReadAsync(key, cancellationToken).ConfigureAwait(false), goalRevision, contract.Fingerprint, contract));
        }

        var edges = new List<DependencyEdge>();
        edges.AddRange(descriptor.Dependencies.Select(dependency => new DependencyEdge(
            projectId, EntityKinds.Service, descriptor.ServiceId, EntityKinds.Service, dependency, "DEPENDS_ON", descriptor.SourceFingerprint)));
        edges.AddRange(descriptor.Contracts.Select(contract => new DependencyEdge(
            projectId, EntityKinds.Service, descriptor.ServiceId, EntityKinds.Contract, contract.ContractId, "OWNS_CONTRACT", contract.Fingerprint)));
        foreach (var contract in descriptor.Contracts)
        {
            edges.AddRange(contract.Consumers.Select(consumer => new DependencyEdge(
                projectId, EntityKinds.Service, consumer, EntityKinds.Contract, contract.ContractId, "CONSUMES", contract.Fingerprint)));
        }

        var result = await stateStore.CompareAndSwapBatchAsync(new BatchTransactionRequest(
            operationId,
            mutations,
            "SERVICE_TOPOLOGY_CHANGED",
            "SERVICE_REGISTERED",
            System.Text.Json.JsonSerializer.Serialize(new { descriptor.ServiceId, contracts = descriptor.Contracts.Count, dependencies = descriptor.Dependencies.Count }),
            edges), cancellationToken).ConfigureAwait(false);
        return new ServiceRegistrationResult(result.Status, result.CurrentGeneration, descriptor.Contracts.Count, descriptor.Dependencies.Count);
    }

    public async Task<IReadOnlyList<ServiceDescriptor>> ActiveServicesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var states = await stateStore.ReadAsync(projectId, EntityKinds.Service, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        return states.Select(state => System.Text.Json.JsonSerializer.Deserialize<ServiceDescriptor>(state.PayloadJson)
            ?? throw new InvalidDataException($"Invalid service descriptor {state.Key.EntityId}"))
            .ToArray();
    }

    private static StateMutation Mutation(StateKey key, StateSnapshot? current, long revision, string fingerprint, object payload) => new(
        key,
        current?.Generation ?? 0,
        revision,
        StateLifecycle.Valid,
        "SERVICE_TOPOLOGY_ENGINE",
        "3.0.0",
        fingerprint,
        fingerprint,
        System.Text.Json.JsonSerializer.Serialize(payload),
        RetentionTier.Hot);

    private static void Validate(string projectId, ServiceDescriptor descriptor)
    {
        _ = new StateKey(projectId, EntityKinds.Service, descriptor.ServiceId).Normalize();
        if (descriptor.Contracts.Count > 256 || descriptor.Dependencies.Count > 256 || descriptor.Endpoints.Count > 256 ||
            string.IsNullOrWhiteSpace(descriptor.Runtime) || string.IsNullOrWhiteSpace(descriptor.RuntimeVersion) || string.IsNullOrWhiteSpace(descriptor.SourceFingerprint))
        {
            throw new ArgumentException("Service descriptor is invalid or exceeds bounded topology limits", nameof(descriptor));
        }

        foreach (var contract in descriptor.Contracts)
        {
            _ = new StateKey(projectId, EntityKinds.Contract, contract.ContractId).Normalize();
            if (string.IsNullOrWhiteSpace(contract.Fingerprint) || contract.Consumers.Count > 256)
            {
                throw new ArgumentException("Service contract descriptor is invalid", nameof(descriptor));
            }
        }
    }
}

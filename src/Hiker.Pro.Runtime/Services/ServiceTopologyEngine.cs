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
public sealed record MinimumRuntimeSet(IReadOnlyList<string> Services, IReadOnlyList<string> MissingDependencies, int DependencyEdges, string Status);

public sealed class ServiceTopologyEngine(StateCoordinator state)
{
    public async Task<ServiceRegistrationResult> RegisterAsync(
        string operationId,
        string projectId,
        long goalRevision,
        ServiceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        Validate(projectId, descriptor);
        var receipt = await state.ReadOperationReceiptAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (receipt?.Status == "COMPLETE")
        {
            return new ServiceRegistrationResult("IDEMPOTENT_REPLAY", receipt.Generation ?? 0, descriptor.Contracts.Count, descriptor.Dependencies.Count);
        }

        var serviceKey = new StateKey(projectId, EntityKinds.Service, descriptor.ServiceId);
        var currentService = await state.ReadAsync(serviceKey, cancellationToken).ConfigureAwait(false);
        var mutations = new List<StateMutation>
        {
            Mutation(serviceKey, currentService, goalRevision, descriptor.SourceFingerprint, descriptor)
        };
        foreach (var contract in descriptor.Contracts.OrderBy(static item => item.ContractId, StringComparer.Ordinal))
        {
            var key = new StateKey(projectId, EntityKinds.Contract, contract.ContractId);
            mutations.Add(Mutation(key, await state.ReadAsync(key, cancellationToken).ConfigureAwait(false), goalRevision, contract.Fingerprint, contract));
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

        var result = await state.CommitBatchAsync(new BatchTransactionRequest(
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
        var states = await state.ReadAsync(projectId, EntityKinds.Service, StateLifecycle.Valid, 1000, cancellationToken).ConfigureAwait(false);
        return states.Select(state => System.Text.Json.JsonSerializer.Deserialize<ServiceDescriptor>(state.PayloadJson)
            ?? throw new InvalidDataException($"Invalid service descriptor {state.Key.EntityId}"))
            .ToArray();
    }

    public async Task<MinimumRuntimeSet> ResolveMinimumRuntimeSetAsync(
        string projectId,
        IReadOnlyList<string> affectedServiceIds,
        int maximumServices = 256,
        CancellationToken cancellationToken = default)
    {
        if (affectedServiceIds.Count is < 1 or > 128 || maximumServices is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(affectedServiceIds), "Runtime set budgets are invalid");
        var services = (await ActiveServicesAsync(projectId, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static service => service.ServiceId, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(affectedServiceIds.Distinct(StringComparer.Ordinal));
        var edges = 0;
        while (queue.Count > 0)
        {
            var serviceId = queue.Dequeue();
            if (!selected.Add(serviceId)) continue;
            if (selected.Count > maximumServices)
                return new MinimumRuntimeSet(selected.OrderBy(static value => value, StringComparer.Ordinal).ToArray(), missing.ToArray(), edges, "REQUIRES_REVIEW");
            if (!services.TryGetValue(serviceId, out var service))
            {
                missing.Add(serviceId);
                continue;
            }

            foreach (var dependency in service.Dependencies)
            {
                edges++;
                queue.Enqueue(dependency);
            }
        }

        return new MinimumRuntimeSet(
            selected.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            missing.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            edges,
            missing.Count == 0 ? "RESOLVED" : "REQUIRES_REVIEW");
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

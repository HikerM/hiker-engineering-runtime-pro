using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.State;

namespace Hiker.Pro.Environment;

internal static class EnvironmentStateCodec
{
    public const string Authority = "ENVIRONMENT_CONTROL_PLANE";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(StateSnapshot snapshot) =>
        JsonSerializer.Deserialize<T>(snapshot.PayloadJson, Options)
        ?? throw new InvalidDataException($"Invalid {snapshot.Key.Kind} environment payload");

    public static StateMutation Mutation(
        StateKey key,
        StateSnapshot? current,
        long goalRevision,
        StateLifecycle lifecycle,
        string sourceFingerprint,
        object payload,
        RetentionTier retention = RetentionTier.Hot) => new(
        key,
        current?.Generation ?? 0,
        goalRevision,
        lifecycle,
        Authority,
        SqliteSchema.CurrentVersion,
        sourceFingerprint,
        sourceFingerprint,
        Json(payload),
        retention);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Fingerprint(EnvironmentCandidate candidate)
    {
        var canonical = new
        {
            candidate.ProviderId,
            candidate.Purpose,
            Nodes = candidate.Graph.Nodes
                .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
                .Select(static node => new
                {
                    node.NodeId,
                    node.ProviderId,
                    node.Purpose,
                    Capabilities = node.Capabilities.OrderBy(static value => value, StringComparer.Ordinal),
                    Actions = node.Actions.OrderBy(static value => value, StringComparer.Ordinal),
                    Resources = node.Resources
                        .OrderBy(static resource => resource.ProviderId, StringComparer.Ordinal)
                        .ThenBy(static resource => resource.ResourceType, StringComparer.Ordinal)
                        .ThenBy(static resource => resource.ResourceId, StringComparer.Ordinal)
                        .Select(static resource => new
                        {
                            resource.ResourceType,
                            resource.ResourceId,
                            resource.ProviderId,
                            resource.Scope,
                            Attributes = resource.Attributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                            resource.SharingMode,
                            resource.Capacity,
                            resource.SupportsIsolation,
                            resource.Destructive
                        }),
                    Attributes = node.Attributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                    SecretReferences = node.SecretReferences
                        .OrderBy(static secret => secret.ProviderId, StringComparer.Ordinal)
                        .ThenBy(static secret => secret.Reference, StringComparer.Ordinal)
                }),
            Edges = candidate.Graph.Edges
                .OrderBy(static edge => edge.FromNodeId, StringComparer.Ordinal)
                .ThenBy(static edge => edge.ToNodeId, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Relation, StringComparer.Ordinal),
            ToolchainFacts = candidate.ToolchainFacts.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            HealthProbes = candidate.HealthProbes.OrderBy(static value => value, StringComparer.Ordinal),
            Actions = candidate.StartOrAttachActions.OrderBy(static value => value, StringComparer.Ordinal),
            Evidence = candidate.EvidenceRefs.OrderBy(static value => value, StringComparer.Ordinal),
            candidate.SourceFingerprint
        };
        return Hash(Json(canonical));
    }
}

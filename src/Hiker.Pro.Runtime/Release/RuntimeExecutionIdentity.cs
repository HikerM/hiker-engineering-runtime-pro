using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hiker.Pro.Release;

public sealed record RuntimeExecutionIdentity(
    string RuntimeIdentity,
    string BuildCommit,
    string BuildFingerprint,
    string LauncherVersion,
    long InstallGeneration,
    string ActivationEpoch,
    string RuntimeChannel,
    bool LauncherBound)
{
    public static RuntimeExecutionIdentity Current { get; } = FromEnvironment();

    public string EnrichEventPayload(string payloadJson)
    {
        if (!LauncherBound) return payloadJson;
        var node = JsonNode.Parse(payloadJson) as JsonObject ?? new JsonObject { ["payload"] = JsonNode.Parse(payloadJson) };
        node["runtime_identity"] = RuntimeIdentity;
        node["build_fingerprint"] = BuildFingerprint;
        return node.ToJsonString();
    }

    private static RuntimeExecutionIdentity FromEnvironment()
    {
        var version = BuildVersionFacts.Current;
        var identity = Token("HIKER_RUNTIME_IDENTITY");
        var commit = Token("HIKER_RUNTIME_BUILD_COMMIT");
        var launcher = Token("HIKER_LAUNCHER_VERSION");
        var epoch = Token("HIKER_ACTIVATION_EPOCH");
        var channel = Token("HIKER_RUNTIME_CHANNEL");
        var generation = long.TryParse(
            System.Environment.GetEnvironmentVariable("HIKER_INSTALL_GENERATION"),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) && parsed >= 0 ? parsed : 0;
        var bound = identity is not null && commit is not null && launcher is not null && epoch is not null;
        var effectiveIdentity = identity ?? $"direct-{version.Product}";
        var effectiveCommit = commit ?? "UNBOUND";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{effectiveIdentity}\n{effectiveCommit}"))).ToLowerInvariant();
        return new RuntimeExecutionIdentity(
            effectiveIdentity,
            effectiveCommit,
            fingerprint,
            launcher ?? "DIRECT",
            generation,
            epoch ?? "DIRECT",
            channel ?? "direct",
            bound);
    }

    private static string? Token(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\'))
            return null;
        return value;
    }
}

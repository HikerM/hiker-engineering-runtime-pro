using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hiker.Pro.State;

namespace Hiker.Pro.ChangeIntelligence;

internal static class ChangeStateCodec
{
    public const string Authority = "CHANGE_INTELLIGENCE_PLANE";
    public const string SchemaVersion = "3.0.0";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T FromJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException($"Invalid {typeof(T).Name} JSON payload");

    public static T Parse<T>(StateSnapshot snapshot) =>
        JsonSerializer.Deserialize<T>(snapshot.PayloadJson, Options)
        ?? throw new InvalidDataException($"Invalid {snapshot.Key.Kind} payload: {snapshot.Key.EntityId}");

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
        SchemaVersion,
        sourceFingerprint,
        Hash(Json(payload)),
        Json(payload),
        retention);

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Token(string value, string name, int maximum = 256)
    {
        var token = value?.Trim() ?? string.Empty;
        if (token.Length is 0 || token.Length > maximum) throw new ArgumentException($"{name} must contain 1-{maximum} characters", name);
        RejectSensitive(token, name);
        return token;
    }

    public static IReadOnlyList<string> Tokens(IReadOnlyList<string> values, string name, int maximumCount = 256)
    {
        if (values.Count > maximumCount) throw new ArgumentOutOfRangeException(name, $"{name} exceeds {maximumCount} items");
        return values.Select(value => Token(value, name)).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    public static void RejectSensitive(string value, string name)
    {
        if (value.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("api_key=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("api-key=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("C:\\Users\\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/home/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Change Intelligence state accepts bounded semantic identifiers, never secrets or developer paths", name);
        }
    }
}

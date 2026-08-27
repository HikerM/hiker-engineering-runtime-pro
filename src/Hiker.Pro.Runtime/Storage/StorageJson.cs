using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hiker.Pro.Storage;

internal static class StorageJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };
}

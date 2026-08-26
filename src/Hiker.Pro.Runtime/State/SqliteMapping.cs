using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.State;

internal static class SqliteMapping
{
    public static void ValidateJson(string value, string name)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} must be valid JSON", name, exception);
        }
    }

    public static StateSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        return new StateSnapshot(
            new StateKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            reader.GetInt64(3),
            reader.GetInt64(4),
            Enum.Parse<StateLifecycle>(reader.GetString(5), ignoreCase: true),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture),
            Enum.Parse<RetentionTier>(reader.GetString(6), ignoreCase: true));
    }

    public static DependencyEdge ReadEdge(SqliteDataReader reader)
    {
        return new DependencyEdge(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            Enum.Parse<StateLifecycle>(reader.GetString(7), ignoreCase: true));
    }

    public static void AddStateKey(SqliteCommand command, StateKey key)
    {
        var normalized = key.Normalize();
        command.Parameters.AddWithValue("$project", normalized.ProjectId);
        command.Parameters.AddWithValue("$kind", normalized.Kind);
        command.Parameters.AddWithValue("$entity", normalized.EntityId);
    }

    public static string EnumToken<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
}

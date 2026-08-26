using System.Reflection;

namespace Hiker.Pro.Release;

public sealed record BuildVersionFacts(string Product, string Runtime, string StateSchema)
{
    public static BuildVersionFacts Current { get; } = FromAssembly(typeof(BuildVersionFacts).Assembly);

    private static BuildVersionFacts FromAssembly(Assembly assembly)
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static item => item.Key is not null && item.Value is not null)
            .ToDictionary(static item => item.Key!, static item => item.Value!, StringComparer.Ordinal);
        return new BuildVersionFacts(
            Required(values, "HikerProductVersion"),
            Required(values, "HikerRuntimeVersion"),
            Required(values, "HikerStateSchemaVersion"));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Missing build version metadata: {key}");
}

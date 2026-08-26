using System.Text.Json;
using System.Xml.Linq;

namespace Hiker.Pro.ReleaseTool;

internal static class RepositoryVersionReader
{
    public static async Task<RepositoryVersionFacts> ReadAndValidateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var propsPath = Path.Combine(root, "eng", "Version.props");
        var versionPath = Path.Combine(root, "VERSION");
        var factsPath = Path.Combine(root, "release-versions.json");
        if (!File.Exists(propsPath) || !File.Exists(versionPath) || !File.Exists(factsPath))
        {
            throw new InvalidDataException("Version source or generated version facts are missing");
        }

        var document = XDocument.Load(propsPath, LoadOptions.None);
        var source = new RepositoryVersionFacts(
            Required(document, "HikerProductVersion"),
            Required(document, "HikerRuntimeVersion"),
            Required(document, "HikerStateSchemaVersion"),
            string.Empty,
            string.Empty);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(factsPath, cancellationToken).ConfigureAwait(false));
        var rootElement = json.RootElement;
        var facts = source with
        {
            CommunityBaseline = Required(rootElement, "community_baseline"),
            CommunityCommit = Required(rootElement, "community_commit")
        };
        var mismatches = new List<string>();
        Compare("VERSION", source.Product, (await File.ReadAllTextAsync(versionPath, cancellationToken).ConfigureAwait(false)).Trim(), mismatches);
        Compare("release-versions.product", source.Product, Required(rootElement, "product"), mismatches);
        Compare("release-versions.runtime", source.Runtime, Required(rootElement, "runtime"), mismatches);
        Compare("release-versions.state_schema", source.StateSchema, Required(rootElement, "state_schema"), mismatches);
        if (mismatches.Count > 0)
        {
            throw new InvalidDataException($"Version facts drift: {string.Join(", ", mismatches)}");
        }

        _ = Hiker.Pro.Trust.SemanticVersion.Compare(facts.Product, facts.Product);
        _ = Hiker.Pro.Trust.SemanticVersion.Compare(facts.Runtime, facts.Runtime);
        _ = Hiker.Pro.Trust.SemanticVersion.Compare(facts.StateSchema, facts.StateSchema);
        return facts;
    }

    private static string Required(XDocument document, string name) =>
        document.Descendants(name).Select(static element => element.Value.Trim()).SingleOrDefault() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Version source is missing {name}");

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Release version facts are missing {name}");

    private static void Compare(string source, string expected, string actual, ICollection<string> mismatches)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            mismatches.Add($"{source} expected {expected} actual {actual}");
        }
    }
}

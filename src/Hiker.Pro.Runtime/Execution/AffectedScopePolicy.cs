namespace Hiker.Pro.Execution;

internal static class AffectedScopePolicy
{
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? scopes)
    {
        var values = scopes is null || scopes.Count == 0 ? new[] { "." } : scopes;
        if (values.Count > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(scopes), "Predicted affected scope exceeds 128 entries");
        }

        return values.Select(static value =>
        {
            var normalized = value.Replace('\\', '/').Trim().Trim('/');
            if (normalized.Length == 0 || normalized == ".")
            {
                return ".";
            }

            if (Path.IsPathRooted(value) || normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal) || normalized.Contains("/../", StringComparison.Ordinal))
            {
                throw new ArgumentException("Affected scopes must be repository-relative paths", nameof(scopes));
            }

            return normalized;
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Overlap(IReadOnlyList<string> left, IReadOnlyList<string> right) => left.Any(a => right.Any(b =>
        a == "." || b == "." ||
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith($"{b}/", StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith($"{a}/", StringComparison.OrdinalIgnoreCase)));
}

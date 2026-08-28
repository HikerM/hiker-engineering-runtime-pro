using System.Security.Cryptography;
using System.Text;

namespace Hiker.Pro.State;

internal static class SchemaSqlChecksum
{
    public static string Compute(string sql) => Hash(Canonicalize(sql));

    public static bool IsCompatible(string? checksum, string sql)
    {
        if (string.IsNullOrWhiteSpace(checksum))
        {
            return false;
        }

        var canonical = Canonicalize(sql);
        return string.Equals(checksum, Hash(canonical), StringComparison.Ordinal) ||
               string.Equals(checksum, Hash(canonical.Replace("\n", "\r\n", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    private static string Canonicalize(string sql) => sql
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}

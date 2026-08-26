using System.Text.RegularExpressions;

namespace Hiker.Pro.Trust;

public static partial class SensitiveContentPolicy
{
    public static bool IsPrivateKeyFileName(string fileName) => PrivateKeyName().IsMatch(fileName);

    public static bool ContainsPrivateKey(string content) => PrivateKeyContent().IsMatch(content);

    public static bool ContainsCredentialToken(string content) => CredentialToken().IsMatch(content);

    public static bool ContainsConcreteDeveloperHome(string content)
    {
        foreach (Match match in DeveloperHome().Matches(content))
        {
            if (!match.Value.Contains('<') &&
                !match.Value.Contains('>') &&
                !match.Value.Contains('%') &&
                !match.Value.Contains('$') &&
                !match.Value.Contains('{') &&
                !match.Value.Contains('}'))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("(^|[._-])(private|signing)[._-]?(key)?\\.(pem|key|p8|p12|pfx)$", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyName();

    [GeneratedRegex("-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyContent();

    [GeneratedRegex("(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})")]
    private static partial Regex CredentialToken();

    [GeneratedRegex("([A-Za-z]:(?:\\\\){1,2}[U]sers(?:\\\\){1,2}[^\\\\\"']+|/[h]ome/[^/\\s\"']+|/[U]sers/[^/\\s\"']+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperHome();
}

using System.Text.Json;

namespace Hiker.Pro.Verification;

internal sealed record VerificationOptions(
    string Scenario,
    string RepositoryRoot,
    string OutputPath,
    string? PublicRepositoryRoot,
    bool DockerEnabled,
    bool CodexExecuteEnabled,
    int FileScale);

internal sealed record VerificationCheck(
    string Id,
    string Status,
    string EvidenceLevel,
    string Detail,
    IReadOnlyDictionary<string, object?> Metrics);

internal sealed record VerificationReport(
    string SchemaVersion,
    string Scenario,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string ProductVersion,
    IReadOnlyList<VerificationCheck> Checks);

internal static class VerificationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

using System.Text.Json;
using Hiker.Pro.Verification;
using Hiker.Pro.Release;

var options = Parse(args);
var startedAt = DateTimeOffset.UtcNow;
var gitCommit = (await ProcessTools.RequireAsync("git", options.RepositoryRoot, new[] { "rev-parse", "HEAD" }, TimeSpan.FromSeconds(30))).StandardOutput.Trim();
var gitStatus = (await ProcessTools.RequireAsync("git", options.RepositoryRoot, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, TimeSpan.FromSeconds(30))).StandardOutput;
var checks = new List<VerificationCheck>();
try
{
    switch (options.Scenario)
    {
        case "state-aging":
            checks.AddRange(await new StateAgingScenario().RunAsync());
            break;
        case "large-repository":
            checks.AddRange(await new LargeRepositoryScenario(options).RunAsync());
            break;
        case "real-services":
            checks.AddRange(await new RealServicesScenario(options).RunAsync());
            break;
        case "commercial":
            checks.AddRange(await new CommercialBoundaryScenario(options).RunAsync());
            break;
        case "commercial-security":
            checks.AddRange(await new CommercialSecurityScenario().RunAsync());
            break;
        case "codex-runtime":
            checks.AddRange(await new CodexRuntimeScenario(options).RunAsync());
            break;
        case "environment-control":
            checks.AddRange(await new EnvironmentControlPlaneScenario().RunAsync());
            break;
        case "change-intelligence":
            checks.AddRange(await new ChangeIntelligenceScenario(options).RunAsync());
            break;
        case "all":
            checks.AddRange(await new StateAgingScenario().RunAsync());
            checks.AddRange(await new LargeRepositoryScenario(options).RunAsync());
            checks.AddRange(await new RealServicesScenario(options).RunAsync());
            checks.AddRange(await new CommercialBoundaryScenario(options).RunAsync());
            checks.AddRange(await new EnvironmentControlPlaneScenario().RunAsync());
            checks.AddRange(await new ChangeIntelligenceScenario(options).RunAsync());
            break;
        default:
            throw new ArgumentException($"Unknown scenario: {options.Scenario}");
    }
}
catch (Exception exception)
{
    checks.Add(new VerificationCheck(
        "UNHANDLED",
        "BLOCKED",
        "DETERMINISTIC_VERIFIED",
        $"{exception.GetType().Name}: {exception.Message}",
        new Dictionary<string, object?>()));
}

var status = checks.Count > 0 && checks.All(static check => check.Status == "PASS") ? "PASS" : "BLOCKED";
var versionPath = Path.Combine(options.RepositoryRoot, "VERSION");
var report = new VerificationReport(
    "hiker-pro-verification/v1",
    options.Scenario,
    status,
    startedAt,
    DateTimeOffset.UtcNow,
    File.Exists(versionPath) ? (await File.ReadAllTextAsync(versionPath)).Trim() : "UNKNOWN",
    BuildVersionFacts.Current.Runtime,
    BuildVersionFacts.Current.StateSchema,
    gitCommit,
    !string.IsNullOrWhiteSpace(gitStatus),
    checks);
Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(report, VerificationJson.Options));
Console.WriteLine(JsonSerializer.Serialize(report, VerificationJson.Options));
return status == "PASS" ? 0 : 2;

static VerificationOptions Parse(string[] values)
{
    var scenario = "all";
    var repository = Directory.GetCurrentDirectory();
    string? output = null;
    string? publicRoot = null;
    var docker = false;
    var codexExecute = false;
    var fileScale = 100_000;
    for (var index = 0; index < values.Length; index++)
    {
        switch (values[index])
        {
            case "--scenario" when index + 1 < values.Length:
                scenario = values[++index];
                break;
            case "--root" when index + 1 < values.Length:
                repository = Path.GetFullPath(values[++index]);
                break;
            case "--output" when index + 1 < values.Length:
                output = Path.GetFullPath(values[++index]);
                break;
            case "--public-root" when index + 1 < values.Length:
                publicRoot = Path.GetFullPath(values[++index]);
                break;
            case "--docker":
                docker = true;
                break;
            case "--codex-execute":
                codexExecute = true;
                break;
            case "--file-scale" when index + 1 < values.Length && int.TryParse(values[++index], out var parsed):
                fileScale = parsed;
                break;
        }
    }

    output ??= Path.Combine(repository, "artifacts", "verification", $"{scenario}.json");
    if (!Directory.Exists(repository) || fileScale is < 10_000 or > 500_000)
    {
        throw new ArgumentException("Verification root or file scale is invalid");
    }

    return new VerificationOptions(scenario, repository, output, publicRoot, docker, codexExecute, fileScale);
}

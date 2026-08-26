using System.Text.Json;
using Hiker.Pro.ReleaseTool;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker-pro-release <version-check|build|verify|gate> [options]");
    return 0;
}

try
{
    switch (args[0])
    {
        case "version-check":
            {
                var root = Path.GetFullPath(ValueAfter("--root") ?? Directory.GetCurrentDirectory());
                var facts = await RepositoryVersionReader.ReadAndValidateAsync(root).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", facts }, json));
                return 0;
            }
        case "build":
            {
                var root = Path.GetFullPath(ValueAfter("--root") ?? Directory.GetCurrentDirectory());
                var options = new ReleaseBuildOptions(
                    root,
                    Path.GetFullPath(ValueAfter("--output") ?? Path.Combine(root, "artifacts", "release")),
                    Required("--private-key"),
                    Required("--key-id"),
                    ValueAfter("--runtime") ?? "win-x64",
                    ValueAfter("--channel") ?? "rc",
                    ValueAfter("--configuration") ?? "Release");
                var result = await new ReleasePackageBuilder().BuildAsync(options).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(result, json));
                return 0;
            }
        case "verify":
            {
                var options = new ReleaseVerifyOptions(
                Required("--package"),
                Required("--public-key"),
                Required("--key-id"),
                Required("--public-key-sha256"),
                ValueAfter("--report"));
                var result = await new ReleasePackageVerifier().VerifyAsync(options).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(result, json));
                return result.Status == "PASS" ? 0 : 2;
            }
        case "gate":
            {
                var root = Path.GetFullPath(ValueAfter("--root") ?? Directory.GetCurrentDirectory());
                var evidence = Path.GetFullPath(ValueAfter("--evidence") ?? Path.Combine(root, "artifacts", "verification"));
                var options = new CommercialGateOptions(
                    root,
                    Required("--public-root"),
                    evidence,
                    Path.GetFullPath(ValueAfter("--release-root") ?? Path.Combine(root, "artifacts", "release")),
                    Path.GetFullPath(ValueAfter("--test-results") ?? Path.Combine(evidence, "unit-tests.trx")),
                    Path.GetFullPath(ValueAfter("--output") ?? Path.Combine(evidence, "commercial-release-gate.json")));
                var result = await new CommercialReleaseGate().RunAsync(options).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(result, json));
                return result.Status == "PASS" ? 0 : 2;
            }
        default:
            Console.Error.WriteLine("Unknown release command");
            return 2;
    }
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        status = "BLOCKED",
        error = exception.GetType().Name,
        message = exception.Message
    }, json));
    return 2;
}

string Required(string name) => ValueAfter(name) is { Length: > 0 } value
    ? value
    : throw new ArgumentException($"Missing {name}");

string? ValueAfter(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

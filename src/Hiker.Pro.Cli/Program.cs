using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.Context;
using Hiker.Pro.Release;

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
};

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker <attach|checkpoint|resume|init|doctor|status|version|backup|artifact-backup|storage-status|storage-maintain|restore|export|migrate-community|finalize-legacy|fork> [--root <project>] [--project-id <advanced/recovery>] [doctor --deep]");
    return 0;
}

if (args[0] == "version")
{
    var version = BuildVersionFacts.Current;
    if (args.Contains("--json", StringComparer.Ordinal))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            product_version = version.Product,
            runtime_api_version = version.Runtime,
            state_schema_version = version.StateSchema,
            live_adoption_protocol = LiveAdoptionProtocol.CurrentVersion
        }, options));
    }
    else
    {
        Console.WriteLine($"{version.Product} runtime/{version.Runtime} schema/{version.StateSchema}");
    }
    return 0;
}

var root = ValueAfter(args, "--root") ?? Directory.GetCurrentDirectory();
try
{
    return await new ProjectCommandRunner(root, options).RunAsync(args).ConfigureAwait(false);
}
catch (Hiker.Pro.State.ProjectBootstrapException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        status = exception.Preflight.Status,
        project = exception.Preflight
    }, options));
    return 2;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        status = "FAILED",
        error = exception.GetType().Name,
        message = exception.Message
    }, options));
    return 2;
}

static string? ValueAfter(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

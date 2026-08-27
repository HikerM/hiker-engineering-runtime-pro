using System.Text.Json;
using System.Text.Json.Serialization;
using Hiker.Pro.Release;

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
};

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker <init|doctor|status|resume|version|backup|restore|export|migrate-community|fork> [--root <project>] [--project-id <advanced/recovery>]");
    return 0;
}

if (args[0] == "version")
{
    var version = BuildVersionFacts.Current;
    Console.WriteLine($"{version.Product} runtime/{version.Runtime} schema/{version.StateSchema}");
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

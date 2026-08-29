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
    Console.WriteLine("hiker <establish-current-authority|attach|checkpoint|resume|init|doctor|status|project-facts|version|backup|artifact-backup|storage-status|storage-maintain|restore|export|migrate-community|finalize-legacy|fork> [--root <project>] [--project-id <advanced/recovery>] [doctor --deep]");
    return 0;
}

if (args[0] == "version")
{
    var version = BuildVersionFacts.Current;
    if (args.Contains("--json", StringComparer.Ordinal))
    {
        var runtimeIdentity = RuntimeExecutionIdentity.Current;
        CliMachineContract.WritePayload(Console.Out, options, "version", new
        {
            product_version = version.Product,
            runtime_api_version = version.Runtime,
            state_schema_version = version.StateSchema,
            live_adoption_protocol = LiveAdoptionProtocol.CurrentVersion,
            runtimeIdentity.BuildCommit,
            runtimeIdentity.RuntimeIdentity,
            runtimeIdentity.LauncherVersion,
            runtimeIdentity.InstallGeneration,
            runtimeIdentity.ActivationEpoch,
            runtimeIdentity.RuntimeChannel
        }, status: "READY", proState: ProRuntimeStates.Active);
    }
    else
    {
        Console.WriteLine($"{version.Product} runtime/{version.Runtime} schema/{version.StateSchema}");
    }
    return 0;
}

var root = ValueAfter(args, "--root") ?? Directory.GetCurrentDirectory();
var jsonMode = args.Contains("--json", StringComparer.Ordinal);
try
{
    return await new ProjectCommandRunner(root, options).RunAsync(args).ConfigureAwait(false);
}
catch (Hiker.Pro.State.ProjectBootstrapException exception)
{
    if (jsonMode)
    {
        CliMachineContract.WriteFailure(
            Console.Out,
            options,
            args[0],
            exception.Preflight.Status,
            CliExitCodes.Blocked,
            ProRuntimeStates.RequiredBlocked,
            new { project = exception.Preflight });
        Console.Error.WriteLine($"hiker: {exception.Preflight.Status}: {exception.Preflight.ReasonCode}");
    }
    else
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            status = exception.Preflight.Status,
            project = exception.Preflight
        }, options));
    }
    return CliExitCodes.Blocked;
}
catch (LiveTurnCheckpointValidationException exception)
{
    if (jsonMode)
    {
        CliMachineContract.WriteFailure(
            Console.Out,
            options,
            args[0],
            "USAGE_ERROR",
            CliExitCodes.Usage,
            ProRuntimeStates.RequiredBlocked,
            new
            {
                error = exception.GetType().Name,
                reason_code = exception.ReasonCode,
                invalid_field = exception.Field,
                accepted_values = exception.AcceptedValues,
                message = exception.Message
            });
        Console.Error.WriteLine($"hiker: USAGE_ERROR: {exception.ReasonCode}: {exception.Message}");
    }
    else
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            status = "USAGE_ERROR",
            error = exception.GetType().Name,
            reason_code = exception.ReasonCode,
            invalid_field = exception.Field,
            accepted_values = exception.AcceptedValues,
            message = exception.Message
        }, options));
    }
    return CliExitCodes.Usage;
}
catch (ArgumentException exception)
{
    if (jsonMode)
    {
        CliMachineContract.WriteFailure(
            Console.Out,
            options,
            args[0],
            "USAGE_ERROR",
            CliExitCodes.Usage,
            ProRuntimeStates.RequiredBlocked,
            new { error = exception.GetType().Name, message = exception.Message });
    }
    if (jsonMode)
    {
        Console.Error.WriteLine($"hiker: USAGE_ERROR: {exception.Message}");
    }
    else
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            status = "USAGE_ERROR",
            error = exception.GetType().Name,
            message = exception.Message
        }, options));
    }
    return CliExitCodes.Usage;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    if (jsonMode)
    {
        CliMachineContract.WriteFailure(
            Console.Out,
            options,
            args[0],
            "FAILED",
            CliExitCodes.Failed,
            ProRuntimeStates.RequiredBlocked,
            new { error = exception.GetType().Name, message = exception.Message });
    }
    if (jsonMode)
    {
        Console.Error.WriteLine($"hiker: FAILED: {exception.GetType().Name}: {exception.Message}");
    }
    else
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            status = "FAILED",
            error = exception.GetType().Name,
            message = exception.Message
        }, options));
    }
    return CliExitCodes.Failed;
}

static string? ValueAfter(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

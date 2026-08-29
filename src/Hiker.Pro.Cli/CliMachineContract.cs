using System.Text.Json;
using System.Text.Json.Nodes;
using Hiker.Pro.Safety;

internal static class CliMachineContract
{
    public const string ContractVersion = "hiker-cli/v1";
    public const int MaxDocumentBytes = 256 * 1024;

    public static int WritePayload<T>(
        TextWriter output,
        JsonSerializerOptions options,
        string command,
        T payload,
        int? exitCode = null,
        string? status = null,
        string? proState = null)
    {
        var body = JsonSerializer.SerializeToNode(payload, options) as JsonObject ?? new JsonObject
        {
            ["result"] = JsonSerializer.SerializeToNode(payload, options)
        };
        var resolvedStatus = status ?? FindStatus(body) ?? "COMPLETE";
        var ok = FindBoolean(body, "ok") ?? IsSuccessfulStatus(resolvedStatus);
        var resolvedExitCode = exitCode ?? (ok ? CliExitCodes.Success : CliExitCodes.Blocked);
        var envelope = new JsonObject
        {
            ["contract_version"] = ContractVersion,
            ["command"] = command,
            ["ok"] = ok,
            ["status"] = resolvedStatus,
            ["pro_state"] = proState ?? ResolveProState(command, ok, resolvedStatus),
            ["exit_code"] = resolvedExitCode
        };

        foreach (var property in body)
        {
            if (envelope.ContainsKey(property.Key)) continue;
            envelope[property.Key] = property.Value?.DeepClone();
        }

        output.WriteLine(MachineResultFuse.Apply(envelope, options, MaxDocumentBytes).Document);
        return resolvedExitCode;
    }

    public static int WriteFailure(
        TextWriter output,
        JsonSerializerOptions options,
        string command,
        string status,
        int exitCode,
        string proState,
        object details) => WritePayload(output, options, command, details, exitCode, status, proState);

    private static bool IsSuccessfulStatus(string status) => status is
        "ACTIVE_EXISTING_PROJECT" or
        "ALREADY_ADOPTED" or
        "ALREADY_COMPLETED" or
        "ALREADY_FINALIZED" or
        "ALREADY_IMPORTED" or
        "ALREADY_CHECKPOINTED" or
        "COMMITTED" or
        "COMPLETE" or
        "CREATED" or
        "FINALIZED" or
        "HEALTHY" or
        "IDEMPOTENT_REPLAY" or
        "IMPORTED" or
        "INITIALIZED" or
        "LIVE_SESSION_ADOPTED" or
        "READY" or
        "RESTORED" or
        "REUSED" or
        "TURN_CHECKPOINTED" or
        "VERIFIED";

    private static string ResolveProState(string command, bool ok, string status)
    {
        if (!ok)
        {
            return status is "WAIT_SAFE_BOUNDARY" or "GOAL_ADOPTION_REQUIRED" or "TASK_ADOPTION_REQUIRED"
                ? ProRuntimeStates.Degraded
                : ProRuntimeStates.RequiredBlocked;
        }

        return command is "init" or "migrate-community"
            ? ProRuntimeStates.Degraded
            : ProRuntimeStates.Active;
    }

    private static bool? FindBoolean(JsonObject body, string name) =>
        body.TryGetPropertyValue(name, out var value) && value is JsonValue scalar && scalar.TryGetValue<bool>(out var result)
            ? result
            : null;

    private static string? FindStatus(JsonObject body)
    {
        if (body.TryGetPropertyValue("status", out var direct) && direct is JsonValue scalar && scalar.TryGetValue<string>(out var status))
        {
            return status;
        }

        foreach (var propertyName in new[] { "result", "import", "backup", "restore", "export", "repair", "health", "storage", "project", "resume", "fork" })
        {
            if (body.TryGetPropertyValue(propertyName, out var nested) && nested is JsonObject nestedObject)
            {
                var nestedStatus = FindStatus(nestedObject);
                if (!string.IsNullOrWhiteSpace(nestedStatus)) return nestedStatus;
            }
        }

        return null;
    }
}

internal static class CliExitCodes
{
    public const int Success = 0;
    public const int Blocked = 2;
    public const int Usage = 64;
    public const int Failed = 70;
}

internal static class ProRuntimeStates
{
    public const string Active = "PRO_ACTIVE";
    public const string Degraded = "PRO_DEGRADED";
    public const string CommunityFallback = "COMMUNITY_FALLBACK";
    public const string RequiredBlocked = "PRO_REQUIRED_BLOCKED";
}

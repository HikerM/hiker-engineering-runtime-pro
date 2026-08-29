using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hiker.Pro.Safety;

public sealed record MachineResultFuseReceipt(string Document, bool Truncated, int ObservedBytes, int ReturnedCount, string? PayloadSha256);

public static class MachineResultFuse
{
    public static MachineResultFuseReceipt Apply(JsonObject envelope, JsonSerializerOptions options, int maxDocumentBytes = 256 * 1024)
    {
        var document = envelope.ToJsonString(options); var observed = Encoding.UTF8.GetByteCount(document);
        if (observed <= maxDocumentBytes) return new MachineResultFuseReceipt(document, false, observed, envelope.Count, null);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();
        var bounded = new JsonObject();
        foreach (var name in new[] { "contract_version", "command", "ok", "status", "pro_state", "exit_code" })
            if (envelope.TryGetPropertyValue(name, out var value)) bounded[name] = value?.DeepClone();
        bounded["result_truncated"] = true; bounded["observed_bytes"] = observed; bounded["returned_count"] = envelope.Count;
        bounded["artifact_reference"] = null; bounded["payload_sha256"] = digest; bounded["reason_code"] = "RESULT_BUDGET_EXCEEDED";
        return new MachineResultFuseReceipt(bounded.ToJsonString(options), true, observed, envelope.Count, digest);
    }
}

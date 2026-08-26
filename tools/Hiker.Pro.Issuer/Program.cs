using System.Security.Cryptography;
using System.Text.Json;
using Hiker.Pro.Trust;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("hiker-license-issuer sign --purpose <purpose> --key-id <id> --private-key <pem> --payload <json> --output <json>");
    return 0;
}

if (args[0] != "sign")
{
    Console.Error.WriteLine("Unknown issuer command");
    return 2;
}

try
{
    var purpose = Required("--purpose");
    var keyId = Required("--key-id");
    var privateKeyPath = Path.GetFullPath(Required("--private-key"));
    var payloadPath = Path.GetFullPath(Required("--payload"));
    var outputPath = Path.GetFullPath(Required("--output"));
    if (string.Equals(privateKeyPath, outputPath, StringComparison.OrdinalIgnoreCase) || string.Equals(payloadPath, outputPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Output must not overwrite key or payload input");
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(payloadPath).ConfigureAwait(false));
    var payload = CanonicalJson.Serialize(document.RootElement);
    using var key = ECDsa.Create();
    key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath).ConfigureAwait(false));
    var signature = key.SignData(payload, HashAlgorithmName.SHA256);
    var envelope = new SignedEnvelope(
        "hiker-signed-envelope/v1",
        SignedEnvelopeVerifier.Algorithm,
        keyId,
        purpose,
        Encode(payload),
        Encode(signature));
    var content = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var temporary = $"{outputPath}.{Guid.NewGuid():N}.tmp";
    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
    await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
    {
        await writer.WriteAsync(content).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
    File.Move(temporary, outputPath, overwrite: false);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "SIGNED", purpose, key_id = keyId, output = Path.GetFileName(outputPath) }));
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "FAILED", error = exception.GetType().Name, message = exception.Message }));
    return 2;
}

string Required(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])
        ? args[index + 1]
        : throw new ArgumentException($"Missing {name}");
}

static string Encode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

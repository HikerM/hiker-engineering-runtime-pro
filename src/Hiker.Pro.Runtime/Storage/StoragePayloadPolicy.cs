namespace Hiker.Pro.Storage;

using System.Text.Json;

internal static class StoragePayloadPolicy
{
    private static readonly string[] Forbidden =
    {
        "BEGIN PRIVATE KEY", "BEGIN OPENSSH PRIVATE KEY", "password=", "api_key=", "api-key=", "Bearer ", "token="
    };

    public static void EnsureArchiveSafe(string payload)
    {
        if (Forbidden.Any(value => payload.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Storage archive rejects secret-bearing payloads");
        try
        {
            using var document = JsonDocument.Parse(payload);
            EnsureElementSafe(document.RootElement);
        }
        catch (JsonException)
        {
            // Non-JSON payloads are still covered by the assignment/Bearer/private-key markers above.
        }
    }

    private static void EnsureElementSafe(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (normalized is "password" or "token" or "apikey" or "credential" or "privatekey" or "clientsecret" or "accesskey")
                    throw new InvalidDataException("Storage archive rejects secret-valued JSON properties");
                EnsureElementSafe(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray()) EnsureElementSafe(value);
        }
    }


    public static async Task EnsureArchiveFileSafeAsync(string path, CancellationToken cancellationToken)
    {
        var markers = Forbidden
            .Select(static marker => System.Text.Encoding.UTF8.GetBytes(marker.ToLowerInvariant()))
            .ToArray();
        var buffer = new byte[128 * 1024];
        var carry = Array.Empty<byte>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var window = new byte[carry.Length + read];
            carry.CopyTo(window, 0);
            Array.Copy(buffer, 0, window, carry.Length, read);
            for (var index = 0; index < window.Length; index++)
            {
                if (window[index] is >= (byte)'A' and <= (byte)'Z') window[index] += 32;
            }
            foreach (var marker in markers)
            {
                if (window.AsSpan().IndexOf(marker) >= 0)
                    throw new InvalidDataException("Archive content contains a prohibited secret marker");
            }
            var keep = Math.Min(128, window.Length);
            carry = window[^keep..];
        }
    }
}

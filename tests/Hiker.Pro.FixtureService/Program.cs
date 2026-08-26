using System.Text.Json;

var options = Parse(args);
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
var application = builder.Build();
application.MapGet("/health", () => Results.Json(new
{
    status = "HEALTHY",
    service = options.Name,
    process_id = Environment.ProcessId
}));
application.MapPost("/work", async (HttpRequest request) =>
{
    using var document = await JsonDocument.ParseAsync(request.Body);
    return Results.Json(new
    {
        status = "ACCEPTED",
        service = options.Name,
        payload_hash = document.RootElement.GetRawText().GetHashCode(StringComparison.Ordinal)
    });
});
await application.RunAsync();

static FixtureOptions Parse(string[] values)
{
    string? name = null;
    var port = 0;
    for (var index = 0; index < values.Length; index++)
    {
        switch (values[index])
        {
            case "--name" when index + 1 < values.Length:
                name = values[++index];
                break;
            case "--port" when index + 1 < values.Length && int.TryParse(values[++index], out var parsed):
                port = parsed;
                break;
        }
    }

    if (string.IsNullOrWhiteSpace(name) || port is < 1024 or > 65535)
    {
        throw new ArgumentException("Fixture service requires --name and a non-privileged --port");
    }

    return new FixtureOptions(name, port);
}

internal sealed record FixtureOptions(string Name, int Port);

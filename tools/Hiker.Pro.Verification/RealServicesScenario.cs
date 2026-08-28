using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Hiker.Pro.Verification;

internal sealed class RealServicesScenario(VerificationOptions options)
{
    public async Task<IReadOnlyList<VerificationCheck>> RunAsync()
    {
        var checks = new List<VerificationCheck>();
        checks.Add(await RunHttpServicesAsync().ConfigureAwait(false));
        checks.Add(options.DockerEnabled
            ? await RunPostgresAsync().ConfigureAwait(false)
            : new VerificationCheck(
                "REAL-DB",
                "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "PostgreSQL scenario requires explicit --docker",
                Empty()));
        return checks;
    }

    private async Task<VerificationCheck> RunHttpServicesAsync()
    {
        var assembly = Path.Combine(
            options.RepositoryRoot,
            "tests",
            "Hiker.Pro.FixtureService",
            "bin",
            "Release",
            "net9.0",
            "Hiker.Pro.FixtureService.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("Build the Release fixture service before verification", assembly);
        }

        var names = new[] { "frontend", "gateway", "api", "worker", "cache-proxy" };
        var processes = new List<Process>();
        var captureTasks = new List<Task>();
        var ports = new List<int>();
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"hiker-pro-services-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            foreach (var name in names)
            {
                var port = FreePort();
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = options.RepositoryRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(assembly);
                start.ArgumentList.Add("--name");
                start.ArgumentList.Add(name);
                start.ArgumentList.Add("--port");
                start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start fixture service");
                processes.Add(process);
                ports.Add(port);
                captureTasks.Add(CaptureAsync(process.StandardOutput, Path.Combine(outputDirectory, $"{name}.stdout.log")));
                captureTasks.Add(CaptureAsync(process.StandardError, Path.Combine(outputDirectory, $"{name}.stderr.log")));
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var latencies = new List<double>();
            for (var index = 0; index < names.Length; index++)
            {
                var endpoint = new Uri($"http://127.0.0.1:{ports[index]}/health");
                var healthy = false;
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        using var response = await client.GetAsync(endpoint).ConfigureAwait(false);
                        stopwatch.Stop();
                        if (response.IsSuccessStatusCode)
                        {
                            var payload = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
                            healthy = payload.GetProperty("status").GetString() == "HEALTHY" &&
                                      payload.GetProperty("service").GetString() == names[index];
                            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }
                    catch (TaskCanceledException)
                    {
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }

                if (!healthy)
                {
                    throw new InvalidOperationException($"Fixture service {names[index]} did not become healthy");
                }
            }

            return new VerificationCheck(
                "REAL-PROCESS-PORT-HTTP",
                "PASS",
                "REAL_INTEGRATION_PROVEN",
                "Five isolated executable fixture services passed process, port and HTTP health validation",
                new Dictionary<string, object?>
                {
                    ["services"] = names.Length,
                    ["unique_ports"] = ports.Distinct().Count(),
                    ["health_p95_ms"] = ProcessTools.Percentile(latencies, 0.95)
                });
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            await Task.WhenAll(captureTasks).ConfigureAwait(false);

            NormalizeAndDelete(outputDirectory);
        }
    }

    private async Task<VerificationCheck> RunPostgresAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var container = $"hiker-pro-pg-{suffix}";
        var password = $"fixture-{Guid.NewGuid():N}";
        var label = $"hiker.pro.fixture={suffix}";
        var created = false;
        try
        {
            await ProcessTools.RequireAsync("docker", options.RepositoryRoot, new[]
            {
                "run", "--detach", "--name", container, "--label", label,
                "--tmpfs", "/var/lib/postgresql/data:rw",
                "--env", $"POSTGRES_PASSWORD={password}", "--env", "POSTGRES_DB=hiker_fixture",
                "postgres:16.14"
            }, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            created = true;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var ready = await PsqlAsync(container, "SELECT 1;").ConfigureAwait(false);
                if (ready.ExitCode == 0 && ready.StandardOutput.Trim() == "1")
                {
                    break;
                }

                if (attempt == 59)
                {
                    throw new InvalidOperationException("PostgreSQL fixture did not become ready");
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            const string seed = "CREATE TABLE workload(id bigint PRIMARY KEY, tenant_id int NOT NULL, external_id text NOT NULL, payload text NOT NULL);" +
                                "INSERT INTO workload SELECT g, (g % 100)::int, 'key-' || g, repeat('x', 96) FROM generate_series(1, 100000) g; ANALYZE workload;";
            await RequirePsqlAsync(container, seed).ConfigureAwait(false);
            var before = await MeasurePlansAsync(container).ConfigureAwait(false);
            await RequirePsqlAsync(container, "CREATE INDEX workload_lookup_idx ON workload(tenant_id, external_id); ANALYZE workload;").ConfigureAwait(false);
            var after = await MeasurePlansAsync(container).ConfigureAwait(false);
            var concurrency = await ConcurrentQueriesAsync(container).ConfigureAwait(false);
            var improved = ProcessTools.Percentile(after, 0.95) < ProcessTools.Percentile(before, 0.95);
            return new VerificationCheck(
                "REAL-DB",
                improved && concurrency.Errors == 0 ? "PASS" : "BLOCKED",
                "REAL_INTEGRATION_PROVEN",
                "PostgreSQL 16 fixture used 100k real rows and measured an indexed before/after workload",
                new Dictionary<string, object?>
                {
                    ["engine"] = "PostgreSQL 16.14",
                    ["rows"] = 100_000,
                    ["before_p50_ms"] = ProcessTools.Percentile(before, 0.50),
                    ["before_p95_ms"] = ProcessTools.Percentile(before, 0.95),
                    ["before_p99_ms"] = ProcessTools.Percentile(before, 0.99),
                    ["after_p50_ms"] = ProcessTools.Percentile(after, 0.50),
                    ["after_p95_ms"] = ProcessTools.Percentile(after, 0.95),
                    ["after_p99_ms"] = ProcessTools.Percentile(after, 0.99),
                    ["concurrent_clients"] = 8,
                    ["concurrent_queries"] = concurrency.Queries,
                    ["client_qps"] = concurrency.Qps,
                    ["errors"] = concurrency.Errors,
                    ["pool_wait"] = "NOT_MEASURED",
                    ["lock_wait"] = "NOT_MEASURED",
                    ["saturation_point"] = "NOT_MEASURED"
                });
        }
        finally
        {
            if (created)
            {
                var identity = await ProcessTools.RunAsync(
                    "docker",
                    options.RepositoryRoot,
                    new[] { "inspect", "--format", "{{ index .Config.Labels \"hiker.pro.fixture\" }}", container },
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (identity.ExitCode == 0 && identity.StandardOutput.Trim() == suffix)
                {
                    await ProcessTools.RequireAsync(
                        "docker",
                        options.RepositoryRoot,
                        new[] { "rm", "--force", container },
                        TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<double[]> MeasurePlansAsync(string container)
    {
        var values = new List<double>();
        for (var index = 0; index < 25; index++)
        {
            var key = 42 + (index * 100);
            var json = await RequirePsqlAsync(
                container,
                $"EXPLAIN (ANALYZE, FORMAT JSON) SELECT payload FROM workload WHERE tenant_id=42 AND external_id='key-{key}';").ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            values.Add(document.RootElement[0].GetProperty("Execution Time").GetDouble());
        }

        return values.ToArray();
    }

    private async Task<(int Queries, int Errors, double Qps)> ConcurrentQueriesAsync(string container)
    {
        const int clients = 8;
        const int perClient = 20;
        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, clients).Select(async client =>
        {
            var errors = 0;
            for (var index = 0; index < perClient; index++)
            {
                var key = 42 + (((client * perClient) + index) % 999) * 100;
                var result = await PsqlAsync(container, $"SELECT payload FROM workload WHERE tenant_id=42 AND external_id='key-{key}';").ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    errors++;
                }
            }

            return errors;
        })).ConfigureAwait(false);
        stopwatch.Stop();
        var total = clients * perClient;
        return (total, results.Sum(), total / stopwatch.Elapsed.TotalSeconds);
    }

    private Task<ProcessResult> DockerAsync(string container, params string[] command) =>
        ProcessTools.RunAsync("docker", options.RepositoryRoot, new[] { "exec", container }.Concat(command), TimeSpan.FromMinutes(2));

    private Task<ProcessResult> PsqlAsync(string container, string sql) =>
        DockerAsync(container, "psql", "-U", "postgres", "-d", "hiker_fixture", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", sql);

    private async Task<string> RequirePsqlAsync(string container, string sql)
    {
        var result = await PsqlAsync(container, sql).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"PostgreSQL fixture failed: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Trim();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task CaptureAsync(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path, append: false);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static void NormalizeAndDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static IReadOnlyDictionary<string, object?> Empty() => new Dictionary<string, object?>();
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Hiker.Pro.Data;

public sealed record DatabaseProfile(
    string Engine,
    string Version,
    string JournalMode,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Indexes,
    string Fingerprint);

public sealed record QueryPlanNode(int Order, string Detail);

public sealed record QueryAssessment(
    string Status,
    IReadOnlyList<QueryPlanNode> Plan,
    bool FullTableScan,
    bool UsesIndex,
    IReadOnlyList<string> Findings,
    TimeSpan AnalysisDuration);

public sealed record DatabaseConcurrencyResult(
    int Workers,
    int Operations,
    int Succeeded,
    int BusyFailures,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaxMilliseconds);

public sealed record QueryObservation(string ParentOperationId, string QueryFingerprint, string ParameterFingerprint, TimeSpan Duration);

public sealed record NPlusOneFinding(string ParentOperationId, string QueryFingerprint, int Executions, int DistinctParameters, double TotalMilliseconds);

public interface IDatabasePerformanceAdapter
{
    Task<DatabaseProfile> ProfileAsync(CancellationToken cancellationToken = default);
    Task<QueryAssessment> AnalyzeReadQueryAsync(string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default);
    Task<DatabaseConcurrencyResult> ProbeConcurrencyAsync(int workers, int operationsPerWorker, bool allowFixtureWrites, CancellationToken cancellationToken = default);
}

public sealed class SqlitePerformanceAdapter(string connectionString) : IDatabasePerformanceAdapter
{
    public async Task<DatabaseProfile> ProfileAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var version = await ScalarAsync(connection, "SELECT sqlite_version();", cancellationToken).ConfigureAwait(false);
        var journal = await ScalarAsync(connection, "PRAGMA journal_mode;", cancellationToken).ConfigureAwait(false);
        var tables = await NamesAsync(connection, "table", cancellationToken).ConfigureAwait(false);
        var indexes = await NamesAsync(connection, "index", cancellationToken).ConfigureAwait(false);
        var fingerprint = Hash(string.Join('\n', new[] { version, journal }.Concat(tables).Concat(indexes)));
        return new DatabaseProfile("SQLite", version, journal, tables, indexes, fingerprint);
    }

    public async Task<QueryAssessment> AnalyzeReadQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var normalized = sql.TrimStart();
        if (!normalized.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Performance analysis accepts read-only SELECT/CTE statements only");
        }

        var timer = Stopwatch.StartNew();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        var nodes = new List<QueryPlanNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new QueryPlanNode(reader.GetInt32(0), reader.GetString(3)));
        }

        timer.Stop();
        var fullScan = nodes.Any(static node => node.Detail.Contains("SCAN ", StringComparison.OrdinalIgnoreCase) &&
            !node.Detail.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase) &&
            !node.Detail.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase) &&
            !node.Detail.Contains("CONSTANT ROW", StringComparison.OrdinalIgnoreCase));
        var index = nodes.Any(static node => node.Detail.Contains("USING", StringComparison.OrdinalIgnoreCase) && node.Detail.Contains("INDEX", StringComparison.OrdinalIgnoreCase));
        var findings = new List<string>();
        if (fullScan) findings.Add("FULL_TABLE_SCAN");
        if (!index) findings.Add("NO_INDEX_OBSERVED");
        return new QueryAssessment(fullScan ? "REVIEW" : "PASS", nodes, fullScan, index, findings, timer.Elapsed);
    }

    public async Task<DatabaseConcurrencyResult> ProbeConcurrencyAsync(
        int workers,
        int operationsPerWorker,
        bool allowFixtureWrites,
        CancellationToken cancellationToken = default)
    {
        if (!allowFixtureWrites)
        {
            throw new InvalidOperationException("Concurrency probe requires an explicit disposable-fixture write grant");
        }

        if (workers is < 1 or > 64 || operationsPerWorker is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(workers));
        }

        await using (var setup = new SqliteConnection(connectionString))
        {
            await setup.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS __hiker_concurrency_probe(id INTEGER PRIMARY KEY, worker INTEGER NOT NULL, value INTEGER NOT NULL); DELETE FROM __hiker_concurrency_probe;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var durations = new ConcurrentBag<double>();
        var succeeded = 0;
        var busy = 0;
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=1000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var iteration = 0; iteration < operationsPerWorker; iteration++)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO __hiker_concurrency_probe(worker,value) VALUES($worker,$value);";
                    command.Parameters.AddWithValue("$worker", worker);
                    command.Parameters.AddWithValue("$value", iteration);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref succeeded);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                {
                    Interlocked.Increment(ref busy);
                }
                finally
                {
                    timer.Stop();
                    durations.Add(timer.Elapsed.TotalMilliseconds);
                }
            }
        }, cancellationToken)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var sorted = durations.OrderBy(static value => value).ToArray();
        return new DatabaseConcurrencyResult(
            workers,
            workers * operationsPerWorker,
            succeeded,
            busy,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            sorted.Length == 0 ? 0 : sorted[^1]);
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> NamesAsync(SqliteConnection connection, string type, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type=$type AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static partial class DataPerformanceEngine
{
    public static IReadOnlyList<NPlusOneFinding> DetectNPlusOne(IEnumerable<QueryObservation> observations, int threshold = 8) => observations
        .GroupBy(static item => (item.ParentOperationId, item.QueryFingerprint))
        .Select(static group => new NPlusOneFinding(
            group.Key.ParentOperationId,
            group.Key.QueryFingerprint,
            group.Count(),
            group.Select(static item => item.ParameterFingerprint).Distinct(StringComparer.Ordinal).Count(),
            group.Sum(static item => item.Duration.TotalMilliseconds)))
        .Where(finding => finding.Executions >= threshold && finding.DistinctParameters >= threshold / 2)
        .OrderByDescending(static finding => finding.TotalMilliseconds)
        .ToArray();
}

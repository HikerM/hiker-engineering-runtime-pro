using Hiker.Pro.State;

namespace Hiker.Pro.Data;

public sealed record PerformanceContract(
    double MaximumP95Milliseconds,
    double MaximumP99Milliseconds,
    double MaximumErrorRate,
    double MaximumPoolWaitP95Milliseconds,
    double MaximumLockWaitP95Milliseconds,
    int MaximumDeadlocks,
    double MinimumQps);

public sealed record WorkloadEvidence(
    string WorkloadId,
    bool FunctionalPassed,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double Qps,
    double Tps,
    double ErrorRate,
    double PoolWaitP95Milliseconds,
    double LockWaitP95Milliseconds,
    int Deadlocks,
    int ConcurrentClients);

public sealed record PerformanceRegressionResult(
    string Status,
    WorkloadEvidence Before,
    WorkloadEvidence After,
    IReadOnlyList<string> Violations);

public sealed record ConnectionPoolObservation(double WaitMilliseconds, bool TimedOut);
public sealed record ConnectionPoolAssessment(double P50WaitMilliseconds, double P95WaitMilliseconds, int Timeouts, string Status);
public sealed record LockObservation(double WaitMilliseconds, bool Deadlock, bool TimedOut);
public sealed record LockAssessment(double P50WaitMilliseconds, double P95WaitMilliseconds, int Deadlocks, int Timeouts, string Status);
public sealed record SaturationSample(int ConcurrentClients, double Qps, double P95Milliseconds, double ErrorRate);
public sealed record SaturationAssessment(int? SaturationPoint, IReadOnlyList<SaturationSample> Samples, string Status);

public static partial class DataPerformanceEngine
{
    public static PerformanceRegressionResult Evaluate(
        PerformanceContract contract,
        WorkloadEvidence before,
        WorkloadEvidence after)
    {
        Validate(contract, before, after);
        var violations = new List<string>();
        if (!after.FunctionalPassed) violations.Add("FUNCTIONAL_FAILURE");
        if (after.P95Milliseconds > contract.MaximumP95Milliseconds) violations.Add("P95_CONTRACT");
        if (after.P99Milliseconds > contract.MaximumP99Milliseconds) violations.Add("P99_CONTRACT");
        if (after.ErrorRate > contract.MaximumErrorRate) violations.Add("ERROR_RATE_CONTRACT");
        if (after.PoolWaitP95Milliseconds > contract.MaximumPoolWaitP95Milliseconds) violations.Add("POOL_WAIT_CONTRACT");
        if (after.LockWaitP95Milliseconds > contract.MaximumLockWaitP95Milliseconds) violations.Add("LOCK_WAIT_CONTRACT");
        if (after.Deadlocks > contract.MaximumDeadlocks) violations.Add("DEADLOCK_CONTRACT");
        if (after.Qps < contract.MinimumQps) violations.Add("QPS_CONTRACT");
        if (after.P95Milliseconds > before.P95Milliseconds * 1.20) violations.Add("P95_REGRESSION");
        if (after.P99Milliseconds > before.P99Milliseconds * 1.20) violations.Add("P99_REGRESSION");
        return new PerformanceRegressionResult(
            violations.Count == 0 ? "PASS" : "PERFORMANCE_FAIL",
            before,
            after,
            ContractCollections.Freeze(violations));
    }

    public static ConnectionPoolAssessment AnalyzePool(IEnumerable<ConnectionPoolObservation> observations)
    {
        var values = observations.ToArray();
        if (values.Length == 0) throw new ArgumentException("Connection pool observations are required", nameof(observations));
        var waits = values.Select(static item => item.WaitMilliseconds).OrderBy(static value => value).ToArray();
        var timeouts = values.Count(static item => item.TimedOut);
        return new ConnectionPoolAssessment(Percentile(waits, 0.50), Percentile(waits, 0.95), timeouts, timeouts == 0 ? "PASS" : "POOL_EXHAUSTED");
    }

    public static LockAssessment AnalyzeLocks(IEnumerable<LockObservation> observations)
    {
        var values = observations.ToArray();
        if (values.Length == 0) throw new ArgumentException("Lock observations are required", nameof(observations));
        var waits = values.Select(static item => item.WaitMilliseconds).OrderBy(static value => value).ToArray();
        var deadlocks = values.Count(static item => item.Deadlock);
        var timeouts = values.Count(static item => item.TimedOut);
        return new LockAssessment(
            Percentile(waits, 0.50),
            Percentile(waits, 0.95),
            deadlocks,
            timeouts,
            deadlocks > 0 ? "DEADLOCK_DETECTED" : timeouts > 0 ? "LOCK_TIMEOUT" : "PASS");
    }

    public static SaturationAssessment FindSaturation(IEnumerable<SaturationSample> observations)
    {
        var samples = observations.OrderBy(static item => item.ConcurrentClients).ToArray();
        if (samples.Length < 2 || samples.Any(static item => item.ConcurrentClients < 1 || item.Qps < 0 || item.P95Milliseconds < 0 || item.ErrorRate < 0))
            throw new ArgumentException("At least two valid saturation samples are required", nameof(observations));
        int? saturation = null;
        for (var index = 1; index < samples.Length; index++)
        {
            var prior = samples[index - 1];
            var current = samples[index];
            var qpsGain = prior.Qps <= 0 ? 1 : (current.Qps - prior.Qps) / prior.Qps;
            if (current.ErrorRate > 0 || (qpsGain < 0.10 && current.P95Milliseconds > prior.P95Milliseconds * 1.50))
            {
                saturation = current.ConcurrentClients;
                break;
            }
        }

        return new SaturationAssessment(saturation, ContractCollections.Freeze(samples), saturation is null ? "NOT_REACHED" : "SATURATED");
    }

    private static void Validate(PerformanceContract contract, WorkloadEvidence before, WorkloadEvidence after)
    {
        var values = new[]
        {
            contract.MaximumP95Milliseconds, contract.MaximumP99Milliseconds, contract.MaximumErrorRate,
            contract.MaximumPoolWaitP95Milliseconds, contract.MaximumLockWaitP95Milliseconds, contract.MinimumQps,
            before.P50Milliseconds, before.P95Milliseconds, before.P99Milliseconds, before.Qps, before.Tps, before.ErrorRate,
            after.P50Milliseconds, after.P95Milliseconds, after.P99Milliseconds, after.Qps, after.Tps, after.ErrorRate
        };
        if (values.Any(static value => double.IsNaN(value) || double.IsInfinity(value) || value < 0) ||
            contract.MaximumDeadlocks < 0 || before.Deadlocks < 0 || after.Deadlocks < 0 ||
            before.ConcurrentClients < 1 || after.ConcurrentClients < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(contract), "Performance evidence and contract values must be finite and non-negative");
        }
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

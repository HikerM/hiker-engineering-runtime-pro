namespace Hiker.Pro.Storage;

public sealed class StorageBudgetEvaluator
{
    public StorageBudgetReport Evaluate(StorageMetrics metrics, StorageBudget budget)
    {
        var exceeded = new List<string>();
        if (metrics.CacheBytes > budget.CacheBytes) exceeded.Add("CACHE_OVER_BUDGET");
        if (metrics.TempBytes > budget.TempBytes) exceeded.Add("TEMP_OVER_BUDGET");
        if (metrics.ArtifactBytes > budget.ArtifactBytes) exceeded.Add("ARTIFACT_OVER_BUDGET");
        if (metrics.ArchiveBytes > budget.ArchiveBytes) exceeded.Add("ARCHIVE_OVER_BUDGET");
        if (metrics.HotStateBytes > budget.HotStateBytes) exceeded.Add("HOT_STATE_OVER_BUDGET");
        if (metrics.WarmStateBytes > budget.WarmStateBytes) exceeded.Add("WARM_STATE_OVER_BUDGET");
        var actions = new List<string>();
        if (exceeded.Contains("TEMP_OVER_BUDGET", StringComparer.Ordinal)) actions.Add("COLLECT_EXPIRED_TEMP");
        if (exceeded.Contains("CACHE_OVER_BUDGET", StringComparer.Ordinal)) actions.Add("COLLECT_REBUILDABLE_CACHE");
        if (exceeded.Contains("ARTIFACT_OVER_BUDGET", StringComparer.Ordinal)) actions.Add("ANALYZE_UNREACHABLE_ARTIFACTS");
        if (exceeded.Contains("HOT_STATE_OVER_BUDGET", StringComparer.Ordinal) || exceeded.Contains("WARM_STATE_OVER_BUDGET", StringComparer.Ordinal))
            actions.Add("ROLLUP_AND_COMPACT_WHEN_IDLE");
        if (exceeded.Contains("ARCHIVE_OVER_BUDGET", StringComparer.Ordinal)) actions.Add("REVIEW_ARCHIVE_RETENTION_POLICY");
        return new StorageBudgetReport(exceeded.Count == 0 ? "WITHIN_BUDGET" : "BUDGET_EXCEEDED", exceeded, actions, metrics);
    }
}

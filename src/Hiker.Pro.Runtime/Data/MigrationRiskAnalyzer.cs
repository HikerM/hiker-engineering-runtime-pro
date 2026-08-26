namespace Hiker.Pro.Data;

public enum MigrationOperationKind
{
    AddNullableColumn,
    AddRequiredColumn,
    CreateIndex,
    CreateIndexOnline,
    DropColumn,
    AlterColumn,
    RebuildTable,
    DataBackfill
}

public sealed record MigrationChange(
    string Table,
    MigrationOperationKind Operation,
    long EstimatedRows,
    bool Transactional,
    bool Reversible,
    bool OnlineCapable);

public sealed record MigrationRiskFinding(string Table, string Code, string Severity, string RequiredEvidence);

public sealed record MigrationRiskAssessment(string Risk, IReadOnlyList<MigrationRiskFinding> Findings, bool RequiresRehearsal, bool RequiresRollbackPlan);

public static class MigrationRiskAnalyzer
{
    public static MigrationRiskAssessment Assess(IReadOnlyList<MigrationChange> changes)
    {
        if (changes.Count is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(changes));
        }

        var findings = new List<MigrationRiskFinding>();
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Table) || change.EstimatedRows < 0)
            {
                throw new ArgumentException("Migration change has invalid table or row estimate", nameof(changes));
            }

            if (change.Operation is MigrationOperationKind.RebuildTable or MigrationOperationKind.DropColumn or MigrationOperationKind.AlterColumn)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "TABLE_REWRITE_OR_DESTRUCTIVE_CHANGE", "HIGH", "DISPOSABLE_CLONE_REHEARSAL"));
            }

            if (change.Operation == MigrationOperationKind.CreateIndex && change.EstimatedRows >= 1_000_000 && !change.OnlineCapable)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "LARGE_BLOCKING_INDEX_BUILD", "HIGH", "LOCK_DURATION_MEASUREMENT"));
            }

            if (change.Operation == MigrationOperationKind.AddRequiredColumn && change.EstimatedRows > 0)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "REQUIRED_COLUMN_EXISTING_ROWS", "HIGH", "BACKFILL_AND_DEFAULT_PLAN"));
            }

            if (change.Operation == MigrationOperationKind.DataBackfill && change.EstimatedRows >= 100_000)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "LARGE_BACKFILL", "MEDIUM", "BATCH_SIZE_AND_RESUME_EVIDENCE"));
            }

            if (!change.Reversible)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "IRREVERSIBLE_CHANGE", "HIGH", "RESTORE_TEST"));
            }

            if (!change.Transactional)
            {
                findings.Add(new MigrationRiskFinding(change.Table, "NON_TRANSACTIONAL_MIGRATION", "MEDIUM", "PARTIAL_FAILURE_RECOVERY"));
            }
        }

        var risk = findings.Any(static item => item.Severity == "HIGH") ? "HIGH" : findings.Count > 0 ? "MEDIUM" : "LOW";
        return new MigrationRiskAssessment(
            risk,
            findings,
            risk == "HIGH",
            findings.Any(static item => item.Code is "IRREVERSIBLE_CHANGE" or "TABLE_REWRITE_OR_DESTRUCTIVE_CHANGE"));
    }
}

using Xunit;

// SQLitePCL pool cleanup is process-wide. Keeping the test assembly serial makes
// fixture teardown deterministic without changing production connection pooling.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

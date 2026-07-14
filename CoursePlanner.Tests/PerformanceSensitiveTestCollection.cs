namespace CoursePlanner.Tests;

// Wall-clock budgets are meaningful only when another test is not deliberately
// saturating the same process. The production concurrency tests still create
// their own controlled contention; this collection only serializes benchmarks.
[CollectionDefinition(PerformanceSensitiveTestCollection.Name, DisableParallelization = true)]
public sealed class PerformanceSensitiveTestCollection
{
    public const string Name = "Performance-sensitive benchmarks";
}

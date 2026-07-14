namespace CoursePlanner.Tests;

[CollectionDefinition("SQLite global pool mutation", DisableParallelization = true)]
public sealed class SqliteGlobalPoolTestCollection
{
    public const string Name = "SQLite global pool mutation";
}

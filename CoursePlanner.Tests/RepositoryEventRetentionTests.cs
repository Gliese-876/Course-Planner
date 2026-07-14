using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class RepositoryEventRetentionTests
{
    [Fact]
    public void LegacyAndNewEventsArePrunedToTheNewestBoundedSet()
    {
        using var directory = new TemporaryDirectory();
        var repository = new SqliteAppRepository(directory.Path);
        repository.Initialize();
        using (var connection = Open(repository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE values_to_insert(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM values_to_insert WHERE value < $count
                )
                INSERT INTO app_events (occurred_at, level, event_name, message)
                SELECT '2026-07-13T00:00:00Z', 'Info', 'legacy-' || value, 'message-' || value
                FROM values_to_insert
                """;
            command.Parameters.AddWithValue("$count", SqliteAppRepository.MaximumEventRows + 25);
            command.ExecuteNonQuery();
        }

        repository.Log("Info", "newest", "newest message");

        Assert.Equal(SqliteAppRepository.MaximumEventRows, CountEvents(repository.DatabasePath));
        var summaries = repository.ReadEventSummaries(SqliteAppRepository.MaximumEventRows);
        Assert.Equal(SqliteAppRepository.MaximumEventRows, summaries.Count);
        Assert.Contains("newest", summaries[0], StringComparison.Ordinal);
        Assert.DoesNotContain(summaries, summary => summary.Contains("legacy-1 -", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadingEventSummariesDoesNotSilentlyPruneTheRepository()
    {
        using var directory = new TemporaryDirectory();
        var repository = new SqliteAppRepository(directory.Path);
        repository.Initialize();
        using (var connection = Open(repository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE values_to_insert(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM values_to_insert WHERE value < $count
                )
                INSERT INTO app_events (occurred_at, level, event_name, message)
                SELECT '2026-07-13T00:00:00Z', 'Info', 'legacy-' || value, 'message-' || value
                FROM values_to_insert
                """;
            command.Parameters.AddWithValue("$count", SqliteAppRepository.MaximumEventRows + 5);
            command.ExecuteNonQuery();
        }

        var summaries = repository.ReadEventSummaries(10);

        Assert.Equal(10, summaries.Count);
        Assert.Equal(SqliteAppRepository.MaximumEventRows + 5, CountEvents(repository.DatabasePath));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void EventSummaryLimitCannotRequestAnUnboundedRead(int limit)
    {
        using var directory = new TemporaryDirectory();
        var repository = new SqliteAppRepository(directory.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.ReadEventSummaries(limit));
    }

    [Fact]
    public void OversizedEventFieldsAreTruncatedWithoutLeavingAnUnpairedSurrogate()
    {
        using var directory = new TemporaryDirectory();
        var repository = new SqliteAppRepository(directory.Path);
        var message = new string('a', SqliteAppRepository.MaximumEventMessageLength - 1) + "😀tail";

        repository.Log(
            new string('L', SqliteAppRepository.MaximumEventLevelLength + 10),
            new string('E', SqliteAppRepository.MaximumEventNameLength + 10),
            message);

        using var connection = Open(repository.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT level, event_name, message FROM app_events ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(SqliteAppRepository.MaximumEventLevelLength, reader.GetString(0).Length);
        Assert.Equal(SqliteAppRepository.MaximumEventNameLength, reader.GetString(1).Length);
        var storedMessage = reader.GetString(2);
        Assert.True(storedMessage.Length <= SqliteAppRepository.MaximumEventMessageLength);
        Assert.False(storedMessage.Length > 0 && char.IsHighSurrogate(storedMessage[^1]));
    }

    [Fact]
    public void TextLogNamesDoNotCollideWithinTheSameSecond()
    {
        using var directory = new TemporaryDirectory();

        var first = LogFileService.WriteTextLog(directory.Path, ["first"]);
        var second = LogFileService.WriteTextLog(directory.Path, ["second"]);

        Assert.NotEqual(first, second);
        Assert.Equal(["first"], File.ReadAllLines(first));
        Assert.Equal(["second"], File.ReadAllLines(second));
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long CountEvents(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_events";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

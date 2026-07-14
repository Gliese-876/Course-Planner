using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class RepositoryInitializationTests
{
    [Fact]
    public void InterruptedTemporaryInitializationIsDiscardedBeforePublishingACompleteDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var interruptedDatabase = Path.Combine(
            workspace.Path,
            $".course-planner-initialize-{Guid.NewGuid():N}.sqlite");
        CreateIncompleteDatabase(interruptedDatabase);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            File.WriteAllText(interruptedDatabase + suffix, $"interrupted{suffix}");
        var unrelatedPath = Path.Combine(
            workspace.Path,
            ".course-planner-initialize-not-a-guid.sqlite");
        File.WriteAllText(unrelatedPath, "unrelated");

        var repository = new SqliteAppRepository(workspace.Path);
        var loaded = repository.LoadOrCreate();

        Assert.NotEmpty(loaded.Semesters);
        Assert.True(File.Exists(repository.DatabasePath));
        Assert.False(File.Exists(interruptedDatabase));
        Assert.All(
            new[] { "-wal", "-shm", "-journal" },
            suffix => Assert.False(File.Exists(interruptedDatabase + suffix)));
        Assert.False(File.Exists(Path.Combine(
            workspace.Path,
            ".course-planner-initialize.lock")));
        Assert.Equal("unrelated", File.ReadAllText(unrelatedPath));
        Assert.NotEmpty(new SqliteAppRepository(workspace.Path).LoadOrCreate().Semesters);
    }

    [Fact]
    public async Task ConcurrentInitializersConvergeOnOneStrictlyValidDatabase()
    {
        using var workspace = new TemporaryDirectory();
        const int initializerCount = 8;
        using var start = new Barrier(initializerCount + 1);
        var initializers = Enumerable.Range(0, initializerCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    new SqliteAppRepository(workspace.Path).Initialize();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.SignalAndWait();
        await Task.WhenAll(initializers).WaitAsync(TimeSpan.FromSeconds(10));

        var repository = new SqliteAppRepository(workspace.Path);
        Assert.NotEmpty(repository.LoadOrCreate().Semesters);
        Assert.Empty(Directory.EnumerateFiles(
            workspace.Path,
            ".course-planner-initialize-*"));
    }

    [Fact]
    public async Task ConcurrentFirstLoadsReturnTheSinglePersistedSeedWithoutBusyFailures()
    {
        using var workspace = new TemporaryDirectory();
        const int loaderCount = 8;
        using var start = new Barrier(loaderCount + 1);
        using var seedEntered = new ManualResetEventSlim();
        using var releaseSeed = new ManualResetEventSlim();
        var seedCallCount = 0;
        Func<CoursePlanner.Core.PlannerDocument> seedFactory = () =>
        {
            Interlocked.Increment(ref seedCallCount);
            seedEntered.Set();
            if (!releaseSeed.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The concurrent initialization seed was not released.");
            return CoursePlanner.Core.SeedData.Create("Concurrent first seed", "Concurrent first plan");
        };
        var loaders = Enumerable.Range(0, loaderCount)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    return new SqliteAppRepository(workspace.Path, seedFactory).LoadOrCreate();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.SignalAndWait();
        try
        {
            Assert.True(seedEntered.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseSeed.Set();
        }
        var documents = await Task.WhenAll(loaders).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, Volatile.Read(ref seedCallCount));
        Assert.All(documents, document =>
        {
            Assert.Equal("Concurrent first seed", Assert.Single(document.Semesters).SemesterName);
            Assert.Equal("Concurrent first plan", Assert.Single(document.Plans).PlanName);
        });
        var planId = Assert.Single(documents[0].Plans).PlanId;
        Assert.All(documents, document => Assert.Equal(planId, Assert.Single(document.Plans).PlanId));
        var persisted = new SqliteAppRepository(workspace.Path).LoadOrCreate();
        Assert.Equal(planId, Assert.Single(persisted.Plans).PlanId);
        Assert.Equal("Concurrent first seed", Assert.Single(persisted.Semesters).SemesterName);
    }

    [Fact]
    public void PublishedDatabaseCleansTemporarySidecarsLeftByAnInterruptedFinalizer()
    {
        using var workspace = new TemporaryDirectory();
        var repository = new SqliteAppRepository(workspace.Path);
        repository.Initialize();
        var movedTemporaryDatabase = Path.Combine(
            workspace.Path,
            $".course-planner-initialize-{Guid.NewGuid():N}.sqlite");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            File.WriteAllText(movedTemporaryDatabase + suffix, $"orphaned{suffix}");

        new SqliteAppRepository(workspace.Path).Initialize();

        Assert.All(
            new[] { "-wal", "-shm", "-journal" },
            suffix => Assert.False(File.Exists(movedTemporaryDatabase + suffix)));
        Assert.False(File.Exists(Path.Combine(
            workspace.Path,
            ".course-planner-initialize.lock")));
        Assert.NotEmpty(repository.LoadOrCreate().Semesters);
    }

    private static void CreateIncompleteDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE app_state (id TEXT PRIMARY KEY NOT NULL)";
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
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

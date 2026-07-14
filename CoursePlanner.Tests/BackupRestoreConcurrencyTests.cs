using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class BackupRestoreConcurrencyTests
{
    [Fact]
    public async Task PendingRestoreDoesNotBlockARestoreForADifferentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var firstTarget = Repository(workspace, "target-a", "Original A");
        var secondTarget = Repository(workspace, "target-b", "Original B");
        var firstSource = Repository(workspace, "different-source-a", "Candidate A");
        var secondSource = Repository(workspace, "different-source-b", "Candidate B");
        var firstBackup = Path.Combine(workspace.Path, "different-first.zip");
        var secondBackup = Path.Combine(workspace.Path, "different-second.zip");
        BackupService.CreateBackup(firstSource.DatabasePath, firstBackup);
        BackupService.CreateBackup(secondSource.DatabasePath, secondBackup);
        var automaticDirectory = workspace.CreateSubdirectory("different-automatic");

        using var first = BackupService.BeginRestoreWithPreBackup(
            firstTarget.DatabasePath,
            firstBackup,
            automaticDirectory);
        using var second = await Task.Run(() => BackupService.BeginRestoreWithPreBackup(
                secondTarget.DatabasePath,
                secondBackup,
                automaticDirectory))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task PendingRestoreDoesNotBlockABackupForADifferentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var target = Repository(workspace, "backup-target-a", "Pending candidate target");
        var restoreSource = Repository(workspace, "backup-source-a", "Pending candidate");
        var independentSource = Repository(workspace, "backup-source-b", "Independent source");
        var restoreBackup = Path.Combine(workspace.Path, "pending-restore.zip");
        var independentBackup = Path.Combine(workspace.Path, "independent.zip");
        BackupService.CreateBackup(restoreSource.DatabasePath, restoreBackup);

        using var pendingRestore = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            restoreBackup,
            workspace.CreateSubdirectory("backup-different-automatic"));

        await Task.Run(() => BackupService.CreateBackup(
                independentSource.DatabasePath,
                independentBackup))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(pendingRestore.IsCompleted);
        var restoredDirectory = workspace.CreateSubdirectory("independent-restored");
        var restoredRepository = new SqliteAppRepository(restoredDirectory);
        BackupService.RestoreWithPreBackup(
            restoredRepository.DatabasePath,
            independentBackup,
            workspace.CreateSubdirectory("independent-restored-automatic"));
        Assert.Equal("Independent source", restoredRepository.LoadOrCreate().Plans[0].PlanName);
    }

    [Fact]
    public async Task RestoresForTheSameDatabaseRemainSerializedUntilTheFirstTransactionEnds()
    {
        using var workspace = new TemporaryDirectory();
        var targetRepository = Repository(workspace, "target", "Original target");
        var firstSource = Repository(workspace, "source-a", "Candidate A");
        var secondSource = Repository(workspace, "source-b", "Candidate B");
        var firstBackup = Path.Combine(workspace.Path, "first.zip");
        var secondBackup = Path.Combine(workspace.Path, "second.zip");
        BackupService.CreateBackup(firstSource.DatabasePath, firstBackup);
        BackupService.CreateBackup(secondSource.DatabasePath, secondBackup);
        var automaticDirectory = workspace.CreateSubdirectory("automatic");

        var first = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            firstBackup,
            automaticDirectory);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(() =>
        {
            secondEntered.SetResult();
            return BackupService.BeginRestoreWithPreBackup(
                targetRepository.DatabasePath,
                secondBackup,
                automaticDirectory);
        });

        BackupRestoreTransaction? second = null;
        try
        {
            await secondEntered.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(secondTask.IsCompleted);

            first.Rollback();
            second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
            second.Rollback();

            Assert.Equal(
                "Original target",
                new SqliteAppRepository(Path.GetDirectoryName(targetRepository.DatabasePath)!).LoadOrCreate()
                    .Plans[0].PlanName);
        }
        finally
        {
            if (!first.IsCompleted)
                first.Rollback();
            if (second is null && secondTask.IsCompletedSuccessfully)
                second = await secondTask;
            if (second is not null && !second.IsCompleted)
                second.Rollback();
        }
    }

    [Fact]
    public async Task BackupForTheSameDatabaseWaitsUntilAPendingRestoreIsResolved()
    {
        using var workspace = new TemporaryDirectory();
        var targetRepository = Repository(workspace, "target-backup", "Original target");
        var sourceRepository = Repository(workspace, "source-backup", "Pending candidate");
        var sourceBackup = Path.Combine(workspace.Path, "pending-source.zip");
        var concurrentBackup = Path.Combine(workspace.Path, "concurrent.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, sourceBackup);

        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            sourceBackup,
            workspace.CreateSubdirectory("automatic-backup"));
        var backupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupTask = Task.Run(() =>
        {
            backupEntered.SetResult();
            BackupService.CreateBackup(targetRepository.DatabasePath, concurrentBackup);
        });

        try
        {
            await backupEntered.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(backupTask.IsCompleted);

            transaction.Rollback();
            await backupTask.WaitAsync(TimeSpan.FromSeconds(10));

            var restoredDirectory = workspace.CreateSubdirectory("restored-concurrent-backup");
            var restoredRepository = new SqliteAppRepository(restoredDirectory);
            BackupService.RestoreWithPreBackup(
                restoredRepository.DatabasePath,
                concurrentBackup,
                workspace.CreateSubdirectory("restored-automatic"));
            Assert.Equal("Original target", restoredRepository.LoadOrCreate().Plans[0].PlanName);
        }
        finally
        {
            if (!transaction.IsCompleted)
                transaction.Rollback();
            await backupTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void TerminalRetryCannotChooseAnIntentForANewTransaction()
    {
        using var workspace = new TemporaryDirectory();
        var target = Repository(workspace, "retry-without-intent-target", "Original A");
        var source = Repository(workspace, "retry-without-intent-source", "Candidate B");
        var backupPath = Path.Combine(workspace.Path, "retry-without-intent.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        using var transaction = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("retry-without-intent-automatic"));

        Assert.Throws<InvalidOperationException>(transaction.RetryTerminalAction);

        Assert.False(transaction.IsCompleted);
        Assert.True(Directory.Exists(transaction.RecoveryDirectory));
        Assert.Equal("Candidate B", target.LoadExistingForVerification().Plans[0].PlanName);

        transaction.Rollback();

        Assert.True(transaction.IsCompleted);
        Assert.Equal("Original A", target.LoadExistingForVerification().Plans[0].PlanName);
    }

    [Fact]
    public void CommitRejectsAReplacedCandidateLocksItsIntentAndCanRetryOnlyAfterExactCandidateReturn()
    {
        using var workspace = new TemporaryDirectory();
        var target = Repository(workspace, "commit-cas-target", "Original A");
        var source = Repository(workspace, "commit-cas-source", "Candidate B");
        var backupPath = Path.Combine(workspace.Path, "commit-cas.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        var transaction = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("commit-cas-automatic"));
        var exactCandidateBytes = File.ReadAllBytes(target.DatabasePath);
        var third = TestDocumentFactory.CreatePopulated();
        third.Plans[0].PlanName = "Concurrent third state C";
        target.Save(third, "concurrent.third");

        var exception = Assert.Throws<BackupRestoreCandidateChangedException>(transaction.Commit);

        Assert.False(transaction.IsCompleted);
        Assert.True(Directory.Exists(exception.RecoveryDirectory));
        Assert.Equal("Concurrent third state C", target.LoadExistingForVerification().Plans[0].PlanName);
        Assert.Throws<InvalidOperationException>(transaction.Rollback);

        RestoreExactDatabaseBytes(target.DatabasePath, exactCandidateBytes);
        transaction.Commit();

        Assert.True(transaction.IsCompleted);
        Assert.Equal("Candidate B", target.LoadExistingForVerification().Plans[0].PlanName);
        Assert.False(Directory.Exists(exception.RecoveryDirectory));
    }

    [Fact]
    public void RollbackRejectsAReplacedCandidateWithoutOverwritingTheThirdState()
    {
        using var workspace = new TemporaryDirectory();
        var target = Repository(workspace, "rollback-cas-target", "Original A");
        var source = Repository(workspace, "rollback-cas-source", "Candidate B");
        var backupPath = Path.Combine(workspace.Path, "rollback-cas.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        var transaction = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("rollback-cas-automatic"));
        var exactCandidateBytes = File.ReadAllBytes(target.DatabasePath);
        var third = TestDocumentFactory.CreatePopulated();
        third.Plans[0].PlanName = "Rollback must preserve third state C";
        target.Save(third, "concurrent.third");

        var exception = Assert.Throws<BackupRestoreCandidateChangedException>(transaction.Rollback);

        Assert.False(transaction.IsCompleted);
        Assert.Equal(
            "Rollback must preserve third state C",
            target.LoadExistingForVerification().Plans[0].PlanName);
        Assert.Throws<InvalidOperationException>(transaction.Commit);
        Assert.True(Directory.Exists(exception.RecoveryDirectory));

        RestoreExactDatabaseBytes(target.DatabasePath, exactCandidateBytes);
        transaction.Rollback();

        Assert.True(transaction.IsCompleted);
        Assert.Equal("Original A", target.LoadExistingForVerification().Plans[0].PlanName);
    }

    [Fact]
    public async Task FailedRollbackRetainsThePathLeaseUntilSameIntentRetryCompletes()
    {
        using var workspace = new TemporaryDirectory();
        var target = Repository(workspace, "lease-poison-target", "Original A");
        var source = Repository(workspace, "lease-poison-source", "Candidate B");
        var backupPath = Path.Combine(workspace.Path, "lease-poison.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        var automaticDirectory = workspace.CreateSubdirectory("lease-poison-automatic");
        var transaction = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            automaticDirectory);

        using (new FileStream(
                   target.DatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(transaction.Rollback);
            Assert.False(transaction.IsCompleted);
            Assert.True(Directory.Exists(transaction.RecoveryDirectory));
            Assert.Throws<InvalidOperationException>(transaction.Commit);

            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await Task.Run(() => BackupService.BeginRestoreWithPreBackup(
                        target.DatabasePath,
                        backupPath,
                        automaticDirectory))
                    .WaitAsync(TimeSpan.FromSeconds(10));
            });
        }

        transaction.Rollback();

        Assert.True(transaction.IsCompleted);
        Assert.Equal("Original A", target.LoadExistingForVerification().Plans[0].PlanName);
        using var later = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            automaticDirectory);
        later.Rollback();
    }

    private static SqliteAppRepository Repository(
        TemporaryDirectory workspace,
        string directoryName,
        string planName)
    {
        var repository = new SqliteAppRepository(workspace.CreateSubdirectory(directoryName));
        var document = TestDocumentFactory.CreatePopulated();
        document.Plans[0].PlanName = planName;
        repository.Save(document, "test.seed");
        return repository;
    }

    private static void RestoreExactDatabaseBytes(string databasePath, byte[] bytes)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            File.Delete(databasePath + suffix);
        File.WriteAllBytes(databasePath, bytes);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubdirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

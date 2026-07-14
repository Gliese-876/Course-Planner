using System.Diagnostics;
using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class UndoRedoCheckpointTests
{
    private readonly ITestOutputHelper _output;

    public UndoRedoCheckpointTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CheckpointCreationAndRestoreDoNotCloneImmutableSnapshotPayloads()
    {
        var document = CreateLargeValidDocument(
            courseCount: 2_200,
            notesLength: 1_900,
            minimumTextCharacters: 4_000_000);
        var history = new PlannerUndoRedo();
        for (var index = 0; index < 3; index++)
        {
            document.Plans[0].PlanName = $"Large state {index}";
            Assert.True(history.Capture(document));
        }

        Assert.InRange(
            history.HistoryBytes,
            10L * 1024 * 1024,
            PlannerUndoRedo.MaxHistoryBytes);

        var beforeCreate = GC.GetAllocatedBytesForCurrentThread();
        var checkpoint = history.CreateCheckpoint();
        var createAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeCreate;

        history.Clear();
        var beforeRestore = GC.GetAllocatedBytesForCurrentThread();
        history.RestoreCheckpoint(checkpoint);
        var restoreAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeRestore;

        _output.WriteLine(
            "History: {0:N0} bytes; checkpoint: {1:N0} bytes allocated; restore: {2:N0} bytes allocated.",
            history.HistoryBytes,
            createAllocations,
            restoreAllocations);

        Assert.True(
            createAllocations < 512 * 1024,
            $"Creating a checkpoint allocated {createAllocations:N0} bytes for " +
            $"{history.HistoryBytes:N0} bytes of immutable history.");
        Assert.True(
            restoreAllocations < 512 * 1024,
            $"Restoring a checkpoint allocated {restoreAllocations:N0} bytes for " +
            $"{history.HistoryBytes:N0} bytes of immutable history.");
    }

    [Fact]
    public void CheckpointRemainsStableWhenLiveHistoryIsClearedAndRebuilt()
    {
        var document = CreateSmallDocument("A");
        var history = new PlannerUndoRedo();
        Assert.True(history.Capture(document));
        document.Plans[0].PlanName = "B";
        Assert.True(history.Capture(document));
        var checkpoint = history.CreateCheckpoint();

        history.Clear();
        document.Plans[0].PlanName = "unrelated live history";
        Assert.True(history.Capture(document));

        history.RestoreCheckpoint(checkpoint);
        document.Plans[0].PlanName = "current";
        var restoredB = history.Undo(document);
        Assert.NotNull(restoredB);
        Assert.Equal("B", restoredB.Plans[0].PlanName);

        var restoredA = history.Undo(restoredB);
        Assert.NotNull(restoredA);
        Assert.Equal("A", restoredA.Plans[0].PlanName);
    }

    [Fact]
    public void SuccessfulLargeDocumentSaveDoesNotCloneItsSerializedStateAfterPersistence()
    {
        var document = CreateLargeValidDocument(
            courseCount: 2_200,
            notesLength: 1_900,
            minimumTextCharacters: 4_000_000);
        DocumentConsistencyService.Ensure(document);
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => document,
            saveDocument: (_, _) => { });

        for (var index = 0; index < 2; index++)
        {
            session.CaptureUndo();
            session.Document.Plans[0].PlanName = $"Committed large state {index}";
            session.Save($"large.{index}");
        }

        session.CaptureUndo();
        session.Document.Plans[0].PlanName = "Measured large state";
        var baselineStart = GC.GetAllocatedBytesForCurrentThread();
        DocumentConsistencyService.Ensure(session.Document);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(session.Document, JsonDefaults.CompactOptions);
        var baselineAllocations = GC.GetAllocatedBytesForCurrentThread() - baselineStart;

        var stopwatch = Stopwatch.StartNew();
        var saveStart = GC.GetAllocatedBytesForCurrentThread();
        session.Save("large.measured");
        var saveAllocations = GC.GetAllocatedBytesForCurrentThread() - saveStart;
        stopwatch.Stop();

        _output.WriteLine(
            "Document: {0:N0} bytes; baseline serialization: {1:N0} bytes; " +
            "session save: {2:N0} bytes and {3:N1} ms.",
            serialized.Length,
            baselineAllocations,
            saveAllocations,
            stopwatch.Elapsed.TotalMilliseconds);
        Assert.True(
            saveAllocations <= baselineAllocations + 512 * 1024,
            $"Saving allocated {saveAllocations:N0} bytes versus a {baselineAllocations:N0}-byte " +
            "consistency-and-serialization baseline; the persisted snapshot was cloned.");
    }

    [Fact]
    public void FiftyEntryRollbackDoesNotCloneTheEntirePersistedHistory()
    {
        var document = CreateLargeValidDocument(courseCount: 50, notesLength: 1_800);
        DocumentConsistencyService.Ensure(document);
        var failSave = false;
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => document,
            saveDocument: (_, _) =>
            {
                if (failSave)
                    throw new IOException("measured rollback failure");
            });
        for (var index = 0; index < PlannerUndoRedo.MaxHistoryEntries; index++)
        {
            session.CaptureUndo();
            session.Document.Plans[0].PlanName = $"Rollback history {index}";
            session.Save($"history.{index}");
        }

        Assert.Equal(PlannerUndoRedo.MaxHistoryEntries, session.UndoRedo.UndoCount);
        var persistedName = session.Document.Plans[0].PlanName;
        var persistedHistoryBytes = session.UndoRedo.HistoryBytes;
        session.CaptureUndo();
        session.Document.Plans[0].PlanName = "must roll back";
        failSave = true;

        var stopwatch = Stopwatch.StartNew();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<IOException>(() => session.Save("history.rollback"));
        var rollbackAllocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        stopwatch.Stop();

        _output.WriteLine(
            "50-entry history: {0:N0} bytes; rollback: {1:N0} bytes allocated and {2:N1} ms.",
            persistedHistoryBytes,
            rollbackAllocations,
            stopwatch.Elapsed.TotalMilliseconds);
        Assert.Equal(persistedName, session.Document.Plans[0].PlanName);
        Assert.Equal(PlannerUndoRedo.MaxHistoryEntries, session.UndoRedo.UndoCount);
        Assert.Equal(persistedHistoryBytes, session.UndoRedo.HistoryBytes);
        Assert.True(
            rollbackAllocations < persistedHistoryBytes / 2,
            $"Rollback allocated {rollbackAllocations:N0} bytes for a " +
            $"{persistedHistoryBytes:N0}-byte immutable history.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Synchronous rollback took {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
    }

    [Fact]
    public void NearCapacityRealRepositorySaveCompletesWithinTheSynchronousSafetyBudget()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var document = CreateLargeValidDocument(
                courseCount: 2_200,
                notesLength: 1_900,
                minimumTextCharacters: 4_000_000);
            DocumentConsistencyService.Ensure(document);
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(repository, loadDocument: () => document);
            session.CaptureUndo();
            session.Document.Plans[0].PlanName = "Repository warmup";
            session.Save("large.repository.warmup");

            session.CaptureUndo();
            session.Document.Plans[0].PlanName = "Repository measured";
            var stopwatch = Stopwatch.StartNew();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            session.Save("large.repository.measured");
            var allocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            stopwatch.Stop();

            _output.WriteLine(
                "Near-capacity repository save: {0:N0} bytes allocated and {1:N1} ms.",
                allocations,
                stopwatch.Elapsed.TotalMilliseconds);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Near-capacity synchronous repository save took {stopwatch.Elapsed.TotalSeconds:N2}s.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AuxiliaryStateIdentityParticipatesInDuplicateDetectionAndUndo()
    {
        var document = CreateSmallDocument("same document");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(0);
        history.RegisterStateParticipant(participant, document);

        Assert.True(history.Capture(document));
        Assert.False(history.Capture(document));

        participant.Current = new TestState(1);
        Assert.True(history.Capture(document));
        Assert.Equal(2, history.UndoCount);

        var restored = history.Undo(document);

        Assert.NotNull(restored);
        Assert.Equal("same document", restored.Plans[0].PlanName);
        Assert.Equal(0, participant.Current.Value);
    }

    [Fact]
    public void EquivalentFreshAuxiliarySnapshotsDoNotCreatePhantomHistory()
    {
        var document = CreateSmallDocument("same document");
        var history = new PlannerUndoRedo();
        var participant = new CanonicalizingTestStateParticipant();
        history.RegisterStateParticipant(participant, document);

        Assert.True(history.Capture(document));
        Assert.False(history.Capture(document));

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void ThrowingAuxiliaryEqualityLeavesHistoryUntouchedAndCanBeRetried()
    {
        var document = CreateSmallDocument("equality");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(0);
        history.RegisterStateParticipant(participant, document);
        Assert.True(history.Capture(document));
        var historyBytes = history.HistoryBytes;
        participant.ThrowOnEquivalence = true;

        Assert.Throws<InvalidOperationException>(() => history.Capture(document));

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(historyBytes, history.HistoryBytes);
        participant.ThrowOnEquivalence = false;
        Assert.False(history.Capture(document));
    }

    [Fact]
    public void ThrowingAuxiliaryRestoreLeavesUndoRedoUntouchedAndCanBeRetried()
    {
        var document = CreateSmallDocument("before");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(0);
        history.RegisterStateParticipant(participant, document);
        Assert.True(history.Capture(document));
        var historyBytes = history.HistoryBytes;
        document.Plans[0].PlanName = "current";
        participant.Current = new TestState(1);
        participant.ThrowOnRestore = true;

        Assert.Throws<InvalidOperationException>(() => history.Undo(document));

        Assert.Equal("current", document.Plans[0].PlanName);
        Assert.Equal(1, participant.Current.Value);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(historyBytes, history.HistoryBytes);
        participant.ThrowOnRestore = false;
        var restored = history.Undo(document);
        Assert.NotNull(restored);
        Assert.Equal("before", restored.Plans[0].PlanName);
        Assert.Equal(0, participant.Current.Value);

        historyBytes = history.HistoryBytes;
        participant.ThrowOnRestore = true;
        Assert.Throws<InvalidOperationException>(() => history.Redo(restored));
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);
        Assert.Equal(historyBytes, history.HistoryBytes);
        Assert.Equal(0, participant.Current.Value);

        participant.ThrowOnRestore = false;
        var redone = history.Redo(restored);
        Assert.NotNull(redone);
        Assert.Equal("current", redone.Plans[0].PlanName);
        Assert.Equal(1, participant.Current.Value);
    }

    [Fact]
    public void ThrowingCheckpointRestoreAndClearLeaveHistoryUntouchedAndCanBeRetried()
    {
        var document = CreateSmallDocument("checkpoint before");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(0);
        history.RegisterStateParticipant(participant, document);
        Assert.True(history.Capture(document));
        var checkpoint = history.CreateCheckpoint(document);
        document.Plans[0].PlanName = "checkpoint current";
        participant.Current = new TestState(1);
        Assert.True(history.Capture(document));
        var historyBytes = history.HistoryBytes;
        participant.ThrowOnRestore = true;

        Assert.Throws<InvalidOperationException>(() => history.RestoreCheckpoint(checkpoint));

        Assert.Equal(2, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(historyBytes, history.HistoryBytes);
        Assert.Equal(1, participant.Current.Value);
        participant.ThrowOnRestore = false;
        history.RestoreCheckpoint(checkpoint);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, participant.Current.Value);

        participant.ThrowOnClear = true;
        historyBytes = history.HistoryBytes;
        Assert.Throws<InvalidOperationException>(history.Clear);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(historyBytes, history.HistoryBytes);
        Assert.Equal(0, participant.Current.Value);
        participant.ThrowOnClear = false;
        history.Clear();
        Assert.Equal(0, history.UndoCount);
        Assert.Same(TestStateParticipant.Cleared, participant.Current);
    }

    [Fact]
    public void CheckpointRestoresCurrentAuxiliaryStateAndClearResetsIt()
    {
        var document = CreateSmallDocument("checkpoint");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(7);
        history.RegisterStateParticipant(participant, document);
        Assert.True(history.Capture(document));
        var checkpoint = history.CreateCheckpoint(document);

        participant.Current = new TestState(8);
        history.RestoreCheckpoint(checkpoint);

        Assert.Equal(7, participant.Current.Value);
        Assert.Equal(1, history.UndoCount);

        history.Clear();

        Assert.Same(TestStateParticipant.Cleared, participant.Current);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(0, history.HistoryBytes);
    }

    [Fact]
    public void ApplyingCheckpointCurrentStateUsesCapturedIdentityWithoutChangingHistory()
    {
        var document = CreateSmallDocument("canonical state");
        var history = new PlannerUndoRedo();
        var participant = new CanonicalizingTestStateParticipant();
        history.RegisterStateParticipant(participant, document);
        Assert.True(history.Capture(document));
        var beforeUndoCount = history.UndoCount;
        var beforeBytes = history.HistoryBytes;
        var checkpoint = history.CreateCheckpoint(document);
        var captured = participant.LastCaptured;

        history.ApplyCheckpointCurrentState(checkpoint);

        Assert.NotNull(captured);
        Assert.Same(captured, participant.Current);
        Assert.Equal(beforeUndoCount, history.UndoCount);
        Assert.Equal(beforeBytes, history.HistoryBytes);
    }

    [Fact]
    public void CrossInstanceCheckpointRestoreAndApplyAreRejectedWithoutSideEffects()
    {
        var sourceDocument = CreateSmallDocument("source");
        var source = new PlannerUndoRedo();
        Assert.True(source.Capture(sourceDocument));
        var foreignCheckpoint = source.CreateCheckpoint();

        var targetDocument = CreateSmallDocument("target A");
        var target = new PlannerUndoRedo();
        Assert.True(target.Capture(targetDocument));
        targetDocument.Plans[0].PlanName = "target B";
        Assert.True(target.Capture(targetDocument));
        var undoCount = target.UndoCount;
        var redoCount = target.RedoCount;
        var historyBytes = target.HistoryBytes;

        Assert.Throws<ArgumentException>(() => target.RestoreCheckpoint(foreignCheckpoint));
        Assert.Throws<ArgumentException>(() => target.ApplyCheckpointCurrentState(foreignCheckpoint));

        Assert.Equal(undoCount, target.UndoCount);
        Assert.Equal(redoCount, target.RedoCount);
        Assert.Equal(historyBytes, target.HistoryBytes);
        var restored = target.Undo(targetDocument);
        Assert.NotNull(restored);
        Assert.Equal("target A", restored.Plans[0].PlanName);
    }

    [Fact]
    public void TrimmedHistoryRetainsTheAuxiliaryStateForEveryRetainedEntry()
    {
        var document = CreateSmallDocument("unchanged document");
        var history = new PlannerUndoRedo();
        var participant = new TestStateParticipant(-1);
        history.RegisterStateParticipant(participant, document);
        for (var value = 0; value < PlannerUndoRedo.MaxHistoryEntries + 10; value++)
        {
            participant.Current = new TestState(value);
            Assert.True(history.Capture(document));
        }

        Assert.Equal(PlannerUndoRedo.MaxHistoryEntries, history.UndoCount);
        var restored = history.Undo(document);

        Assert.NotNull(restored);
        Assert.Equal(PlannerUndoRedo.MaxHistoryEntries + 8, participant.Current.Value);
    }

    [Fact]
    public void ParticipantCaptureFailureRollsBackBeforeTheSaveDelegateIsCalled()
    {
        var document = CreateSmallDocument("persisted");
        var saveCalls = 0;
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => document,
            saveDocument: (_, _) => saveCalls++);
        var participant = new TestStateParticipant(0);
        session.UndoRedo.RegisterStateParticipant(participant, session.Document);
        session.CaptureUndo();
        session.Document.Plans[0].PlanName = "must roll back";
        participant.ThrowOnCapture = true;

        Assert.Throws<InvalidOperationException>(() => session.Save("capture.failure"));

        Assert.Equal(0, saveCalls);
        Assert.Equal("persisted", session.Document.Plans[0].PlanName);
        Assert.False(session.UndoRedo.CanUndo);
        Assert.False(session.UndoRedo.CanRedo);
        Assert.Equal(0, participant.Current.Value);
    }

    [Fact]
    public void FailedParticipantRegistrationLeavesHistoryUnregistered()
    {
        var document = CreateSmallDocument("registration");
        var history = new PlannerUndoRedo();
        var rejected = new TestStateParticipant(1) { ThrowOnCapture = true };

        Assert.Throws<InvalidOperationException>(() =>
            history.RegisterStateParticipant(rejected, document));

        var accepted = new TestStateParticipant(2);
        history.RegisterStateParticipant(accepted, document);
        Assert.True(history.Capture(document));
        Assert.Equal(2, accepted.Current.Value);
    }

    [Fact]
    public void SuccessfulSaveAlignsCurrentAuxiliaryStateToThePersistedCheckpointIdentity()
    {
        var document = CreateSmallDocument("persisted");
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => document,
            saveDocument: (_, _) => { });
        var participant = new CanonicalizingTestStateParticipant();
        session.UndoRedo.RegisterStateParticipant(participant, session.Document);
        session.Document.Plans[0].PlanName = "committed";

        session.Save("canonical.commit");

        Assert.NotNull(participant.LastCaptured);
        Assert.Same(participant.LastCaptured, participant.Current);
    }

    [Fact]
    public void SuccessfulEquivalentReplaceAlignsCurrentAuxiliaryStateToTheAcceptedCheckpointIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var document = CreateSmallDocument("equivalent replacement");
            var saveCalls = 0;
            var session = new DocumentSession(
                new SqliteAppRepository(directory),
                loadDocument: () => document,
                saveDocument: (_, _) => saveCalls++);
            var participant = new CanonicalizingTestStateParticipant();
            session.UndoRedo.RegisterStateParticipant(participant, session.Document);
            var replacement = JsonDefaults.Clone(session.Document);

            session.ReplaceDocument(replacement, "canonical.equivalent-replace");

            Assert.Same(replacement, session.Document);
            Assert.Equal(0, saveCalls);
            Assert.NotNull(participant.LastCaptured);
            Assert.Same(participant.LastCaptured, participant.Current);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulReloadAlignsCurrentAuxiliaryStateToThePersistedCheckpointIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var storedDocument = CreateSmallDocument("before reload");
            var session = new DocumentSession(
                new SqliteAppRepository(directory),
                loadDocument: () => JsonDefaults.Clone(storedDocument),
                saveDocument: (_, _) => { });
            var participant = new CanonicalizingTestStateParticipant();
            session.UndoRedo.RegisterStateParticipant(participant, session.Document);
            storedDocument = CreateSmallDocument("reloaded state");

            session.ReloadFromRepository();

            Assert.Equal("reloaded state", session.Document.Plans[0].PlanName);
            Assert.NotNull(participant.LastCaptured);
            Assert.Same(participant.LastCaptured, participant.Current);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulRestoreAlignsCurrentAuxiliaryStateBeforeCommittingTheTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceDirectory = Path.Combine(root, "source");
            var targetDirectory = Path.Combine(root, "target");
            var automaticDirectory = Path.Combine(root, "automatic");
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var candidate = CreateSmallDocument("restored state");
            sourceRepository.Save(candidate, "source.seed");
            targetRepository.Save(CreateSmallDocument("original state"), "target.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            var participant = new CanonicalizingTestStateParticipant();
            session.UndoRedo.RegisterStateParticipant(participant, session.Document);

            using var transaction = session.BeginBackupRestore(backupPath, automaticDirectory);
            session.ApplyBackupRestore(transaction);

            Assert.True(transaction.IsCompleted);
            Assert.Equal("restored state", session.Document.Plans[0].PlanName);
            Assert.NotNull(participant.LastCaptured);
            Assert.Same(participant.LastCaptured, participant.Current);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static PlannerDocument CreateLargeValidDocument(
        int courseCount,
        int notesLength,
        int minimumTextCharacters = 0)
    {
        var document = CreateSmallDocument("Large state");
        document.CourseLibrary = Enumerable.Range(0, courseCount)
            .Select(index => new CourseOffering
            {
                OfferingId = $"course-{index}",
                SemesterId = "semester",
                CourseName = $"Course {index}",
                Notes = new string((char)('a' + index % 26), notesLength),
                Color = "#C3637A"
            })
            .ToList();
        Assert.InRange(
            PlannerDocumentTextCapacity.Count(document),
            minimumTextCharacters,
            PlannerDataLimits.MaxAggregateTextCharacters);
        return document;
    }

    private static PlannerDocument CreateSmallDocument(string planName)
    {
        var semester = new Semester
        {
            SemesterId = "semester",
            SemesterName = "Semester",
            StartDate = new DateOnly(2026, 1, 5),
            EndDate = new DateOnly(2026, 5, 3),
            WeekStartDay = WeekStartDay.Monday,
            WeekCount = 17,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        var plan = new SelectionPlan
        {
            PlanId = "plan",
            SemesterId = semester.SemesterId,
            PlanName = planName,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ModifiedAt = DateTimeOffset.UnixEpoch
        };
        return new PlannerDocument
        {
            Semesters = [semester],
            Plans = [plan],
            Settings = new AppSettings
            {
                CurrentSemesterId = semester.SemesterId,
                CurrentPlanId = plan.PlanId,
                OpenPlanIds = [plan.PlanId]
            }
        };
    }

    private sealed class TestState(int value) : PlannerUndoRedoState
    {
        public int Value { get; } = value;
    }

    private sealed class TestStateParticipant(int initialValue)
        : IPlannerUndoRedoStateParticipant<TestState>
    {
        public static readonly TestState Cleared = new(int.MinValue);

        public TestState Current { get; set; } = new(initialValue);
        public bool ThrowOnCapture { get; set; }
        public bool ThrowOnEquivalence { get; set; }
        public bool ThrowOnRestore { get; set; }
        public bool ThrowOnClear { get; set; }

        public TestState CaptureState(PlannerDocument document)
        {
            if (ThrowOnCapture)
                throw new InvalidOperationException("Injected participant capture failure.");
            return Current;
        }

        public bool AreEquivalent(TestState left, TestState right)
        {
            if (ThrowOnEquivalence)
                throw new InvalidOperationException("Injected participant equality failure.");
            return left.Value == right.Value;
        }

        public void RestoreState(TestState state)
        {
            if (ThrowOnRestore)
                throw new InvalidOperationException("Injected participant restore failure.");
            Current = state;
        }

        public void ClearState()
        {
            if (ThrowOnClear)
                throw new InvalidOperationException("Injected participant clear failure.");
            Current = Cleared;
        }
    }

    private sealed class CanonicalizingTestStateParticipant
        : IPlannerUndoRedoStateParticipant<TestState>
    {
        public TestState Current { get; private set; } = new(-1);
        public TestState? LastCaptured { get; private set; }

        public TestState CaptureState(PlannerDocument document)
        {
            LastCaptured = new TestState(document.Plans.Count);
            return LastCaptured;
        }

        public bool AreEquivalent(TestState left, TestState right) => left.Value == right.Value;

        public void RestoreState(TestState state) => Current = state;

        public void ClearState() => Current = TestStateParticipant.Cleared;
    }
}

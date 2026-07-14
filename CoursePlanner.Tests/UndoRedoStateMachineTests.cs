using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class UndoRedoStateMachineTests
{
    [Fact]
    public void UndoRedoMatchesReferenceModelAcrossFixedSeedRandomizedInterleavings()
    {
        const int seed = 0x51A7E;
        var random = new Random(seed);
        var document = CreateDocument("initial");
        var actual = new PlannerUndoRedo();
        var expected = new ReferenceHistory();
        var checkpoints = new List<(PlannerUndoRedo.Checkpoint Actual, ReferenceCheckpoint Expected)>();

        for (var step = 0; step < 10_000; step++)
        {
            switch (random.Next(10))
            {
                case 0:
                case 1:
                    document.Plans[0].PlanName = $"state-{step}-{random.Next(17)}";
                    document.Settings.Theme = (ThemeMode)random.Next(3);
                    break;
                case 2:
                    Assert.Equal(expected.Capture(document), actual.Capture(document));
                    break;
                case 3:
                    {
                        var expectedDocument = expected.Undo(document);
                        var actualDocument = actual.Undo(document);
                        AssertDocumentsEqual(expectedDocument, actualDocument, seed, step);
                        if (actualDocument is not null)
                            document = actualDocument;
                        break;
                    }
                case 4:
                    {
                        var expectedDocument = expected.Redo(document);
                        var actualDocument = actual.Redo(document);
                        AssertDocumentsEqual(expectedDocument, actualDocument, seed, step);
                        if (actualDocument is not null)
                            document = actualDocument;
                        break;
                    }
                case 5:
                    actual.Clear();
                    expected.Clear();
                    break;
                case 6:
                    checkpoints.Add((actual.CreateCheckpoint(), expected.CreateCheckpoint()));
                    if (checkpoints.Count > 16)
                        checkpoints.RemoveAt(0);
                    break;
                case 7 when checkpoints.Count > 0:
                    {
                        var checkpoint = checkpoints[random.Next(checkpoints.Count)];
                        actual.RestoreCheckpoint(checkpoint.Actual);
                        expected.RestoreCheckpoint(checkpoint.Expected);
                        break;
                    }
                default:
                    var first = actual.Capture(document);
                    var expectedFirst = expected.Capture(document);
                    var second = actual.Capture(document);
                    var expectedSecond = expected.Capture(document);
                    Assert.Equal(expectedFirst, first);
                    Assert.Equal(expectedSecond, second);
                    break;
            }

            Assert.Equal(expected.UndoCount, actual.UndoCount);
            Assert.Equal(expected.RedoCount, actual.RedoCount);
            Assert.Equal(expected.HistoryBytes, actual.HistoryBytes);
            Assert.Equal(expected.UndoCount > 0, actual.CanUndo);
            Assert.Equal(expected.RedoCount > 0, actual.CanRedo);
            Assert.InRange(actual.UndoCount + actual.RedoCount, 0, PlannerUndoRedo.MaxHistoryEntries);
            Assert.InRange(actual.HistoryBytes, 0, PlannerUndoRedo.MaxHistoryBytes);
        }
    }

    [Fact]
    public void DocumentSessionMatchesTransactionalReferenceModelAcrossFixedSeedFailuresAndBranches()
    {
        const int seed = 0xD0C5E55;
        var random = new Random(seed);
        var initial = CreateDocument("initial");
        var storage = new MemoryStorage(initial);
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(repository, storage.Load, storage.Save);
        var expected = new ReferenceSession(initial);
        var changedCalls = 0;
        var rollbackCalls = 0;
        session.Changed += (_, _) => changedCalls++;
        session.RolledBack += (_, _) => rollbackCalls++;

        for (var step = 0; step < 2_000; step++)
        {
            var operation = random.Next(11);
            var injectFailure = random.Next(5) == 0;
            switch (operation)
            {
                case 0:
                case 1:
                case 2:
                    {
                        var notify = operation != 2;
                        var newName = $"mutated-{step}-{random.Next(31)}";
                        storage.FailSave = injectFailure;
                        var thrown = Record.Exception(() =>
                        {
                            session.CaptureUndo();
                            session.Document.Plans[0].PlanName = newName;
                            session.Save($"mutate.{step}", notify);
                        });
                        storage.FailSave = false;
                        if (injectFailure)
                        {
                            Assert.IsType<IOException>(thrown);
                            expected.RollBack();
                        }
                        else
                        {
                            Assert.Null(thrown);
                            expected.CommitMutation(newName, $"mutate.{step}", notify);
                        }
                        break;
                    }
                case 3:
                    session.CaptureUndo();
                    session.Save($"noop.{step}");
                    expected.NoOp();
                    break;
                case 4:
                    {
                        var hadUndo = expected.UndoCount > 0;
                        storage.FailSave = injectFailure;
                        var thrown = Record.Exception(() => session.Undo());
                        storage.FailSave = false;
                        if (injectFailure && hadUndo)
                        {
                            Assert.IsType<IOException>(thrown);
                            expected.RollBack();
                        }
                        else
                        {
                            Assert.Null(thrown);
                            expected.Undo();
                        }
                        break;
                    }
                case 5:
                    {
                        var hadRedo = expected.RedoCount > 0;
                        storage.FailSave = injectFailure;
                        var thrown = Record.Exception(() => session.Redo());
                        storage.FailSave = false;
                        if (injectFailure && hadRedo)
                        {
                            Assert.IsType<IOException>(thrown);
                            expected.RollBack();
                        }
                        else
                        {
                            Assert.Null(thrown);
                            expected.Redo();
                        }
                        break;
                    }
                case 6:
                    {
                        var replacement = CreateDocument($"replacement-{step}");
                        storage.FailSave = injectFailure;
                        var thrown = Record.Exception(() => session.ReplaceDocument(replacement, $"replace.{step}"));
                        storage.FailSave = false;
                        if (injectFailure)
                        {
                            Assert.IsType<IOException>(thrown);
                            expected.RollBack();
                        }
                        else
                        {
                            Assert.Null(thrown);
                            expected.Replace(replacement, $"replace.{step}");
                        }
                        break;
                    }
                case 7:
                    {
                        var external = CreateDocument($"external-{step}");
                        storage.ReplaceExternally(external);
                        expected.ReplaceStorageExternally(external);
                        storage.FailLoad = injectFailure;
                        var thrown = Record.Exception(session.ReloadFromRepository);
                        storage.FailLoad = false;
                        if (injectFailure)
                        {
                            Assert.IsType<IOException>(thrown);
                            Assert.True(session.IsStorageConsistencyUnknown);

                            // A failed reload leaves the observed durable state
                            // unknown by design. Exercise the sole recovery
                            // boundary immediately so the reference model never
                            // authorizes a later write over unobserved storage.
                            session.ReloadFromRepository();
                            expected.Reload();
                            Assert.False(session.IsStorageConsistencyUnknown);
                        }
                        else
                        {
                            Assert.Null(thrown);
                            expected.Reload();
                        }
                        break;
                    }
                case 8:
                    {
                        storage.RejectSave = true;
                        var thrown = Record.Exception(() =>
                        {
                            session.CaptureUndo();
                            session.Document.Plans[0].PlanName = $"capacity-rejected-{step}";
                            session.Save($"capacity.{step}");
                        });
                        storage.RejectSave = false;
                        Assert.IsType<InvalidDataException>(thrown);
                        expected.RollBack();
                        break;
                    }
                case 9:
                    {
                        var equivalent = JsonDefaults.Clone(session.Document);
                        session.ReplaceDocument(equivalent, $"equivalent.replace.{step}");
                        expected.ReplaceWithEquivalentGraph();
                        break;
                    }
                default:
                    session.CaptureUndo();
                    session.Document.Plans[0].PlanName = session.Document.Plans[0].PlanName;
                    session.Save($"identity-noop.{step}", notify: false);
                    expected.NoOp();
                    break;
            }

            Assert.Equal(expected.CurrentJson, Snapshot(session.Document));
            Assert.Equal(expected.StorageJson, Snapshot(storage.StoredDocument));
            Assert.Equal(expected.UndoCount, session.UndoRedo.UndoCount);
            Assert.Equal(expected.RedoCount, session.UndoRedo.RedoCount);
            Assert.Equal(expected.HistoryBytes, session.UndoRedo.HistoryBytes);
            Assert.Equal(expected.EventNames, storage.EventNames);
            Assert.Equal(expected.ChangedCalls, changedCalls);
            Assert.Equal(expected.RollbackCalls, rollbackCalls);
        }
    }

    [Fact]
    public void PlannerSelectionCanonicalizesStaleClonesAndRejectsForgedObjectsWithoutSaving()
    {
        var initial = TestDocumentFactory.CreatePopulated();
        var storage = new MemoryStorage(initial);
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(repository, storage.Load, storage.Save);
        var viewModel = new PlannerViewModel(session, new LocalizationService(session));
        var livePlan = viewModel.CurrentPlan!;
        var liveSemester = viewModel.CurrentSemester!;
        var stalePlanClone = JsonDefaults.Clone(livePlan);
        stalePlanClone.PlanName = "detached plan payload must not leak";
        var staleSemesterClone = JsonDefaults.Clone(liveSemester);
        staleSemesterClone.SemesterName = "detached semester payload must not leak";

        viewModel.CurrentPlan = stalePlanClone;
        viewModel.CurrentSemester = staleSemesterClone;

        Assert.Same(
            session.Document.Plans.Single(plan => plan.PlanId == livePlan.PlanId),
            viewModel.CurrentPlan);
        Assert.Same(
            session.Document.Semesters.Single(semester => semester.SemesterId == liveSemester.SemesterId),
            viewModel.CurrentSemester);
        Assert.DoesNotContain(viewModel.OpenPlans, plan => ReferenceEquals(plan, stalePlanClone));
        Assert.Empty(storage.EventNames);

        var before = Snapshot(session.Document);
        var forgedPlan = JsonDefaults.Clone(livePlan);
        forgedPlan.PlanId = "forged-plan";
        var forgedSemester = JsonDefaults.Clone(liveSemester);
        forgedSemester.SemesterId = "forged-semester";

        viewModel.CurrentPlan = forgedPlan;
        viewModel.CurrentSemester = forgedSemester;

        Assert.Same(livePlan, viewModel.CurrentPlan);
        Assert.Same(liveSemester, viewModel.CurrentSemester);
        Assert.Equal(before, Snapshot(session.Document));
        Assert.Empty(storage.EventNames);

        var otherLivePlan = viewModel.OpenPlans.Single(plan => plan.PlanId != livePlan.PlanId);
        var staleBasePlan = JsonDefaults.Clone(otherLivePlan);
        var staleCurrentPlan = JsonDefaults.Clone(livePlan);
        viewModel.OpenComparison(staleBasePlan, staleCurrentPlan);
        Assert.Same(otherLivePlan, viewModel.BaseComparePlan);
        Assert.Same(livePlan, viewModel.CurrentPlan);

        var comparisonBaseBeforeForgery = viewModel.BaseComparePlan;
        var comparisonCurrentBeforeForgery = viewModel.CurrentPlan;
        forgedPlan.SemesterId = liveSemester.SemesterId;
        viewModel.OpenComparison(forgedPlan, staleCurrentPlan);
        Assert.Same(comparisonBaseBeforeForgery, viewModel.BaseComparePlan);
        Assert.Same(comparisonCurrentBeforeForgery, viewModel.CurrentPlan);

        var currentBeforeNullAssignment = viewModel.CurrentPlan;
        viewModel.CurrentPlan = null;
        Assert.Same(currentBeforeNullAssignment, viewModel.CurrentPlan);
        Assert.Equal(currentBeforeNullAssignment?.PlanId, session.Document.Settings.CurrentPlanId);

        var liveCourse = session.Document.CourseLibrary[0];
        var staleCourse = JsonDefaults.Clone(liveCourse);
        viewModel.SelectedCourse = staleCourse;
        Assert.Same(liveCourse, viewModel.SelectedCourse);
        var forgedCourse = JsonDefaults.Clone(liveCourse);
        forgedCourse.OfferingId = "forged-course";
        viewModel.SelectedCourse = forgedCourse;
        Assert.Same(liveCourse, viewModel.SelectedCourse);

        staleCourse.CourseName = "detached course payload must not leak";
        viewModel.BeginEditLibraryCourse(staleCourse);
        Assert.Same(liveCourse, viewModel.ActiveEdit?.SourceCourse);
        Assert.Equal(liveCourse.CourseName, viewModel.ActiveEdit?.Course.CourseName);
        viewModel.DiscardActiveCourseEdit();
        viewModel.BeginEditLibraryCourse(forgedCourse);
        Assert.Null(viewModel.ActiveEdit);

        var staleRenamePlan = JsonDefaults.Clone(viewModel.CurrentPlan!);
        var renameValidation = viewModel.RenamePlan(staleRenamePlan, "Canonical live rename");
        Assert.True(renameValidation.IsValid);
        Assert.Equal("Canonical live rename", viewModel.CurrentPlan?.PlanName);
        var afterRename = Snapshot(session.Document);
        var eventsAfterRename = storage.EventNames.Count;
        var forgedRenamePlan = JsonDefaults.Clone(viewModel.CurrentPlan!);
        forgedRenamePlan.PlanId = "forged-rename-plan";
        var forgedRenameValidation = viewModel.RenamePlan(forgedRenamePlan, "Must not rename");
        Assert.False(forgedRenameValidation.IsValid);
        Assert.Contains(forgedRenameValidation.Errors, issue => issue.Code == "PlanUnavailable");
        Assert.Equal(afterRename, Snapshot(session.Document));
        Assert.Equal(eventsAfterRename, storage.EventNames.Count);
    }

    [Fact]
    public void PlannerViewModelMaintainsLiveProjectionsAcrossFixedSeedRandomizedFailures()
    {
        const int seed = 0x71E4D;
        var random = new Random(seed);
        var initial = TestDocumentFactory.CreatePopulated();
        var storage = new MemoryStorage(initial);
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(repository, storage.Load, storage.Save);
        var viewModel = new PlannerViewModel(session, new LocalizationService(session));

        for (var step = 0; step < 1_000; step++)
        {
            var before = CaptureViewModelState(session, storage);
            storage.FailSave = random.Next(6) == 0;
            Exception? thrown;
            try
            {
                thrown = Record.Exception(() => RunPlannerOperation(viewModel, session, random, step));
            }
            finally
            {
                storage.FailSave = false;
            }

            if (thrown is not null)
            {
                Assert.True(
                    thrown is IOException,
                    $"Unexpected {thrown.GetType().Name} for seed {seed} at step {step}: {thrown}");
                AssertViewModelStateEqual(before, session, storage, seed, step);
            }

            AssertPlannerProjectionInvariants(viewModel, session, storage, seed, step);
        }
    }

    private static void RunPlannerOperation(
        PlannerViewModel viewModel,
        DocumentSession session,
        Random random,
        int step)
    {
        switch (random.Next(17))
        {
            case 0:
                if (session.Document.Plans.Count < 12)
                {
                    if ((step & 1) == 0)
                        viewModel.TryCreatePlanFromTab(out _, out _);
                    else
                        viewModel.TryCreatePlan($"Created {step}", out _, out _);
                }
                else if (viewModel.CurrentPlan is not null)
                {
                    viewModel.DeleteCurrentPlan();
                }
                break;
            case 1:
                if (session.Document.Plans.Count < 12)
                    viewModel.TryCopyCurrentPlan(out _, out _);
                break;
            case 2:
                if (viewModel.CurrentPlan is { } renamePlan)
                    viewModel.RenamePlan(renamePlan, $"Renamed {step}");
                break;
            case 3:
                if (viewModel.AllPlans.Count > 0)
                    viewModel.CurrentPlan = viewModel.AllPlans[random.Next(viewModel.AllPlans.Count)];
                break;
            case 4:
                if (viewModel.OpenPlans.Count > 0)
                {
                    viewModel.PersistOpenPlanOrder(
                        viewModel.OpenPlans.Select(plan => plan.PlanId).Reverse().ToArray());
                }
                break;
            case 5:
                viewModel.ClearCurrentPlan();
                break;
            case 6:
                if (viewModel.CurrentPlan is { } addPlan)
                {
                    var existingIds = addPlan.Snapshots
                        .Select(snapshot => snapshot.CourseOfferingId)
                        .ToHashSet(StringComparer.Ordinal);
                    var addable = session.Document.CourseLibrary.FirstOrDefault(course =>
                        !existingIds.Contains(course.OfferingId));
                    if (addable is not null)
                    {
                        viewModel.AddCourseToCurrentPlan(
                            addable,
                            DuplicateResolution.SkipExisting,
                            ConflictResolution.KeepConflict);
                    }
                }
                break;
            case 7:
                if (viewModel.CurrentPlan?.Snapshots.FirstOrDefault() is { } removable)
                    viewModel.RemoveCourseFromCurrentPlan(removable.CourseOfferingId);
                break;
            case 8:
                session.Undo();
                break;
            case 9:
                session.Redo();
                break;
            case 10:
                if (session.Document.Plans.Count > 1 && viewModel.CurrentPlan is not null)
                    viewModel.DeleteCurrentPlan();
                break;
            case 11:
                if (viewModel.OpenPlans.Count > 1)
                    viewModel.ClosePlanTab(viewModel.OpenPlans[random.Next(viewModel.OpenPlans.Count)]);
                break;
            case 12:
                if (viewModel.OpenPlans.Count > 0)
                    viewModel.ToggleComparisonPlanSelection(viewModel.OpenPlans[random.Next(viewModel.OpenPlans.Count)]);
                break;
            case 13:
                viewModel.ClearComparisonPlanSelection();
                break;
            case 14:
                if (viewModel.CurrentPlan is { } currentPlan)
                    viewModel.CurrentPlan = JsonDefaults.Clone(currentPlan);
                break;
            case 15:
                if (viewModel.CurrentPlan is { } sameNamePlan)
                    viewModel.RenamePlan(sameNamePlan, $"  {sameNamePlan.PlanName}  ");
                break;
            default:
                if (viewModel.CurrentPlan is { Snapshots.Count: > 1 } registrationPlan)
                {
                    viewModel.PersistRegistrationOrder(
                        registrationPlan.Snapshots
                            .Select(snapshot => snapshot.SnapshotId)
                            .Reverse()
                            .ToArray());
                }
                break;
        }
    }

    private static ViewModelState CaptureViewModelState(DocumentSession session, MemoryStorage storage) =>
        new(
            Snapshot(session.Document),
            Snapshot(storage.StoredDocument),
            session.UndoRedo.UndoCount,
            session.UndoRedo.RedoCount,
            session.UndoRedo.HistoryBytes,
            storage.EventNames.ToArray());

    private static void AssertViewModelStateEqual(
        ViewModelState expected,
        DocumentSession session,
        MemoryStorage storage,
        int seed,
        int step)
    {
        Assert.True(
            expected.DocumentJson.AsSpan().SequenceEqual(Snapshot(session.Document)),
            $"Document rollback mismatch for seed {seed} at step {step}.");
        Assert.True(
            expected.StorageJson.AsSpan().SequenceEqual(Snapshot(storage.StoredDocument)),
            $"Storage rollback mismatch for seed {seed} at step {step}.");
        Assert.Equal(expected.UndoCount, session.UndoRedo.UndoCount);
        Assert.Equal(expected.RedoCount, session.UndoRedo.RedoCount);
        Assert.Equal(expected.HistoryBytes, session.UndoRedo.HistoryBytes);
        Assert.Equal(expected.EventNames, storage.EventNames);
    }

    private static void AssertPlannerProjectionInvariants(
        PlannerViewModel viewModel,
        DocumentSession session,
        MemoryStorage storage,
        int seed,
        int step)
    {
        var document = session.Document;
        Assert.True(
            Snapshot(document).AsSpan().SequenceEqual(Snapshot(storage.StoredDocument)),
            $"Committed document/storage mismatch for seed {seed} at step {step}.");
        Assert.All(
            viewModel.Semesters,
            projected => Assert.Contains(document.Semesters, item => ReferenceEquals(item, projected)));
        Assert.All(
            viewModel.AllPlans,
            projected => Assert.Contains(document.Plans, item => ReferenceEquals(item, projected)));
        Assert.All(
            viewModel.OpenPlans,
            projected => Assert.Contains(document.Plans, item => ReferenceEquals(item, projected)));
        Assert.All(
            viewModel.LibraryCourses,
            projected => Assert.Contains(document.CourseLibrary, item => ReferenceEquals(item, projected)));

        Assert.Equal(document.Semesters.Count, document.Semesters.Select(item => item.SemesterId).Distinct().Count());
        Assert.Equal(document.Plans.Count, document.Plans.Select(item => item.PlanId).Distinct().Count());
        Assert.Equal(
            document.Settings.OpenPlanIds.Count,
            document.Settings.OpenPlanIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            document.Settings.OpenPlanIds,
            id => Assert.Contains(document.Plans, plan => plan.PlanId == id));
        Assert.All(
            document.Plans,
            plan => Assert.Contains(document.Semesters, semester => semester.SemesterId == plan.SemesterId));
        Assert.All(
            document.Plans.SelectMany(plan => plan.Snapshots),
            snapshot => Assert.Contains(
                document.CourseLibrary,
                course => course.OfferingId == snapshot.CourseOfferingId));

        if (viewModel.CurrentPlan is { } currentPlan)
        {
            Assert.Contains(document.Plans, plan => ReferenceEquals(plan, currentPlan));
            Assert.Contains(document.Settings.OpenPlanIds, id => id == currentPlan.PlanId);
            Assert.Equal(currentPlan.PlanId, document.Settings.CurrentPlanId);
            Assert.Equal(currentPlan.SemesterId, viewModel.CurrentSemester?.SemesterId);
        }
        else
        {
            Assert.Null(document.Settings.CurrentPlanId);
        }

        if (viewModel.CurrentSemester is { } currentSemester)
        {
            Assert.Contains(document.Semesters, semester => ReferenceEquals(semester, currentSemester));
            Assert.Equal(currentSemester.SemesterId, document.Settings.CurrentSemesterId);
            Assert.InRange(viewModel.CurrentWeek, 1, Math.Max(1, currentSemester.WeekCount));
        }

        Assert.All(
            viewModel.SelectedComparisonPlanIds,
            id => Assert.Contains(viewModel.OpenPlans, plan => plan.PlanId == id));
        Assert.InRange(session.UndoRedo.UndoCount + session.UndoRedo.RedoCount, 0, PlannerUndoRedo.MaxHistoryEntries);
        Assert.InRange(session.UndoRedo.HistoryBytes, 0, PlannerUndoRedo.MaxHistoryBytes);
    }

    private sealed record ViewModelState(
        byte[] DocumentJson,
        byte[] StorageJson,
        int UndoCount,
        int RedoCount,
        long HistoryBytes,
        string[] EventNames);

    private static void AssertDocumentsEqual(
        PlannerDocument? expected,
        PlannerDocument? actual,
        int seed,
        int step)
    {
        Assert.True(
            expected is null == (actual is null),
            $"Null result mismatch for seed {seed} at step {step}.");
        if (expected is not null && actual is not null)
            Assert.Equal(Snapshot(expected), Snapshot(actual));
    }

    private static PlannerDocument CreateDocument(string planName)
    {
        var semester = new Semester
        {
            SemesterId = "semester",
            SemesterName = "Semester",
            StartDate = new DateOnly(2026, 1, 5),
            EndDate = new DateOnly(2026, 5, 3),
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
        var document = new PlannerDocument
        {
            Semesters = [semester],
            CourseLibrary =
            [
                new CourseOffering
                {
                    SemesterId = semester.SemesterId,
                    CourseName = "Course",
                    Teacher = "Teacher",
                    Notes = "state",
                    ModifiedAt = DateTimeOffset.UnixEpoch
                }
            ],
            Plans = [plan],
            Settings = new AppSettings
            {
                CurrentSemesterId = semester.SemesterId,
                CurrentPlanId = plan.PlanId,
                OpenPlanIds = [plan.PlanId]
            }
        };
        DocumentConsistencyService.Ensure(document);
        return document;
    }

    private static byte[] Snapshot(PlannerDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.CompactOptions);

    private static PlannerDocument Restore(byte[] json)
    {
        var document = JsonSerializer.Deserialize<PlannerDocument>(json, JsonDefaults.CompactOptions)
                       ?? throw new InvalidOperationException("Reference state could not be restored.");
        DocumentConsistencyService.Ensure(document);
        return document;
    }

    private sealed class ReferenceHistory
    {
        private readonly List<byte[]> _undo = [];
        private readonly List<byte[]> _redo = [];

        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public long HistoryBytes => _undo.Sum(entry => (long)entry.Length) + _redo.Sum(entry => (long)entry.Length);

        public bool Capture(PlannerDocument document)
        {
            var entry = Snapshot(document);
            _redo.Clear();
            if (entry.Length > PlannerUndoRedo.MaxHistoryBytes)
            {
                _undo.Clear();
                return false;
            }
            if (_undo.Count > 0 && _undo[^1].AsSpan().SequenceEqual(entry))
                return false;
            _undo.Add(entry);
            Trim();
            return true;
        }

        public PlannerDocument? Undo(PlannerDocument current) => Move(current, _undo, _redo);

        public PlannerDocument? Redo(PlannerDocument current) => Move(current, _redo, _undo);

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        public ReferenceCheckpoint CreateCheckpoint() =>
            new(_undo.ToArray(), _redo.ToArray());

        public void RestoreCheckpoint(ReferenceCheckpoint checkpoint)
        {
            _undo.Clear();
            _undo.AddRange(checkpoint.Undo);
            _redo.Clear();
            _redo.AddRange(checkpoint.Redo);
        }

        private PlannerDocument? Move(PlannerDocument current, List<byte[]> source, List<byte[]> destination)
        {
            if (source.Count == 0)
                return null;
            var currentEntry = Snapshot(current);
            while (source.Count > 0 && source[^1].AsSpan().SequenceEqual(currentEntry))
                source.RemoveAt(source.Count - 1);
            if (source.Count == 0)
                return null;
            var target = source[^1];
            source.RemoveAt(source.Count - 1);
            if (currentEntry.Length <= PlannerUndoRedo.MaxHistoryBytes)
                destination.Add(currentEntry);
            else
                destination.Clear();
            Trim();
            return Restore(target);
        }

        private void Trim()
        {
            while (_undo.Count + _redo.Count > PlannerUndoRedo.MaxHistoryEntries ||
                   HistoryBytes > PlannerUndoRedo.MaxHistoryBytes)
            {
                var history = _undo.Count > 0 ? _undo : _redo;
                history.RemoveAt(0);
            }
        }
    }

    private sealed record ReferenceCheckpoint(byte[][] Undo, byte[][] Redo);

    private sealed class MemoryStorage
    {
        public MemoryStorage(PlannerDocument document)
        {
            StoredDocument = JsonDefaults.Clone(document);
        }

        public PlannerDocument StoredDocument { get; private set; }
        public List<string> EventNames { get; } = [];
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }
        public bool RejectSave { get; set; }

        public PlannerDocument Load()
        {
            if (FailLoad)
                throw new IOException("fixed-seed load failure");
            return JsonDefaults.Clone(StoredDocument);
        }

        public void Save(PlannerDocument document, string eventName)
        {
            if (FailSave)
                throw new IOException("fixed-seed save failure");
            if (RejectSave)
                throw new InvalidDataException("fixed-seed capacity rejection");
            StoredDocument = JsonDefaults.Clone(document);
            EventNames.Add(eventName);
        }

        public void ReplaceExternally(PlannerDocument document) =>
            StoredDocument = JsonDefaults.Clone(document);
    }

    private sealed class ReferenceSession
    {
        private readonly List<byte[]> _undo = [];
        private readonly List<byte[]> _redo = [];
        private PlannerDocument _current;
        private PlannerDocument _storage;

        public ReferenceSession(PlannerDocument initial)
        {
            _current = JsonDefaults.Clone(initial);
            _storage = JsonDefaults.Clone(initial);
        }

        public byte[] CurrentJson => Snapshot(_current);
        public byte[] StorageJson => Snapshot(_storage);
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public long HistoryBytes => _undo.Sum(entry => (long)entry.Length) + _redo.Sum(entry => (long)entry.Length);
        public List<string> EventNames { get; } = [];
        public int ChangedCalls { get; private set; }
        public int RollbackCalls { get; private set; }

        public void CommitMutation(string newName, string eventName, bool notify)
        {
            CaptureCurrent();
            _current.Plans[0].PlanName = newName;
            Commit(eventName, notify);
        }

        public void NoOp()
        {
        }

        public void Undo()
        {
            if (_undo.Count == 0)
                return;
            _redo.Add(Snapshot(_current));
            _current = Restore(RemoveLast(_undo));
            Commit("undo", notify: true);
        }

        public void Redo()
        {
            if (_redo.Count == 0)
                return;
            _undo.Add(Snapshot(_current));
            _current = Restore(RemoveLast(_redo));
            Commit("redo", notify: true);
        }

        public void Replace(PlannerDocument replacement, string eventName)
        {
            _current = JsonDefaults.Clone(replacement);
            _undo.Clear();
            _redo.Clear();
            Commit(eventName, notify: true);
        }

        public void ReplaceStorageExternally(PlannerDocument replacement) =>
            _storage = JsonDefaults.Clone(replacement);

        public void ReplaceWithEquivalentGraph()
        {
            _current = JsonDefaults.Clone(_current);
            _undo.Clear();
            _redo.Clear();
            ChangedCalls++;
        }

        public void Reload()
        {
            _current = JsonDefaults.Clone(_storage);
            _undo.Clear();
            _redo.Clear();
            ChangedCalls++;
        }

        public void RollBack() => RollbackCalls++;

        private void CaptureCurrent()
        {
            _redo.Clear();
            var entry = Snapshot(_current);
            if (_undo.Count == 0 || !_undo[^1].AsSpan().SequenceEqual(entry))
                _undo.Add(entry);
            Trim();
        }

        private void Commit(string eventName, bool notify)
        {
            DocumentConsistencyService.Ensure(_current);
            _storage = JsonDefaults.Clone(_current);
            EventNames.Add(eventName);
            if (notify)
                ChangedCalls++;
            Trim();
        }

        private static byte[] RemoveLast(List<byte[]> history)
        {
            var entry = history[^1];
            history.RemoveAt(history.Count - 1);
            return entry;
        }

        private void Trim()
        {
            while (_undo.Count + _redo.Count > PlannerUndoRedo.MaxHistoryEntries ||
                   HistoryBytes > PlannerUndoRedo.MaxHistoryBytes)
            {
                var history = _undo.Count > 0 ? _undo : _redo;
                history.RemoveAt(0);
            }
        }
    }
}

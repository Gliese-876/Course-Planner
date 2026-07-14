using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class ApplicationServiceTests
{
    [Fact]
    public void AddCourseToCurrentPlanWritesUndoableStableReference()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var course = fixture.Session.Document.CourseLibrary.Single(x => x.CourseName == "Physical Education");

        var result = fixture.ViewModel.AddCourseToCurrentPlan(course, DuplicateResolution.SkipExisting, ConflictResolution.KeepConflict);

        Assert.True(result.Added);
        var snapshot = Assert.Single(plan.Snapshots, x => x.CourseOfferingId == course.OfferingId);
        Assert.Same(course, PlanCourseResolver.CourseForSnapshot(snapshot, fixture.Session.Document.CourseLibrary));

        Assert.True(fixture.Session.Undo());
        Assert.DoesNotContain(fixture.Session.Document.Plans.Single(x => x.PlanId == plan.PlanId).Snapshots, x => x.CourseOfferingId == course.OfferingId);
    }

    [Fact]
    public void CourseEditSessionTracksDiscardAndSaveThroughViewModel()
    {
        using var fixture = ApplicationFixture.Create();
        var originalCount = fixture.Session.Document.CourseLibrary.Count;

        fixture.ViewModel.BeginNewCourseEdit();
        fixture.ViewModel.UpdateActiveCourseEdit(course =>
        {
            course.CourseName = "Service Boundary Course";
            course.Teacher = "T";
            course.Location = "R";
            course.Credits = 1;
        });

        Assert.True(fixture.ViewModel.HasUnsavedCourseEdit);
        fixture.ViewModel.DiscardActiveCourseEdit();
        Assert.False(fixture.ViewModel.HasUnsavedCourseEdit);
        Assert.Equal(originalCount, fixture.Session.Document.CourseLibrary.Count);

        fixture.ViewModel.BeginNewCourseEdit();
        fixture.ViewModel.UpdateActiveCourseEdit(course =>
        {
            course.CourseName = "Saved Service Boundary Course";
            course.Teacher = "T";
            course.Location = "R";
            course.Credits = 1;
        });

        var validation = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.True(validation.IsValid);
        Assert.False(fixture.ViewModel.HasUnsavedCourseEdit);
        Assert.Contains(fixture.Session.Document.CourseLibrary, x => x.CourseName == "Saved Service Boundary Course");
    }

    [Fact]
    public void SavingWithoutAnActiveCourseEditReturnsAnErrorAndDoesNotClaimSuccess()
    {
        using var fixture = ApplicationFixture.Create();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "CourseEditNotActive");
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void RegistrationOrderPersistsAndIsUndoableWithoutReorderingPlanSnapshots()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var originalSnapshotOrder = plan.Snapshots.Select(snapshot => snapshot.SnapshotId).ToList();
        var registrationOrder = originalSnapshotOrder.AsEnumerable().Reverse().ToList();

        var changed = fixture.ViewModel.PersistRegistrationOrder(registrationOrder);

        Assert.True(changed);
        Assert.Equal(originalSnapshotOrder, plan.Snapshots.Select(snapshot => snapshot.SnapshotId));
        Assert.Equal(
            registrationOrder,
            plan.Snapshots.OrderBy(snapshot => snapshot.RegistrationOrder).Select(snapshot => snapshot.SnapshotId));

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        var reloadedPlan = reloaded.Plans.Single(item => item.PlanId == plan.PlanId);
        Assert.Equal(
            registrationOrder,
            reloadedPlan.Snapshots.OrderBy(snapshot => snapshot.RegistrationOrder).Select(snapshot => snapshot.SnapshotId));

        Assert.True(fixture.Session.Undo());
        var restoredPlan = fixture.Session.Document.Plans.Single(item => item.PlanId == plan.PlanId);
        Assert.Equal(
            originalSnapshotOrder,
            restoredPlan.Snapshots.OrderBy(snapshot => snapshot.RegistrationOrder).Select(snapshot => snapshot.SnapshotId));
    }

    [Fact]
    public void RegistrationOrderAutoSaveDoesNotReloadOrDiscardAnActiveCourseEdit()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var registrationOrder = plan.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .Reverse()
            .ToList();
        var changedCount = 0;
        fixture.Session.Changed += (_, _) => changedCount++;
        fixture.ViewModel.BeginNewCourseEdit();
        fixture.ViewModel.UpdateActiveCourseEdit(course => course.CourseName = "Unsaved course");

        var changed = fixture.ViewModel.PersistRegistrationOrder(
            plan.PlanId,
            registrationOrder,
            notify: false);

        Assert.True(changed);
        Assert.Equal(0, changedCount);
        Assert.True(fixture.ViewModel.HasUnsavedCourseEdit);
        Assert.Equal(
            registrationOrder,
            plan.Snapshots.OrderBy(snapshot => snapshot.RegistrationOrder).Select(snapshot => snapshot.SnapshotId));
    }

    [Fact]
    public void ComparisonRequiresControlAndExactlyTwoSelectedPlans()
    {
        using var fixture = ApplicationFixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();

        Assert.False(fixture.ViewModel.IsDetailOpen);
        Assert.False(fixture.ViewModel.CanOpenSelectedComparison);

        fixture.ViewModel.ToggleComparisonPlanSelection(plans[0]);
        fixture.ViewModel.SetComparisonModifierPressed(true);
        Assert.False(fixture.ViewModel.CanOpenSelectedComparison);

        fixture.ViewModel.ToggleComparisonPlanSelection(plans[1]);
        Assert.True(fixture.ViewModel.CanOpenSelectedComparison);

        fixture.ViewModel.SetComparisonModifierPressed(false);
        Assert.False(fixture.ViewModel.CanOpenSelectedComparison);
        Assert.False(fixture.ViewModel.OpenSelectedComparison());

        fixture.ViewModel.SetComparisonModifierPressed(true);
        Assert.True(fixture.ViewModel.OpenSelectedComparison());
    }

    [Fact]
    public void SelectingPlanFromAnotherSemesterAtomicallySwitchesSemesterAndPersistsIt()
    {
        using var fixture = ApplicationFixture.Create();
        var (semester, plan) = AddOpenPlanInAnotherSemester(fixture);
        fixture.ViewModel.CurrentWeek = fixture.ViewModel.CurrentSemester!.WeekCount;

        fixture.ViewModel.CurrentPlan = plan;

        Assert.Same(plan, fixture.ViewModel.CurrentPlan);
        Assert.Same(semester, fixture.ViewModel.CurrentSemester);
        Assert.Equal(semester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        Assert.Equal(semester.WeekCount, fixture.ViewModel.CurrentWeek);

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(plan.PlanId, reloaded.Settings.CurrentPlanId);
        Assert.Equal(semester.SemesterId, reloaded.Settings.CurrentSemesterId);
    }

    [Fact]
    public void ReloadRepairsPersistedCurrentPlanAndSemesterMismatch()
    {
        using var fixture = ApplicationFixture.Create();
        var originalSemesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var (semester, plan) = AddOpenPlanInAnotherSemester(fixture);
        fixture.Session.Document.Settings.CurrentPlanId = plan.PlanId;
        fixture.Session.Document.Settings.CurrentSemesterId = originalSemesterId;

        fixture.Session.Save("test.mismatched-current-context");

        Assert.Same(plan, fixture.ViewModel.CurrentPlan);
        Assert.Same(semester, fixture.ViewModel.CurrentSemester);
        Assert.Equal(semester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
    }

    [Fact]
    public void AddingSemesterSelectsItWithoutBorrowingAPlanFromAnotherSemester()
    {
        using var fixture = ApplicationFixture.Create();

        var created = fixture.Settings.AddSemester();

        Assert.Same(created, fixture.Settings.SelectedSemester);
        Assert.Same(created, fixture.ViewModel.CurrentSemester);
        Assert.Null(fixture.ViewModel.CurrentPlan);
        Assert.Equal(created.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        Assert.Null(fixture.Session.Document.Settings.CurrentPlanId);

        fixture.Session.Document.Settings.Theme =
            fixture.Session.Document.Settings.Theme == ThemeMode.Dark
                ? ThemeMode.Light
                : ThemeMode.Dark;
        fixture.Session.Save("test.unrelated-after-semester-create");

        Assert.Same(created, fixture.ViewModel.CurrentSemester);
        Assert.Null(fixture.ViewModel.CurrentPlan);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(created.SemesterId, reloaded.Settings.CurrentSemesterId);
        Assert.Null(reloaded.Settings.CurrentPlanId);
    }

    [Fact]
    public void SwitchingFromPlanlessSemesterBackToSemesterWithOpenPlanStaysConsistent()
    {
        using var fixture = ApplicationFixture.Create();
        var originalSemester = fixture.ViewModel.CurrentSemester!;
        var originalPlan = fixture.ViewModel.CurrentPlan!;
        var planlessSemester = fixture.Settings.AddSemester();
        Assert.Null(fixture.ViewModel.CurrentPlan);

        fixture.ViewModel.CurrentSemester = originalSemester;

        Assert.Same(originalSemester, fixture.ViewModel.CurrentSemester);
        Assert.Same(originalPlan, fixture.ViewModel.CurrentPlan);
        Assert.Equal(originalSemester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        Assert.Equal(originalPlan.PlanId, fixture.Session.Document.Settings.CurrentPlanId);

        fixture.ViewModel.CurrentSemester = planlessSemester;
        Assert.Same(planlessSemester, fixture.ViewModel.CurrentSemester);
        Assert.Null(fixture.ViewModel.CurrentPlan);
        fixture.ViewModel.CurrentSemester = originalSemester;
        Assert.Same(originalPlan, fixture.ViewModel.CurrentPlan);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(originalSemester.SemesterId, reloaded.Settings.CurrentSemesterId);
        Assert.Equal(originalPlan.PlanId, reloaded.Settings.CurrentPlanId);
    }

    [Fact]
    public void DeletingCurrentPlanFallsBackWithinItsSemesterBeforeOtherOpenTabs()
    {
        using var fixture = ApplicationFixture.Create();
        var currentSemester = fixture.ViewModel.CurrentSemester!;
        var deleting = fixture.ViewModel.CurrentPlan!;
        var sameSemesterFallback = fixture.ViewModel.OpenPlans.First(plan =>
            plan.PlanId != deleting.PlanId && plan.SemesterId == currentSemester.SemesterId);
        var (_, otherSemesterPlan) = AddOpenPlanInAnotherSemester(fixture);
        fixture.Session.Document.Settings.OpenPlanIds =
            [otherSemesterPlan.PlanId, deleting.PlanId, sameSemesterFallback.PlanId];

        Assert.True(fixture.ViewModel.DeletePlan(deleting).IsValid);

        Assert.Same(sameSemesterFallback, fixture.ViewModel.CurrentPlan);
        Assert.Same(currentSemester, fixture.ViewModel.CurrentSemester);
        Assert.Equal(sameSemesterFallback.PlanId, fixture.Session.Document.Settings.CurrentPlanId);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(currentSemester.SemesterId, reloaded.Settings.CurrentSemesterId);
        Assert.Equal(sameSemesterFallback.PlanId, reloaded.Settings.CurrentPlanId);
    }

    [Fact]
    public void PlansFromDifferentSemestersCannotEnterComparisonMode()
    {
        using var fixture = ApplicationFixture.Create();
        var originalPlan = fixture.ViewModel.CurrentPlan!;
        var (_, otherPlan) = AddOpenPlanInAnotherSemester(fixture);
        fixture.Session.Save("test.open-cross-semester-plan");

        fixture.ViewModel.ToggleComparisonPlanSelection(originalPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(otherPlan);
        fixture.ViewModel.SetComparisonModifierPressed(true);

        Assert.False(fixture.ViewModel.TryGetSelectedComparison(out _, out _));
        Assert.False(fixture.ViewModel.CanOpenSelectedComparison);
        Assert.False(fixture.ViewModel.OpenSelectedComparison());
        Assert.NotEqual(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
    }

    [Fact]
    public void APlanCannotBeComparedWithItself()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;

        fixture.ViewModel.OpenComparison(plan, plan);

        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Same(plan, fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void AClosedBasePlanCannotReenterComparisonModeThroughAStaleReference()
    {
        using var fixture = ApplicationFixture.Create();
        var basePlan = fixture.ViewModel.OpenPlans[0];
        var currentPlan = fixture.ViewModel.OpenPlans[1];
        fixture.ViewModel.ClosePlanTab(basePlan, persist: false);
        Assert.DoesNotContain(basePlan, fixture.ViewModel.OpenPlans);

        fixture.ViewModel.OpenComparison(basePlan, currentPlan);

        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Same(currentPlan, fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void DeferredPlanTabClosesUpdateMemoryImmediatelyAndPersistOnce()
    {
        using var fixture = ApplicationFixture.Create();
        var closingPlans = fixture.ViewModel.OpenPlans.Take(2).ToArray();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        foreach (var plan in closingPlans)
            fixture.ViewModel.ClosePlanTab(plan, persist: false);

        Assert.DoesNotContain(
            fixture.ViewModel.OpenPlans,
            plan => closingPlans.Any(closing => closing.PlanId == plan.PlanId));
        Assert.Null(fixture.ViewModel.CurrentPlan);
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);

        fixture.ViewModel.PersistPlanTabState();

        Assert.Equal(eventCount + 1, fixture.Session.Repository.ReadEventSummaries().Count);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.DoesNotContain(
            reloaded.Settings.OpenPlanIds,
            planId => closingPlans.Any(closing => closing.PlanId == planId));
    }

    [Fact]
    public void ClosingCurrentMiddleTabSelectsItsRightNeighbor()
    {
        using var fixture = ApplicationFixture.Create();
        fixture.ViewModel.CreatePlan("Right neighbor");
        var openPlans = fixture.ViewModel.OpenPlans.ToArray();
        var closingIndex = openPlans.Length - 2;
        fixture.ViewModel.CurrentPlan = openPlans[closingIndex];

        fixture.ViewModel.ClosePlanTab(openPlans[closingIndex], persist: false);

        Assert.Same(openPlans[closingIndex + 1], fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void ClosingCurrentRightmostTabSelectsItsLeftNeighbor()
    {
        using var fixture = ApplicationFixture.Create();
        fixture.ViewModel.CreatePlan("Previous neighbor");
        var openPlans = fixture.ViewModel.OpenPlans.ToArray();
        var closingIndex = openPlans.Length - 1;
        fixture.ViewModel.CurrentPlan = openPlans[closingIndex];

        fixture.ViewModel.ClosePlanTab(openPlans[closingIndex], persist: false);

        Assert.Same(openPlans[closingIndex - 1], fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void ClosingCurrentTabAcrossSemesterBoundarySwitchesTheWholePlannerContext()
    {
        using var fixture = ApplicationFixture.Create();
        var (otherSemester, otherPlan) = AddOpenPlanInAnotherSemester(fixture);
        fixture.Session.Save("test.open-cross-semester-close-target");
        var expectedPlan = fixture.ViewModel.OpenPlans[^2];
        var expectedSemester = fixture.Session.Document.Semesters.Single(semester =>
            semester.SemesterId == expectedPlan.SemesterId);
        fixture.ViewModel.CurrentPlan = otherPlan;
        fixture.ViewModel.CurrentWeek = otherSemester.WeekCount;

        fixture.ViewModel.ClosePlanTab(otherPlan, persist: false);

        Assert.Same(expectedPlan, fixture.ViewModel.CurrentPlan);
        Assert.Same(expectedSemester, fixture.ViewModel.CurrentSemester);
        Assert.Equal(expectedSemester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        Assert.InRange(fixture.ViewModel.CurrentWeek, 1, expectedSemester.WeekCount);
        Assert.All(
            fixture.ViewModel.LibraryCourses,
            course => Assert.Equal(expectedSemester.SemesterId, course.SemesterId));

        fixture.ViewModel.PersistPlanTabState();
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(expectedPlan.PlanId, reloaded.Settings.CurrentPlanId);
        Assert.Equal(expectedSemester.SemesterId, reloaded.Settings.CurrentSemesterId);
    }

    [Fact]
    public void ClosingTheComparisonBaseTabExitsComparisonMode()
    {
        using var fixture = ApplicationFixture.Create();
        var basePlan = fixture.ViewModel.OpenPlans[0];
        var currentPlan = fixture.ViewModel.OpenPlans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);

        fixture.ViewModel.ClosePlanTab(basePlan, persist: false);

        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.Same(currentPlan, fixture.ViewModel.CurrentPlan);
    }

    [Fact]
    public void ClosingTheComparisonCurrentTabOntoItsBaseExitsComparisonMode()
    {
        using var fixture = ApplicationFixture.Create();
        var basePlan = fixture.ViewModel.OpenPlans[0];
        var currentPlan = fixture.ViewModel.OpenPlans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);

        fixture.ViewModel.ClosePlanTab(currentPlan, persist: false);

        Assert.Same(basePlan, fixture.ViewModel.CurrentPlan);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
    }

    [Fact]
    public void ReplacementPlanPersistsAllDeferredClosesWithOneCreateSave()
    {
        using var fixture = ApplicationFixture.Create();
        var closingPlans = fixture.ViewModel.OpenPlans.ToArray();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        foreach (var plan in closingPlans)
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        var replacement = fixture.ViewModel.CreatePlan();

        Assert.Same(replacement, fixture.ViewModel.CurrentPlan);
        Assert.Equal([replacement.PlanId], fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId));
        Assert.Equal(eventCount + 1, fixture.Session.Repository.ReadEventSummaries().Count);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal([replacement.PlanId], reloaded.Settings.OpenPlanIds);
    }

    [Fact]
    public void ClosingUnmodifiedPlanCreatedFromTabDeletesPlanEntity()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CreatePlanFromTab();

        fixture.ViewModel.ClosePlanTab(plan);

        Assert.DoesNotContain(fixture.Session.Document.Plans, candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.AllPlans, candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, candidate => candidate.PlanId == plan.PlanId);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.DoesNotContain(reloaded.Plans, candidate => candidate.PlanId == plan.PlanId);
    }

    [Fact]
    public void UndoingADeferredNewTabCloseRefreshesPlannerProjections()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CreatePlanFromTab();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        fixture.ViewModel.ClosePlanTab(plan, persist: false);
        Assert.DoesNotContain(
            fixture.Session.Document.Plans,
            candidate => candidate.PlanId == plan.PlanId);
        Assert.True(fixture.Session.Undo());
        var restored = fixture.Session.Document.Plans.Single(candidate => candidate.PlanId == plan.PlanId);

        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.Contains(
            fixture.ViewModel.OpenPlans,
            candidate => ReferenceEquals(candidate, restored));
        Assert.Same(restored, fixture.ViewModel.CurrentPlan);
        Assert.Same(
            fixture.Session.Document.Semesters.Single(semester => semester.SemesterId == restored.SemesterId),
            fixture.ViewModel.CurrentSemester);
    }

    [Fact]
    public void RedoingADeferredNewTabCloseRebindsProjectionsAndPersistsOnce()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CreatePlanFromTab();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        fixture.ViewModel.ClosePlanTab(plan, persist: false);
        Assert.True(fixture.Session.Undo());
        Assert.True(fixture.Session.UndoRedo.CanRedo);

        Assert.True(fixture.Session.Redo());

        Assert.DoesNotContain(
            fixture.Session.Document.Plans,
            candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(
            fixture.ViewModel.OpenPlans,
            candidate => candidate.PlanId == plan.PlanId);
        Assert.DoesNotContain(
            fixture.ViewModel.AllPlans,
            candidate => candidate.PlanId == plan.PlanId);
        Assert.Equal(eventCount + 1, fixture.Session.Repository.ReadEventSummaries().Count);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.DoesNotContain(reloaded.Plans, candidate => candidate.PlanId == plan.PlanId);
    }

    [Fact]
    public void UndoingADeferredNewTabClosePreservesItsDeleteOnCloseBaseline()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CreatePlanFromTab();
        fixture.ViewModel.ClosePlanTab(plan, persist: false);
        Assert.True(fixture.Session.Undo());
        var restored = fixture.ViewModel.OpenPlans.Single(candidate => candidate.PlanId == plan.PlanId);

        fixture.ViewModel.ClosePlanTab(restored);

        Assert.DoesNotContain(
            fixture.Session.Document.Plans,
            candidate => candidate.PlanId == plan.PlanId);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.DoesNotContain(reloaded.Plans, candidate => candidate.PlanId == plan.PlanId);
    }

    [Fact]
    public void ClosingModifiedOrNonTabEmptyPlanOnlyClosesItsTab()
    {
        using var fixture = ApplicationFixture.Create();
        var modifiedPlan = fixture.ViewModel.CreatePlanFromTab();
        Assert.True(fixture.ViewModel.RenamePlan(modifiedPlan, "Edited empty plan").IsValid);
        var ordinaryEmptyPlan = fixture.ViewModel.CreatePlan("Ordinary empty plan");

        fixture.ViewModel.ClosePlanTab(modifiedPlan);
        fixture.ViewModel.ClosePlanTab(ordinaryEmptyPlan);

        Assert.Contains(fixture.Session.Document.Plans, candidate => candidate.PlanId == modifiedPlan.PlanId);
        Assert.Contains(fixture.Session.Document.Plans, candidate => candidate.PlanId == ordinaryEmptyPlan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, candidate => candidate.PlanId == modifiedPlan.PlanId);
        Assert.DoesNotContain(fixture.ViewModel.OpenPlans, candidate => candidate.PlanId == ordinaryEmptyPlan.PlanId);
    }

    [Fact]
    public void BlockedPlanImportDoesNotSaveOrCreateUndoHistory()
    {
        using var fixture = ApplicationFixture.Create();
        var semester = fixture.ViewModel.CurrentSemester!;
        var course = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = "Blocked import course",
            Teacher = "T",
            Location = "R",
            Color = "#336699",
            MeetingTimes =
            {
                new MeetingTime
                {
                    Weekday = 1,
                    StartPeriod = 1,
                    EndPeriod = 1,
                    Weeks = $"{semester.WeekCount}-{semester.WeekCount + 2}"
                }
            }
        };
        CourseIdentityService.AssignOfferingId(course);
        var plan = new SelectionPlan
        {
            PlanId = "blocked-import-plan",
            SemesterId = semester.SemesterId,
            PlanName = "Blocked import plan",
            Snapshots =
            {
                new PlanCourseSnapshot
                {
                    SnapshotId = "blocked-import-snapshot",
                    CourseOfferingId = course.OfferingId,
                    RegistrationOrder = 0
                }
            }
        };
        var json = JsonSerializer.Serialize(new SelectionPlanPackage
        {
            Semester = JsonDefaults.Clone(semester),
            Courses = { course },
            Plan = plan
        }, JsonDefaults.Options);
        var preview = fixture.ViewModel.PreviewImportJson(json);
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.ApplyImportPreview(preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.False(result.Applied);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void RapidSemesterCreationProducesDistinctPersistentIds()
    {
        using var fixture = ApplicationFixture.Create();
        var created = Enumerable.Range(0, 8)
            .Select(_ => fixture.Settings.AddSemester())
            .ToArray();

        Assert.Equal(created.Length, created.Select(semester => semester.SemesterId).Distinct(StringComparer.Ordinal).Count());

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(
            reloaded.Semesters.Count,
            reloaded.Semesters.Select(semester => semester.SemesterId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RenamingLabelMigratesAllCourseReferencesAndPersistsThem()
    {
        using var fixture = ApplicationFixture.Create();
        fixture.Settings.SelectedLabel = fixture.Session.Document.Labels.Single(label => label.Name == PlannerLabels.Morning);

        var result = fixture.Settings.UpsertLabel("Early", LabelKind.Ordinary);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(
            fixture.Session.Document.CourseLibrary.SelectMany(course => course.Labels),
            label => string.Equals(label, PlannerLabels.Morning, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            2,
            fixture.Session.Document.CourseLibrary.Count(course => course.Labels.Contains("Early", StringComparer.OrdinalIgnoreCase)));

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.DoesNotContain(
            reloaded.CourseLibrary.SelectMany(course => course.Labels),
            label => string.Equals(label, PlannerLabels.Morning, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            2,
            reloaded.CourseLibrary.Count(course => course.Labels.Contains("Early", StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData(LabelKind.Ordinary)]
    [InlineData(LabelKind.CourseGroupType)]
    [InlineData(LabelKind.StudyType)]
    public void RenamingReferencedLabelWithinItsKindMigratesTheTypedReference(LabelKind kind)
    {
        using var fixture = ApplicationFixture.Create();
        const string oldName = "Typed source label";
        const string newName = "Typed renamed label";
        var label = new CourseLabel { Name = oldName, Kind = kind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, kind, oldName);
        fixture.Settings.SelectedLabel = label;

        var result = fixture.Settings.UpsertLabel(newName, kind);

        Assert.True(result.IsValid);
        Assert.True(CourseHasLabelReference(course, kind, newName));
        Assert.False(CourseHasLabelReference(course, kind, oldName));
    }

    [Theory]
    [InlineData(LabelKind.Ordinary)]
    [InlineData(LabelKind.CourseGroupType)]
    [InlineData(LabelKind.StudyType)]
    public void SavingReferencedLabelCanonicalizesIdentityEquivalentTypedReference(LabelKind kind)
    {
        using var fixture = ApplicationFixture.Create();
        const string catalogName = "Caf\u00E9 Label";
        const string equivalentReference = "cafe\u0301   label";
        var label = new CourseLabel { Name = catalogName, Kind = kind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, kind, equivalentReference);
        fixture.Settings.SelectedLabel = label;

        var result = fixture.Settings.UpsertLabel(catalogName, kind);

        Assert.True(result.IsValid);
        Assert.True(CourseHasExactLabelReference(course, kind, catalogName));
        Assert.False(CourseHasExactLabelReference(course, kind, equivalentReference));

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        var reloadedCourse = reloaded.CourseLibrary.Single(candidate => candidate.OfferingId == course.OfferingId);
        Assert.True(CourseHasExactLabelReference(reloadedCourse, kind, catalogName));
        Assert.False(CourseHasExactLabelReference(reloadedCourse, kind, equivalentReference));
    }

    [Theory]
    [InlineData(LabelKind.Ordinary)]
    [InlineData(LabelKind.CourseGroupType)]
    [InlineData(LabelKind.StudyType)]
    public void RenamingLabelMigratesIdentityEquivalentTypedReferenceAndPersists(LabelKind kind)
    {
        using var fixture = ApplicationFixture.Create();
        const string catalogName = "Caf\u00E9 Label";
        const string equivalentReference = "cafe\u0301   label";
        const string renamed = "Renamed Typed Label";
        var label = new CourseLabel { Name = catalogName, Kind = kind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, kind, equivalentReference);
        fixture.Settings.SelectedLabel = label;

        var result = fixture.Settings.UpsertLabel(renamed, kind);

        Assert.True(result.IsValid);
        Assert.True(CourseHasExactLabelReference(course, kind, renamed));
        Assert.False(CourseHasLabelReference(course, kind, catalogName));

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        var reloadedCourse = reloaded.CourseLibrary.Single(candidate => candidate.OfferingId == course.OfferingId);
        Assert.True(CourseHasExactLabelReference(reloadedCourse, kind, renamed));
        Assert.False(CourseHasLabelReference(reloadedCourse, kind, catalogName));
    }

    [Theory]
    [InlineData(LabelKind.Ordinary)]
    [InlineData(LabelKind.CourseGroupType)]
    [InlineData(LabelKind.StudyType)]
    public void DeletingLabelRemovesIdentityEquivalentTypedReferenceAndPersists(LabelKind kind)
    {
        using var fixture = ApplicationFixture.Create();
        const string catalogName = "Caf\u00E9 Label";
        const string equivalentReference = "cafe\u0301   label";
        var label = new CourseLabel { Name = catalogName, Kind = kind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, kind, equivalentReference);
        fixture.Settings.SelectedLabel = label;

        fixture.Settings.DeleteSelectedLabel();

        Assert.DoesNotContain(fixture.Session.Document.Labels, candidate =>
            TextRules.IsSameLabel(candidate.Name, catalogName));
        Assert.False(CourseHasLabelReference(course, kind, catalogName));

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        var reloadedCourse = reloaded.CourseLibrary.Single(candidate => candidate.OfferingId == course.OfferingId);
        Assert.DoesNotContain(reloaded.Labels, candidate => TextRules.IsSameLabel(candidate.Name, catalogName));
        Assert.False(CourseHasLabelReference(reloadedCourse, kind, catalogName));
    }

    [Theory]
    [InlineData(LabelKind.Ordinary, LabelKind.CourseGroupType)]
    [InlineData(LabelKind.CourseGroupType, LabelKind.StudyType)]
    [InlineData(LabelKind.StudyType, LabelKind.Ordinary)]
    public void IdentityEquivalentTypedReferenceBlocksLabelKindChange(
        LabelKind sourceKind,
        LabelKind targetKind)
    {
        using var fixture = ApplicationFixture.Create();
        const string catalogName = "Caf\u00E9 Label";
        const string equivalentReference = "cafe\u0301   label";
        var label = new CourseLabel { Name = catalogName, Kind = sourceKind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, sourceKind, equivalentReference);
        fixture.Settings.SelectedLabel = label;
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);

        var result = fixture.Settings.UpsertLabel("Changed Kind Label", targetKind);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "LabelKindInUse");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.True(CourseHasExactLabelReference(course, sourceKind, equivalentReference));
    }

    [Theory]
    [MemberData(nameof(ReferencedLabelKindChanges))]
    public void ReferencedLabelCannotChangeKindWithoutAnExplicitConflictDecision(
        LabelKind sourceKind,
        LabelKind targetKind,
        bool rename)
    {
        using var fixture = ApplicationFixture.Create();
        const string oldName = "Referenced source label";
        var label = new CourseLabel { Name = oldName, Kind = sourceKind, DisplayOrder = 99 };
        fixture.Session.Document.Labels.Add(label);
        var course = fixture.Session.Document.CourseLibrary[0];
        SetCourseLabelReference(course, sourceKind, oldName);
        fixture.Settings.SelectedLabel = label;
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.Settings.UpsertLabel(rename ? "Renamed target label" : oldName, targetKind);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "LabelKindInUse");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void LastSemesterCannotBeDeleted()
    {
        using var fixture = ApplicationFixture.Create();
        fixture.Settings.SelectedSemester = Assert.Single(fixture.Session.Document.Semesters);
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        fixture.Settings.DeleteSelectedSemester();

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void ReloadingInMemoryDocumentWithoutSemestersClearsStaleLibraryProjection()
    {
        using var fixture = ApplicationFixture.Create();
        Assert.NotEmpty(fixture.ViewModel.LibraryCourses);
        fixture.Session.Document.Semesters.Clear();

        fixture.ViewModel.ReloadFromDocument();

        Assert.Null(fixture.ViewModel.CurrentSemester);
        Assert.Null(fixture.ViewModel.CurrentPlan);
        Assert.Empty(fixture.ViewModel.LibraryCourses);
        Assert.Empty(fixture.ViewModel.LibraryGroups);
    }

    [Fact]
    public void AdversarialFilterTextIsBoundedBeforeItScansTheCourseLibrary()
    {
        using var fixture = ApplicationFixture.Create();
        var oversized = new string('x', PlannerDataLimits.MaxTextFieldLength - 1) + "😀tail";

        fixture.ViewModel.SearchText = oversized;
        fixture.ViewModel.LabelFilterText = oversized;
        fixture.ViewModel.GroupFilterText = oversized;
        fixture.ViewModel.StudyFilterText = oversized;
        fixture.ViewModel.TeacherFilterText = oversized;
        fixture.ViewModel.LocationFilterText = oversized;

        Assert.All(
            new[]
            {
                fixture.ViewModel.SearchText,
                fixture.ViewModel.LabelFilterText,
                fixture.ViewModel.GroupFilterText,
                fixture.ViewModel.StudyFilterText,
                fixture.ViewModel.TeacherFilterText,
                fixture.ViewModel.LocationFilterText
            },
            value =>
            {
                Assert.True(value.Length <= PlannerDataLimits.MaxTextFieldLength);
                Assert.False(value.Length > 0 && char.IsHighSurrogate(value[^1]));
            });
    }

    [Fact]
    public void ReferencedCourseCannotBeMovedToAnotherSemesterByOrdinaryEdit()
    {
        using var fixture = ApplicationFixture.Create();
        var referencedCourse = PlanCourseResolver.Courses(
                fixture.ViewModel.CurrentPlan!,
                fixture.Session.Document.CourseLibrary)
            .First();
        var (otherSemester, _) = AddOpenPlanInAnotherSemester(fixture);
        var edited = JsonDefaults.Clone(referencedCourse);
        edited.SemesterId = otherSemester.SemesterId;
        edited.MeetingTimes.ForEach(meeting => meeting.Weeks = "1-4");
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.SaveLibraryCourseEdit(edited, referencedCourse.OfferingId);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "ReferencedCourseSemesterChange");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.Undo());
        AssertEveryPlanCourseBelongsToItsPlanSemester(fixture.Session.Document);
    }

    [Fact]
    public void UnreferencedCourseCanStillBeMovedToAnotherSemester()
    {
        using var fixture = ApplicationFixture.Create();
        var (otherSemester, _) = AddOpenPlanInAnotherSemester(fixture);
        var source = JsonDefaults.Clone(fixture.Session.Document.CourseLibrary[0]);
        source.CourseName = "Unreferenced movable course";
        CourseIdentityService.AssignOfferingId(source);
        fixture.Session.Document.CourseLibrary.Add(source);
        var edited = JsonDefaults.Clone(source);
        edited.SemesterId = otherSemester.SemesterId;
        edited.MeetingTimes.ForEach(meeting => meeting.Weeks = "1-4");

        var result = fixture.ViewModel.SaveLibraryCourseEdit(edited, source.OfferingId);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(fixture.Session.Document.CourseLibrary, course => course.OfferingId == source.OfferingId);
        Assert.Contains(
            fixture.Session.Document.CourseLibrary,
            course => course.SemesterId == otherSemester.SemesterId && course.CourseName == source.CourseName);
        AssertEveryPlanCourseBelongsToItsPlanSemester(fixture.Session.Document);
    }

    [Fact]
    public void RejectedPeriodInsertionDoesNotCreateEmptyUndoHistory()
    {
        using var fixture = ApplicationFixture.Create();
        var semester = fixture.Settings.SelectedSemester!;
        var last = semester.PeriodSchedule.MaxBy(period => period.Period)!;
        last.End = new TimeOnly(23, 55);
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        Assert.Throws<InvalidOperationException>(() => fixture.Settings.AddPeriodAfter(last.Period));

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void RejectedPeriodTimeUpdateDoesNotCreateEmptyUndoHistory()
    {
        using var fixture = ApplicationFixture.Create();
        var semester = fixture.Settings.SelectedSemester!;
        var second = semester.PeriodSchedule.Single(period => period.Period == 2);
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Settings.UpdatePeriodTime(second.Period, new TimeOnly(8, 30), second.End));

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void RejectedLastPeriodDeletionDoesNotCreateEmptyUndoHistory()
    {
        using var fixture = ApplicationFixture.Create();
        var semester = fixture.Settings.SelectedSemester!;
        semester.PeriodSchedule = [semester.PeriodSchedule[0]];
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        Assert.Throws<InvalidOperationException>(() => fixture.Settings.DeletePeriod(1));

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void ResettingAExtendedScheduleRemovesOutOfRangeMeetingsAndKeepsPlanReferencesValid()
    {
        using var fixture = ApplicationFixture.Create();
        var semester = fixture.Settings.SelectedSemester!;
        fixture.Settings.AddPeriodAfter(semester.PeriodSchedule.Max(period => period.Period));
        Assert.Equal(13, semester.PeriodSchedule.Count);

        var unaffectedCourse = fixture.Session.Document.CourseLibrary[0];
        var unaffectedOfferingId = unaffectedCourse.OfferingId;
        var unaffectedModifiedAt = unaffectedCourse.ModifiedAt;
        var course = JsonDefaults.Clone(unaffectedCourse);
        course.CourseName = "Thirteenth period only";
        course.MeetingTimes =
        [
            new MeetingTime
            {
                Weekday = 1,
                StartPeriod = 13,
                EndPeriod = 13,
                Weeks = "1-2"
            }
        ];
        CourseIdentityService.AssignOfferingId(course);
        var originalOfferingId = course.OfferingId;
        fixture.Session.Document.CourseLibrary.Add(course);
        var plan = fixture.ViewModel.CurrentPlan!;
        plan.Snapshots.Add(new PlanCourseSnapshot
        {
            CourseOfferingId = course.OfferingId,
            RegistrationOrder = plan.Snapshots.Count
        });
        fixture.Session.Save("test.extended-period-course");

        fixture.Settings.ResetDefaultPeriods();

        Assert.Equal(12, semester.PeriodSchedule.Count);
        Assert.Empty(course.MeetingTimes);
        Assert.NotEqual(originalOfferingId, course.OfferingId);
        Assert.Equal(unaffectedOfferingId, unaffectedCourse.OfferingId);
        Assert.Equal(unaffectedModifiedAt, unaffectedCourse.ModifiedAt);
        Assert.Contains(
            plan.Snapshots,
            snapshot => string.Equals(snapshot.CourseOfferingId, course.OfferingId, StringComparison.Ordinal));
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(12, reloaded.Semesters.Single(item => item.SemesterId == semester.SemesterId).PeriodSchedule.Count);
        var reloadedCourse = reloaded.CourseLibrary.Single(item => item.CourseName == course.CourseName);
        Assert.Empty(reloadedCourse.MeetingTimes);
        Assert.Contains(
            reloaded.Plans.SelectMany(item => item.Snapshots),
            snapshot => string.Equals(snapshot.CourseOfferingId, reloadedCourse.OfferingId, StringComparison.Ordinal));
    }

    [Fact]
    public void CancellingCrossSemesterConflictHasNoLibrarySaveOrUndoSideEffects()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var conflicting = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Data Structures");
        var otherSemester = JsonDefaults.Clone(fixture.ViewModel.CurrentSemester!);
        otherSemester.SemesterId = "other-semester";
        otherSemester.SemesterName = "Other semester";
        fixture.Session.Document.Semesters.Add(otherSemester);
        var source = JsonDefaults.Clone(conflicting);
        source.SemesterId = otherSemester.SemesterId;
        source.CourseName = "Cross-semester conflict";
        CourseIdentityService.AssignOfferingId(source);
        fixture.Session.Document.CourseLibrary.Add(source);
        fixture.Session.Save("test.cross-semester-source");
        var courseCount = fixture.Session.Document.CourseLibrary.Count;
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.AddCourseToPlan(
            plan,
            source,
            DuplicateResolution.SkipExisting,
            ConflictResolution.Cancel);

        Assert.True(result.Cancelled);
        Assert.Equal(courseCount, fixture.Session.Document.CourseLibrary.Count);
        Assert.DoesNotContain(
            fixture.Session.Document.CourseLibrary,
            course => course.SemesterId == plan.SemesterId && course.CourseName == source.CourseName);
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void SkippingExistingCourseHasNoSaveOrUndoSideEffects()
    {
        using var fixture = ApplicationFixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var existing = PlanCourseResolver.Courses(plan, fixture.Session.Document.CourseLibrary).First();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.AddCourseToPlan(
            plan,
            existing,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.False(result.Added);
        Assert.False(result.Cancelled);
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void BulkSkipAcrossAllPlansHasNoSaveOrUndoSideEffects()
    {
        using var fixture = ApplicationFixture.Create();
        var existing = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Data Structures");
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.AddCourseToPlans(
            fixture.ViewModel.OpenPlans,
            existing,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.Equal(fixture.ViewModel.OpenPlans.Count, result.Skipped);
        Assert.Equal(0, result.Added);
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.Undo());
    }

    [Fact]
    public void EditingCourseIntoAnotherExistingIdentityIsRejectedWithoutMergingCoursesOrPlans()
    {
        using var fixture = ApplicationFixture.Create();
        var source = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Data Structures");
        var existingTarget = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Linear Algebra");
        var edited = JsonDefaults.Clone(source);
        edited.CourseName = existingTarget.CourseName;
        edited.Teacher = existingTarget.Teacher;
        edited.Location = existingTarget.Location;
        edited.MeetingTimes = JsonDefaults.Clone(existingTarget.MeetingTimes);
        edited.Notes = "must not overwrite the existing target";
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.SaveLibraryCourseEdit(edited, source.OfferingId);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "CourseIdentityDuplicate");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void SavingNewCourseWithExistingIdentityIsRejectedWithoutOverwritingExistingCourse()
    {
        using var fixture = ApplicationFixture.Create();
        var existing = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Linear Algebra");
        var duplicate = JsonDefaults.Clone(existing);
        duplicate.OfferingId = "";
        duplicate.Notes = "must not overwrite the existing course";
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.SaveLibraryCourseEdit(duplicate, originalOfferingId: null);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "CourseIdentityDuplicate");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void SavingAnEditWhoseSourceWasDeletedCannotResurrectTheCourse()
    {
        using var fixture = ApplicationFixture.Create();
        var source = fixture.Session.Document.CourseLibrary.Single(course => course.CourseName == "Physical Education");
        var edited = JsonDefaults.Clone(source);
        edited.Notes = "stale editor must not recreate this deleted course";
        fixture.Session.Document.CourseLibrary.Remove(source);
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        var result = fixture.ViewModel.SaveLibraryCourseEdit(edited, source.OfferingId);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "CourseEditSourceMissing");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void CopyingPlanRegeneratesEverySnapshotIdentityAndPersistsTheCopy()
    {
        using var fixture = ApplicationFixture.Create();
        var source = fixture.ViewModel.CurrentPlan!;
        var sourceSnapshotIds = source.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .ToHashSet(StringComparer.Ordinal);

        var copy = fixture.ViewModel.CopyCurrentPlan();

        Assert.NotEqual(source.PlanId, copy.PlanId);
        Assert.Equal(
            source.Snapshots.Select(snapshot => snapshot.CourseOfferingId),
            copy.Snapshots.Select(snapshot => snapshot.CourseOfferingId));
        Assert.Equal(
            source.Snapshots.Select(snapshot => snapshot.RegistrationOrder),
            copy.Snapshots.Select(snapshot => snapshot.RegistrationOrder));
        Assert.All(copy.Snapshots, snapshot =>
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.SnapshotId));
            Assert.DoesNotContain(snapshot.SnapshotId, sourceSnapshotIds);
        });
        Assert.Equal(
            copy.Snapshots.Count,
            copy.Snapshots.Select(snapshot => snapshot.SnapshotId).Distinct(StringComparer.Ordinal).Count());

        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Contains(reloaded.Plans, plan => plan.PlanId == copy.PlanId);
    }

    [Fact]
    public void CopyingAMaximumLengthPlanKeepsTheGeneratedNameAValidWindowsComponent()
    {
        using var fixture = ApplicationFixture.Create();
        var source = fixture.ViewModel.CurrentPlan!;
        var maximumLengthName = new string('A', WindowsFileNameRules.MaxComponentLength);
        Assert.True(fixture.ViewModel.RenamePlan(source, maximumLengthName).IsValid);

        var copy = fixture.ViewModel.CopyCurrentPlan();

        Assert.NotEqual(source.PlanName, copy.PlanName);
        Assert.True(
            WindowsFileNameRules.ValidateFileComponent(copy.PlanName).IsValid,
            string.Join(", ", WindowsFileNameRules.ValidateFileComponent(copy.PlanName).Errors.Select(issue => issue.Code)));
        Assert.True(copy.PlanName.Length <= WindowsFileNameRules.MaxComponentLength);
    }

    [Theory]
    [InlineData(0x13579BDF)]
    [InlineData(0x2468ACE)]
    [InlineData(0x5EED1234)]
    public void RandomPlannerStateSequencesPreserveContextAndComparisonInvariants(int seed)
    {
        using var fixture = ApplicationFixture.Create();
        var (_, otherPlan) = AddOpenPlanInAnotherSemester(fixture);
        var siblingPlan = new SelectionPlan
        {
            PlanId = $"other-semester-sibling-{seed:X8}",
            SemesterId = otherPlan.SemesterId,
            PlanName = $"Other sibling {seed:X8}",
            DisplayOrder = fixture.Session.Document.Plans.Count
        };
        fixture.Session.Document.Plans.Add(siblingPlan);
        fixture.Session.Document.Settings.OpenPlanIds.Add(siblingPlan.PlanId);
        fixture.Session.Save("test.state-sequence-seed");
        fixture.Session.UndoRedo.Clear();

        var random = new Random(seed);
        var stalePlans = fixture.Session.Document.Plans.ToList();
        for (var step = 0; step < 96; step++)
        {
            var allCandidates = stalePlans
                .Concat(fixture.Session.Document.Plans)
                .ToArray();
            switch (random.Next(13))
            {
                case 0 when allCandidates.Length > 0:
                    fixture.ViewModel.TryOpenPlan(allCandidates[random.Next(allCandidates.Length)], out _);
                    break;
                case 1 when fixture.ViewModel.OpenPlans.Count > 0:
                    fixture.ViewModel.ClosePlanTab(
                        fixture.ViewModel.OpenPlans[random.Next(fixture.ViewModel.OpenPlans.Count)],
                        persist: false);
                    break;
                case 2:
                    fixture.ViewModel.PersistPlanTabState();
                    break;
                case 3 when fixture.ViewModel.OpenPlans.Count > 0:
                    fixture.ViewModel.PersistOpenPlanOrder(
                        fixture.ViewModel.OpenPlans
                            .OrderBy(_ => random.Next())
                            .Select(plan => plan.PlanId)
                            .ToArray());
                    break;
                case 4 when allCandidates.Length > 0:
                    fixture.ViewModel.OpenComparison(
                        allCandidates[random.Next(allCandidates.Length)],
                        allCandidates[random.Next(allCandidates.Length)]);
                    break;
                case 5:
                    fixture.ViewModel.SwapComparison();
                    break;
                case 6 when allCandidates.Length > 0:
                    fixture.ViewModel.ToggleComparisonPlanSelection(
                        allCandidates[random.Next(allCandidates.Length)]);
                    break;
                case 7:
                    fixture.ViewModel.SetComparisonModifierPressed(random.Next(2) == 0);
                    fixture.ViewModel.OpenSelectedComparison();
                    break;
                case 8 when fixture.ViewModel.Semesters.Count > 0:
                    fixture.ViewModel.CurrentSemester =
                        fixture.ViewModel.Semesters[random.Next(fixture.ViewModel.Semesters.Count)];
                    break;
                case 9 when fixture.ViewModel.CurrentPlan is not null:
                    fixture.ViewModel.RenamePlan(
                        fixture.ViewModel.CurrentPlan,
                        $"State {seed:X8}-{step:D3}");
                    break;
                case 10:
                    fixture.Session.Undo();
                    break;
                case 11:
                    fixture.Session.Redo();
                    break;
                case 12 when fixture.Session.Document.Plans.Count < 12:
                    if (fixture.ViewModel.TryCreatePlanFromTab(out var created, out _) && created is not null)
                        stalePlans.Add(created);
                    break;
            }

            AssertPlannerStateInvariants(fixture, seed, step);
            stalePlans.AddRange(fixture.Session.Document.Plans.Where(plan =>
                stalePlans.All(stale => !ReferenceEquals(stale, plan))));
        }

        fixture.ViewModel.PersistPlanTabState();
        AssertPlannerStateInvariants(fixture, seed, 96);
    }

    private static void AssertPlannerStateInvariants(
        ApplicationFixture fixture,
        int seed,
        int step)
    {
        var context = $"Seed {seed:X8}, step {step}";
        var document = fixture.Session.Document;
        var documentPlanIds = document.Plans.Select(plan => plan.PlanId).ToHashSet(StringComparer.Ordinal);
        var openIds = document.Settings.OpenPlanIds;
        Assert.True(
            openIds.Count == openIds.Distinct(StringComparer.Ordinal).Count() &&
            openIds.All(documentPlanIds.Contains),
            $"{context}: persisted open-plan IDs are not a distinct subset of plans.");
        Assert.Equal(
            openIds.Order(StringComparer.Ordinal),
            fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId).Order(StringComparer.Ordinal));
        Assert.All(
            fixture.ViewModel.OpenPlans,
            projected => Assert.Contains(document.Plans, live => ReferenceEquals(live, projected)));

        if (fixture.ViewModel.CurrentPlan is { } currentPlan)
        {
            Assert.Contains(document.Plans, live => ReferenceEquals(live, currentPlan));
            Assert.Contains(fixture.ViewModel.OpenPlans, live => ReferenceEquals(live, currentPlan));
            Assert.Equal(currentPlan.PlanId, document.Settings.CurrentPlanId);
            Assert.NotNull(fixture.ViewModel.CurrentSemester);
            Assert.Equal(currentPlan.SemesterId, fixture.ViewModel.CurrentSemester!.SemesterId);
        }
        else
        {
            Assert.Null(document.Settings.CurrentPlanId);
        }

        if (fixture.ViewModel.CurrentSemester is { } currentSemester)
        {
            Assert.Contains(document.Semesters, live => ReferenceEquals(live, currentSemester));
            Assert.Equal(currentSemester.SemesterId, document.Settings.CurrentSemesterId);
            Assert.InRange(fixture.ViewModel.CurrentWeek, 1, currentSemester.WeekCount);
            if (!fixture.ViewModel.AllSemesters)
            {
                Assert.All(
                    fixture.ViewModel.LibraryCourses,
                    course => Assert.Equal(currentSemester.SemesterId, course.SemesterId));
            }
        }

        Assert.True(
            fixture.ViewModel.SelectedComparisonPlanIds.Count <= 2 &&
            fixture.ViewModel.SelectedComparisonPlanIds.Distinct(StringComparer.Ordinal).Count() ==
            fixture.ViewModel.SelectedComparisonPlanIds.Count &&
            fixture.ViewModel.SelectedComparisonPlanIds.All(id =>
                fixture.ViewModel.OpenPlans.Any(plan => plan.PlanId == id)),
            $"{context}: comparison selection contains stale or duplicate plans.");

        if (fixture.ViewModel.ViewMode == PlannerViewMode.Comparison)
        {
            var basePlan = Assert.IsType<SelectionPlan>(fixture.ViewModel.BaseComparePlan);
            var comparisonCurrent = Assert.IsType<SelectionPlan>(fixture.ViewModel.CurrentPlan);
            Assert.NotEqual(basePlan.PlanId, comparisonCurrent.PlanId);
            Assert.Equal(basePlan.SemesterId, comparisonCurrent.SemesterId);
            Assert.Contains(fixture.ViewModel.OpenPlans, plan => ReferenceEquals(plan, basePlan));
            Assert.Contains(fixture.ViewModel.OpenPlans, plan => ReferenceEquals(plan, comparisonCurrent));
        }
        else
        {
            Assert.Null(fixture.ViewModel.BaseComparePlan);
        }

        Assert.Equal(
            document.CourseLibrary.Count,
            document.CourseLibrary.Select(course => course.OfferingId).Distinct(StringComparer.Ordinal).Count());
        foreach (var plan in document.Plans)
            foreach (var snapshot in plan.Snapshots)
            {
                var course = PlanCourseResolver.CourseForSnapshot(snapshot, document.CourseLibrary);
                Assert.NotNull(course);
                Assert.Equal(plan.SemesterId, course!.SemesterId);
            }
    }

    private sealed class ApplicationFixture : IDisposable
    {
        private ApplicationFixture(
            string directory,
            DocumentSession session,
            PlannerViewModel viewModel,
            SettingsViewModel settings)
        {
            DirectoryPath = directory;
            Session = session;
            ViewModel = viewModel;
            Settings = settings;
        }

        public string DirectoryPath { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }
        public SettingsViewModel Settings { get; }

        public static ApplicationFixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var session = new DocumentSession(new SqliteAppRepository(directory));
            session.ReplaceDocument(TestDocumentFactory.CreatePopulated(), "test.seed");
            var localization = new LocalizationService(session);
            var viewModel = new PlannerViewModel(session, localization);
            var settings = new SettingsViewModel(session, localization, new TestThemeService());
            return new ApplicationFixture(directory, session, viewModel, settings);
        }

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (System.IO.Directory.Exists(DirectoryPath))
                        System.IO.Directory.Delete(DirectoryPath, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static (Semester Semester, SelectionPlan Plan) AddOpenPlanInAnotherSemester(ApplicationFixture fixture)
    {
        var semester = JsonDefaults.Clone(fixture.ViewModel.CurrentSemester!);
        semester.SemesterId = "other-semester-context";
        semester.SemesterName = "Other semester context";
        semester.WeekCount = 4;
        semester.EndDate = semester.StartDate.AddDays(27);
        semester.DisplayOrder = fixture.Session.Document.Semesters.Count;
        fixture.Session.Document.Semesters.Add(semester);

        var plan = new SelectionPlan
        {
            PlanId = "other-semester-plan",
            SemesterId = semester.SemesterId,
            PlanName = "Other semester plan",
            DisplayOrder = fixture.Session.Document.Plans.Count
        };
        fixture.Session.Document.Plans.Add(plan);
        fixture.Session.Document.Settings.OpenPlanIds.Add(plan.PlanId);
        return (semester, plan);
    }

    public static IEnumerable<object[]> ReferencedLabelKindChanges()
    {
        foreach (var sourceKind in Enum.GetValues<LabelKind>())
            foreach (var targetKind in Enum.GetValues<LabelKind>())
            {
                if (sourceKind == targetKind)
                    continue;
                yield return [sourceKind, targetKind, false];
                yield return [sourceKind, targetKind, true];
            }
    }

    private static void SetCourseLabelReference(CourseOffering course, LabelKind kind, string name)
    {
        switch (kind)
        {
            case LabelKind.Ordinary:
                course.Labels.Add(name);
                break;
            case LabelKind.CourseGroupType:
                course.CourseGroupType = name;
                break;
            case LabelKind.StudyType:
                course.StudyType = name;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static bool CourseHasLabelReference(CourseOffering course, LabelKind kind, string name) =>
        kind switch
        {
            LabelKind.Ordinary => course.Labels.Any(label => TextRules.IsSameLabel(label, name)),
            LabelKind.CourseGroupType => TextRules.IsSameLabel(course.CourseGroupType, name),
            LabelKind.StudyType => TextRules.IsSameLabel(course.StudyType, name),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static bool CourseHasExactLabelReference(CourseOffering course, LabelKind kind, string name) =>
        kind switch
        {
            LabelKind.Ordinary => course.Labels.Contains(name, StringComparer.Ordinal),
            LabelKind.CourseGroupType => string.Equals(course.CourseGroupType, name, StringComparison.Ordinal),
            LabelKind.StudyType => string.Equals(course.StudyType, name, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static void AssertEveryPlanCourseBelongsToItsPlanSemester(PlannerDocument document)
    {
        foreach (var plan in document.Plans)
            foreach (var snapshot in plan.Snapshots)
            {
                var course = PlanCourseResolver.CourseForSnapshot(snapshot, document.CourseLibrary);
                Assert.NotNull(course);
                Assert.Equal(plan.SemesterId, course!.SemesterId);
            }
    }

    private sealed class TestThemeService : IThemeService
    {
        public ThemeMode RequestedTheme { get; private set; } = ThemeMode.FollowSystem;
        public ResolvedThemeMode ResolvedTheme => ResolveTheme(RequestedTheme);
        public bool IsHighContrast => false;

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) =>
            requestedTheme == ThemeMode.Dark ? ResolvedThemeMode.Dark : ResolvedThemeMode.Light;

        public void ApplyTheme(ThemeMode theme)
        {
            RequestedTheme = theme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(theme, ResolvedTheme, IsHighContrast));
        }

        public void RefreshTheme() =>
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(RequestedTheme, ResolvedTheme, IsHighContrast));
    }
}

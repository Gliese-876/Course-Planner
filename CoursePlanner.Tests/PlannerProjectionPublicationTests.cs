using System.ComponentModel;
using System.Collections.Specialized;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class PlannerProjectionPublicationTests
{
    [Fact]
    public void SuccessfulSemesterSwitchPublishesTheRebuiltLibraryProjectionToPageSubscribers()
    {
        var fixture = Fixture.Create();
        using var page = new PageProjectionProbe(fixture.ViewModel);

        fixture.ViewModel.CurrentSemester = fixture.TargetSemester;

        Assert.True(page.NotificationCount > 0);
        Assert.Equal("Target semester", fixture.ViewModel.CurrentSemester?.SemesterName);
        Assert.Equal("Target course", CourseNames(fixture.ViewModel));
        Assert.Equal(CourseNames(fixture.ViewModel), page.RenderedCourseNames);
    }

    [Fact]
    public void SuccessfulSemesterSwitchCollectionEventsExposeOnlyTheCompleteTargetState()
    {
        var fixture = Fixture.Create();
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);

        fixture.ViewModel.CurrentSemester = fixture.TargetSemester;

        Assert.NotEmpty(probe.Snapshots);
        Assert.All(probe.Snapshots, snapshot =>
        {
            Assert.Equal("Target semester", snapshot.SemesterName);
            Assert.Equal("Target plan", snapshot.PlanName);
            Assert.Equal("Target course", snapshot.CourseNames);
            Assert.Equal("Target course", snapshot.GroupedCourseNames);
            Assert.Null(snapshot.SelectedCourseName);
        });
    }

    [Fact]
    public void SuccessfulCrossSemesterPlanSwitchCollectionEventsExposeOnlyTheCompleteTargetState()
    {
        var fixture = Fixture.Create();
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);

        Assert.True(fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out var validation));
        Assert.True(validation.IsValid);

        Assert.NotEmpty(probe.Snapshots);
        Assert.All(probe.Snapshots, snapshot =>
        {
            Assert.Equal("Target semester", snapshot.SemesterName);
            Assert.Equal("Target plan", snapshot.PlanName);
            Assert.Equal("Target course", snapshot.CourseNames);
            Assert.Equal("Target course", snapshot.GroupedCourseNames);
            Assert.Null(snapshot.SelectedCourseName);
        });
    }

    [Fact]
    public void OpeningAClosedCrossSemesterPlanPublishesTheTabOnlyAfterTheCompleteTargetState()
    {
        var fixture = Fixture.Create(targetInitiallyOpen: false);
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);

        Assert.True(fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out var validation));
        Assert.True(validation.IsValid);

        Assert.Contains(probe.Snapshots, snapshot => snapshot.Source == "OpenPlans");
        Assert.All(probe.Snapshots, snapshot =>
        {
            Assert.Equal("Target semester", snapshot.SemesterName);
            Assert.Equal("Target plan", snapshot.PlanName);
            Assert.Contains("Target plan", snapshot.OpenPlanNames);
            Assert.Equal("Target course", snapshot.CourseNames);
            Assert.Equal("Target course", snapshot.GroupedCourseNames);
            Assert.Null(snapshot.SelectedCourseName);
        });
    }

    [Fact]
    public void FailedSemesterSwitchCollectionEventsNeverExposeMixedState()
    {
        var fixture = Fixture.Create();
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);
        fixture.FailSemesterSave = true;

        Assert.Throws<IOException>(() =>
            fixture.ViewModel.CurrentSemester = fixture.TargetSemester);

        Assert.NotEmpty(probe.Snapshots);
        Assert.DoesNotContain(probe.Snapshots, snapshot => snapshot.SemesterName == "Target semester");
        Assert.Contains(probe.Snapshots, snapshot => snapshot.SemesterName == "Persisted semester");
        Assert.All(probe.Snapshots, AssertCompletePersistedOrTargetState);
        Assert.Equal("Persisted course", fixture.ViewModel.SelectedCourse?.CourseName);
    }

    [Fact]
    public void SuccessfulReloadCollectionEventsExposeOnlyTheCompleteTargetState()
    {
        var fixture = Fixture.Create();
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        fixture.Session.Document.Settings.CurrentSemesterId = fixture.TargetSemester.SemesterId;
        fixture.Session.Document.Settings.CurrentPlanId = fixture.TargetPlan.PlanId;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);

        fixture.ViewModel.ReloadFromDocument();

        Assert.NotEmpty(probe.Snapshots);
        Assert.All(probe.Snapshots, snapshot =>
        {
            Assert.Equal("Target semester", snapshot.SemesterName);
            Assert.Equal("Target plan", snapshot.PlanName);
            Assert.Contains("Target plan", snapshot.OpenPlanNames);
            Assert.Equal("Target course", snapshot.CourseNames);
            Assert.Equal("Target course", snapshot.GroupedCourseNames);
            Assert.Null(snapshot.SelectedCourseName);
        });
    }

    [Fact]
    public void FailedCrossSemesterPlanSwitchPublishesTheRestoredLibraryProjectionToPageSubscribers()
    {
        var fixture = Fixture.Create();
        using var page = new PageProjectionProbe(fixture.ViewModel);
        fixture.FailPlanSave = true;

        Assert.Throws<IOException>(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        Assert.True(page.NotificationCount > 0);
        Assert.Equal("Persisted semester", fixture.ViewModel.CurrentSemester?.SemesterName);
        Assert.Equal("Persisted course", CourseNames(fixture.ViewModel));
        Assert.Equal(CourseNames(fixture.ViewModel), page.RenderedCourseNames);
    }

    [Fact]
    public void FailedCrossSemesterPlanSwitchCollectionEventsNeverExposeMixedState()
    {
        var fixture = Fixture.Create(targetInitiallyOpen: false);
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);
        fixture.FailPlanSave = true;

        Assert.Throws<IOException>(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        Assert.NotEmpty(probe.Snapshots);
        Assert.DoesNotContain(probe.Snapshots, snapshot => snapshot.SemesterName == "Target semester");
        Assert.Contains(probe.Snapshots, snapshot => snapshot.SemesterName == "Persisted semester");
        Assert.All(probe.Snapshots, AssertCompletePersistedOrTargetState);
        Assert.Equal("Persisted course", fixture.ViewModel.SelectedCourse?.CourseName);
    }

    [Fact]
    public void FailedCrossSemesterComparisonRollbackPublishesTheCompleteOriginalStateOnce()
    {
        var fixture = Fixture.Create(
            targetInitiallyOpen: false,
            includeComparisonPlan: true,
            shortTargetSemester: true);
        var currentPlan = fixture.ViewModel.CurrentPlan!;
        var basePlan = fixture.ViewModel.OpenPlans.Single(plan =>
            plan.SemesterId == currentPlan.SemesterId &&
            plan.PlanId != currentPlan.PlanId);
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        fixture.ViewModel.SelectedCourse = fixture.PersistedCourse;
        var expectedSelection = string.Join('|', fixture.ViewModel.SelectedComparisonPlanIds);
        using var probe = new ProjectionCollectionProbe(fixture.ViewModel);
        fixture.BeforePlanSaveFailure = probe.Snapshots.Clear;
        fixture.FailPlanSave = true;

        Assert.Throws<IOException>(() =>
            fixture.ViewModel.TryOpenPlan(fixture.TargetPlan, out _));

        Assert.All(probe.Snapshots, snapshot =>
        {
            Assert.Equal("Persisted semester", snapshot.SemesterName);
            Assert.Equal("Persisted plan", snapshot.PlanName);
            Assert.Equal(12, snapshot.CurrentWeek);
            Assert.Equal("Comparison base plan", snapshot.BasePlanName);
            Assert.Equal(PlannerViewMode.Comparison, snapshot.ViewMode);
            Assert.Equal(expectedSelection, snapshot.SelectedComparisonPlanIds);
            Assert.Equal("Persisted course", snapshot.SelectedCourseName);
            Assert.True(snapshot.IsDetailOpen);
            Assert.Equal("Persisted course", snapshot.CourseNames);
            Assert.Equal("Persisted course", snapshot.GroupedCourseNames);
            Assert.DoesNotContain("Target plan", snapshot.OpenPlanNames);
        });
        Assert.Equal(3, probe.Snapshots.Count);
        Assert.Contains(probe.Snapshots, snapshot => snapshot.Source == "LibraryCourses");
        Assert.Contains(probe.Snapshots, snapshot => snapshot.Source == "LibraryGroups");
        Assert.Contains(probe.Snapshots, snapshot => snapshot.Source == "OpenPlans");
    }

    private static void AssertCompletePersistedOrTargetState(ProjectionSnapshot snapshot)
    {
        if (snapshot.SemesterName == "Target semester")
        {
            Assert.Equal("Target plan", snapshot.PlanName);
            Assert.Contains("Target plan", snapshot.OpenPlanNames);
            Assert.Equal("Target course", snapshot.CourseNames);
            Assert.Equal("Target course", snapshot.GroupedCourseNames);
            Assert.Null(snapshot.SelectedCourseName);
            return;
        }

        Assert.Equal("Persisted semester", snapshot.SemesterName);
        Assert.Equal("Persisted plan", snapshot.PlanName);
        Assert.Contains("Persisted plan", snapshot.OpenPlanNames);
        Assert.Equal("Persisted course", snapshot.CourseNames);
        Assert.Equal("Persisted course", snapshot.GroupedCourseNames);
        Assert.Equal("Persisted course", snapshot.SelectedCourseName);
    }

    private static string CourseNames(PlannerViewModel viewModel) => string.Join(
        '|',
        viewModel.LibraryGroups
            .SelectMany(group => group.Courses)
            .Select(course => course.CourseName)
            .Order(StringComparer.Ordinal));

    private sealed class PageProjectionProbe : IDisposable
    {
        private readonly PlannerViewModel _viewModel;

        public PageProjectionProbe(PlannerViewModel viewModel)
        {
            _viewModel = viewModel;
            RenderedCourseNames = CourseNames(viewModel);
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        public string RenderedCourseNames { get; private set; }
        public int NotificationCount { get; private set; }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            NotificationCount++;
            RenderedCourseNames = CourseNames(_viewModel);
        }

        public void Dispose() =>
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private sealed record ProjectionSnapshot(
        string Source,
        string? SemesterName,
        string? PlanName,
        int CurrentWeek,
        string? BasePlanName,
        PlannerViewMode ViewMode,
        string SelectedComparisonPlanIds,
        bool IsDetailOpen,
        string OpenPlanNames,
        string CourseNames,
        string GroupedCourseNames,
        string? SelectedCourseName);

    private sealed class ProjectionCollectionProbe : IDisposable
    {
        private readonly PlannerViewModel _viewModel;

        public ProjectionCollectionProbe(PlannerViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.OpenPlans.CollectionChanged += OpenPlans_CollectionChanged;
            _viewModel.LibraryCourses.CollectionChanged += LibraryCourses_CollectionChanged;
            _viewModel.LibraryGroups.CollectionChanged += LibraryGroups_CollectionChanged;
        }

        public List<ProjectionSnapshot> Snapshots { get; } = [];

        private void OpenPlans_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            Capture("OpenPlans");

        private void LibraryCourses_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            Capture("LibraryCourses");

        private void LibraryGroups_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            Capture("LibraryGroups");

        private void Capture(string source) => Snapshots.Add(new ProjectionSnapshot(
            source,
            _viewModel.CurrentSemester?.SemesterName,
            _viewModel.CurrentPlan?.PlanName,
            _viewModel.CurrentWeek,
            _viewModel.BaseComparePlan?.PlanName,
            _viewModel.ViewMode,
            string.Join('|', _viewModel.SelectedComparisonPlanIds),
            _viewModel.IsDetailOpen,
            string.Join('|', _viewModel.OpenPlans
                .Select(plan => plan.PlanName)
                .Order(StringComparer.Ordinal)),
            string.Join('|', _viewModel.LibraryCourses
                .Select(course => course.CourseName)
                .Order(StringComparer.Ordinal)),
            CourseNames(_viewModel),
            _viewModel.SelectedCourse?.CourseName));

        public void Dispose()
        {
            _viewModel.OpenPlans.CollectionChanged -= OpenPlans_CollectionChanged;
            _viewModel.LibraryCourses.CollectionChanged -= LibraryCourses_CollectionChanged;
            _viewModel.LibraryGroups.CollectionChanged -= LibraryGroups_CollectionChanged;
        }
    }

    private sealed class Fixture
    {
        private Fixture(
            DocumentSession session,
            PlannerViewModel viewModel,
            CourseOffering persistedCourse,
            Semester targetSemester,
            SelectionPlan targetPlan)
        {
            Session = session;
            ViewModel = viewModel;
            PersistedCourse = persistedCourse;
            TargetSemester = targetSemester;
            TargetPlan = targetPlan;
        }

        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }
        public CourseOffering PersistedCourse { get; }
        public Semester TargetSemester { get; }
        public SelectionPlan TargetPlan { get; }
        public bool FailPlanSave { get; set; }
        public bool FailSemesterSave { get; set; }
        public Action? BeforePlanSaveFailure { get; set; }

        public static Fixture Create(
            bool targetInitiallyOpen = true,
            bool includeComparisonPlan = false,
            bool shortTargetSemester = false)
        {
            var document = SeedData.Create("Persisted semester", "Persisted plan");
            var persistedSemester = document.Semesters[0];
            var persistedCourse = CreateCourse(
                persistedSemester.SemesterId,
                "Persisted course",
                colorIndex: 0);
            document.CourseLibrary.Add(persistedCourse);

            if (includeComparisonPlan)
            {
                var comparisonPlan = new SelectionPlan
                {
                    SemesterId = persistedSemester.SemesterId,
                    PlanName = "Comparison base plan",
                    DisplayOrder = document.Plans.Count
                };
                document.Plans.Add(comparisonPlan);
                document.Settings.OpenPlanIds.Add(comparisonPlan.PlanId);
            }

            var targetSemester = JsonDefaults.Clone(persistedSemester);
            targetSemester.SemesterId = "target-semester";
            targetSemester.SemesterName = "Target semester";
            targetSemester.DisplayOrder = 1;
            if (shortTargetSemester)
            {
                targetSemester.EndDate = targetSemester.StartDate.AddDays(27);
                targetSemester.WeekCount = 4;
            }
            document.Semesters.Add(targetSemester);

            var targetCourse = CreateCourse(
                targetSemester.SemesterId,
                "Target course",
                colorIndex: 1);
            document.CourseLibrary.Add(targetCourse);
            var targetPlan = new SelectionPlan
            {
                SemesterId = targetSemester.SemesterId,
                PlanName = "Target plan",
                DisplayOrder = document.Plans.Count
            };
            targetPlan.Snapshots.Add(new PlanCourseSnapshot
            {
                CourseOfferingId = targetCourse.OfferingId
            });
            document.Plans.Add(targetPlan);
            if (targetInitiallyOpen)
                document.Settings.OpenPlanIds.Add(targetPlan.PlanId);
            DocumentConsistencyService.Ensure(document);

            Fixture? fixture = null;
            var repository = new SqliteAppRepository(Path.Combine(
                Path.GetTempPath(),
                $"course-planner-projection-{Guid.NewGuid():N}"));
            var session = new DocumentSession(
                repository,
                loadDocument: () => document,
                saveDocument: (_, eventName) =>
                {
                    if (fixture?.FailPlanSave == true && eventName == "planner.plan")
                    {
                        fixture.BeforePlanSaveFailure?.Invoke();
                        throw new IOException("Injected planner.plan failure.");
                    }
                    if (fixture?.FailSemesterSave == true && eventName == "planner.semester")
                        throw new IOException("Injected planner.semester failure.");
                });
            var viewModel = new PlannerViewModel(session, new LocalizationService(session));
            fixture = new Fixture(session, viewModel, persistedCourse, targetSemester, targetPlan);
            return fixture;
        }

        private static CourseOffering CreateCourse(
            string semesterId,
            string name,
            int colorIndex)
        {
            var course = new CourseOffering
            {
                SemesterId = semesterId,
                CourseName = name,
                Teacher = "Teacher",
                Location = "Room",
                Credits = 1,
                Color = CourseColorService.Generate(colorIndex)
            };
            CourseIdentityService.AssignOfferingId(course);
            return course;
        }
    }
}

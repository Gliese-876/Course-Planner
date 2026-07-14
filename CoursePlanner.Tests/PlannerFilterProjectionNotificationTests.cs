using System.ComponentModel;
using System.Collections.Specialized;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class PlannerFilterProjectionNotificationTests
{
    [Theory]
    [InlineData(nameof(PlannerViewModel.SearchText), "Data Structures", "Data Structures")]
    [InlineData(nameof(PlannerViewModel.LabelFilterText), PlannerLabels.Morning, "Data Structures|Linear Algebra")]
    [InlineData(nameof(PlannerViewModel.GroupFilterText), PlannerLabels.Major, "Data Structures|Human-Computer Interaction|Linear Algebra")]
    [InlineData(nameof(PlannerViewModel.StudyFilterText), PlannerLabels.Required, "Linear Algebra")]
    [InlineData(nameof(PlannerViewModel.TeacherFilterText), "Dr. Chen", "Academic Writing")]
    [InlineData(nameof(PlannerViewModel.LocationFilterText), "Gym", "Physical Education")]
    [InlineData(nameof(PlannerViewModel.AllSemesters), "true", "Academic Writing|Data Structures|Future Networks|Human-Computer Interaction|Linear Algebra|Physical Education")]
    public void FilterChangeNotificationPublishesTheRebuiltLibraryProjection(
        string propertyName,
        string value,
        string expectedCourseNames)
    {
        using var fixture = Fixture.Create();
        var expected = expectedCourseNames.Split('|').Order(StringComparer.Ordinal).ToList();
        var coursesBefore = CourseNames(fixture.ViewModel.LibraryCourses);
        var groupedCoursesBefore = CourseNames(
            fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses));
        object? filterAtChanging = null;
        List<string>? coursesAtChanging = null;
        List<string>? groupedCoursesAtChanging = null;
        List<string>? coursesAtNotification = null;
        List<string>? groupedCoursesAtNotification = null;
        var matchingChangingCount = 0;
        var matchingNotificationCount = 0;
        PropertyChangingEventHandler changingHandler = (_, args) =>
        {
            if (!string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
                return;

            matchingChangingCount++;
            filterAtChanging = ReadFilter(fixture.ViewModel, propertyName);
            coursesAtChanging = CourseNames(fixture.ViewModel.LibraryCourses);
            groupedCoursesAtChanging = CourseNames(
                fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses));
        };
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (!string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
                return;

            matchingNotificationCount++;
            coursesAtNotification = CourseNames(fixture.ViewModel.LibraryCourses);
            groupedCoursesAtNotification = CourseNames(
                fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses));
        };
        fixture.ViewModel.PropertyChanging += changingHandler;
        fixture.ViewModel.PropertyChanged += handler;

        ApplyFilter(fixture.ViewModel, propertyName, value);

        Assert.Equal(1, matchingChangingCount);
        Assert.Equal(1, matchingNotificationCount);
        Assert.Equal(
            propertyName == nameof(PlannerViewModel.AllSemesters) ? false : "",
            filterAtChanging);
        Assert.Equal(coursesBefore, coursesAtChanging);
        Assert.Equal(groupedCoursesBefore, groupedCoursesAtChanging);
        Assert.Equal(expected, coursesAtNotification);
        Assert.Equal(expected, groupedCoursesAtNotification);
        Assert.Equal(expected, CourseNames(fixture.ViewModel.LibraryCourses));
        Assert.Equal(
            expected,
            CourseNames(fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses)));

        ApplyFilter(fixture.ViewModel, propertyName, value);

        Assert.Equal(1, matchingChangingCount);
        Assert.Equal(1, matchingNotificationCount);
    }

    [Fact]
    public void ReentrantFilterChangeDuringProjectionPublicationLeavesEveryPublishedSnapshotConsistent()
    {
        using var fixture = Fixture.Create();
        var propertyChangingSnapshots = new List<FilterProjectionSnapshot>();
        var propertyChangedSnapshots = new List<FilterProjectionSnapshot>();
        var reentered = false;
        NotifyCollectionChangedEventHandler collectionHandler = (_, _) =>
        {
            if (reentered)
                return;

            reentered = true;
            fixture.ViewModel.SearchText = "Linear Algebra";
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += collectionHandler;
        fixture.ViewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName != nameof(PlannerViewModel.SearchText))
                return;

            propertyChangingSnapshots.Add(CaptureSearchProjection(fixture.ViewModel));
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PlannerViewModel.SearchText))
                return;

            propertyChangedSnapshots.Add(CaptureSearchProjection(fixture.ViewModel));
        };

        fixture.ViewModel.SearchText = "Data Structures";

        Assert.True(reentered);
        Assert.Equal("Linear Algebra", fixture.ViewModel.SearchText);
        Assert.Equal(["Linear Algebra"], CourseNames(fixture.ViewModel.LibraryCourses));
        Assert.Equal(
            ["Linear Algebra"],
            CourseNames(fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses)));
        Assert.Equal(2, propertyChangingSnapshots.Count);
        Assert.Equal("", propertyChangingSnapshots[0].Filter);
        Assert.Equal("Data Structures", propertyChangingSnapshots[1].Filter);
        Assert.All(propertyChangingSnapshots, AssertSearchProjectionIsConsistent);
        Assert.Equal(2, propertyChangedSnapshots.Count);
        Assert.All(propertyChangedSnapshots, AssertSearchProjectionIsConsistent);
    }

    [Fact]
    public void FailedProjectionRefreshLeavesTheFilterAndPublishedProjectionUnchanged()
    {
        using var fixture = Fixture.Create();
        var coursesBefore = CourseNames(fixture.ViewModel.LibraryCourses);
        var groupedCoursesBefore = CourseNames(
            fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses));
        var searchChangingNotifications = 0;
        var searchNotifications = 0;
        fixture.ViewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.SearchText))
                searchChangingNotifications++;
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlannerViewModel.SearchText))
                searchNotifications++;
        };
        fixture.Session.Document.Labels.Add(
            JsonDefaults.Clone(fixture.Session.Document.Labels[0]));

        Assert.Throws<ArgumentException>(() =>
            fixture.ViewModel.SearchText = "Data Structures");

        Assert.Equal("", fixture.ViewModel.SearchText);
        Assert.Equal(1, searchChangingNotifications);
        Assert.Equal(0, searchNotifications);
        Assert.Equal(coursesBefore, CourseNames(fixture.ViewModel.LibraryCourses));
        Assert.Equal(
            groupedCoursesBefore,
            CourseNames(fixture.ViewModel.LibraryGroups.SelectMany(group => group.Courses)));
    }

    [Theory]
    [InlineData(nameof(PlannerViewModel.SearchText))]
    [InlineData(nameof(PlannerViewModel.LabelFilterText))]
    [InlineData(nameof(PlannerViewModel.GroupFilterText))]
    [InlineData(nameof(PlannerViewModel.StudyFilterText))]
    [InlineData(nameof(PlannerViewModel.TeacherFilterText))]
    [InlineData(nameof(PlannerViewModel.LocationFilterText))]
    public void DifferentRawTextValuesThatBoundToTheSameValueDoNotNotifyOrRefresh(
        string propertyName)
    {
        using var fixture = Fixture.Create();
        var bounded = new string('x', PlannerDataLimits.MaxTextFieldLength);
        ApplyFilter(fixture.ViewModel, propertyName, bounded + "a");
        var propertyChangingCount = 0;
        var propertyChangedCount = 0;
        var collectionChangedCount = 0;
        fixture.ViewModel.PropertyChanging += (_, args) =>
        {
            if (args.PropertyName == propertyName)
                propertyChangingCount++;
        };
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == propertyName)
                propertyChangedCount++;
        };
        fixture.ViewModel.LibraryCourses.CollectionChanged += (_, _) =>
            collectionChangedCount++;
        fixture.ViewModel.LibraryGroups.CollectionChanged += (_, _) =>
            collectionChangedCount++;

        ApplyFilter(fixture.ViewModel, propertyName, bounded + "b");

        Assert.Equal(bounded, ReadFilter(fixture.ViewModel, propertyName));
        Assert.Equal(0, propertyChangingCount);
        Assert.Equal(0, propertyChangedCount);
        Assert.Equal(0, collectionChangedCount);
    }

    private static void ApplyFilter(PlannerViewModel viewModel, string propertyName, string value)
    {
        switch (propertyName)
        {
            case nameof(PlannerViewModel.SearchText):
                viewModel.SearchText = value;
                break;
            case nameof(PlannerViewModel.LabelFilterText):
                viewModel.LabelFilterText = value;
                break;
            case nameof(PlannerViewModel.GroupFilterText):
                viewModel.GroupFilterText = value;
                break;
            case nameof(PlannerViewModel.StudyFilterText):
                viewModel.StudyFilterText = value;
                break;
            case nameof(PlannerViewModel.TeacherFilterText):
                viewModel.TeacherFilterText = value;
                break;
            case nameof(PlannerViewModel.LocationFilterText):
                viewModel.LocationFilterText = value;
                break;
            case nameof(PlannerViewModel.AllSemesters):
                viewModel.AllSemesters = bool.Parse(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null);
        }
    }

    private static object ReadFilter(PlannerViewModel viewModel, string propertyName) =>
        propertyName switch
        {
            nameof(PlannerViewModel.SearchText) => viewModel.SearchText,
            nameof(PlannerViewModel.LabelFilterText) => viewModel.LabelFilterText,
            nameof(PlannerViewModel.GroupFilterText) => viewModel.GroupFilterText,
            nameof(PlannerViewModel.StudyFilterText) => viewModel.StudyFilterText,
            nameof(PlannerViewModel.TeacherFilterText) => viewModel.TeacherFilterText,
            nameof(PlannerViewModel.LocationFilterText) => viewModel.LocationFilterText,
            nameof(PlannerViewModel.AllSemesters) => viewModel.AllSemesters,
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null)
        };

    private static List<string> CourseNames(IEnumerable<CourseOffering> courses) =>
        courses.Select(course => course.CourseName).Order(StringComparer.Ordinal).ToList();

    private static FilterProjectionSnapshot CaptureSearchProjection(PlannerViewModel viewModel) =>
        new(
            viewModel.SearchText,
            CourseNames(viewModel.LibraryCourses),
            CourseNames(viewModel.LibraryGroups.SelectMany(group => group.Courses)));

    private static void AssertSearchProjectionIsConsistent(FilterProjectionSnapshot snapshot)
    {
        string[] expected = snapshot.Filter switch
        {
            "" =>
            [
                "Academic Writing",
                "Data Structures",
                "Human-Computer Interaction",
                "Linear Algebra",
                "Physical Education"
            ],
            "Data Structures" => ["Data Structures"],
            "Linear Algebra" => ["Linear Algebra"],
            _ => throw new Xunit.Sdk.XunitException($"Unexpected search filter '{snapshot.Filter}'.")
        };
        Assert.Equal(expected, snapshot.Courses);
        Assert.Equal(expected, snapshot.GroupedCourses);
    }

    private sealed record FilterProjectionSnapshot(
        string Filter,
        IReadOnlyList<string> Courses,
        IReadOnlyList<string> GroupedCourses);

    private sealed class Fixture : IDisposable
    {
        private Fixture(string directory, DocumentSession session, PlannerViewModel viewModel)
        {
            Directory = directory;
            Session = session;
            ViewModel = viewModel;
        }

        private string Directory { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }

        public static Fixture Create()
        {
            var document = TestDocumentFactory.CreatePopulated();
            AddCourseInAnotherSemester(document);
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(
                repository,
                loadDocument: () => document,
                saveDocument: (_, _) => { });
            var localization = new LocalizationService(session);
            return new Fixture(directory, session, new PlannerViewModel(session, localization));
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static void AddCourseInAnotherSemester(PlannerDocument document)
        {
            var semester = JsonDefaults.Clone(document.Semesters[0]);
            semester.SemesterId = "future-semester";
            semester.SemesterName = "Future Semester";
            semester.DisplayOrder = document.Semesters.Count;
            document.Semesters.Add(semester);

            var course = new CourseOffering
            {
                SemesterId = semester.SemesterId,
                CourseName = "Future Networks",
                Teacher = "Prof. Future",
                Location = "Future Lab",
                Credits = 3,
                CourseGroupType = PlannerLabels.Major,
                StudyType = PlannerLabels.Elective
            };
            CourseIdentityService.AssignOfferingId(course);
            document.CourseLibrary.Add(course);
            DocumentConsistencyService.Ensure(document);
        }
    }
}

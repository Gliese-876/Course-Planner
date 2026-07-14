using CoursePlanner.Core;
using CoursePlanner.Services;
using System.Diagnostics;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class RenderSignatureSafetyTests
{
    [Fact]
    public void TimetableSignatureCannotCollideWhenPlanIdsContainTheOldDelimiter()
    {
        var semester = CreateSemester();
        var stateA = TimetableRenderSignatureService.Build(
            semester,
            CreatePlan("x|y", "Plan"),
            CreatePlan("z", "Plan"),
            PlannerViewMode.Comparison,
            4,
            "Dark",
            []);
        var stateB = TimetableRenderSignatureService.Build(
            semester,
            CreatePlan("x", "Plan"),
            CreatePlan("y|z", "Plan"),
            PlannerViewMode.Comparison,
            4,
            "Dark",
            []);

        Assert.NotEqual(stateA, stateB);
    }

    [Fact]
    public void TimetableSignatureIncludesEverySemesterValueThatChangesRenderedHeadersOrSlots()
    {
        var baseline = CreateSemester();
        var original = Signature(baseline, CreatePlan("plan", "Original"));

        var renamedPlan = CreatePlan("plan", "Renamed");
        Assert.NotEqual(original, Signature(baseline, renamedPlan));

        var renamedSemester = JsonDefaults.Clone(baseline);
        renamedSemester.SemesterName = "Renamed semester";
        Assert.NotEqual(original, Signature(renamedSemester, CreatePlan("plan", "Original")));

        var shiftedStart = JsonDefaults.Clone(baseline);
        shiftedStart.StartDate = shiftedStart.StartDate.AddDays(7);
        shiftedStart.EndDate = shiftedStart.EndDate.AddDays(7);
        Assert.NotEqual(original, Signature(shiftedStart, CreatePlan("plan", "Original")));

        var sundayFirst = JsonDefaults.Clone(baseline);
        sundayFirst.WeekStartDay = WeekStartDay.Sunday;
        Assert.NotEqual(original, Signature(sundayFirst, CreatePlan("plan", "Original")));

        var changedPeriods = JsonDefaults.Clone(baseline);
        changedPeriods.PeriodSchedule[0].Start = new TimeOnly(7, 45);
        Assert.NotEqual(original, Signature(changedPeriods, CreatePlan("plan", "Original")));
    }

    [Fact]
    public void LibrarySignatureDistinguishesACommaInsideOneLabelFromTwoLabels()
    {
        var semester = CreateSemester();
        var oneLabel = CreateCourse(["alpha,beta"]);
        var twoLabels = JsonDefaults.Clone(oneLabel);
        twoLabels.Labels = ["alpha", "beta"];

        var first = CourseLibraryRenderSignature.Build(
            LanguageMode.English,
            semester,
            null,
            1,
            [oneLabel],
            [CreateGroup(oneLabel)]);
        var second = CourseLibraryRenderSignature.Build(
            LanguageMode.English,
            semester,
            null,
            1,
            [twoLabels],
            [CreateGroup(twoLabels)]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void LibrarySignatureIncludesSemesterSchedulingInputsUsedByConflictStatus()
    {
        var semester = CreateSemester();
        var course = CreateCourse([]);
        var original = CourseLibraryRenderSignature.Build(
            LanguageMode.English,
            semester,
            null,
            1,
            [course],
            [CreateGroup(course)]);

        var changed = JsonDefaults.Clone(semester);
        changed.WeekCount++;
        Assert.NotEqual(
            original,
            CourseLibraryRenderSignature.Build(
                LanguageMode.English,
                changed,
                null,
                1,
                [course],
                [CreateGroup(course)]));
    }

    [Fact]
    public void MaximumTimetableSignatureBuildIndexesCoursesOnceInsteadOfOncePerSnapshot()
    {
        var courses = Enumerable.Range(0, PlannerDataLimits.MaxCourses)
            .Select(index =>
            {
                var course = CreateCourse([]);
                course.OfferingId = index.ToString();
                course.CourseName = $"Course {index}";
                return course;
            })
            .ToList();
        var plan = CreatePlan("plan", "Maximum plan");
        plan.Snapshots = courses.Select((course, index) => new PlanCourseSnapshot
        {
            SnapshotId = index.ToString(),
            CourseOfferingId = course.OfferingId,
            RegistrationOrder = index
        }).ToList();
        var stopwatch = Stopwatch.StartNew();

        var signature = TimetableRenderSignatureService.Build(
            CreateSemester(),
            plan,
            null,
            PlannerViewMode.Week,
            1,
            "Light",
            courses);

        stopwatch.Stop();
        Assert.NotEmpty(signature);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Signature creation took {stopwatch.Elapsed}.");
    }

    private static string Signature(Semester semester, SelectionPlan plan) =>
        TimetableRenderSignatureService.Build(
            semester,
            plan,
            null,
            PlannerViewMode.Week,
            1,
            "Light",
            []);

    private static Semester CreateSemester() => new()
    {
        SemesterId = "semester",
        SemesterName = "Semester",
        StartDate = new DateOnly(2030, 2, 18),
        EndDate = new DateOnly(2030, 6, 9),
        WeekCount = 16,
        WeekStartDay = WeekStartDay.Monday,
        PeriodSchedule =
        [
            new PeriodDefinition
            {
                Period = 1,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(8, 45)
            }
        ]
    };

    private static SelectionPlan CreatePlan(string id, string name) => new()
    {
        PlanId = id,
        SemesterId = "semester",
        PlanName = name
    };

    private static CourseOffering CreateCourse(List<string> labels) => new()
    {
        OfferingId = new string('a', 64),
        SemesterId = "semester",
        CourseName = "Course",
        Teacher = "Teacher",
        Location = "Room",
        Credits = 3,
        CourseGroupType = "Group",
        StudyType = "Study",
        Labels = labels,
        Color = "#123456",
        ModifiedAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static LibraryGroup CreateGroup(CourseOffering course) => new()
    {
        SemesterName = "Semester",
        CourseGroupType = course.CourseGroupType ?? "",
        StudyType = course.StudyType ?? "",
        Courses = [course]
    };
}

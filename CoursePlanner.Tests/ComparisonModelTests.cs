using CoursePlanner.Core;

namespace CoursePlanner.Tests;

public sealed class ComparisonModelTests
{
    [Fact]
    public void ComparisonPreservesCommonRemovedAndAddedCoursesInOneSlot()
    {
        var semester = SemesterWithFourPeriods();
        var common = Course(semester, "B", 1, 1);
        var removed = Course(semester, "A", 1, 1);
        var added = Course(semester, "C", 1, 1);
        var basePlan = Plan(semester, common, removed);
        var currentPlan = Plan(semester, common, added);

        var difference = Assert.Single(PlannerDomainService.Compare(
            basePlan,
            currentPlan,
            semester,
            1,
            [common, removed, added]));

        Assert.Equal(DifferenceKind.Replaced, difference.Kind);
        Assert.Equal(["A", "B"], CourseNames(difference.BaseCourses));
        Assert.Equal(["B", "C"], CourseNames(difference.CurrentCourses));
        Assert.Equal(["B"], CourseNames(difference.UnchangedCourses));
        Assert.Equal(["A"], CourseNames(difference.RemovedCourses));
        Assert.Equal(["C"], CourseNames(difference.AddedCourses));
        Assert.Same(removed, difference.BaseCourse);
        Assert.Same(added, difference.CurrentCourse);
    }

    [Fact]
    public void ComparisonSetsAndRepresentativesAreIndependentOfPlanAndLibraryOrder()
    {
        var semester = SemesterWithFourPeriods();
        var common = Course(semester, "B", 1, 1);
        var removedA = Course(semester, "A", 1, 1);
        var removedD = Course(semester, "D", 1, 1);
        var addedC = Course(semester, "C", 1, 1);
        var addedE = Course(semester, "E", 1, 1);
        var forward = Assert.Single(PlannerDomainService.Compare(
            Plan(semester, common, removedD, removedA),
            Plan(semester, addedE, common, addedC),
            semester,
            1,
            [removedD, addedE, common, addedC, removedA]));
        var reverse = Assert.Single(PlannerDomainService.Compare(
            Plan(semester, removedA, removedD, common),
            Plan(semester, addedC, common, addedE),
            semester,
            1,
            [addedE, removedA, addedC, removedD, common]));

        Assert.Equal(CourseNames(forward.BaseCourses), CourseNames(reverse.BaseCourses));
        Assert.Equal(CourseNames(forward.CurrentCourses), CourseNames(reverse.CurrentCourses));
        Assert.Equal(CourseNames(forward.UnchangedCourses), CourseNames(reverse.UnchangedCourses));
        Assert.Equal(CourseNames(forward.RemovedCourses), CourseNames(reverse.RemovedCourses));
        Assert.Equal(CourseNames(forward.AddedCourses), CourseNames(reverse.AddedCourses));
        Assert.Equal(
            forward.BaseCourses.Select(course => course.OfferingId),
            reverse.BaseCourses.Select(course => course.OfferingId));
        Assert.Equal(
            forward.CurrentCourses.Select(course => course.OfferingId),
            reverse.CurrentCourses.Select(course => course.OfferingId));
        Assert.Equal(forward.BaseCourse?.OfferingId, reverse.BaseCourse?.OfferingId);
        Assert.Contains(forward.BaseCourse!, forward.RemovedCourses);
        Assert.Equal(forward.CurrentCourse?.OfferingId, reverse.CurrentCourse?.OfferingId);
        Assert.Contains(forward.CurrentCourse!, forward.AddedCourses);

        var courses = new[] { common, removedA, removedD, addedC, addedE };
        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(courses, semester, 1, [forward]);
        Assert.Equal(5, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(5, block.ConflictCount));
        Assert.Equal(
            DifferenceKind.Unchanged,
            Assert.Single(blocks, block => block.Course.OfferingId == common.OfferingId).Difference?.Kind);
        Assert.All(
            blocks.Where(block => block.Course.OfferingId != common.OfferingId),
            block => Assert.Equal(DifferenceKind.Replaced, block.Difference?.Kind));
    }

    [Fact]
    public void ComparisonExportAndRenderingContainTheCompleteCourseUnion()
    {
        var semester = SemesterWithFourPeriods();
        var common = Course(semester, "B", 1, 1);
        var removed = Course(semester, "A", 1, 1);
        var added = Course(semester, "C", 1, 1);
        var basePlan = Plan(semester, common, removed);
        var currentPlan = Plan(semester, common, added);
        var library = new[] { common, removed, added };
        var differences = PlannerDomainService.Compare(basePlan, currentPlan, semester, 1, library);

        var exportCourses = TimetableRenderModelService.CoursesForExport(currentPlan, library, differences);
        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(exportCourses, semester, 1, differences);

        Assert.Equal(["A", "B", "C"], CourseNames(exportCourses));
        Assert.Equal(["A", "B", "C"], CourseNames(blocks.Select(block => block.Course)));
        Assert.Equal(
            DifferenceKind.Unchanged,
            Assert.Single(blocks, block => block.Course.OfferingId == common.OfferingId).Difference?.Kind);
        Assert.Equal(
            DifferenceKind.Replaced,
            Assert.Single(blocks, block => block.Course.OfferingId == removed.OfferingId).Difference?.Kind);
        Assert.Equal(
            DifferenceKind.Replaced,
            Assert.Single(blocks, block => block.Course.OfferingId == added.OfferingId).Difference?.Kind);
    }

    [Fact]
    public void ComparisonRenderingSplitsACourseWhenDifferenceChangesAcrossPeriods()
    {
        var semester = SemesterWithFourPeriods();
        var removed = Course(semester, "A", 1, 3);
        var added = Course(semester, "C", 2, 2);
        var basePlan = Plan(semester, removed);
        var currentPlan = Plan(semester, added);
        var library = new[] { removed, added };
        var differences = PlannerDomainService.Compare(basePlan, currentPlan, semester, 1, library);
        var exportCourses = TimetableRenderModelService.CoursesForExport(currentPlan, library, differences);

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(exportCourses, semester, 1, differences);

        Assert.Equal(
            new[]
            {
                (Start: 1, End: 1, Kind: DifferenceKind.Removed),
                (Start: 2, End: 2, Kind: DifferenceKind.Replaced),
                (Start: 3, End: 3, Kind: DifferenceKind.Removed)
            },
            blocks
                .Where(block => block.Course.OfferingId == removed.OfferingId)
                .OrderBy(block => block.StartPeriod)
                .Select(block => (block.StartPeriod, block.EndPeriod, block.Difference!.Kind))
                .ToArray());
        var addedBlock = Assert.Single(blocks, block => block.Course.OfferingId == added.OfferingId);
        Assert.Equal((2, 2, DifferenceKind.Replaced),
            (addedBlock.StartPeriod, addedBlock.EndPeriod, addedBlock.Difference?.Kind));
    }

    [Fact]
    public void OneSidedDeltaDoesNotColorCommonOrUnrelatedCourses()
    {
        var semester = SemesterWithFourPeriods();
        var common = Course(semester, "B", 1, 1);
        var added = Course(semester, "C", 1, 1);
        var unrelated = Course(semester, "D", 1, 1);
        var basePlan = Plan(semester, common);
        var currentPlan = Plan(semester, common, added);
        var differences = PlannerDomainService.Compare(
            basePlan,
            currentPlan,
            semester,
            1,
            [unrelated, added, common]);

        var difference = Assert.Single(differences);
        Assert.Equal(DifferenceKind.Added, difference.Kind);
        Assert.Equal(["B"], CourseNames(difference.UnchangedCourses));
        Assert.Empty(difference.RemovedCourses);
        Assert.Equal(["C"], CourseNames(difference.AddedCourses));

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            [unrelated, common, added],
            semester,
            1,
            differences);
        Assert.Null(Assert.Single(blocks, block => block.Course.OfferingId == unrelated.OfferingId).Difference);
        Assert.Equal(
            DifferenceKind.Unchanged,
            Assert.Single(blocks, block => block.Course.OfferingId == common.OfferingId).Difference?.Kind);
        Assert.Equal(
            DifferenceKind.Added,
            Assert.Single(blocks, block => block.Course.OfferingId == added.OfferingId).Difference?.Kind);
    }

    private static string[] CourseNames(IEnumerable<CourseOffering> courses) =>
        courses.Select(course => course.CourseName).Order(StringComparer.Ordinal).ToArray();

    private static SelectionPlan Plan(Semester semester, params CourseOffering[] courses) =>
        new()
        {
            SemesterId = semester.SemesterId,
            Snapshots = courses
                .Select(course => new PlanCourseSnapshot { CourseOfferingId = course.OfferingId })
                .ToList()
        };

    private static CourseOffering Course(Semester semester, string name, int startPeriod, int endPeriod)
    {
        var course = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = name,
            Teacher = "Teacher",
            Location = "Room",
            Color = "#123456",
            MeetingTimes =
            [
                new MeetingTime
                {
                    Weekday = 1,
                    StartPeriod = startPeriod,
                    EndPeriod = endPeriod,
                    Weeks = "1"
                }
            ]
        };
        CourseIdentityService.AssignOfferingId(course);
        return course;
    }

    private static Semester SemesterWithFourPeriods() =>
        new()
        {
            SemesterId = "semester",
            SemesterName = "Semester",
            StartDate = new DateOnly(2026, 9, 7),
            EndDate = new DateOnly(2026, 9, 13),
            WeekCount = 1,
            WeekStartDay = WeekStartDay.Monday,
            PeriodSchedule = Enumerable.Range(1, 4)
                .Select(period => new PeriodDefinition
                {
                    Period = period,
                    Start = new TimeOnly(8 + period, 0),
                    End = new TimeOnly(8 + period, 45)
                })
                .ToList()
        };
}

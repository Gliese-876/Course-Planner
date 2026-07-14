using System.Diagnostics;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class TimetableRenderStressTests
{
    [Fact]
    public void MultiWeekBuilderMatchesIndependentWeekRendering()
    {
        var semester = SemesterWithPeriods(weeks: 6, periods: 8);
        var courses = Enumerable.Range(0, 48)
            .Select(index => Course(
                index,
                weekday: (index % 7) + 1,
                startPeriod: (index % 6) + 1,
                endPeriod: Math.Min(8, (index % 6) + 2),
                weeks: "1-6",
                parity: index % 3 == 0 ? WeekParity.Odd : WeekParity.All))
            .ToList();
        var requestedWeeks = Enumerable.Range(1, semester.WeekCount).ToArray();

        var combined = TimetableRenderModelService.BuildCourseBlocksByWeek(
            courses,
            semester,
            requestedWeeks);

        foreach (var week in requestedWeeks)
        {
            var independent = TimetableRenderModelService.BuildWeekCourseBlocks(courses, semester, week);
            Assert.NotEmpty(independent);
            Assert.Equal(BlockSignatures(independent), BlockSignatures(combined[week]));
        }
    }

    [Fact]
    public void OverviewOccupancyMatchesFullSlotExpansionForVisibleRows()
    {
        var semester = SemesterWithPeriods(weeks: 6, periods: 12);
        var courses = Enumerable.Range(0, 80)
            .Select(index => Course(
                index,
                weekday: (index % 7) + 1,
                startPeriod: (index % 10) + 1,
                endPeriod: Math.Min(12, (index % 10) + 3),
                weeks: "1-6",
                parity: index % 2 == 0 ? WeekParity.Odd : WeekParity.Even))
            .ToList();

        var actual = TimetableRenderModelService.BuildOverviewCourseBySlot(
            courses,
            semester,
            maximumPeriod: 8);
        var expected = PlannerDomainService.ExpandSlots(courses, semester)
            .Where(pair => pair.Key.Period <= 8)
            .ToDictionary(pair => pair.Key, pair => pair.Value[0]);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (slot, expectedCourse) in expected)
            Assert.Same(expectedCourse, actual[slot]);
    }

    [Fact]
    public void MaximumPlanRowsAcrossSixtyWeeksBuildWithinAUserVisibleDelay()
    {
        var semester = SemesterWithPeriods(weeks: 60, periods: 16);
        var courses = Enumerable.Range(0, PlannerDataLimits.MaxMeetingRowsPerPlan)
            .Select(index => Course(index, 1, 1, 16, "1-60"))
            .ToList();
        var stopwatch = Stopwatch.StartNew();

        var blocks = TimetableRenderModelService.BuildCourseBlocksByWeek(
            courses,
            semester,
            Enumerable.Range(1, 60));

        stopwatch.Stop();
        Assert.Equal(60 * PlannerDataLimits.MaxMeetingRowsPerPlan, blocks.Values.Sum(value => value.Count));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Building the bounded maximum timetable took {stopwatch.Elapsed.TotalSeconds:0.000}s.");
    }

    [Fact]
    public void DenseReplacementComparisonDoesNotRebuildCourseSetsPerBlockAndPeriod()
    {
        const int commonCourseCount = 400;
        var semester = SemesterWithPeriods(weeks: 1, periods: 64);
        var common = Enumerable.Range(0, commonCourseCount)
            .Select(index => Course(index, 1, 1, 64, "1"))
            .ToList();
        var removed = Course(commonCourseCount, 1, 1, 64, "1");
        var added = Course(commonCourseCount + 1, 1, 1, 64, "1");
        var library = common.Append(removed).Append(added).ToList();
        var basePlan = Plan(common.Append(removed));
        var currentPlan = Plan(common.Append(added));
        var differences = PlannerDomainService.Compare(basePlan, currentPlan, semester, 1, library);
        var exportCourses = TimetableRenderModelService.CoursesForExport(currentPlan, library, differences);
        var stopwatch = Stopwatch.StartNew();

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            exportCourses,
            semester,
            1,
            differences);

        stopwatch.Stop();
        Assert.Equal(commonCourseCount + 2, blocks.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Rendering a bounded dense comparison took {stopwatch.Elapsed.TotalSeconds:0.000}s.");
    }

    [Fact]
    public void MaximumTwoPlanComparisonKeepsTheCompleteFourThousandCourseUnionBounded()
    {
        var semester = SemesterWithPeriods(weeks: 1, periods: 16);
        var baseCourses = Enumerable.Range(0, PlannerDataLimits.MaxMeetingRowsPerPlan)
            .Select(index => Course(index, 1, 1, 16, "1"))
            .ToList();
        var currentCourses = Enumerable.Range(
                PlannerDataLimits.MaxMeetingRowsPerPlan,
                PlannerDataLimits.MaxMeetingRowsPerPlan)
            .Select(index => Course(index, 1, 1, 16, "1"))
            .ToList();
        var library = baseCourses.Concat(currentCourses).ToList();
        var basePlan = Plan(baseCourses);
        var currentPlan = Plan(currentCourses);
        var stopwatch = Stopwatch.StartNew();

        var differences = PlannerDomainService.Compare(basePlan, currentPlan, semester, 1, library);
        var union = TimetableRenderModelService.CoursesForExport(currentPlan, library, differences);
        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            union,
            semester,
            1,
            differences);

        stopwatch.Stop();
        Assert.Equal(PlannerDataLimits.MaxMeetingRowsPerPlan * 2, union.Count);
        Assert.Equal(PlannerDataLimits.MaxMeetingRowsPerPlan * 2, blocks.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Building the maximum two-plan union took {stopwatch.Elapsed.TotalSeconds:0.000}s.");
    }

    private static IReadOnlyList<string> BlockSignatures(IEnumerable<TimetableCourseBlock> blocks) =>
        blocks.Select(block => string.Join(
                '|',
                block.Course.OfferingId,
                block.Slot.Week,
                block.Slot.Weekday,
                block.StartPeriod,
                block.EndPeriod,
                block.ConflictIndex,
                block.ConflictCount,
                block.Difference?.Kind.ToString() ?? "none"))
            .ToList();

    private static Semester SemesterWithPeriods(int weeks, int periods) => new()
    {
        SemesterId = "semester",
        SemesterName = "Stress",
        StartDate = new DateOnly(2026, 1, 5),
        EndDate = new DateOnly(2026, 1, 5).AddDays((weeks * 7) - 1),
        WeekCount = weeks,
        WeekStartDay = WeekStartDay.Monday,
        PeriodSchedule = Enumerable.Range(1, periods)
            .Select(period => new PeriodDefinition
            {
                Period = period,
                Start = new TimeOnly(8, 0).AddMinutes(period * 30),
                End = new TimeOnly(8, 0).AddMinutes((period * 30) + 25)
            })
            .ToList()
    };

    private static CourseOffering Course(
        int index,
        int weekday,
        int startPeriod,
        int endPeriod,
        string weeks,
        WeekParity parity = WeekParity.All) => new()
        {
            OfferingId = $"course-{index:D5}",
            SemesterId = "semester",
            CourseName = $"Course {index}",
            Color = "#336699",
            MeetingTimes =
        {
            new MeetingTime
            {
                Weekday = weekday,
                StartPeriod = startPeriod,
                EndPeriod = endPeriod,
                Weeks = weeks,
                WeekParity = parity
            }
        }
        };

    private static SelectionPlan Plan(IEnumerable<CourseOffering> courses) => new()
    {
        SemesterId = "semester",
        Snapshots = courses.Select(course => new PlanCourseSnapshot
        {
            CourseOfferingId = course.OfferingId
        }).ToList()
    };
}

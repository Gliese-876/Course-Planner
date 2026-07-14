using System.Diagnostics;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class TimetableConflictStressTests
{
    [Fact]
    public void IntervalConflictLookupMatchesSlotExpansionAcrossRandomSchedules()
    {
        var random = new Random(73021);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var semester = Semester(weeks: 8, periods: 12);
            var courses = Enumerable.Range(0, 80)
                .Select(index => RandomCourse(random, index))
                .ToList();
            var source = courses[0];
            var plan = new SelectionPlan
            {
                SemesterId = semester.SemesterId,
                Snapshots = courses.Select(course => new PlanCourseSnapshot
                {
                    CourseOfferingId = course.OfferingId
                }).ToList()
            };
            var sourceSlots = PlannerDomainService.ExpandSlots(source, semester).ToHashSet();
            var expected = PlanCourseResolver.Courses(plan, courses)
                .Where(course => !string.Equals(course.OfferingId, source.OfferingId, StringComparison.Ordinal) &&
                                 PlannerDomainService.ExpandSlots(course, semester).Any(sourceSlots.Contains))
                .DistinctBy(course => course.OfferingId)
                .Select(course => course.OfferingId)
                .ToList();

            var actual = PlannerDomainService.FindConflicts(plan, source, semester, courses)
                .Select(course => course.OfferingId)
                .ToList();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ConflictSlotCounterMatchesDistinctCourseSlotSemantics()
    {
        var random = new Random(9901);
        var semester = Semester(weeks: 8, periods: 12);
        var courses = Enumerable.Range(0, 300)
            .Select(index => RandomCourse(random, index))
            .ToList();
        courses[0].MeetingTimes.Add(JsonDefaults.Clone(courses[0].MeetingTimes[0]));
        var expanded = PlannerDomainService.ExpandSlots(courses.Append(courses[0]), semester);
        var expected = expanded.Count(pair =>
            pair.Value.Select(course => course.OfferingId).Distinct(StringComparer.Ordinal).Count() > 1);

        var actual = TimetableConflictService.CountConflictSlots(courses.Append(courses[0]), semester);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MaximumMeetingRowsConflictMetricsStayWithinInteractiveBudget()
    {
        var semester = Semester(weeks: SemesterRules.MaxWeekCount, periods: 128);
        var courses = Enumerable.Range(0, PlannerDataLimits.MaxMeetingRowsPerPlan)
            .Select(index => new CourseOffering
            {
                OfferingId = $"course-{index:D4}",
                SemesterId = semester.SemesterId,
                CourseName = $"Course {index}",
                Color = "#336699",
                MeetingTimes =
                {
                    new MeetingTime
                    {
                        Weekday = 1,
                        StartPeriod = 1,
                        EndPeriod = 128,
                        Weeks = "1-60"
                    }
                }
            })
            .ToList();
        var stopwatch = Stopwatch.StartNew();

        var conflicts = TimetableConflictService.CountConflictSlots(courses, semester);

        stopwatch.Stop();
        Assert.Equal(SemesterRules.MaxWeekCount * 128, conflicts);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Maximum conflict metrics took {stopwatch.Elapsed.TotalSeconds:0.000}s.");
    }

    [Fact]
    public void MaximumCandidateRowsWithoutOverlapAvoidPerPeriodExpansion()
    {
        var semester = Semester(weeks: SemesterRules.MaxWeekCount, periods: 128);
        var source = new CourseOffering
        {
            OfferingId = "source",
            SemesterId = semester.SemesterId,
            CourseName = "Source",
            MeetingTimes =
            {
                new MeetingTime { Weekday = 1, StartPeriod = 1, EndPeriod = 128, Weeks = "1-60" }
            }
        };
        var candidates = Enumerable.Range(0, PlannerDataLimits.MaxMeetingRowsPerPlan - 1)
            .Select(index => new CourseOffering
            {
                OfferingId = $"candidate-{index:D4}",
                SemesterId = semester.SemesterId,
                CourseName = $"Candidate {index}",
                MeetingTimes =
                {
                    new MeetingTime { Weekday = 2, StartPeriod = 1, EndPeriod = 128, Weeks = "1-60" }
                }
            })
            .ToList();
        var plan = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            Snapshots = candidates.Select(course => new PlanCourseSnapshot
            {
                CourseOfferingId = course.OfferingId
            }).ToList()
        };
        var stopwatch = Stopwatch.StartNew();

        var conflicts = PlannerDomainService.FindConflicts(plan, source, semester, candidates).ToList();

        stopwatch.Stop();
        Assert.Empty(conflicts);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Maximum non-overlapping lookup took {stopwatch.Elapsed.TotalSeconds:0.000}s.");
    }

    private static CourseOffering RandomCourse(Random random, int index)
    {
        var course = new CourseOffering
        {
            OfferingId = $"random-{index:D3}",
            SemesterId = "semester",
            CourseName = $"Random {index}",
            Color = "#336699"
        };
        var expressions = new[] { "1-8", "1,3,5,7", "2-6", "4", "1-2,6-8" };
        var meetingCount = random.Next(1, 5);
        for (var meetingIndex = 0; meetingIndex < meetingCount; meetingIndex++)
        {
            var start = random.Next(-1, 14);
            var length = random.Next(0, 5);
            course.MeetingTimes.Add(new MeetingTime
            {
                Weekday = random.Next(0, 9),
                StartPeriod = start,
                EndPeriod = start + length,
                Weeks = expressions[random.Next(expressions.Length)],
                WeekParity = (WeekParity)random.Next(0, 3)
            });
        }
        return course;
    }

    private static Semester Semester(int weeks, int periods) => new()
    {
        SemesterId = "semester",
        SemesterName = "Semester",
        StartDate = new DateOnly(2026, 1, 5),
        EndDate = SemesterRules.CalculateEndDate(new DateOnly(2026, 1, 5), weeks, WeekStartDay.Monday),
        WeekCount = weeks,
        WeekStartDay = WeekStartDay.Monday,
        PeriodSchedule = Enumerable.Range(1, periods)
            .Select(period => new PeriodDefinition
            {
                Period = period,
                Start = new TimeOnly(0, 0).AddMinutes(period),
                End = new TimeOnly(0, 0).AddMinutes(period + 1)
            }).ToList()
    };
}

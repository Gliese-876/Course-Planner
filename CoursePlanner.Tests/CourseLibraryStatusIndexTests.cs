using System.Collections;
using System.Diagnostics;
using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class CourseLibraryStatusIndexTests
{
    [Fact]
    public void ConflictStatusHasPriorityAndIsComputedForEveryCourseSharingASlot()
    {
        var semester = CreateSemester();
        var first = CreateCourse("first", 1, 1, 2);
        first.EnrolledCount = first.Capacity = 10;
        var second = CreateCourse("second", 1, 2, 3);
        var plan = CreatePlan(first, second);

        var index = CourseLibraryStatusService.CreateIndex(
            plan,
            semester,
            1,
            [first, second],
            key => key);

        Assert.Equal(CourseLibraryStatusKind.Conflict, index.Resolve(first).Kind);
        Assert.Equal(CourseLibraryStatusKind.Conflict, index.Resolve(second).Kind);
    }

    [Fact]
    public void IndexPreservesFullTightCurrentPlanAndAbsentStatuses()
    {
        var semester = CreateSemester();
        var full = CreateCourse("full", 1, 1, 1);
        full.EnrolledCount = full.Capacity = 20;
        var tight = CreateCourse("tight", 2, 1, 1);
        tight.EnrolledCount = 9;
        tight.Capacity = 10;
        var current = CreateCourse("current", 3, 1, 1);
        var absent = CreateCourse("absent", 4, 1, 1);
        var plan = CreatePlan(full, tight, current);

        var index = CourseLibraryStatusService.CreateIndex(
            plan,
            semester,
            1,
            [full, tight, current, absent],
            key => key);

        Assert.Equal(CourseLibraryStatusKind.Full, index.Resolve(full).Kind);
        Assert.Equal(CourseLibraryStatusKind.Tight, index.Resolve(tight).Kind);
        Assert.Equal(CourseLibraryStatusKind.CurrentPlan, index.Resolve(current).Kind);
        Assert.Equal(CourseLibraryStatusKind.None, index.Resolve(absent).Kind);
    }

    [Fact]
    public void MaximumPlanStatusIndexMaterializesTheLibraryOnceAndAvoidsPerCoursePlanScans()
    {
        var courses = Enumerable.Range(0, PlannerDataLimits.MaxCourses)
            .Select(index => CreateCourse(index.ToString(), 1, 1, 1, scheduled: false))
            .ToList();
        var source = new CountingEnumerable<CourseOffering>(courses);
        var plan = CreatePlan(courses.ToArray());
        var stopwatch = Stopwatch.StartNew();

        var index = CourseLibraryStatusService.CreateIndex(
            plan,
            CreateSemester(),
            1,
            source,
            key => key);
        foreach (var course in courses)
            Assert.Equal(CourseLibraryStatusKind.CurrentPlan, index.Resolve(course).Kind);

        stopwatch.Stop();
        Assert.Equal(1, source.EnumerationCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Indexing took {stopwatch.Elapsed}.");
    }

    private static Semester CreateSemester() => new()
    {
        SemesterId = "semester",
        SemesterName = "Semester",
        StartDate = new DateOnly(2030, 1, 7),
        EndDate = new DateOnly(2030, 4, 28),
        WeekCount = 16,
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

    private static CourseOffering CreateCourse(
        string id,
        int weekday,
        int start,
        int end,
        bool scheduled = true) => new()
        {
            OfferingId = id,
            SemesterId = "semester",
            CourseName = id,
            MeetingTimes = scheduled
            ?
            [
                new MeetingTime
                {
                    Weekday = weekday,
                    StartPeriod = start,
                    EndPeriod = end,
                    Weeks = "1"
                }
            ]
            : []
        };

    private static SelectionPlan CreatePlan(params CourseOffering[] courses) => new()
    {
        SemesterId = "semester",
        Snapshots = courses.Select((course, index) => new PlanCourseSnapshot
        {
            SnapshotId = index.ToString(),
            CourseOfferingId = course.OfferingId,
            RegistrationOrder = index
        }).ToList()
    };

    private sealed class CountingEnumerable<T>(IEnumerable<T> source) : IEnumerable<T>
    {
        private int _enumerationCount;

        public int EnumerationCount => Volatile.Read(ref _enumerationCount);

        public IEnumerator<T> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerationCount);
            return source.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

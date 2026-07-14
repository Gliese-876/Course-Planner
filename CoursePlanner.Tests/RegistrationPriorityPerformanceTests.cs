using System.Diagnostics;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class RegistrationPriorityPerformanceTests
{
    [Fact]
    public void ApplyOrderHandlesTheMaximumReverseSnapshotOrderWithinAnInteractiveBudget()
    {
        var plan = new SelectionPlan
        {
            Snapshots = Enumerable.Range(0, PlannerDataLimits.MaxSnapshotsPerPlan)
                .Select(index => new PlanCourseSnapshot
                {
                    SnapshotId = $"snapshot-{index:D4}",
                    CourseOfferingId = $"course-{index:D4}",
                    RegistrationOrder = index
                })
                .ToList()
        };
        var requestedOrder = plan.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .Reverse()
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var changed = RegistrationPriorityService.ApplyOrder(plan, requestedOrder);
        stopwatch.Stop();

        Assert.True(changed);
        Assert.Equal(0, plan.Snapshots[^1].RegistrationOrder);
        Assert.Equal(PlannerDataLimits.MaxSnapshotsPerPlan - 1, plan.Snapshots[0].RegistrationOrder);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(50),
            $"Applying 5,000 reversed snapshot ids took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void AnalyzeHandlesTheMaximumMeetingRowShapeWithinAnInteractiveBudget()
    {
        const int planCourseCount = 400;
        const int alternativeCount = 600;
        var planCourses = Enumerable.Range(0, planCourseCount)
            .Select(index => ParallelCourse($"plan-{index}", weekday: 1))
            .ToList();
        var alternatives = Enumerable.Range(0, alternativeCount)
            .Select(index => ParallelCourse($"alternative-{index}", weekday: 2))
            .ToList();
        var plan = new SelectionPlan { SemesterId = "semester" };
        for (var index = 0; index < planCourses.Count; index++)
        {
            plan.Snapshots.Add(new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-{index}",
                CourseOfferingId = planCourses[index].OfferingId,
                RegistrationOrder = index
            });
        }

        var library = planCourses.Concat(alternatives).ToList();
        Assert.Equal(2_000, library.Sum(course => course.MeetingTimes.Count));

        var stopwatch = Stopwatch.StartNew();
        var analyses = RegistrationPriorityService.Analyze(plan, library);
        stopwatch.Stop();

        Assert.Equal(planCourseCount, analyses.Count);
        Assert.All(analyses, analysis => Assert.True(analysis.EffectiveAlternatives > 500d));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Analyze took {stopwatch.Elapsed.TotalSeconds:F3}s for 2,000 meeting rows.");
    }

    private static CourseOffering ParallelCourse(string offeringId, int weekday) => new()
    {
        OfferingId = offeringId,
        SemesterId = "semester",
        CourseName = "Adversarial Parallel Course",
        Credits = 3m,
        StudyType = PlannerLabels.Elective,
        EnrolledCount = 1,
        Capacity = 10,
        MeetingTimes =
        {
            new MeetingTime
            {
                Weekday = weekday,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = "1-16"
            },
            new MeetingTime
            {
                Weekday = weekday,
                StartPeriod = 3,
                EndPeriod = 4,
                Weeks = "1-16"
            }
        }
    };
}

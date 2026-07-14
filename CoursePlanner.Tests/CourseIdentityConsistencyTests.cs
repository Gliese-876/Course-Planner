using CoursePlanner.Core;

namespace CoursePlanner.Tests;

public sealed class CourseIdentityConsistencyTests
{
    [Fact]
    public void ConsistencyCanonicalizesLegacyOfferingIdsWithoutDroppingPlanReferences()
    {
        var course = CreateCourse("1-3");
        course.OfferingId = "legacy-imported-id";
        var snapshot = new PlanCourseSnapshot
        {
            SnapshotId = "snapshot-1",
            CourseOfferingId = course.OfferingId
        };
        var document = new PlannerDocument
        {
            Semesters =
            [
                new Semester
                {
                    SemesterId = "semester-1",
                    SemesterName = "Semester",
                    StartDate = new DateOnly(2026, 9, 1),
                    EndDate = new DateOnly(2026, 12, 21),
                    WeekCount = 16,
                    WeekStartDay = WeekStartDay.Monday,
                    PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
                }
            ],
            CourseLibrary = [course],
            Plans =
            [
                new SelectionPlan
                {
                    PlanId = "plan-1",
                    SemesterId = "semester-1",
                    PlanName = "Plan",
                    Snapshots = [snapshot]
                }
            ],
            Settings = new AppSettings
            {
                CurrentSemesterId = "semester-1",
                CurrentPlanId = "plan-1",
                OpenPlanIds = ["plan-1"]
            }
        };

        DocumentConsistencyService.Ensure(document);

        var canonicalId = Assert.Single(document.CourseLibrary).OfferingId;
        Assert.NotEqual("legacy-imported-id", canonicalId);
        Assert.Equal(canonicalId, Assert.Single(document.Plans[0].Snapshots).CourseOfferingId);
    }

    private static CourseOffering CreateCourse(string weeks) => new()
    {
        SemesterId = "semester-1",
        CourseName = "Data Structures",
        Teacher = "Teacher",
        Location = "A101",
        Credits = 3m,
        MeetingTimes =
        [
            new MeetingTime
            {
                Weekday = 1,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = weeks,
                WeekParity = WeekParity.All
            }
        ]
    };
}

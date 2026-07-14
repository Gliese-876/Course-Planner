using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class CourseLockingAndInactiveWeekTests
{
    [Fact]
    public void InactiveMeetingsAreOptionalAndCarryASeparateRenderState()
    {
        var semester = Semester();
        var inactive = Course("inactive", "1,3", weekday: 1, start: 1, end: 2);

        Assert.Empty(TimetableRenderModelService.BuildWeekCourseBlocks([inactive], semester, 2));

        var block = Assert.Single(TimetableRenderModelService.BuildWeekCourseBlocks(
            [inactive],
            semester,
            2,
            includeInactiveMeetings: true));

        Assert.False(block.IsInRequestedWeek);
        Assert.False(block.HasConflictInRequestedWeek);
        Assert.Equal(1, block.StartPeriod);
        Assert.Equal(2, block.EndPeriod);
    }

    [Fact]
    public void ActiveMeetingWinsOverAnOverlappingInactiveMeetingForTheSameCourse()
    {
        var semester = Semester();
        var course = Course("mixed", "1", weekday: 1, start: 1, end: 2);
        course.MeetingTimes.Add(new MeetingTime
        {
            Weekday = 1,
            StartPeriod = 1,
            EndPeriod = 2,
            Weeks = "2"
        });

        var block = Assert.Single(TimetableRenderModelService.BuildWeekCourseBlocks(
            [course],
            semester,
            2,
            includeInactiveMeetings: true));

        Assert.True(block.IsInRequestedWeek);
    }

    [Fact]
    public void InactiveOverlapUsesLayoutLanesWithoutReportingARealWeekConflict()
    {
        var semester = Semester();
        var active = Course("active", "2", weekday: 1, start: 1, end: 2);
        var inactive = Course("inactive", "1", weekday: 1, start: 1, end: 2);

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            [active, inactive],
            semester,
            2,
            includeInactiveMeetings: true);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(2, block.ConflictCount));
        Assert.All(blocks, block => Assert.False(block.HasConflictInRequestedWeek));
    }

    [Fact]
    public void TwoActiveOverlapsStillReportARealWeekConflict()
    {
        var semester = Semester();
        var first = Course("first", "2", weekday: 1, start: 1, end: 2);
        var second = Course("second", "2", weekday: 1, start: 2, end: 3);

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            [first, second],
            semester,
            2,
            includeInactiveMeetings: true);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.True(block.HasConflictInRequestedWeek));
    }

    [Fact]
    public void LockedSnapshotRoundTripsThroughRepositoryAndKeepsNullOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var document = TestDocumentFactory.CreatePopulated();
            var plan = document.Plans[0];
            var planId = plan.PlanId;
            var snapshot = plan.Snapshots[0];
            var offeringId = snapshot.CourseOfferingId;
            snapshot.IsLocked = true;
            RegistrationPriorityService.NormalizeOrders(plan);
            var repository = new SqliteAppRepository(directory);

            repository.Save(document, "locked-course");
            var loaded = repository.LoadOrCreate();
            Assert.Null(repository.LastRecoveryArtifactPath);
            var loadedPlan = loaded.Plans.Single(candidate => candidate.PlanId == planId);
            var loadedSnapshot = loadedPlan.Snapshots.Single(candidate =>
                candidate.CourseOfferingId == offeringId);

            Assert.True(loadedSnapshot.IsLocked);
            Assert.Null(loadedSnapshot.RegistrationOrder);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LockedSnapshotRoundTripsThroughSelectionPlanExchange()
    {
        var source = TestDocumentFactory.CreatePopulated();
        var sourcePlan = source.Plans[0];
        sourcePlan.Snapshots[0].IsLocked = true;
        RegistrationPriorityService.NormalizeOrders(sourcePlan);

        var json = ImportExportService.ExportSelectionPlanJson(source, sourcePlan);
        var preview = ImportExportService.PreviewSelectionPlan(new PlannerDocument(), json);
        var importedPlan = Assert.Single(preview.Items, item => item.Kind == "plan").Plan;

        Assert.NotNull(importedPlan);
        Assert.True(importedPlan.Snapshots[0].IsLocked);
        Assert.Null(importedPlan.Snapshots[0].RegistrationOrder);
    }

    private static Semester Semester() => new()
    {
        SemesterId = "semester",
        SemesterName = "Semester",
        StartDate = new DateOnly(2026, 1, 5),
        EndDate = new DateOnly(2026, 2, 1),
        WeekCount = 4,
        WeekStartDay = WeekStartDay.Monday,
        PeriodSchedule =
        [
            new PeriodDefinition { Period = 1, Start = new TimeOnly(8, 0), End = new TimeOnly(8, 45) },
            new PeriodDefinition { Period = 2, Start = new TimeOnly(8, 55), End = new TimeOnly(9, 40) },
            new PeriodDefinition { Period = 3, Start = new TimeOnly(10, 0), End = new TimeOnly(10, 45) }
        ]
    };

    private static CourseOffering Course(
        string id,
        string weeks,
        int weekday,
        int start,
        int end) => new()
        {
            OfferingId = id,
            SemesterId = "semester",
            CourseName = id,
            MeetingTimes =
            {
                new MeetingTime
                {
                    Weekday = weekday,
                    StartPeriod = start,
                    EndPeriod = end,
                    Weeks = weeks
                }
            }
        };
}

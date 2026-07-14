using System.Text.Json;
using CoursePlanner.Core;

namespace CoursePlanner.Services;

public static class TimetableRenderSignatureService
{
    public static string Build(
        Semester? semester,
        SelectionPlan? currentPlan,
        SelectionPlan? baseComparePlan,
        PlannerViewMode viewMode,
        int currentWeek,
        string themeKey,
        IEnumerable<CourseOffering> libraryCourses)
    {
        ArgumentNullException.ThrowIfNull(themeKey);
        ArgumentNullException.ThrowIfNull(libraryCourses);

        var library = libraryCourses.ToList();
        var signature = new TimetableSignature(
            SemesterSignature.Create(semester),
            PlanSignature.Create(currentPlan, library),
            PlanSignature.Create(baseComparePlan, library),
            viewMode,
            currentWeek,
            themeKey);
        return JsonSerializer.Serialize(signature, JsonDefaults.CompactOptions);
    }

    private sealed record TimetableSignature(
        SemesterSignature? Semester,
        PlanSignature? CurrentPlan,
        PlanSignature? BaseComparePlan,
        PlannerViewMode ViewMode,
        int CurrentWeek,
        string ThemeKey);

    private sealed record SemesterSignature(
        string SemesterId,
        string SemesterName,
        int StartDayNumber,
        int EndDayNumber,
        int WeekCount,
        WeekStartDay WeekStartDay,
        IReadOnlyList<PeriodSignature> Periods)
    {
        public static SemesterSignature? Create(Semester? semester) => semester is null
            ? null
            : new SemesterSignature(
                semester.SemesterId,
                semester.SemesterName,
                semester.StartDate.DayNumber,
                semester.EndDate.DayNumber,
                semester.WeekCount,
                semester.WeekStartDay,
                semester.PeriodSchedule
                    .OrderBy(period => period.Period)
                    .Select(period => new PeriodSignature(
                        period.Period,
                        period.Start.Ticks,
                        period.End.Ticks))
                    .ToArray());
    }

    private sealed record PeriodSignature(int Period, long StartTicks, long EndTicks);

    private sealed record PlanSignature(
        string PlanId,
        string PlanName,
        IReadOnlyList<SnapshotSignature> Snapshots)
    {
        public static PlanSignature? Create(
            SelectionPlan? plan,
            IReadOnlyList<CourseOffering> library)
        {
            if (plan is null)
                return null;

            var courseIndex = PlanCourseResolver.BuildCourseIndex(library);
            return new PlanSignature(
                plan.PlanId,
                plan.PlanName,
                plan.Snapshots.Select(snapshot =>
                {
                    var course = courseIndex.GetValueOrDefault(snapshot.CourseOfferingId);
                    return new SnapshotSignature(
                        snapshot.SnapshotId,
                        snapshot.CourseOfferingId,
                        snapshot.IsLocked,
                        snapshot.SnapshotAt.UtcTicks,
                        course is null ? null : CourseSignature.Create(course));
                }).ToArray());
        }
    }

    private sealed record SnapshotSignature(
        string SnapshotId,
        string CourseOfferingId,
        bool IsLocked,
        long SnapshotAtUtcTicks,
        CourseSignature? Course);

    private sealed record CourseSignature(
        string OfferingId,
        string CourseName,
        string Teacher,
        string Location,
        int? EnrolledCount,
        int? Capacity,
        string Color)
    {
        public static CourseSignature Create(CourseOffering course) =>
            new(
                course.OfferingId,
                course.CourseName,
                course.Teacher,
                course.Location,
                course.EnrolledCount,
                course.Capacity,
                course.Color);
    }
}

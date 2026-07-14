using System.Text.Json;
using CoursePlanner.Core;

namespace CoursePlanner.Services;

public static class CourseLibraryRenderSignature
{
    public static string Build(
        LanguageMode resolvedLanguage,
        Semester? currentSemester,
        SelectionPlan? currentPlan,
        int currentWeek,
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<LibraryGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(libraryCourses);
        ArgumentNullException.ThrowIfNull(groups);

        var library = libraryCourses.ToList();
        var signature = new LibrarySignature(
            resolvedLanguage,
            SemesterSignature.Create(currentSemester),
            PlanSignature.Create(currentPlan, library),
            currentWeek,
            groups.Select(GroupSignature.Create).ToArray());
        return JsonSerializer.Serialize(signature, JsonDefaults.CompactOptions);
    }

    private sealed record LibrarySignature(
        LanguageMode Language,
        SemesterSignature? Semester,
        PlanSignature? Plan,
        int CurrentWeek,
        IReadOnlyList<GroupSignature> Groups);

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
                    .Select(PeriodSignature.Create)
                    .ToArray());
    }

    private sealed record PeriodSignature(int Period, long StartTicks, long EndTicks)
    {
        public static PeriodSignature Create(PeriodDefinition period) =>
            new(period.Period, period.Start.Ticks, period.End.Ticks);
    }

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
                        course is null
                            ? null
                            : new CapacitySignature(
                                course.EnrolledCount,
                                course.Capacity));
                }).ToArray());
        }
    }

    private sealed record SnapshotSignature(
        string SnapshotId,
        string CourseOfferingId,
        bool IsLocked,
        long SnapshotAtUtcTicks,
        CapacitySignature? Capacity);

    private sealed record CapacitySignature(int? EnrolledCount, int? Capacity);

    private sealed record GroupSignature(
        string SemesterName,
        string CourseGroupType,
        string StudyType,
        IReadOnlyList<CourseSignature> Courses)
    {
        public static GroupSignature Create(LibraryGroup group) =>
            new(
                group.SemesterName,
                group.CourseGroupType,
                group.StudyType,
                group.Courses.Select(CourseSignature.Create).ToArray());
    }

    private sealed record CourseSignature(
        string OfferingId,
        string SemesterId,
        string CourseName,
        string Teacher,
        string Location,
        decimal Credits,
        string? CourseGroupType,
        string? StudyType,
        IReadOnlyList<string> Labels,
        IReadOnlyList<MeetingSignature> Meetings,
        int? EnrolledCount,
        int? Capacity,
        string Color,
        long ModifiedAtUtcTicks)
    {
        public static CourseSignature Create(CourseOffering course) =>
            new(
                course.OfferingId,
                course.SemesterId,
                course.CourseName,
                course.Teacher,
                course.Location,
                course.Credits,
                course.CourseGroupType,
                course.StudyType,
                course.Labels.OrderBy(label => label, StringComparer.OrdinalIgnoreCase).ToArray(),
                course.MeetingTimes
                    .OrderBy(meeting => meeting.Weekday)
                    .ThenBy(meeting => meeting.StartPeriod)
                    .ThenBy(meeting => meeting.EndPeriod)
                    .ThenBy(meeting => meeting.WeekParity)
                    .ThenBy(meeting => meeting.Weeks, StringComparer.Ordinal)
                    .Select(MeetingSignature.Create)
                    .ToArray(),
                course.EnrolledCount,
                course.Capacity,
                course.Color,
                course.ModifiedAt.UtcTicks);
    }

    private sealed record MeetingSignature(
        int Weekday,
        int StartPeriod,
        int EndPeriod,
        string Weeks,
        WeekParity WeekParity)
    {
        public static MeetingSignature Create(MeetingTime meeting) =>
            new(
                meeting.Weekday,
                meeting.StartPeriod,
                meeting.EndPeriod,
                meeting.Weeks,
                meeting.WeekParity);
    }
}

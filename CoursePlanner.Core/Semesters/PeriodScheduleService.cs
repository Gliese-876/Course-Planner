using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public sealed class PeriodScheduleCourseIdentityConflictException : InvalidOperationException
{
    public PeriodScheduleCourseIdentityConflictException()
        : base("Changing the period schedule would make distinct course offerings identical.")
    {
    }
}

public static class PeriodScheduleService
{
    private const long PeriodDurationTicks = 45 * TimeSpan.TicksPerMinute;
    private const long MinimumBreakTicks = 10 * TimeSpan.TicksPerMinute;

    public static PeriodDefinition AddPeriodAfter(
        Semester semester,
        int? selectedPeriodNumber,
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<PlanCourseSnapshot> planSnapshots)
    {
        ArgumentNullException.ThrowIfNull(semester);
        ArgumentNullException.ThrowIfNull(libraryCourses);
        ArgumentNullException.ThrowIfNull(planSnapshots);
        var capacity = PlannerCapacityRules.ValidateCanAddPeriod(semester.PeriodSchedule.Count);
        if (!capacity.IsValid)
            throw new InvalidOperationException(capacity.Errors[0].Code);

        var ordered = semester.PeriodSchedule.OrderBy(x => x.Period).ToList();
        var insertionIndex = selectedPeriodNumber is null
            ? ordered.Count
            : ordered.FindIndex(period => period.Period == selectedPeriodNumber.Value) is var selectedIndex && selectedIndex >= 0
                ? selectedIndex + 1
                : throw new InvalidOperationException("Selected period does not exist.");

        var previous = insertionIndex == 0 ? null : ordered[insertionIndex - 1];
        var suffix = ordered.Skip(insertionIndex).ToList();
        var startTicks = previous is null
            ? new TimeOnly(8, 0).Ticks
            : previous.End.Ticks + MinimumBreakTicks;
        var endTicks = startTicks + PeriodDurationTicks;
        if (startTicks >= TimeSpan.TicksPerDay || endTicks >= TimeSpan.TicksPerDay)
            throw new InvalidOperationException("The period schedule cannot cross midnight.");

        var suffixShift = suffix.Count == 0
            ? 0L
            : Math.Max(0L, endTicks + MinimumBreakTicks - suffix[0].Start.Ticks);
        if (suffix.Any(period => period.Start.Ticks + suffixShift >= TimeSpan.TicksPerDay ||
                                 period.End.Ticks + suffixShift >= TimeSpan.TicksPerDay))
        {
            throw new InvalidOperationException("The period schedule cannot cross midnight.");
        }

        var courses = libraryCourses.ToList();
        var snapshots = planSnapshots.ToList();
        foreach (var existing in suffix)
        {
            existing.Period++;
            if (suffixShift == 0)
                continue;
            existing.Start = new TimeOnly(existing.Start.Ticks + suffixShift);
            existing.End = new TimeOnly(existing.End.Ticks + suffixShift);
        }

        var period = new PeriodDefinition
        {
            Period = insertionIndex + 1,
            Start = new TimeOnly(startTicks),
            End = new TimeOnly(endTicks)
        };
        semester.PeriodSchedule.Add(period);
        semester.PeriodSchedule = semester.PeriodSchedule.OrderBy(x => x.Period).ToList();
        ReindexCourseMeetingsAfterInsert(courses, snapshots, period.Period);
        return period;
    }

    public static void DeletePeriod(
        Semester semester,
        int periodNumber,
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<PlanCourseSnapshot> planSnapshots)
    {
        ArgumentNullException.ThrowIfNull(semester);
        ArgumentNullException.ThrowIfNull(libraryCourses);
        ArgumentNullException.ThrowIfNull(planSnapshots);
        if (semester.PeriodSchedule.All(x => x.Period != periodNumber))
            throw new InvalidOperationException("Selected period does not exist.");
        if (semester.PeriodSchedule.All(x => x.Period == periodNumber))
            throw new InvalidOperationException("A semester must contain at least one period.");

        var courses = libraryCourses.ToList();
        var snapshots = planSnapshots.ToList();
        EnsureProjectedCourseIdentitiesAreUnique(
            courses,
            course => TransformMeetingsAfterDelete(course, periodNumber));

        semester.PeriodSchedule.RemoveAll(x => x.Period == periodNumber);
        foreach (var period in semester.PeriodSchedule.Where(x => x.Period > periodNumber))
            period.Period--;
        semester.PeriodSchedule = semester.PeriodSchedule.OrderBy(x => x.Period).ToList();
        ReindexCourseMeetingsAfterDelete(courses, snapshots, periodNumber);
    }

    public static void UpdatePeriodTime(Semester semester, int periodNumber, TimeOnly start, TimeOnly end)
    {
        var period = semester.PeriodSchedule.FirstOrDefault(x => x.Period == periodNumber);
        if (period is null)
            throw new InvalidOperationException("Selected period does not exist.");
        var previous = semester.PeriodSchedule
            .Where(candidate => candidate.Period < periodNumber)
            .MaxBy(candidate => candidate.Period);
        var next = semester.PeriodSchedule
            .Where(candidate => candidate.Period > periodNumber)
            .MinBy(candidate => candidate.Period);
        if (end <= start || previous is not null && start < previous.End || next is not null && end > next.Start)
            throw new InvalidOperationException("Period times must be ordered and must not overlap.");

        period.Start = start;
        period.End = end;
        semester.PeriodSchedule = semester.PeriodSchedule.OrderBy(x => x.Period).ToList();
    }

    public static void ResetToDefault(
        Semester semester,
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<PlanCourseSnapshot> planSnapshots)
    {
        ArgumentNullException.ThrowIfNull(semester);
        ArgumentNullException.ThrowIfNull(libraryCourses);
        ArgumentNullException.ThrowIfNull(planSnapshots);
        var courses = libraryCourses.ToList();
        var snapshots = planSnapshots.ToList();
        var defaults = PeriodScheduleFactory.CreateDefault12();
        var maximumPeriod = defaults.Count;
        EnsureProjectedCourseIdentitiesAreUnique(
            courses,
            course => TrimMeetingsToMaximumPeriod(course, maximumPeriod));

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var course in courses)
        {
            var originalOfferingId = course.OfferingId;
            if (TrimMeetingsToMaximumPeriod(course, maximumPeriod))
                UpdateCourseIdentity(course, originalOfferingId, idMap);
        }

        semester.PeriodSchedule = defaults;
        UpdateSnapshotReferences(snapshots, idMap);
    }

    private static void ReindexCourseMeetingsAfterInsert(
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<PlanCourseSnapshot> planSnapshots,
        int insertedPeriod)
    {
        var snapshots = planSnapshots.ToList();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var course in libraryCourses)
            ReindexCourseMeetingsAfterInsert(course, insertedPeriod, idMap);

        UpdateSnapshotReferences(snapshots, idMap);
    }

    private static void ReindexCourseMeetingsAfterDelete(
        IEnumerable<CourseOffering> libraryCourses,
        IEnumerable<PlanCourseSnapshot> planSnapshots,
        int deletedPeriod)
    {
        var snapshots = planSnapshots.ToList();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var course in libraryCourses)
            ReindexCourseMeetingsAfterDelete(course, deletedPeriod, idMap);

        UpdateSnapshotReferences(snapshots, idMap);
    }

    private static void ReindexCourseMeetingsAfterInsert(
        CourseOffering course,
        int insertedPeriod,
        Dictionary<string, string> idMap)
    {
        var originalOfferingId = course.OfferingId;
        var changed = false;
        foreach (var meeting in course.MeetingTimes)
        {
            if (meeting.StartPeriod >= insertedPeriod)
            {
                meeting.StartPeriod++;
                changed = true;
            }
            if (meeting.EndPeriod >= insertedPeriod)
            {
                meeting.EndPeriod++;
                changed = true;
            }
        }

        if (changed)
            UpdateCourseIdentity(course, originalOfferingId, idMap);
    }

    private static void ReindexCourseMeetingsAfterDelete(
        CourseOffering course,
        int deletedPeriod,
        Dictionary<string, string> idMap)
    {
        var originalOfferingId = course.OfferingId;
        if (!TransformMeetingsAfterDelete(course, deletedPeriod))
            return;

        UpdateCourseIdentity(course, originalOfferingId, idMap);
    }

    private static bool TransformMeetingsAfterDelete(CourseOffering course, int deletedPeriod)
    {
        var changed = false;
        var updated = new List<MeetingTime>(course.MeetingTimes.Count);
        foreach (var meeting in course.MeetingTimes)
        {
            if (meeting.StartPeriod == deletedPeriod && meeting.EndPeriod == deletedPeriod)
            {
                changed = true;
                continue;
            }

            if (meeting.StartPeriod > deletedPeriod)
            {
                meeting.StartPeriod--;
                changed = true;
            }
            if (meeting.EndPeriod >= deletedPeriod)
            {
                meeting.EndPeriod--;
                changed = true;
            }
            updated.Add(meeting);
        }

        if (changed)
            course.MeetingTimes = updated;
        return changed;
    }

    private static bool TrimMeetingsToMaximumPeriod(CourseOffering course, int maximumPeriod)
    {
        var changed = false;
        var updated = new List<MeetingTime>(course.MeetingTimes.Count);
        foreach (var meeting in course.MeetingTimes)
        {
            if (meeting.StartPeriod > maximumPeriod)
            {
                changed = true;
                continue;
            }

            if (meeting.EndPeriod > maximumPeriod)
            {
                meeting.EndPeriod = maximumPeriod;
                changed = true;
            }
            updated.Add(meeting);
        }

        if (changed)
            course.MeetingTimes = updated;
        return changed;
    }

    private static void EnsureProjectedCourseIdentitiesAreUnique(
        IEnumerable<CourseOffering> courses,
        Func<CourseOffering, bool> transform)
    {
        var projectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var course in courses)
        {
            var projected = JsonDefaults.Clone(course);
            transform(projected);
            if (!projectedIds.Add(CourseIdentityService.GenerateOfferingId(projected)))
                throw new PeriodScheduleCourseIdentityConflictException();
        }
    }

    private static void UpdateCourseIdentity(
        CourseOffering course,
        string originalOfferingId,
        Dictionary<string, string> idMap)
    {
        CourseIdentityService.AssignOfferingId(course);
        course.ModifiedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(originalOfferingId) &&
            !string.Equals(originalOfferingId, course.OfferingId, StringComparison.Ordinal))
        {
            idMap[originalOfferingId] = course.OfferingId;
        }
    }

    private static void UpdateSnapshotReferences(
        IEnumerable<PlanCourseSnapshot> snapshots,
        IReadOnlyDictionary<string, string> idMap)
    {
        if (idMap.Count == 0)
            return;

        foreach (var snapshot in snapshots)
        {
            if (!idMap.TryGetValue(snapshot.CourseOfferingId, out var currentOfferingId))
                continue;

            snapshot.CourseOfferingId = currentOfferingId;
            snapshot.SnapshotAt = DateTimeOffset.UtcNow;
        }
    }
}

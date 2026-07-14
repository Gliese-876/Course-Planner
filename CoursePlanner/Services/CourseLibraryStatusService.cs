using CoursePlanner.Core;

namespace CoursePlanner.Services;

public enum CourseLibraryStatusKind
{
    None,
    CurrentPlan,
    Conflict,
    Full,
    Tight
}

public sealed record CourseLibraryStatus(CourseLibraryStatusKind Kind, string? Text)
{
    public static CourseLibraryStatus None { get; } = new(CourseLibraryStatusKind.None, null);
}

public sealed class CourseLibraryStatusIndex
{
    private readonly IReadOnlyDictionary<CourseOffering, CourseLibraryStatus> _byReference;
    private readonly IReadOnlyDictionary<string, CourseLibraryStatus> _byOfferingId;

    internal CourseLibraryStatusIndex(
        IReadOnlyDictionary<CourseOffering, CourseLibraryStatus> byReference,
        IReadOnlyDictionary<string, CourseLibraryStatus> byOfferingId)
    {
        _byReference = byReference;
        _byOfferingId = byOfferingId;
    }

    public CourseLibraryStatus Resolve(CourseOffering course)
    {
        ArgumentNullException.ThrowIfNull(course);
        if (_byReference.TryGetValue(course, out var status))
            return status;
        return !string.IsNullOrWhiteSpace(course.OfferingId) &&
               _byOfferingId.TryGetValue(course.OfferingId, out status)
            ? status
            : CourseLibraryStatus.None;
    }
}

public static class CourseLibraryStatusService
{
    public static CourseLibraryStatusIndex CreateIndex(
        SelectionPlan? plan,
        Semester? semester,
        int currentWeek,
        IEnumerable<CourseOffering> libraryCourses,
        Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(libraryCourses);
        ArgumentNullException.ThrowIfNull(text);

        var library = libraryCourses.ToList();
        var coursesById = library
            .Where(course => !string.IsNullOrWhiteSpace(course.OfferingId))
            .GroupBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var plannedCourses = plan?.Snapshots
            .Select(snapshot => coursesById.GetValueOrDefault(snapshot.CourseOfferingId))
            .Where(course => course is not null)
            .Cast<CourseOffering>()
            .DistinctBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToList()
            ?? [];

        var conflicts = FindCurrentWeekConflicts(plannedCourses, semester, currentWeek);
        var byReference = new Dictionary<CourseOffering, CourseLibraryStatus>(ReferenceEqualityComparer.Instance);
        var byOfferingId = new Dictionary<string, CourseLibraryStatus>(StringComparer.Ordinal);
        foreach (var course in plannedCourses)
        {
            var status = ResolvePlannedCourseStatus(course, conflicts.Contains(course), text);
            byReference[course] = status;
            byOfferingId[course.OfferingId] = status;
        }

        return new CourseLibraryStatusIndex(byReference, byOfferingId);
    }

    private static HashSet<CourseOffering> FindCurrentWeekConflicts(
        IReadOnlyList<CourseOffering> plannedCourses,
        Semester? semester,
        int currentWeek)
    {
        var conflicts = new HashSet<CourseOffering>(ReferenceEqualityComparer.Instance);
        if (semester is null || plannedCourses.Count < 2)
            return conflicts;

        var firstCourseBySlot = new Dictionary<TimetableSlot, CourseOffering>();
        foreach (var course in plannedCourses)
        {
            foreach (var slot in PlannerDomainService.ExpandSlots(course, semester, currentWeek).Distinct())
            {
                if (!firstCourseBySlot.TryAdd(slot, course) &&
                    !ReferenceEquals(firstCourseBySlot[slot], course))
                {
                    conflicts.Add(firstCourseBySlot[slot]);
                    conflicts.Add(course);
                }
            }
        }

        return conflicts;
    }

    private static CourseLibraryStatus ResolvePlannedCourseStatus(
        CourseOffering course,
        bool hasConflict,
        Func<string, string> text)
    {
        if (hasConflict)
            return new CourseLibraryStatus(CourseLibraryStatusKind.Conflict, text("Conflict"));

        if (course.EnrolledCount is { } enrolled &&
            course.Capacity is { } capacity &&
            capacity > 0 &&
            enrolled >= capacity)
        {
            return new CourseLibraryStatus(CourseLibraryStatusKind.Full, text("Full"));
        }

        if (course.EnrolledCount is { } tightEnrolled &&
            course.Capacity is { } tightCapacity &&
            tightCapacity > 0 &&
            tightEnrolled / (double)tightCapacity >= 0.9)
        {
            return new CourseLibraryStatus(CourseLibraryStatusKind.Tight, text("Tight"));
        }

        return new CourseLibraryStatus(CourseLibraryStatusKind.CurrentPlan, text("CurrentPlan"));
    }
}

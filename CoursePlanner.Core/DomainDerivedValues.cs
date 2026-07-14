namespace CoursePlanner.Core;

public static class CourseDerivedValues
{
    public static string CapacityText(CourseOffering course) =>
        course.EnrolledCount.HasValue && course.Capacity.HasValue
            ? $"{course.EnrolledCount}/{course.Capacity}"
            : "";
}

public static class SelectionPlanMetrics
{
    public static int CourseCount(SelectionPlan plan) =>
        plan.Snapshots.Count;

    public static decimal TotalCredits(SelectionPlan plan, IEnumerable<CourseOffering> libraryCourses)
        => TotalCredits(PlanCourseResolver.Courses(plan, libraryCourses));

    public static decimal TotalCredits(IEnumerable<CourseOffering> courses)
    {
        ArgumentNullException.ThrowIfNull(courses);
        var total = 0m;
        foreach (var credits in courses.Select(course => course.Credits))
            total = SaturatingAdd(total, credits);
        return total;
    }

    private static decimal SaturatingAdd(decimal left, decimal right)
    {
        if (right > 0 && left > decimal.MaxValue - right)
            return decimal.MaxValue;
        if (right < 0 && left < decimal.MinValue - right)
            return decimal.MinValue;
        return left + right;
    }
}

public static class PlanCourseResolver
{
    public static Dictionary<string, CourseOffering> BuildCourseIndex(IEnumerable<CourseOffering> libraryCourses) =>
        libraryCourses
            .Where(x => !string.IsNullOrWhiteSpace(x.OfferingId))
            .DistinctBy(x => x.OfferingId)
            .ToDictionary(x => x.OfferingId, StringComparer.Ordinal);

    public static CourseOffering? CourseForSnapshot(
        PlanCourseSnapshot snapshot,
        IEnumerable<CourseOffering> libraryCourses)
    {
        var index = BuildCourseIndex(libraryCourses);
        return index.GetValueOrDefault(snapshot.CourseOfferingId);
    }

    public static IEnumerable<CourseOffering> Courses(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses)
    {
        var index = BuildCourseIndex(libraryCourses);
        return Courses(plan, index);
    }

    public static IEnumerable<CourseOffering> Courses(
        SelectionPlan plan,
        IReadOnlyDictionary<string, CourseOffering> courseIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(courseIndex);
        foreach (var snapshot in plan.Snapshots)
        {
            if (courseIndex.TryGetValue(snapshot.CourseOfferingId, out var course))
                yield return course;
        }
    }

    public static int RemoveMissingReferences(PlannerDocument document)
    {
        var index = BuildCourseIndex(document.CourseLibrary);
        var changed = 0;
        foreach (var plan in document.Plans)
        {
            for (var i = plan.Snapshots.Count - 1; i >= 0; i--)
            {
                if (index.ContainsKey(plan.Snapshots[i].CourseOfferingId))
                    continue;

                plan.Snapshots.RemoveAt(i);
                changed++;
            }
        }

        return changed;
    }
}

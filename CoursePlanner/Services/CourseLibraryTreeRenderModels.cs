using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed class CourseLibraryTreeGroup
{
    public string SemesterName { get; init; } = "";
    public string CourseGroupTypeText { get; init; } = "";
    public string StudyTypeText { get; init; } = "";
    public List<CourseLibraryTreeCourse> Courses { get; init; } = new();
}

public sealed class CourseLibraryTreeCourse
{
    public string OfferingId { get; init; } = "";
    public string CourseName { get; init; } = "";
    public string Summary { get; init; } = "";
    public CourseLibraryStatus Status { get; init; } = CourseLibraryStatus.None;
}

public static class CourseLibraryTreeRenderModelBuilder
{
    public static IReadOnlyList<CourseLibraryTreeGroup> Build(
        IEnumerable<LibraryGroup> groups,
        AppLocalizer localizer,
        Func<CourseOffering, string> formatCourseSummary,
        Func<CourseOffering, CourseLibraryStatus> statusProvider)
    {
        return groups.Select(group => new CourseLibraryTreeGroup
        {
            SemesterName = group.SemesterName,
            CourseGroupTypeText = localizer.LocalizeKnownLabel(group.CourseGroupType),
            StudyTypeText = localizer.LocalizeKnownLabel(group.StudyType),
            Courses = group.Courses.Select(course => new CourseLibraryTreeCourse
            {
                OfferingId = course.OfferingId,
                CourseName = course.CourseName,
                Summary = formatCourseSummary(course),
                Status = statusProvider(course)
            }).ToList()
        }).ToList();
    }
}

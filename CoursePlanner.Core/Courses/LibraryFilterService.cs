using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class LibraryFilterService
{
    public static IEnumerable<CourseOffering> Filter(
        IEnumerable<CourseOffering> courses,
        CourseFilter filter,
        string currentSemesterId)
    {
        var query = courses;
        if (!filter.AllSemesters)
            query = query.Where(x => x.SemesterId == currentSemesterId);

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var text = filter.SearchText.Trim();
            query = query.Where(x =>
                Contains(x.CourseName, text) ||
                Contains(x.Teacher, text) ||
                Contains(x.Location, text) ||
                Contains(x.CourseGroupType, text) ||
                Contains(x.StudyType, text) ||
                Contains(x.Credits.ToString(CultureInfo.CurrentCulture), text) ||
                Contains(x.Notes, text) ||
                x.Labels.Any(label => Contains(label, text)));
        }

        if (filter.OrdinaryLabels.Count > 0)
            query = query.Where(x => x.Labels.Any(label => filter.OrdinaryLabels.Contains(label)));
        if (filter.CourseGroupTypes.Count > 0)
            query = query.Where(x => filter.CourseGroupTypes.Contains(x.CourseGroupType ?? ""));
        if (filter.StudyTypes.Count > 0)
            query = query.Where(x => filter.StudyTypes.Contains(x.StudyType ?? ""));
        if (filter.Teachers.Count > 0)
            query = query.Where(x => filter.Teachers.Any(text => Contains(x.Teacher, text)));
        if (filter.Locations.Count > 0)
            query = query.Where(x => filter.Locations.Any(text => Contains(x.Location, text)));

        return query.OrderBy(x => x.CourseName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Teacher, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Location, StringComparer.CurrentCultureIgnoreCase);
    }

    public static List<LibraryGroup> Group(
        IEnumerable<CourseOffering> courses,
        IEnumerable<Semester> semesters,
        IEnumerable<CourseLabel> labels)
    {
        var semesterById = semesters.ToDictionary(x => x.SemesterId, StringComparer.Ordinal);
        var order = labels.ToDictionary(x => (x.Kind, x.Name), x => x.DisplayOrder, new LabelOrderComparer());

        return courses
            .GroupBy(course =>
            {
                semesterById.TryGetValue(course.SemesterId, out var semester);
                return new
                {
                    SemesterName = semester?.SemesterName ?? course.SemesterId,
                    SemesterOrder = semester?.DisplayOrder ?? int.MaxValue,
                    Group = string.IsNullOrWhiteSpace(course.CourseGroupType) ? PlannerLabels.Uncategorized : course.CourseGroupType!,
                    Study = string.IsNullOrWhiteSpace(course.StudyType) ? PlannerLabels.Uncategorized : course.StudyType!
                };
            })
            .OrderBy(group => group.Key.SemesterOrder)
            .ThenBy(group => group.Key.SemesterName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => SortValue(LabelKind.CourseGroupType, group.Key.Group, order))
            .ThenBy(group => SortValue(LabelKind.StudyType, group.Key.Study, order))
            .Select(group => new LibraryGroup
            {
                SemesterName = group.Key.SemesterName,
                CourseGroupType = group.Key.Group,
                StudyType = group.Key.Study,
                Courses = group.ToList()
            })
            .ToList();
    }

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;

    private static int SortValue(LabelKind kind, string value, Dictionary<(LabelKind Kind, string Name), int> order) =>
        value == PlannerLabels.Uncategorized ? int.MaxValue : order.TryGetValue((kind, value), out var index) ? index : int.MaxValue - 1;

    private sealed class LabelOrderComparer : IEqualityComparer<(LabelKind Kind, string Name)>
    {
        public bool Equals((LabelKind Kind, string Name) x, (LabelKind Kind, string Name) y) =>
            x.Kind == y.Kind && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((LabelKind Kind, string Name) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}

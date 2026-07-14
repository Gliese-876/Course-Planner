using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed class CourseEditModel
{
    public string SemesterId { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public decimal Credits { get; set; }
    public int? EnrolledCount { get; set; }
    public int? Capacity { get; set; }
    public string CourseGroupType { get; set; } = "";
    public string StudyType { get; set; } = "";
    public string LabelsText { get; set; } = "";
    public List<MeetingTime> Meetings { get; set; } = new();
    public string Color { get; set; } = "";
    public string Notes { get; set; } = "";
}

public static class CourseEditMapper
{
    public static CourseEditModel FromCourse(CourseOffering course, AppLocalizer localizer) => new()
    {
        SemesterId = course.SemesterId,
        CourseName = course.CourseName,
        Teacher = course.Teacher,
        Location = course.Location,
        Credits = course.Credits,
        EnrolledCount = course.EnrolledCount,
        Capacity = course.Capacity,
        CourseGroupType = localizer.LocalizeKnownLabel(course.CourseGroupType),
        StudyType = localizer.LocalizeKnownLabel(course.StudyType),
        LabelsText = string.Join(", ", course.Labels),
        Meetings = CloneMeetings(course.MeetingTimes),
        Color = course.Color,
        Notes = course.Notes
    };

    public static void ApplyToCourse(CourseOffering course, CourseEditModel model, AppLocalizer localizer)
    {
        course.SemesterId = model.SemesterId.Trim();
        course.CourseName = model.CourseName.Trim();
        course.Teacher = model.Teacher.Trim();
        course.Location = model.Location.Trim();
        course.Credits = model.Credits;
        course.EnrolledCount = model.EnrolledCount;
        course.Capacity = model.Capacity;
        course.CourseGroupType = CanonicalOptional(model.CourseGroupType, localizer);
        course.StudyType = CanonicalOptional(model.StudyType, localizer);
        course.Labels = TextTokenParser.SplitTokens(model.LabelsText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        course.MeetingTimes = CloneMeetings(model.Meetings);
        course.Color = CourseColorService.NormalizeUserInput(model.Color);
        course.Notes = model.Notes;
    }

    public static List<MeetingTime> CloneMeetings(IEnumerable<MeetingTime> meetings) =>
        meetings
            .Select(meeting => new MeetingTime
            {
                Weekday = meeting.Weekday,
                StartPeriod = meeting.StartPeriod,
                EndPeriod = meeting.EndPeriod,
                Weeks = meeting.Weeks,
                WeekParity = meeting.WeekParity
            })
            .ToList();

    private static string? CanonicalOptional(string value, AppLocalizer localizer)
    {
        var canonical = localizer.CanonicalizeKnownLabel(value);
        return string.IsNullOrWhiteSpace(canonical) ? null : canonical;
    }
}

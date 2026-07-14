using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class CourseEditFingerprint
{
    public static string Capture(CourseOffering course)
    {
        var editableState = new
        {
            course.SemesterId,
            courseName = course.CourseName ?? "",
            teacher = course.Teacher ?? "",
            location = course.Location ?? "",
            course.Credits,
            courseGroupType = NormalizeOptionalText(course.CourseGroupType),
            studyType = NormalizeOptionalText(course.StudyType),
            labels = course.Labels
                .Select(NormalizeOptionalText)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            meetingTimes = course.MeetingTimes
                .OrderBy(x => x.Weekday)
                .ThenBy(x => x.StartPeriod)
                .ThenBy(x => x.EndPeriod)
                .ThenBy(x => x.WeekParity)
                .ThenBy(x => TextRules.NormalizeIdentityText(x.Weeks), StringComparer.Ordinal)
                .Select(x => new
                {
                    x.Weekday,
                    x.StartPeriod,
                    x.EndPeriod,
                    weeks = TextRules.NormalizeIdentityText(x.Weeks),
                    x.WeekParity
                })
                .ToList(),
            notes = NormalizeLineEndings(course.Notes),
            course.EnrolledCount,
            course.Capacity,
            color = NormalizeColor(course.Color)
        };

        return JsonSerializer.Serialize(editableState, JsonDefaults.Options);
    }

    private static string NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private static string NormalizeLineEndings(string? value) =>
        (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string NormalizeColor(string? value) =>
        CourseColorService.IsValidHex(value) ? CourseColorService.NormalizeHex(value!) : NormalizeOptionalText(value);
}

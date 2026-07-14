using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class CourseIdentityService
{
    public static string GenerateOfferingId(CourseOffering course)
    {
        var identity = new
        {
            semesterId = TextRules.NormalizeIdentityText(course.SemesterId),
            courseName = TextRules.NormalizeIdentityText(course.CourseName),
            teacher = TextRules.NormalizeIdentityText(course.Teacher),
            location = TextRules.NormalizeIdentityText(course.Location),
            meetingTimes = course.MeetingTimes
                .OrderBy(x => x.Weekday)
                .ThenBy(x => x.StartPeriod)
                .ThenBy(x => x.EndPeriod)
                .ThenBy(x => x.WeekParity)
                .ThenBy(x => TextRules.NormalizeIdentityText(x.Weeks), StringComparer.Ordinal)
                .Select(x => new
                {
                    weekday = x.Weekday,
                    startPeriod = x.StartPeriod,
                    endPeriod = x.EndPeriod,
                    weeks = TextRules.NormalizeIdentityText(x.Weeks),
                    weekParity = x.WeekParity
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(identity, JsonDefaults.Options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void AssignOfferingId(CourseOffering course) => course.OfferingId = GenerateOfferingId(course);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class CourseFactory
{
    public static CourseOffering CreateBlank(Semester? semester, int colorIndex, int weekday = 1, int period = 1)
    {
        var weekCount = Math.Max(1, semester?.WeekCount ?? 16);
        return new CourseOffering
        {
            SemesterId = semester?.SemesterId ?? "",
            CourseName = "",
            Color = CourseColorService.Generate(colorIndex),
            MeetingTimes =
            {
                new MeetingTime
                {
                    Weekday = Math.Clamp(weekday, 1, 7),
                    StartPeriod = Math.Max(1, period),
                    EndPeriod = Math.Max(1, period),
                    Weeks = $"1-{weekCount}"
                }
            }
        };
    }
}

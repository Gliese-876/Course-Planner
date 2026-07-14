using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class PeriodScheduleFactory
{
    public static List<PeriodDefinition> CreateDefault12()
    {
        var starts = new[]
        {
            "08:00", "08:55", "10:00", "10:55",
            "13:30", "14:25", "15:30", "16:25",
            "18:00", "18:55", "19:50", "20:45"
        };

        return starts.Select((start, index) =>
        {
            var startTime = TimeOnly.ParseExact(start, "HH:mm", CultureInfo.InvariantCulture);
            return new PeriodDefinition
            {
                Period = index + 1,
                Start = startTime,
                End = startTime.AddMinutes(45)
            };
        }).ToList();
    }
}

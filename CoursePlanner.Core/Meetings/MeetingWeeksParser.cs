using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public sealed class WeekParseResult
{
    public SortedSet<int> Weeks { get; } = new();
    public List<string> InvalidTokens { get; } = new();
    public List<int> OutOfRangeWeeks { get; } = new();
    public bool HadAnyToken { get; set; }
    public bool WasBounded { get; set; }

    public bool IsValid => InvalidTokens.Count == 0;
}

public static class MeetingWeeksParser
{
    public const int MaxExpressionLength = 1024;
    // With a 1,024 UTF-16-code-unit expression limit, at most 512 non-empty
    // one-character comma-delimited tokens can occur. Keeping the token bound
    // aligned with that structural maximum avoids silently dropping valid
    // trailing weeks while retaining a hard parser bound.
    public const int MaxTokenCount = (MaxExpressionLength + 1) / 2;
    public const int MaxExpandedWeeks = 512;

    public static WeekParseResult ParseDetailed(string? expression, int weekCount, WeekParity parity = WeekParity.All)
    {
        var result = new WeekParseResult();
        var value = string.IsNullOrWhiteSpace(expression) ? "1-" + weekCount : expression.Trim();
        if (value.Length > MaxExpressionLength)
        {
            result.HadAnyToken = true;
            result.WasBounded = true;
            result.InvalidTokens.Add("WeeksExpressionTooLong");
            return result;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > MaxTokenCount)
        {
            result.WasBounded = true;
            parts = parts.Take(MaxTokenCount).ToArray();
        }

        var effectiveWeekCount = Math.Min(Math.Max(weekCount, 0), MaxExpandedWeeks);
        if (weekCount > MaxExpandedWeeks)
            result.WasBounded = true;

        foreach (var rawPart in parts)
        {
            result.HadAnyToken = true;
            var bounds = rawPart.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 ||
                !int.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
            {
                result.InvalidTokens.Add(rawPart);
                continue;
            }

            var end = start;
            if (bounds.Length == 2 &&
                !int.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
            {
                result.InvalidTokens.Add(rawPart);
                continue;
            }

            if (start < 1 || end < start)
            {
                result.InvalidTokens.Add(rawPart);
                continue;
            }

            if (end > weekCount)
            {
                result.WasBounded = true;
                AddOutOfRangeSample(result, Math.Max(start, weekCount + 1));
            }
            if (end > effectiveWeekCount && weekCount > MaxExpandedWeeks)
                result.WasBounded = true;

            var visibleEnd = Math.Min(end, effectiveWeekCount);
            for (var week = start; week <= visibleEnd; week++)
            {
                if (!MatchesParity(week, parity))
                    continue;

                if (week > weekCount)
                    continue;

                result.Weeks.Add(week);
            }
        }

        if (!result.HadAnyToken)
            result.InvalidTokens.Add(value);

        return result;
    }

    public static IReadOnlyCollection<int> Parse(string? expression, int weekCount, WeekParity parity = WeekParity.All) =>
        ParseDetailed(expression, weekCount, parity).Weeks;

    private static bool MatchesParity(int week, WeekParity parity) => parity switch
    {
        WeekParity.Odd => week % 2 != 0,
        WeekParity.Even => week % 2 == 0,
        _ => true
    };

    private static void AddOutOfRangeSample(WeekParseResult result, int week)
    {
        if (!result.OutOfRangeWeeks.Contains(week))
            result.OutOfRangeWeeks.Add(week);
    }
}

using System.Globalization;

namespace CoursePlanner.Core;

public static class DateDisplay
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";
    private const string CompactDateFormat = "yyyyMMdd";
    private const string MonthDayFormat = "MM-dd";
    private const string ShortMonthDayFormat = "M-d";

    public static string Date(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string MonthDay(DateOnly date) =>
        date.ToString(MonthDayFormat, CultureInfo.InvariantCulture);

    public static string ShortMonthDay(DateOnly date) =>
        date.ToString(ShortMonthDayFormat, CultureInfo.InvariantCulture);

    public static string CompactDate(DateOnly date) =>
        date.ToString(CompactDateFormat, CultureInfo.InvariantCulture);

    public static string DateTime(DateTime value) =>
        value.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    public static string LocalDateTime(DateTimeOffset value) =>
        DateTime(value.LocalDateTime);
}

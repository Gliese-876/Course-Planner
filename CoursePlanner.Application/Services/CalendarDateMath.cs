using CoursePlanner.Core;

namespace CoursePlanner.Services;

/// <summary>
/// Overflow-safe calendar arithmetic for the date range exposed by the app's
/// localized calendar control.
/// </summary>
public static class CalendarDateMath
{
    public const int MinYear = PlannerDateRange.MinimumYear;
    public const int MaxYear = PlannerDateRange.MaximumYear;
    public const int CalendarCellCount = 42;

    public static readonly DateOnly MinDate = PlannerDateRange.Minimum;
    public static readonly DateOnly MaxDate = PlannerDateRange.Maximum;

    public static DateOnly Clamp(DateOnly date)
    {
        if (date < MinDate)
            return MinDate;
        if (date > MaxDate)
            return MaxDate;
        return date;
    }

    public static DateOnly MonthStart(DateOnly date)
    {
        var clamped = Clamp(date);
        return new DateOnly(clamped.Year, clamped.Month, 1);
    }

    public static DateOnly AddMonthsClamped(DateOnly date, int offset)
    {
        var clamped = Clamp(date);
        var currentMonth = MonthIndex(clamped.Year, clamped.Month);
        var targetMonth = Math.Clamp(
            currentMonth + (long)offset,
            MonthIndex(MinYear, 1),
            MonthIndex(MaxYear, 12));
        var targetYear = (int)(targetMonth / 12);
        var targetMonthNumber = (int)(targetMonth % 12) + 1;
        var targetDay = Math.Min(clamped.Day, DateTime.DaysInMonth(targetYear, targetMonthNumber));
        return new DateOnly(targetYear, targetMonthNumber, targetDay);
    }

    public static DateOnly AddYearsClamped(DateOnly date, int offset)
    {
        var clamped = Clamp(date);
        var targetYear = (int)Math.Clamp((long)clamped.Year + offset, MinYear, MaxYear);
        var targetDay = Math.Min(clamped.Day, DateTime.DaysInMonth(targetYear, clamped.Month));
        return new DateOnly(targetYear, clamped.Month, targetDay);
    }

    public static bool CanAddMonths(DateOnly date, int offset)
    {
        if (offset == 0)
            return true;

        var clamped = Clamp(date);
        var moved = AddMonthsClamped(clamped, offset);
        return clamped.Year != moved.Year || clamped.Month != moved.Month;
    }

    public static bool CanAddYears(DateOnly date, int offset)
    {
        if (offset == 0)
            return true;

        var clamped = Clamp(date);
        return clamped.Year != AddYearsClamped(clamped, offset).Year;
    }

    public static int FirstDayOffset(DateOnly month, DayOfWeek firstDay)
    {
        var normalizedFirstDay = Enum.IsDefined(firstDay) ? firstDay : DayOfWeek.Monday;
        var monthStart = MonthStart(month);
        return ((int)monthStart.DayOfWeek - (int)normalizedFirstDay + 7) % 7;
    }

    public static IReadOnlyList<CalendarDateCell> CreateMonthGrid(DateOnly month, DayOfWeek firstDay)
    {
        var monthStart = MonthStart(month);
        var firstDayOffset = FirstDayOffset(monthStart, firstDay);
        var cells = new CalendarDateCell[CalendarCellCount];

        for (var index = 0; index < cells.Length; index++)
        {
            var dayNumber = (long)monthStart.DayNumber + index - firstDayOffset;
            if (dayNumber < MinDate.DayNumber || dayNumber > MaxDate.DayNumber)
            {
                cells[index] = new CalendarDateCell(index, null, false);
                continue;
            }

            var date = DateOnly.FromDayNumber((int)dayNumber);
            cells[index] = new CalendarDateCell(
                index,
                date,
                date.Year == monthStart.Year && date.Month == monthStart.Month);
        }

        return cells;
    }

    private static long MonthIndex(int year, int month) =>
        (long)year * 12 + month - 1;
}

public readonly record struct CalendarDateCell(int Index, DateOnly? Date, bool IsDisplayMonth)
{
    public bool IsSelectable => Date.HasValue;
}

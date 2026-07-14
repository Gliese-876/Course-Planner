using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class SemesterRules
{
    public const int MaxWeekCount = 60;

    public static int CalculateWeekCount(DateOnly startDate, DateOnly endDate, WeekStartDay weekStartDay)
    {
        if (endDate < startDate || !Enum.IsDefined(weekStartDay))
            return 0;

        var firstWeekStart = AlignedWeekStartDayNumber(startDate, weekStartDay);
        var lastWeekStart = AlignedWeekStartDayNumber(endDate, weekStartDay);
        return ((lastWeekStart - firstWeekStart) / 7) + 1;
    }

    public static DateOnly CalculateEndDate(DateOnly startDate, int weekCount, WeekStartDay weekStartDay)
    {
        if (weekCount is < 1 or > MaxWeekCount)
            throw new ArgumentOutOfRangeException(nameof(weekCount));
        if (!Enum.IsDefined(weekStartDay))
            throw new ArgumentOutOfRangeException(nameof(weekStartDay));

        var firstWeekStart = AlignedWeekStartDayNumber(startDate, weekStartDay);
        var endDayNumber = firstWeekStart + ((weekCount * 7) - 1L);
        return DateOnly.FromDayNumber(ClampDayNumber(endDayNumber));
    }

    public static (int WeekCount, DateOnly EndDate) ProjectSupportedCalendarRange(
        DateOnly startDate,
        int requestedWeekCount,
        WeekStartDay weekStartDay)
    {
        if (!PlannerDateRange.Contains(startDate))
            throw new ArgumentOutOfRangeException(nameof(startDate));
        if (!Enum.IsDefined(weekStartDay))
            throw new ArgumentOutOfRangeException(nameof(weekStartDay));

        var maximumWeekCount = Math.Min(
            MaxWeekCount,
            CalculateWeekCount(startDate, PlannerDateRange.Maximum, weekStartDay));
        var weekCount = Math.Clamp(requestedWeekCount, 1, maximumWeekCount);
        var endDate = CalculateEndDate(startDate, weekCount, weekStartDay);
        if (endDate > PlannerDateRange.Maximum)
            endDate = PlannerDateRange.Maximum;
        return (weekCount, endDate);
    }

    public static DateOnly GetWeekStart(DateOnly date, WeekStartDay weekStartDay)
    {
        if (!Enum.IsDefined(weekStartDay))
            return date;

        return DateOnly.FromDayNumber(ClampDayNumber(AlignedWeekStartDayNumber(date, weekStartDay)));
    }

    public static IReadOnlyList<int> GetWeekdayOrder(WeekStartDay weekStartDay) =>
        weekStartDay == WeekStartDay.Monday
            ? new[] { 1, 2, 3, 4, 5, 6, 7 }
            : new[] { 7, 1, 2, 3, 4, 5, 6 };

    public static IReadOnlyList<DateOnly> GetWeekDates(Semester semester, int week)
    {
        var effectiveWeekCount = Math.Clamp(semester.WeekCount, 1, MaxWeekCount);
        var clamped = Math.Clamp(week, 1, effectiveWeekCount);
        var weekStartDayNumber = AlignedWeekStartDayNumberSafe(semester.StartDate, semester.WeekStartDay) +
                                 ((clamped - 1) * 7L);
        return Enumerable.Range(0, 7)
            .Select(offset => DateOnly.FromDayNumber(ClampDayNumber(weekStartDayNumber + offset)))
            .ToList();
    }

    public static bool IsOutsideSemester(Semester semester, DateOnly date) =>
        date < semester.StartDate || date > semester.EndDate;

    public static string WeekRangeText(Semester semester, int week)
    {
        var dates = GetWeekDates(semester, week);
        return $"{DateDisplay.Date(dates.Min())} - {DateDisplay.Date(dates.Max())}";
    }

    public static ValidationResult ValidateSemester(Semester semester, IEnumerable<Semester> existing)
    {
        var result = new ValidationResult();
        if (string.IsNullOrWhiteSpace(semester.SemesterName))
            result.Error("SemesterNameRequired");
        if (semester.SemesterName?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("SemesterNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        foreach (var issue in WindowsFileNameRules.ValidateFileComponent(semester.SemesterName ?? "").Errors)
            result.Errors.Add(issue);
        if (existing.Any(x => x.SemesterId != semester.SemesterId &&
                              TextRules.IsSameIdentityText(x.SemesterName, semester.SemesterName)))
            result.Error("SemesterNameDuplicate");
        if (semester.EndDate < semester.StartDate)
            result.Error("SemesterDateRange");
        if (!PlannerDateRange.Contains(semester.StartDate) ||
            !PlannerDateRange.Contains(semester.EndDate))
        {
            result.Error(
                "SemesterDateSupportedRange",
                PlannerDateRange.MinimumYear.ToString(CultureInfo.InvariantCulture),
                PlannerDateRange.MaximumYear.ToString(CultureInfo.InvariantCulture));
        }
        var validWeekCount = semester.WeekCount is >= 1 and <= MaxWeekCount;
        if (!validWeekCount)
            result.Error("SemesterWeekCount");
        var validWeekStartDay = Enum.IsDefined(semester.WeekStartDay);
        if (!validWeekStartDay)
            result.Error("InvalidWeekStartDay");
        if (semester.PeriodSchedule.Count == 0)
            result.Error("PeriodScheduleRequired");
        if (semester.PeriodSchedule.Count > PlannerDataLimits.MaxPeriodsPerSemester)
            result.Error("PeriodScheduleMaximum", PlannerDataLimits.MaxPeriodsPerSemester.ToString());

        if (semester.EndDate >= semester.StartDate && validWeekStartDay)
        {
            var calculated = CalculateWeekCount(semester.StartDate, semester.EndDate, semester.WeekStartDay);
            if (calculated > MaxWeekCount && validWeekCount)
                result.Error("SemesterWeekCount");
            if (validWeekCount && calculated != semester.WeekCount)
                result.Error("SemesterEndDateWeekCountMismatch");
        }

        var orderedPeriods = semester.PeriodSchedule
            .Take(PlannerDataLimits.MaxPeriodsPerSemester)
            .OrderBy(x => x.Period)
            .ToList();
        foreach (var period in orderedPeriods)
        {
            if (period.Period < 1)
                result.Error("PeriodNumber");
            if (period.End <= period.Start)
                result.Error("PeriodTimeRange");
        }

        if (orderedPeriods.Select((period, index) => period.Period == index + 1).Any(valid => !valid))
            result.Error("PeriodNumberSequence");

        if (orderedPeriods.Zip(orderedPeriods.Skip(1), (previous, current) => previous.End > current.Start).Any(overlaps => overlaps))
            result.Error("PeriodTimeOverlap");

        return result;
    }

    private static int AlignedWeekStartDayNumber(DateOnly date, WeekStartDay weekStartDay)
    {
        var desired = weekStartDay == WeekStartDay.Monday ? DayOfWeek.Monday : DayOfWeek.Sunday;
        var offset = ((int)date.DayOfWeek - (int)desired + 7) % 7;
        return date.DayNumber - offset;
    }

    private static int AlignedWeekStartDayNumberSafe(DateOnly date, WeekStartDay weekStartDay) =>
        Enum.IsDefined(weekStartDay)
            ? AlignedWeekStartDayNumber(date, weekStartDay)
            : date.DayNumber;

    private static int ClampDayNumber(long dayNumber) =>
        (int)Math.Clamp(dayNumber, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber);
}

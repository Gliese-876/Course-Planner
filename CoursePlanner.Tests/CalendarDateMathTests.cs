using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class CalendarDateMathTests
{
    [Theory]
    [InlineData(1, 1, 1, 1900, 1, 1)]
    [InlineData(1899, 12, 31, 1900, 1, 1)]
    [InlineData(1900, 1, 1, 1900, 1, 1)]
    [InlineData(2000, 2, 29, 2000, 2, 29)]
    [InlineData(2100, 12, 31, 2100, 12, 31)]
    [InlineData(2101, 1, 1, 2100, 12, 31)]
    [InlineData(9999, 12, 31, 2100, 12, 31)]
    public void ClampKeepsEveryDateInsideTheSupportedCalendarRange(
        int year,
        int month,
        int day,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        Assert.Equal(
            new DateOnly(expectedYear, expectedMonth, expectedDay),
            CalendarDateMath.Clamp(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(1900, 1, 1, -1, 1900, 1, 1)]
    [InlineData(1900, 1, 31, 1, 1900, 2, 28)]
    [InlineData(2024, 1, 31, 1, 2024, 2, 29)]
    [InlineData(2024, 12, 31, 1, 2025, 1, 31)]
    [InlineData(2100, 12, 31, 1, 2100, 12, 31)]
    public void AddMonthsClampedSaturatesAndPreservesARepresentableDay(
        int year,
        int month,
        int day,
        int offset,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        Assert.Equal(
            new DateOnly(expectedYear, expectedMonth, expectedDay),
            CalendarDateMath.AddMonthsClamped(new DateOnly(year, month, day), offset));
    }

    [Theory]
    [InlineData(1900, 1, 1, -1, 1900, 1, 1)]
    [InlineData(2024, 2, 29, 1, 2025, 2, 28)]
    [InlineData(2099, 12, 31, 1, 2100, 12, 31)]
    [InlineData(2100, 12, 31, 1, 2100, 12, 31)]
    public void AddYearsClampedSaturatesAndHandlesLeapDays(
        int year,
        int month,
        int day,
        int offset,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        Assert.Equal(
            new DateOnly(expectedYear, expectedMonth, expectedDay),
            CalendarDateMath.AddYearsClamped(new DateOnly(year, month, day), offset));
    }

    [Fact]
    public void NavigationWithExtremeOffsetsNeverThrowsOrEscapesTheSupportedRange()
    {
        Assert.Equal(CalendarDateMath.MinDate, CalendarDateMath.AddMonthsClamped(DateOnly.MinValue, int.MinValue));
        Assert.Equal(CalendarDateMath.MaxDate, CalendarDateMath.AddMonthsClamped(DateOnly.MaxValue, int.MaxValue));
        Assert.Equal(CalendarDateMath.MinDate, CalendarDateMath.AddYearsClamped(DateOnly.MinValue, int.MinValue));
        Assert.Equal(CalendarDateMath.MaxDate, CalendarDateMath.AddYearsClamped(DateOnly.MaxValue, int.MaxValue));
    }

    [Fact]
    public void EveryPublicCalendarOperationAcceptsDateDomainExtremes()
    {
        foreach (var date in new[] { DateOnly.MinValue, CalendarDateMath.MinDate, CalendarDateMath.MaxDate, DateOnly.MaxValue })
        {
            _ = CalendarDateMath.Clamp(date);
            _ = CalendarDateMath.MonthStart(date);
            _ = CalendarDateMath.FirstDayOffset(date, (DayOfWeek)int.MaxValue);
            _ = CalendarDateMath.CreateMonthGrid(date, (DayOfWeek)int.MinValue);

            foreach (var offset in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
            {
                _ = CalendarDateMath.AddMonthsClamped(date, offset);
                _ = CalendarDateMath.AddYearsClamped(date, offset);
                _ = CalendarDateMath.CanAddMonths(date, offset);
                _ = CalendarDateMath.CanAddYears(date, offset);
            }
        }
    }

    [Theory]
    [InlineData(1900, 1, 1, DayOfWeek.Monday, 0)]
    [InlineData(1900, 1, 1, DayOfWeek.Sunday, 1)]
    [InlineData(2026, 1, 1, DayOfWeek.Monday, 3)]
    [InlineData(2026, 1, 1, DayOfWeek.Sunday, 4)]
    public void FirstDayOffsetSupportsMondayAndSundayWeekStarts(
        int year,
        int month,
        int day,
        DayOfWeek firstDay,
        int expected)
    {
        Assert.Equal(
            expected,
            CalendarDateMath.FirstDayOffset(new DateOnly(year, month, day), firstDay));
    }

    [Theory]
    [InlineData(1900, 1, DayOfWeek.Monday)]
    [InlineData(1900, 1, DayOfWeek.Sunday)]
    [InlineData(1900, 12, DayOfWeek.Monday)]
    [InlineData(2100, 1, DayOfWeek.Sunday)]
    [InlineData(2100, 12, DayOfWeek.Monday)]
    [InlineData(2100, 12, DayOfWeek.Sunday)]
    public void MonthGridAlwaysHas42StableCellsWithoutOutOfRangeDates(
        int year,
        int month,
        DayOfWeek firstDay)
    {
        var cells = CalendarDateMath.CreateMonthGrid(new DateOnly(year, month, 1), firstDay);

        Assert.Equal(42, cells.Count);
        Assert.Equal(Enumerable.Range(0, 42), cells.Select(cell => cell.Index));
        Assert.All(
            cells.Where(cell => cell.Date.HasValue),
            cell => Assert.InRange(cell.Date!.Value, CalendarDateMath.MinDate, CalendarDateMath.MaxDate));
        Assert.Equal(
            DateTime.DaysInMonth(year, month),
            cells.Count(cell => cell.IsDisplayMonth));
        Assert.All(cells.Where(cell => cell.IsDisplayMonth), cell => Assert.True(cell.IsSelectable));
    }

    [Fact]
    public void BoundaryGridUsesNonSelectablePlaceholdersInsteadOfDuplicateClampedDates()
    {
        var minimum = CalendarDateMath.CreateMonthGrid(CalendarDateMath.MinDate, DayOfWeek.Sunday);
        var maximum = CalendarDateMath.CreateMonthGrid(CalendarDateMath.MaxDate, DayOfWeek.Monday);

        Assert.Null(minimum[0].Date);
        Assert.False(minimum[0].IsSelectable);
        Assert.Contains(maximum, cell => cell.Date is null && !cell.IsSelectable);
        Assert.Equal(
            minimum.Where(cell => cell.Date.HasValue).Select(cell => cell.Date).Distinct().Count(),
            minimum.Count(cell => cell.Date.HasValue));
        Assert.Equal(
            maximum.Where(cell => cell.Date.HasValue).Select(cell => cell.Date).Distinct().Count(),
            maximum.Count(cell => cell.Date.HasValue));
    }

    [Fact]
    public void NavigationAvailabilityIsFalseAtRangeEdges()
    {
        Assert.False(CalendarDateMath.CanAddMonths(CalendarDateMath.MinDate, -1));
        Assert.True(CalendarDateMath.CanAddMonths(CalendarDateMath.MinDate, 1));
        Assert.True(CalendarDateMath.CanAddMonths(CalendarDateMath.MaxDate, -1));
        Assert.False(CalendarDateMath.CanAddMonths(CalendarDateMath.MaxDate, 1));
        Assert.False(CalendarDateMath.CanAddYears(CalendarDateMath.MinDate, -1));
        Assert.False(CalendarDateMath.CanAddYears(CalendarDateMath.MaxDate, 1));
    }

    [Theory]
    [InlineData(2100, 12, 31, WeekStartDay.Monday, 60, 1)]
    [InlineData(2100, 12, 24, WeekStartDay.Sunday, 60, 2)]
    public void SemesterWeekProjectionKeepsWeekCountAndEndDateConsistentAtCalendarMaximum(
        int year,
        int month,
        int day,
        WeekStartDay weekStartDay,
        int requestedWeekCount,
        int expectedWeekCount)
    {
        var start = new DateOnly(year, month, day);

        var projection = SemesterRules.ProjectSupportedCalendarRange(
            start,
            requestedWeekCount,
            weekStartDay);

        Assert.Equal(expectedWeekCount, projection.WeekCount);
        Assert.InRange(projection.EndDate, start, PlannerDateRange.Maximum);
        Assert.Equal(
            projection.WeekCount,
            SemesterRules.CalculateWeekCount(start, projection.EndDate, weekStartDay));
    }

    [Fact]
    public void SemesterEditorUsesCoreBoundaryProjectionAndSharedFileNameLimit()
    {
        var code = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "SemestersPage.xaml.cs"));
        var xaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "SemestersPage.xaml"));

        Assert.Contains("SemesterRules.ProjectSupportedCalendarRange", code, StringComparison.Ordinal);
        Assert.Contains("WeekCountBox.Value = projection.WeekCount", code, StringComparison.Ordinal);
        Assert.Contains("SemesterNameBox.MaxLength = WindowsFileNameRules.MaxComponentLength", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxLength=\"255\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerRoutesAllPotentiallyOverflowingDateArithmeticThroughCalendarDateMath()
    {
        var source = File.ReadAllText(ProjectFilePath("CoursePlanner", "Controls", "LocalizedCalendarDatePicker.cs"));

        Assert.DoesNotContain(".AddDays(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddMonths(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddYears(", source, StringComparison.Ordinal);
        Assert.Contains("CalendarDateMath.Clamp", source, StringComparison.Ordinal);
        Assert.Contains("CalendarDateMath.CreateMonthGrid", source, StringComparison.Ordinal);
        Assert.Contains("CalendarDateMath.CanAddMonths", source, StringComparison.Ordinal);
        Assert.Contains("CalendarDateMath.CanAddYears", source, StringComparison.Ordinal);
        Assert.Contains("_calendarPreviousButton.IsEnabled = CanMoveDisplay(-1)", source, StringComparison.Ordinal);
        Assert.Contains("_calendarNextButton.IsEnabled = CanMoveDisplay(1)", source, StringComparison.Ordinal);
        Assert.Contains("$\"CalendarBoundaryCell{index:D2}\"", source, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

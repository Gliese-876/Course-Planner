using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class WeekNumberInputTests
{
    [Theory]
    [InlineData(2.9, 2)]
    [InlineData(3.9, 3)]
    [InlineData(1.999, 1)]
    [InlineData(0, 1)]
    [InlineData(-100, 1)]
    [InlineData(16, 16)]
    [InlineData(16.9, 16)]
    [InlineData(1_000_000_000_000d, 16)]
    public void NormalizeReturnsAnIntegralWeekWithinTheSemester(double requestedValue, int expected)
    {
        Assert.Equal(expected, WeekNumberInput.Normalize(requestedValue, currentWeek: 2, weekCount: 16));
    }

    [Fact]
    public void NormalizeUsesTheClampedCurrentWeekForEmptyInput()
    {
        Assert.Equal(1, WeekNumberInput.Normalize(double.NaN, currentWeek: -10, weekCount: 16));
        Assert.Equal(12, WeekNumberInput.Normalize(double.NaN, currentWeek: 12, weekCount: 16));
        Assert.Equal(16, WeekNumberInput.Normalize(double.NaN, currentWeek: 99, weekCount: 16));
    }

    [Fact]
    public void NormalizeHandlesInfiniteAndDegenerateBoundsWithoutOverflow()
    {
        Assert.Equal(16, WeekNumberInput.Normalize(double.PositiveInfinity, currentWeek: 2, weekCount: 16));
        Assert.Equal(1, WeekNumberInput.Normalize(double.NegativeInfinity, currentWeek: 2, weekCount: 16));
        Assert.Equal(1, WeekNumberInput.Normalize(double.MaxValue, currentWeek: 2, weekCount: 0));
        Assert.Equal(1, WeekNumberInput.Normalize(double.NaN, currentWeek: int.MaxValue, weekCount: int.MinValue));
    }

    [Fact]
    public void PlannerAlwaysSynchronizesTheVisibleNumberBoxAfterNormalization()
    {
        var xaml = File.ReadAllText(RepositoryPaths.FromRoot("CoursePlanner", "Pages", "PlannerPage.xaml"));
        var code = File.ReadAllText(RepositoryPaths.FromRoot("CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var handler = Slice(
            code,
            "private void WeekNumberBox_ValueChanged",
            "private async void SwapCompare_Click");
        var synchronization = Slice(
            code,
            "private void SynchronizeWeekNumberBox",
            "private void ApplyWeekHeaderState");

        Assert.Contains("SmallChange=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WeekNumberBox.Maximum = maximumWeek", synchronization, StringComparison.Ordinal);
        Assert.Contains("WeekNumberBox.Value = ViewModel.CurrentWeek", synchronization, StringComparison.Ordinal);
        Assert.Contains("_synchronizingWeekNumberBox = true", synchronization, StringComparison.Ordinal);
        Assert.Contains("WeekNumberInput.Normalize", handler, StringComparison.Ordinal);
        Assert.Contains("finally", handler, StringComparison.Ordinal);
        Assert.Contains("SynchronizeWeekNumberBox();", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp((int)args.NewValue", handler, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        Assert.True(end > start, $"Missing marker after {startMarker}: {endMarker}");
        return source[start..end];
    }
}

namespace CoursePlanner.Services;

public static class WeekNumberInput
{
    public static int Normalize(double requestedValue, int currentWeek, int weekCount)
    {
        var maximumWeek = Math.Max(1, weekCount);
        var normalizedCurrentWeek = Math.Clamp(currentWeek, 1, maximumWeek);
        if (double.IsNaN(requestedValue))
            return normalizedCurrentWeek;
        if (requestedValue <= 1)
            return 1;
        if (requestedValue >= maximumWeek)
            return maximumWeek;

        return (int)Math.Truncate(requestedValue);
    }
}

namespace CoursePlanner.Core;

public static class PlannerDateRange
{
    public const int MinimumYear = 1900;
    public const int MaximumYear = 2100;

    public static readonly DateOnly Minimum = new(MinimumYear, 1, 1);
    public static readonly DateOnly Maximum = new(MaximumYear, 12, 31);

    public static bool Contains(DateOnly date) => date >= Minimum && date <= Maximum;
}

namespace CoursePlanner.Services;

public enum TimetableUiPresentationMode
{
    FullGrid,
    VirtualizedList
}

public sealed record TimetableUiMaterializationDecision(
    TimetableUiPresentationMode Mode,
    long EstimatedVisualElementCount,
    int CourseBlockCount,
    int PaneCount);

public static class TimetableUiMaterializationPolicy
{
    // A normal course block materializes roughly 13 XAML elements plus event and
    // layout registrations. The estimate deliberately rounds that up to 16. A
    // blank/header grid cell is cheaper, but still owns a Border and content.
    internal const int EstimatedElementsPerCourseBlock = 16;
    internal const int EstimatedElementsPerGridCell = 3;
    public const int MaximumMaterializedVisualElements = 12_000;

    public static TimetableUiMaterializationDecision Evaluate(
        int periodCount,
        params int[] courseBlockCountsByPane)
    {
        if (periodCount < 0)
            throw new ArgumentOutOfRangeException(nameof(periodCount));
        ArgumentNullException.ThrowIfNull(courseBlockCountsByPane);
        if (courseBlockCountsByPane.Length == 0)
            throw new ArgumentException("At least one timetable pane is required.", nameof(courseBlockCountsByPane));
        if (courseBlockCountsByPane.Any(count => count < 0))
            throw new ArgumentOutOfRangeException(nameof(courseBlockCountsByPane));

        var cellsPerPane = SaturatingAdd(8, SaturatingMultiply(8, periodCount));
        var blocks = courseBlockCountsByPane.Aggregate(
            0L,
            (total, count) => SaturatingAdd(total, count));
        var estimatedElements = SaturatingAdd(
            SaturatingMultiply(
                SaturatingMultiply(cellsPerPane, courseBlockCountsByPane.Length),
                EstimatedElementsPerGridCell),
            SaturatingMultiply(blocks, EstimatedElementsPerCourseBlock));
        var blockCount = blocks > int.MaxValue ? int.MaxValue : (int)blocks;
        return new TimetableUiMaterializationDecision(
            estimatedElements <= MaximumMaterializedVisualElements
                ? TimetableUiPresentationMode.FullGrid
                : TimetableUiPresentationMode.VirtualizedList,
            estimatedElements,
            blockCount,
            courseBlockCountsByPane.Length);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0
            ? 0
            : left > long.MaxValue / right
                ? long.MaxValue
                : left * right;
}

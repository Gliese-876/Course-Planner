using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class TimetableUiMaterializationPolicyTests
{
    [Fact]
    public void OrdinaryWeekUsesTheInteractiveGrid()
    {
        var decision = TimetableUiMaterializationPolicy.Evaluate(16, 100);

        Assert.Equal(TimetableUiPresentationMode.FullGrid, decision.Mode);
        Assert.Equal(100, decision.CourseBlockCount);
    }

    [Fact]
    public void MaximumSinglePlanUsesVirtualizedListInsteadOfThousandsOfButtons()
    {
        var decision = TimetableUiMaterializationPolicy.Evaluate(
            16,
            PlannerDataLimits.MaxMeetingRowsPerPlan);

        Assert.Equal(TimetableUiPresentationMode.VirtualizedList, decision.Mode);
        Assert.True(decision.EstimatedVisualElementCount > TimetableUiMaterializationPolicy.MaximumMaterializedVisualElements);
    }

    [Fact]
    public void MaximumTwoPlanUnionIsBudgetedAsOneComparisonSurface()
    {
        var decision = TimetableUiMaterializationPolicy.Evaluate(
            16,
            PlannerDataLimits.MaxMeetingRowsPerPlan,
            PlannerDataLimits.MaxMeetingRowsPerPlan);

        Assert.Equal(TimetableUiPresentationMode.VirtualizedList, decision.Mode);
        Assert.Equal(PlannerDataLimits.MaxMeetingRowsPerPlan * 2, decision.CourseBlockCount);
        Assert.Equal(2, decision.PaneCount);
    }

    [Fact]
    public void PolicyRejectsNegativeInputsAndSaturatesHugeEstimatesWithoutWraparound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TimetableUiMaterializationPolicy.Evaluate(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TimetableUiMaterializationPolicy.Evaluate(1, -1));
        var huge = TimetableUiMaterializationPolicy.Evaluate(
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);
        Assert.Equal(TimetableUiPresentationMode.VirtualizedList, huge.Mode);
        Assert.True(huge.EstimatedVisualElementCount > 0);
        Assert.Equal(int.MaxValue, huge.CourseBlockCount);
    }

    [Fact]
    public void PlannerPageKeepsEveryDenseBlockInAVirtualizedItemsSource()
    {
        var source = File.ReadAllText(ProjectFile("CoursePlanner", "Pages", "PlannerPage.xaml.cs"));

        Assert.Contains("RenderDenseWeekList", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource = denseItems", source, StringComparison.Ordinal);
        Assert.Contains("TimetableUiMaterializationPolicy.Evaluate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("denseItems.Take(", source, StringComparison.Ordinal);
    }

    private static string ProjectFile(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

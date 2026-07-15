namespace CoursePlanner.Tests;

public sealed class PlannerAsyncEventArchitectureTests
{
    [Fact]
    public void SwapComparisonEventAwaitsTheLeaveGuardInsteadOfDiscardingItsTask()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Pages",
            "PlannerPage.xaml.cs"));
        var start = source.IndexOf("private async void SwapCompare_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task SwapCompareAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var handler = source[start..end];
        Assert.Contains("await SwapCompareAsync();", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = SwapCompareAsync();", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportMergePreviewAwaitsOneProjectionAndHasNoRemovedFilterEvents()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Services",
            "ImportExportCoordinator.cs"));

        Assert.Contains("var projection = await Task.Run", source, StringComparison.Ordinal);
        Assert.Contains(
            "ImportMergePreviewProjectionService.Create(preview, display)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_ = Task.Run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueReportUpdate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("reportUpdateObservers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("statusBox.SelectionChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("searchBox.TextChanged", source, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

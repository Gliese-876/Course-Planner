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
    public void ImportPreviewFilterEventsObserveUpdatesAndDetachBeforeTheDialogIsReleased()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Services",
            "ImportExportCoordinator.cs"));

        Assert.DoesNotContain(
            "async void QueueReportUpdate",
            source,
            StringComparison.Ordinal);
        Assert.Contains("reportUpdateObservers.Add", source, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(reportUpdateObservers)", source, StringComparison.Ordinal);
        Assert.Contains("statusBox.SelectionChanged -=", source, StringComparison.Ordinal);
        Assert.Contains("searchBox.TextChanged -=", source, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

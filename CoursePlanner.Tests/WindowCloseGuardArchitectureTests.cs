namespace CoursePlanner.Tests;

public sealed class WindowCloseGuardArchitectureTests
{
    [Fact]
    public void MainWindowSynchronouslyCancelsNativeClosingBeforeAwaitingResolution()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var handler = Slice(shell, "private async void AppWindow_Closing", "private async Task ShowBackgroundOperationCloseBlockedAsync");

        Assert.Contains("AppWindow.Closing += AppWindow_Closing;", shell, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Closing -= AppWindow_Closing;", shell, StringComparison.Ordinal);
        Assert.Contains("var closeRequest = _windowCloseGuardState.InterceptClose();", handler, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = closeRequest.Cancel;", handler, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("args.Cancel = closeRequest.Cancel;", StringComparison.Ordinal) <
            handler.IndexOf("await ", StringComparison.Ordinal));
        Assert.DoesNotContain("GetDeferral", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetCurrentProcess", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseResolutionUsesThePageGuardAndChecksBusyStateBeforeAndAfterIt()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var handler = Slice(shell, "private async void AppWindow_Closing", "private async Task ShowBackgroundOperationCloseBlockedAsync");

        Assert.Equal(2, Count(handler, "_services.BackgroundOperations.IsBusy"));
        Assert.Contains("if (!await GuardCurrentPageAsync())", handler, StringComparison.Ordinal);
        Assert.Contains("_windowCloseGuardState.RejectResolution();", handler, StringComparison.Ordinal);
        Assert.Contains("if (_windowCloseGuardState.ApproveResolution())", handler, StringComparison.Ordinal);
        Assert.Contains("Close();", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedCloseFlushesDeferredPlanTabsBeforeReleasingTheNativeClose()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var handler = Slice(shell, "private async void AppWindow_Closing", "private async Task ShowBackgroundOperationCloseBlockedAsync");
        var pageGuard = handler.IndexOf("if (!await GuardCurrentPageAsync())", StringComparison.Ordinal);
        var secondBusyGuard = handler.LastIndexOf("_services.BackgroundOperations.IsBusy", StringComparison.Ordinal);
        var flush = handler.IndexOf("CommitPendingPlanTabCloses();", StringComparison.Ordinal);
        var approve = handler.IndexOf("_windowCloseGuardState.ApproveResolution()", StringComparison.Ordinal);
        var close = handler.LastIndexOf("Close();", StringComparison.Ordinal);

        Assert.True(pageGuard >= 0);
        Assert.True(secondBusyGuard > pageGuard);
        Assert.True(flush > secondBusyGuard);
        Assert.True(approve > flush);
        Assert.True(close > approve);
        Assert.True(
            handler.IndexOf("_windowCloseGuardState.RejectResolution();", flush, StringComparison.Ordinal) > flush,
            "The catch path must reject the close resolution when deferred persistence throws.");
    }

    [Fact]
    public void BusyCloseWarningIsLocalizedInEveryCatalog()
    {
        foreach (var language in new[] { "en-US", "zh-Hans" })
        {
            var resources = Read("CoursePlanner.Application", "Resources", language, "Resources.resw");
            Assert.Contains("<data name=\"BackgroundOperation\"", resources, StringComparison.Ordinal);
            Assert.Contains("<data name=\"CloseBlockedByBackgroundOperationTitle\"", resources, StringComparison.Ordinal);
            Assert.Contains("<data name=\"CloseBlockedByBackgroundOperationMessageFormat\"", resources, StringComparison.Ordinal);
        }
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        Assert.True(end > start, $"Missing marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(RepositoryPaths.FromRoot(parts));
}

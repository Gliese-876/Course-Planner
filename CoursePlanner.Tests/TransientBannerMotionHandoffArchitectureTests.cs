namespace CoursePlanner.Tests;

public sealed class TransientBannerMotionHandoffArchitectureTests
{
    [Fact]
    public void BannerExitRetargetsAnInFlightEntranceWithoutRestoringTheBaselineFirst()
    {
        var source = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner",
            "Services",
            "AppAnimationLayer.cs"));
        var exit = MethodBody(
            source,
            "public static async Task<bool> PlayTransientBannerExitThenAsync(",
            "public static void CancelTransientBannerMotion(");
        var handoff = MethodBody(
            source,
            "private static bool TryBeginPaneMotionHandoff(",
            "private static void StopPaneCompositionAnimationsForHandoff(");
        var stopForHandoff = MethodBody(
            source,
            "private static void StopPaneCompositionAnimationsForHandoff(",
            "private static void CompletePaneMotion(");

        Assert.Contains("TryBeginPaneMotionHandoff(banner, state)", exit, StringComparison.Ordinal);
        Assert.Contains("if (!handedOff)", exit, StringComparison.Ordinal);
        Assert.Contains("CapturePaneBaseline(banner, state);", exit, StringComparison.Ordinal);

        Assert.Contains("StopPaneCompositionAnimationsForHandoff(pane, state);", handoff, StringComparison.Ordinal);
        Assert.Contains("state.PaneVersion++;", handoff, StringComparison.Ordinal);
        Assert.Contains("state.PendingPaneExitFinalize = null;", handoff, StringComparison.Ordinal);
        Assert.DoesNotContain("RestorePaneBaseline", handoff, StringComparison.Ordinal);

        Assert.Contains("AnimationStopBehavior.LeaveCurrentValue", stopForHandoff, StringComparison.Ordinal);
        Assert.Contains("completion?.TrySetResult(false);", stopForHandoff, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneBaseOpacity", stopForHandoff, StringComparison.Ordinal);
        Assert.DoesNotContain("PaneBaselinePosition", stopForHandoff, StringComparison.Ordinal);
    }

    [Fact]
    public void BannerExitStillRestoresTheStableBaselineBeforeItsFinalizerRuns()
    {
        var source = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner",
            "Services",
            "AppAnimationLayer.cs"));
        var completeExit = MethodBody(
            source,
            "private static void CompletePaneExit(",
            "private static void CompletePaneMotionForPolicy(");

        var restore = completeExit.IndexOf(
            "RestorePaneBaseline(pane, state);",
            StringComparison.Ordinal);
        var finalize = completeExit.IndexOf("finalize?.Invoke();", StringComparison.Ordinal);

        Assert.True(restore >= 0, "Exit completion must restore the stable XAML baseline.");
        Assert.True(finalize > restore, "The finalizer must run only after baseline restoration.");
    }

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing method boundary: {endMarker}");
        return source[start..end];
    }
}

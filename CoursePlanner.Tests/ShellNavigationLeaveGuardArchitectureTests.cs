namespace CoursePlanner.Tests;

public sealed class ShellNavigationLeaveGuardArchitectureTests
{
    [Fact]
    public void ShellGuardDelegatesToBothEditablePages()
    {
        var shell = ReadMainWindow();
        var guard = Slice(shell, "private Task<bool> GuardCurrentPageAsync", "private async Task<bool> ConfirmUnsavedCourseEditAsync");

        Assert.Contains("PlannerPage plannerPage => plannerPage.ConfirmLeavingCourseEditAsync()", guard, StringComparison.Ordinal);
        Assert.Contains("CourseLibraryPage libraryPage => libraryPage.ConfirmLeavingCourseEditAsync()", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarAndProgrammaticPageChangesUseTheSameCurrentPageGuard()
    {
        var shell = ReadMainWindow();
        var sidebar = Slice(shell, "private async void RootNavigation_SelectionChanged", "private NavigationViewItem NavigationItemForTag");
        var requested = Slice(shell, "private async Task NavigateToRequestedPageAsync", "private void NavigateToPage");

        Assert.Contains("await GuardCurrentPageAsync()", sidebar, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentNavigationTag == \"planner\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("await GuardCurrentPageAsync()", requested, StringComparison.Ordinal);
        Assert.Contains("requestVersion != _navigationRequestVersion", requested, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimumWindowWidthKeepsManagementPagesAboveTheirMasterDetailBreakpoint()
    {
        var shell = ReadMainWindow();
        var smoke = File.ReadAllText(ProjectFilePath("scripts", "Run-UiSmoke.ps1"));

        Assert.Contains(
            "MinimumWindowWidthDip = TwoPaneLayoutService.CompactBreakpoint + NavigationRailWidthAllowanceDip",
            shell,
            StringComparison.Ordinal);
        Assert.Contains("NavigationRailWidthAllowanceDip = 72", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumWindowWidthDip = 480", shell, StringComparison.Ordinal);
        Assert.Contains("Assert-AppWindowMinimumSize 752 360", smoke, StringComparison.Ordinal);
    }

    private static string ReadMainWindow() =>
        File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml.cs"));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        Assert.True(end > start, $"Missing marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

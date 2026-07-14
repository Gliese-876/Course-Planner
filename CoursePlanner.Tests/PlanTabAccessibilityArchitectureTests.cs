namespace CoursePlanner.Tests;

public sealed class PlanTabAccessibilityArchitectureTests
{
    [Fact]
    public void DynamicPlanTabsUseNativeButtonInvokeAndKeyboardSemantics()
    {
        var shell = ReadMainWindow();

        Assert.Contains("private Button CreatePlanTabItem", shell, StringComparison.Ordinal);
        Assert.Contains("IsTabStop = true", shell, StringComparison.Ordinal);
        Assert.Contains("item.Click += ShellPlanTab_Click;", shell, StringComparison.Ordinal);
        Assert.Contains("private async void ShellPlanTab_Click(object sender, RoutedEventArgs e)", shell, StringComparison.Ordinal);
        Assert.Contains("await ActivatePlanTabAsync(plan);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void MouseAndNativeInvokeShareOneSelectionPathWhileMiddleClickStillCloses()
    {
        var shell = ReadMainWindow();
        var pointerHandler = Slice(
            shell,
            "private async void ShellPlanTab_PointerReleased",
            "private async void ShellPlanTab_Click");
        var clickHandler = Slice(
            shell,
            "private async void ShellPlanTab_Click",
            "private async Task ActivatePlanTabAsync");

        Assert.Contains("MiddleButtonReleased", pointerHandler, StringComparison.Ordinal);
        Assert.Contains("await ClosePlanTabAsync(plan, tab);", pointerHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("LeftButtonReleased", pointerHandler, StringComparison.Ordinal);
        Assert.Contains("await ActivatePlanTabAsync(plan);", clickHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleComparisonPlanSelection", pointerHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentPlan = plan", pointerHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void TabAndCloseAutomationIdsContainTheStablePlanId()
    {
        var shell = ReadMainWindow();

        Assert.Contains("$\"ShellPlanTab_{plan.PlanId}\"", shell, StringComparison.Ordinal);
        Assert.Contains("$\"ShellPlanTabClose_{plan.PlanId}\"", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAutomationId(closeButton, PlanTabCloseAutomationId(plan));", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"ShellPlanTab{index}\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanTabAutomationExposesSetPositionAndCurrentStatus()
    {
        var shell = ReadMainWindow();

        Assert.Contains("AutomationProperties.SetPositionInSet(item, index + 1);", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetSizeOfSet(item, _plannerViewModel.OpenPlans.Count);", shell, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetItemStatus(", shell, StringComparison.Ordinal);
        Assert.Contains("tab, isSelected ? _services.Localization.Localizer[\"CurrentPlan\"]", shell, StringComparison.Ordinal);
        Assert.Contains("_services.Localization.Localizer[\"CurrentPlan\"]", shell, StringComparison.Ordinal);
        Assert.Contains("UpdatePlanTabAutomation(tab, isSelected);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseButtonRemainsAnIndependentNativeButtonWithAPlanSpecificName()
    {
        var shell = ReadMainWindow();

        Assert.Contains("var closeButton = new Button", shell, StringComparison.Ordinal);
        Assert.Contains("$\"{_services.Localization.Localizer[\"ClosePlanTab\"]} {plan.PlanName}\"", shell, StringComparison.Ordinal);
        Assert.Contains("closeButton.Click += async (_, _) => await ClosePlanTabAsync(plan, tab);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanRenameInputEnforcesTheDomainFileComponentLimit()
    {
        var shell = ReadMainWindow();
        var renameHandler = Slice(
            shell,
            "private async Task RenamePlanAsync",
            "private Task<bool> GuardCurrentPageAsync");

        Assert.Contains(
            "MaxLength = WindowsFileNameRules.MaxComponentLength",
            renameHandler,
            StringComparison.Ordinal);
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

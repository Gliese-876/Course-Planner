using System.Xml.Linq;

namespace CoursePlanner.Tests;

public sealed class RequestedUiBehaviorArchitectureTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryKeyboardAcceleratorOwnerHasAnExplicitSemanticPresentationPolicy()
    {
        var plannerXaml = XDocument.Load(ReadPath("CoursePlanner", "Pages", "PlannerPage.xaml"));
        var registrationXaml = XDocument.Load(ReadPath("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml"));
        var productionRoot = RepositoryPaths.FromRoot("CoursePlanner");
        var acceleratorSources = Directory
            .EnumerateFiles(productionRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(productionRoot, path);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Contains("obj", StringComparer.OrdinalIgnoreCase) &&
                       !segments.Contains("bin", StringComparer.OrdinalIgnoreCase);
            })
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("new KeyboardAccelerator", StringComparison.Ordinal) ||
                       source.Contains("<KeyboardAccelerator", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(productionRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["Pages/PlannerPage.xaml.cs", "Windows/RegistrationOrderWindow.xaml"],
            acceleratorSources);
        Assert.Equal("Hidden", plannerXaml.Root?.Attribute("KeyboardAcceleratorPlacementMode")?.Value);

        var orderList = registrationXaml
            .Descendants(Presentation + "ListView")
            .Single(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "RegistrationOrderList");
        Assert.Equal("Hidden", orderList.Attribute("KeyboardAcceleratorPlacementMode")?.Value);

        var registrationCode = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        Assert.Contains(
            "ToolTipService.SetToolTip(RegistrationOrderList, text[\"RegistrationOrderListHelp\"])",
            registrationCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UndoAndRedoExposeHistoryAvailabilityThroughAnEnabledOverflowMenu()
    {
        var xaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var code = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var toolbar = Read("CoursePlanner", "Controls", "ResponsiveToolbarController.cs");

        Assert.Contains("x:Name=\"UndoButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RedoButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", ElementStartTag(xaml, "UndoButton"), StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", ElementStartTag(xaml, "RedoButton"), StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", ElementStartTag(xaml, "UndoButtonText"), StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", ElementStartTag(xaml, "RedoButtonText"), StringComparison.Ordinal);
        Assert.Contains("UndoButton.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("Documents.UndoRedo.CanUndo", code, StringComparison.Ordinal);
        Assert.Contains("RedoButton.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("Documents.UndoRedo.CanRedo", code, StringComparison.Ordinal);
        Assert.Contains("CanExecuteHistoryCommand", code, StringComparison.Ordinal);
        Assert.Contains("Documents.StateAccepted += Documents_HistoryStateChanged", code, StringComparison.Ordinal);
        Assert.Contains("Documents.RolledBack += Documents_HistoryStateChanged", code, StringComparison.Ordinal);
        Assert.Contains("_moreButton.IsEnabled = _hiddenCommands.Count > 0", toolbar, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = command.Button.IsEnabled", toolbar, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetWindowSizeButtonOverridesItsFilledDisabledVisual()
    {
        var xaml = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");
        var button = ElementBlock(xaml, "ResetWindowSizeButton", "</Button>");

        Assert.Contains("Background=\"Transparent\"", button, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ButtonBackgroundDisabled\"", button, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"SubtleFillColorTransparentBrush\"", button, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ButtonBorderBrushDisabled\"", button, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AppControlHoverBrush\" Color=\"Transparent\"", button, StringComparison.Ordinal);

        var disabledHoverLayer = Read("CoursePlanner", "Services", "DisabledButtonHoverLayer.cs");
        Assert.Contains(
            "ResourceBrush(Button, \"AppControlHoverBrush\", _background)",
            disabledHoverLayer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBannerHasVersionedThreeSecondMotionAndAnOptionalFileAction()
    {
        var xaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var page = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");
        var chineseResources = Read("CoursePlanner.Application", "Resources", "zh-Hans", "Resources.resw");
        var englishResources = Read("CoursePlanner.Application", "Resources", "en-US", "Resources.resw");

        Assert.Contains("Closing=\"StatusBar_Closing\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"PlannerRoot\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "services:AppAnimationLayer.Profile=\"DynamicChildren\"",
            ElementStartTag(xaml, "PlannerRoot"),
            StringComparison.Ordinal);
        Assert.Contains("<InfoBar.ActionButton>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StatusOpenButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"StatusOpenButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TransientNotificationLifetime", page, StringComparison.Ordinal);
        Assert.Contains("StatusBar.Title = null", page, StringComparison.Ordinal);
        foreach (var genericTitle in new[]
                 {
                     "StatusCompleted",
                     "StatusAttention",
                     "StatusError",
                     "StatusInformation"
                 })
        {
            Assert.DoesNotContain(genericTitle, page, StringComparison.Ordinal);
            Assert.DoesNotContain($"data name=\"{genericTitle}\"", chineseResources, StringComparison.Ordinal);
            Assert.DoesNotContain($"data name=\"{genericTitle}\"", englishResources, StringComparison.Ordinal);
        }
        Assert.Contains("WaitForExpiryAsync", page, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => RequestStatusBarClose(generation))", page, StringComparison.Ordinal);
        var lifetimeStart = page.IndexOf("private async Task RunStatusBannerLifetimeAsync", StringComparison.Ordinal);
        var lifetimeEnd = page.IndexOf("private void StatusBar_Closing", lifetimeStart, StringComparison.Ordinal);
        Assert.True(lifetimeStart >= 0 && lifetimeEnd > lifetimeStart);
        var lifetimeHandler = page[lifetimeStart..lifetimeEnd];
        Assert.Contains("catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))", lifetimeHandler, StringComparison.Ordinal);
        Assert.Contains("RequestStatusBarClose(generation)", lifetimeHandler, StringComparison.Ordinal);
        var closeStart = page.IndexOf("private async void RequestStatusBarClose", StringComparison.Ordinal);
        var closeEnd = page.IndexOf("private void CommitStatusBarClose", closeStart, StringComparison.Ordinal);
        Assert.True(closeStart >= 0 && closeEnd > closeStart);
        var closeHandler = page[closeStart..closeEnd];
        Assert.Contains("catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))", closeHandler, StringComparison.Ordinal);
        Assert.Contains("CommitStatusBarClose(generation)", closeHandler, StringComparison.Ordinal);
        Assert.Contains("PrepareTransientBannerEntrance", page, StringComparison.Ordinal);
        Assert.Contains("PlayTransientBannerExitThenAsync", page, StringComparison.Ordinal);
        Assert.Contains("Launcher.LaunchFileAsync", page, StringComparison.Ordinal);
        Assert.Contains("StorageFile.GetFileFromPathAsync", page, StringComparison.Ordinal);
        var openHandlerStart = page.IndexOf("private async void StatusOpenButton_Click", StringComparison.Ordinal);
        var openHandlerEnd = page.IndexOf("private static string? NormalizeOpenFilePath", openHandlerStart, StringComparison.Ordinal);
        Assert.True(openHandlerStart >= 0 && openHandlerEnd > openHandlerStart);
        var openHandler = page[openHandlerStart..openHandlerEnd];
        Assert.Contains("!StatusBar.IsOpen", openHandler, StringComparison.Ordinal);
        Assert.Contains("_statusCloseInProgress", openHandler, StringComparison.Ordinal);
        Assert.Contains("!string.Equals(_statusOpenPath, path, StringComparison.Ordinal)", openHandler, StringComparison.Ordinal);
        Assert.Contains("PrepareTransientBannerEntrance", animation, StringComparison.Ordinal);
        Assert.Contains("PlayTransientBannerExitThenAsync", animation, StringComparison.Ordinal);
        Assert.Contains("Action<string, InfoBarSeverity, string?> showStatus", coordinator, StringComparison.Ordinal);
        Assert.Equal(2, coordinator.Split("InfoBarSeverity.Success, path", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void CourseLockingInactiveWeekStylingAndTwoDecimalCreditPrecisionStayWired()
    {
        var page = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml.cs");
        var colors = Read("CoursePlanner", "Styles", "DomainColorResources.xaml");
        var chineseResources = Read("CoursePlanner.Application", "Resources", "zh-Hans", "Resources.resw");

        Assert.Contains("ToggleCourseLockAsync", page, StringComparison.Ordinal);
        Assert.Contains("SetCurrentPlanCourseLocked", page, StringComparison.Ordinal);
        Assert.Contains("includeInactiveMeetings: true", page, StringComparison.Ordinal);
        Assert.Contains("CourseNotThisWeekTitlePrefix", page, StringComparison.Ordinal);
        Assert.Contains("AppCourseBlockLockedBrush", colors, StringComparison.Ordinal);
        Assert.Contains("AppCourseBlockOutOfWeekBrush", colors, StringComparison.Ordinal);
        Assert.Contains("<value>非本周</value>", chineseResources, StringComparison.Ordinal);
        Assert.Contains("metrics.TotalCredits:0.##", mainWindow, StringComparison.Ordinal);
    }

    private static string ElementStartTag(string xaml, string name)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");
        var start = xaml.LastIndexOf('<', nameIndex);
        var end = xaml.IndexOf('>', nameIndex);
        Assert.True(start >= 0 && end > start, $"Could not find the start tag for {name}.");
        return xaml[start..(end + 1)];
    }

    private static string ElementBlock(string xaml, string name, string closingTag)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");
        var start = xaml.LastIndexOf('<', nameIndex);
        var end = xaml.IndexOf(closingTag, nameIndex, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not find the block for {name}.");
        return xaml[start..(end + closingTag.Length)];
    }

    private static string Read(params string[] segments) => File.ReadAllText(ReadPath(segments));

    private static string ReadPath(params string[] segments) => RepositoryPaths.FromRoot(segments);
}

namespace CoursePlanner.Tests;

public sealed class UiSmokeProbeArchitectureTests
{
    [Fact]
    public void SemesterWeekCardProbeUsesSemanticInvokeAndWaitsForTheSelectedWeek()
    {
        var smoke = ReadSmokeScript();
        var numericValueHelper = Slice(
            smoke,
            "function Wait-AutomationNumericValue",
            "function Get-NumberBoxEditorAutomationElement");
        var probe = Slice(
            smoke,
            "Test-UI \"Semester overview week card opens week\"",
            "Test-UI \"Week selector visible in week mode\"");

        Assert.Contains("winapp ui invoke SemesterWeekCard2", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("winapp ui click SemesterWeekCard2", probe, StringComparison.Ordinal);
        Assert.Contains("Wait-AutomationNumericValue WeekNumberBox 2", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("--value \"2\"", probe, StringComparison.Ordinal);
        Assert.Contains("RangeValuePattern", numericValueHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Automation.ValuePattern]", numericValueHelper, StringComparison.Ordinal);
        Assert.DoesNotContain(".Current.Name", numericValueHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("winapp", numericValueHelper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WeekNumberProbeCommitsAdversarialTextAndChecksModelTextAndTitleConvergence()
    {
        var smoke = ReadSmokeScript();
        var numericSetter = Slice(
            smoke,
            "function Set-AutomationNumericValue",
            "function Get-NumberBoxEditorAutomationElement");
        var textCommitter = Slice(
            smoke,
            "function Set-NumberBoxTextAndCommit",
            "function Wait-NumberBoxVisibleText");
        var probe = Slice(
            smoke,
            "Test-UI \"Week selector normalizes a fractional UIA value to an integral week\"",
            "Test-UI \"Normalize comparison selection after semester overview\"");
        var fractionalProbe = Slice(
            probe,
            "Test-UI \"Week selector normalizes a fractional UIA value to an integral week\"",
            "Test-UI \"Fractional week normalization preserves the selected week title\"");
        var emptyProbe = Slice(
            probe,
            "Test-UI \"Week selector restores the current week after empty input\"",
            "Test-UI \"Empty week normalization preserves the selected week title\"");

        Assert.Contains("RangeValuePattern", numericSetter, StringComparison.Ordinal);
        Assert.Contains("SetValue($Value)", numericSetter, StringComparison.Ordinal);
        Assert.Contains("SetValue($Text)", textCommitter, StringComparison.Ordinal);
        Assert.Contains("Send-FocusedVirtualKey ([byte]0x0D)", textCommitter, StringComparison.Ordinal);
        Assert.Contains("Set-AutomationNumericValue WeekNumberBox 2.9", fractionalProbe, StringComparison.Ordinal);
        Assert.Contains("Set-NumberBoxTextAndCommit WeekNumberBox \"\"", probe, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(probe, "Wait-AutomationNumericValue WeekNumberBox 2"));
        Assert.Equal(2, CountOccurrences(probe, "Wait-NumberBoxVisibleText WeekNumberBox \"2\""));
        Assert.Equal(2, CountOccurrences(probe, "$script:weekTwoTitleMarker"));
        Assert.True(
            fractionalProbe.IndexOf("Wait-NumberBoxVisibleText", StringComparison.Ordinal) <
            fractionalProbe.IndexOf("Wait-AutomationNumericValue", StringComparison.Ordinal));
        Assert.True(
            emptyProbe.IndexOf("Wait-NumberBoxVisibleText", StringComparison.Ordinal) <
            emptyProbe.IndexOf("Wait-AutomationNumericValue", StringComparison.Ordinal));
    }

    [Fact]
    public void CtrlHeldProbeInvokesInProcessAfterTheControlBecomesEnabled()
    {
        var smoke = ReadSmokeScript();
        var helper = Slice(
            smoke,
            "function Invoke-WhileCtrlHeld",
            "function Assert-DisabledWhileCtrlHeld");

        Assert.Contains("Get-MainWindowAutomationElement", helper, StringComparison.Ordinal);
        Assert.Contains("$element.Current.IsEnabled", helper, StringComparison.Ordinal);
        Assert.Contains("Invoke-AutomationElement $element", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("winapp", helper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimePickerProbeTypesWithoutMouseSimulationOrMidSequenceReactivation()
    {
        var smoke = ReadSmokeScript();
        var focusedKeyHelper = Slice(
            smoke,
            "function Send-FocusedVirtualKey",
            "function Send-FocusedDigitKey");
        var processElementHelper = Slice(
            smoke,
            "function Get-VisibleProcessAutomationElements",
            "function Focus-AutomationElement");
        var probe = Slice(
            smoke,
            "Test-UI \"Compact period time picker supports keyboard entry and circular stepping\"",
            "Test-UI \"Semesters screenshot\"");

        Assert.DoesNotContain("ShowWindow", focusedKeyHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", focusedKeyHelper, StringComparison.Ordinal);
        Assert.Contains("TreeScope]::Children", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("AutomationElement]::ProcessIdProperty", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("$candidate.Current.ProcessId -eq $AppPid", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("[int]$RetryTimeout = 1000", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("$deadline = (Get-Date).AddMilliseconds($RetryTimeout)", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("UI Automation FindAll", processElementHelper, StringComparison.Ordinal);
        Assert.Contains("Send-FocusedDigitKey", probe, StringComparison.Ordinal);
        Assert.Contains("Send-FocusedVirtualKey", probe, StringComparison.Ordinal);
        Assert.Contains("Open-CompactTimePickerAutomationSurface", probe, StringComparison.Ordinal);
        Assert.Contains("Focus-AutomationElement", probe, StringComparison.Ordinal);
        Assert.Contains("Assert-FocusedTimePickerPartValue", probe, StringComparison.Ordinal);
        Assert.Contains("Wait-CompactTimePickerAutomationSurfaceGone", probe, StringComparison.Ordinal);
        Assert.Contains("Assert-CompactTimePickerDisplay", probe, StringComparison.Ordinal);
        Assert.Contains("Get-ProcessAutomationElement \"TimePickerApplyButton\"", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Send-DigitKey ", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Send-VirtualKey ", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("winapp ui click TimePicker", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("winapp ui invoke TimePickerApplyButton", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedHistoryProbeRunsByDefaultBeforeTabNormalizationAndChecksAllFourStates()
    {
        var smoke = ReadSmokeScript();
        var overflowPreparation = Slice(
            smoke,
            "function Prepare-HistoryToolbarOverflow",
            "function Assert-HistoryToolbarPresentation");
        var presentation = Slice(
            smoke,
            "function Assert-HistoryToolbarPresentation",
            "function Wait-HistoryCommandState");
        var probe = Slice(
            smoke,
            "Test-UI \"Requested history buttons expose initial, new-plan, undo, and redo states\"",
            "Test-UI \"At least two plan tabs are available\"");

        Assert.Contains("Prepare-HistoryToolbarOverflow", probe, StringComparison.Ordinal);
        Assert.Contains("Maximize-AppWindow", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("MoveWindow", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("ToolbarMoreButton", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("Get-OptionalMainWindowAutomationElement", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("$more.Current.IsEnabled", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("Direct history commands are a valid presentation", overflowPreparation, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not produce a toolbar-overflow layout", overflowPreparation, StringComparison.Ordinal);
        Assert.Contains("Invoke-ResponsiveToolbarCommand NewPlanButton", probe, StringComparison.Ordinal);
        Assert.Contains("Invoke-ResponsiveToolbarCommand UndoButton", probe, StringComparison.Ordinal);
        Assert.Contains("Invoke-ResponsiveToolbarCommand RedoButton", probe, StringComparison.Ordinal);
        Assert.Contains("$moreVisible -and -not $more.Current.IsEnabled", presentation, StringComparison.Ordinal);
        Assert.Contains("Get-VisibleProcessMenuItemByNames", presentation, StringComparison.Ordinal);
        Assert.Contains("$undoItem.Current.IsEnabled", presentation, StringComparison.Ordinal);
        Assert.Contains("$redoItem.Current.IsEnabled", presentation, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(probe, "Wait-HistoryCommandState"));
        Assert.Equal(4, CountOccurrences(probe, "requested-history-"));
        Assert.True(
            probe.IndexOf("NewPlanButton", StringComparison.Ordinal) <
            probe.IndexOf("UndoButton", StringComparison.Ordinal));
        Assert.True(
            probe.IndexOf("UndoButton", StringComparison.Ordinal) <
            probe.IndexOf("RedoButton", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestedCtrlFProbeChecksDescendantFocusAndOnlyVisibleTooltipPopups()
    {
        var smoke = ReadSmokeScript();
        var chord = Slice(
            smoke,
            "function Send-ControlChord",
            "function Assert-FocusWithinAutomationElement");
        var focus = Slice(
            smoke,
            "function Assert-FocusWithinAutomationElement",
            "function Test-IsTooltipAutomationElement");
        var tooltipDiscovery = Slice(
            smoke,
            "function Get-VisibleCtrlFShortcutTooltips",
            "function Assert-NoCtrlFShortcutTooltips");
        var hover = Slice(
            smoke,
            "function Assert-NoCtrlFShortcutTooltips",
            "function Get-VisibleMainWindowAutomationElement");
        var probe = Slice(
            smoke,
            "Test-UI \"Requested Ctrl+F focuses the course search field\"",
            "$script:requestedCourseName");

        Assert.Contains("[SmokeNative]::Ctrl", chord, StringComparison.Ordinal);
        Assert.Contains("[SmokeNative]::KeyUp", chord, StringComparison.Ordinal);
        Assert.Contains("RawViewWalker.GetParent", focus, StringComparison.Ordinal);
        Assert.Contains("Automation]::Compare", focus, StringComparison.Ordinal);
        Assert.Contains("ProcessIdProperty", tooltipDiscovery, StringComparison.Ordinal);
        Assert.Contains("Test-IsTooltipAutomationElement", tooltipDiscovery, StringComparison.Ordinal);
        Assert.Contains("(?:ctrl|control)", tooltipDiscovery, StringComparison.Ordinal);
        Assert.Contains("SetCursorPos", hover, StringComparison.Ordinal);
        Assert.Contains("Get-VisibleCtrlFShortcutTooltips", hover, StringComparison.Ordinal);
        Assert.Contains("requested-ctrl-f-tooltip-failure.png", hover, StringComparison.Ordinal);
        Assert.Contains("Send-ControlChord ([byte]0x46)", probe, StringComparison.Ordinal);
        Assert.Contains("Assert-FocusWithinAutomationElement CourseSearchBox", probe, StringComparison.Ordinal);
        Assert.Contains("Assert-NoCtrlFShortcutTooltips", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedOrdinaryStatusProbeCoversEntranceThreeSecondLifetimeExitAndNoOpenAction()
    {
        var smoke = ReadSmokeScript();
        var lifetimeProbe = Slice(
            smoke,
            "Test-UI \"Requested ordinary StatusBar auto-closes after three seconds with entrance and exit motion\"",
            "Test-UI \"Requested rapid consecutive statuses keep only the latest notification lifetime\"");
        var replacementProbe = Slice(
            smoke,
            "Test-UI \"Requested rapid consecutive statuses keep only the latest notification lifetime\"",
            "# The saved course provides deterministic state");

        Assert.Contains("Invoke-MainWindowAutomationElement SaveCourseEditButton", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Assert-StatusOpenActionHidden", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("System.Diagnostics.Stopwatch]::StartNew", lifetimeProbe, StringComparison.Ordinal);
        Assert.True(
            lifetimeProbe.IndexOf("$statusLifetime = [System.Diagnostics.Stopwatch]::StartNew()", StringComparison.Ordinal) <
            lifetimeProbe.IndexOf("Invoke-MainWindowAutomationElement SaveCourseEditButton", StringComparison.Ordinal));
        Assert.Contains("Wait-StopwatchElapsed $statusLifetime 2600", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-StopwatchElapsed $statusLifetime 2750", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-StopwatchElapsed $statusLifetime 3000", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-StopwatchElapsed $statusLifetime 3350", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Test-MainWindowAutomationElementInTree StatusBar", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-MainWindowAutomationElementGone StatusBar", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("4000 - $statusLifetime.ElapsedMilliseconds", lifetimeProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-StopwatchElapsed $statusLifetime 3050", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("requested-status-entering.png", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("requested-status-settled.png", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("requested-status-before-auto-exit.png", lifetimeProbe, StringComparison.Ordinal);
        Assert.Contains("requested-status-exiting.png", lifetimeProbe, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(replacementProbe, "Invoke-MainWindowAutomationElement SaveCourseEditButton"));
        Assert.Equal(2, CountOccurrences(replacementProbe, "System.Diagnostics.Stopwatch]::StartNew"));
        var firstClock = replacementProbe.IndexOf("$firstNotification = [System.Diagnostics.Stopwatch]::StartNew()", StringComparison.Ordinal);
        var firstSave = replacementProbe.IndexOf("Invoke-MainWindowAutomationElement SaveCourseEditButton", StringComparison.Ordinal);
        var latestClock = replacementProbe.IndexOf("$latestNotification = [System.Diagnostics.Stopwatch]::StartNew()", StringComparison.Ordinal);
        var latestSave = replacementProbe.IndexOf(
            "Invoke-MainWindowAutomationElement SaveCourseEditButton",
            firstSave + 1,
            StringComparison.Ordinal);
        Assert.True(firstClock >= 0 && firstClock < firstSave);
        Assert.True(latestClock >= 0 && latestClock < latestSave);
        Assert.Contains("Start-Sleep -Milliseconds 250", replacementProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-StopwatchElapsed $firstNotification 3350", replacementProbe, StringComparison.Ordinal);
        Assert.Contains("Wait-StopwatchElapsed $latestNotification 3350", replacementProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-StopwatchElapsed $firstNotification 3050", replacementProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-StopwatchElapsed $latestNotification 3050", replacementProbe, StringComparison.Ordinal);
        Assert.Contains("4000 - $latestNotification.ElapsedMilliseconds", replacementProbe, StringComparison.Ordinal);
        Assert.Contains("stale notification generation", replacementProbe, StringComparison.Ordinal);
        Assert.Contains("requested-status-latest-generation.png", replacementProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedRegistrationProbeCreatesDeterministicPlanStateAndCapturesTransparentDisabledReset()
    {
        var smoke = ReadSmokeScript();
        var mainNameLookup = Slice(
            smoke,
            "function Get-MainWindowAutomationElementByNameFragment",
            "function Scroll-AutomationElementIntoView");
        var processNameLookup = Slice(
            smoke,
            "function Get-VisibleProcessAutomationElementByNames",
            "function Get-VisibleProcessMenuItemByNames");
        var menuLookup = Slice(
            smoke,
            "function Get-VisibleProcessMenuItemByNames",
            "function Get-VisibleProcessButtonByNames");
        var buttonLookup = Slice(
            smoke,
            "function Get-VisibleProcessButtonByNames",
            "function Wait-CourseAddStatus");
        var transparencyHelper = Slice(
            smoke,
            "function Assert-DisabledTransparentButtonBackground",
            "function Wait-UiGone");
        var probe = Slice(
            smoke,
            "Test-UI \"Requested registration reset is disabled with a transparent background\"",
            "Test-UI \"Planner screenshot\"");

        Assert.Contains("LibraryCourseRow", probe, StringComparison.Ordinal);
        Assert.Contains("Send-FocusedVirtualKey ([byte]0x5D)", probe, StringComparison.Ordinal);
        Assert.Contains("Expand-ProcessMenuItemByNames", probe, StringComparison.Ordinal);
        Assert.Contains("Get-StatusBarCloseButton", probe, StringComparison.Ordinal);
        Assert.Contains("Manual StatusBar close collapsed immediately", probe, StringComparison.Ordinal);
        Assert.Contains("requested-status-manual-exiting.png", probe, StringComparison.Ordinal);
        Assert.Contains("Wait-MainWindowAutomationElementEnabled RegistrationOrderButton", probe, StringComparison.Ordinal);
        Assert.Contains("Get-OwnedWinUiWindowHandle", probe, StringComparison.Ordinal);
        Assert.Contains("RegistrationOrderResetSizeButton", probe, StringComparison.Ordinal);
        Assert.Contains("$resetBounds = $resetButton.Current.BoundingRectangle", probe, StringComparison.Ordinal);
        Assert.Contains("[SmokeNative]::SetCursorPos(", probe, StringComparison.Ordinal);
        Assert.Contains("registration-reset-disabled-transparent.png", probe, StringComparison.Ordinal);
        Assert.Contains("registration-reset-disabled-transparent-crop.png", probe, StringComparison.Ordinal);
        Assert.Contains("RegistrationOrderCloseButton", probe, StringComparison.Ordinal);
        Assert.Contains("AutomationElement]::FromHandle", mainNameLookup, StringComparison.Ordinal);
        Assert.Contains("$root.FindAll", mainNameLookup, StringComparison.Ordinal);
        Assert.Contains("$lastError", mainNameLookup, StringComparison.Ordinal);
        Assert.Contains("TreeScope]::Children", processNameLookup, StringComparison.Ordinal);
        Assert.Contains("$window.FindAll", processNameLookup, StringComparison.Ordinal);
        Assert.Contains("TreeScope]::Descendants", processNameLookup, StringComparison.Ordinal);
        Assert.Contains("$lastError", processNameLookup, StringComparison.Ordinal);
        Assert.Contains("Get-VisibleProcessAutomationElementByNames", menuLookup, StringComparison.Ordinal);
        Assert.Contains("ControlType]::MenuItem", menuLookup, StringComparison.Ordinal);
        Assert.Contains("Get-VisibleProcessAutomationElementByNames", buttonLookup, StringComparison.Ordinal);
        Assert.Contains("ControlType]::Button", buttonLookup, StringComparison.Ordinal);
        Assert.Contains("[int]$Timeout = 250", buttonLookup, StringComparison.Ordinal);
        Assert.Contains("$Element.Current.IsEnabled", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("GetPixel", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("maximumChannelDelta", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$insideX = $padding + 3", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$outsideX = $padding - 3", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$sampleFractions = @(0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80)", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$bitmap.GetPixel($insideX, $sampleY)", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$bitmap.GetPixel($outsideX, $sampleY)", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$medianChannelDelta", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains("$matchingSampleCount", transparencyHelper, StringComparison.Ordinal);
        Assert.Contains(
            "$requiredMatchingSampleCount = [int][Math]::Ceiling($channelDeltas.Count * 0.70)",
            transparencyHelper,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$buttonWidth", transparencyHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("$padding - 2", transparencyHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveToolbarAndPlannerRootMotionKeepTheirOverflowAndReflowSourceChains()
    {
        var toolbar = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "Controls", "ResponsiveToolbarController.cs"));
        var plannerXaml = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "Pages", "PlannerPage.xaml"));
        var planner = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var animation = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "Services", "AppAnimationLayer.cs"));

        Assert.Contains("_moreButton.Visibility = _hiddenCommands.Count > 0", toolbar, StringComparison.Ordinal);
        Assert.Contains("_moreButton.IsEnabled = _hiddenCommands.Count > 0", toolbar, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = command.Button.IsEnabled", toolbar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PlannerRoot\"", plannerXaml, StringComparison.Ordinal);
        Assert.Contains("services:AppAnimationLayer.Profile=\"DynamicChildren\"", plannerXaml, StringComparison.Ordinal);
        Assert.Contains("ArmCenterReflow(", planner, StringComparison.Ordinal);
        Assert.Contains("CenterPane.SizeChanged += handler", planner, StringComparison.Ordinal);
        Assert.Contains("AppAnimationLayer.PlayResponsiveWidthReflow(", planner, StringComparison.Ordinal);
        Assert.Contains("AppAnimationLayer.CancelResponsiveWidthReflow(CenterPane)", planner, StringComparison.Ordinal);
        Assert.Contains("case AppAnimationProfile.DynamicChildren:", animation, StringComparison.Ordinal);
        Assert.Contains("AddCollectionTransitions(element, state, includeReorder: false)", animation, StringComparison.Ordinal);
        Assert.Contains("public static void PlayResponsiveWidthReflow(", animation, StringComparison.Ordinal);
        Assert.Contains("visual.StartAnimation(nameof(Visual.Scale), animation)", animation, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedFileOpenProbeUsesOwnedPickerInputAndCannotConfuseNaturalExpiryWithLauncherSuccess()
    {
        var probe = File.ReadAllText(RepositoryPaths.FromRoot("scripts", "Test-StatusOpenAction.ps1"));
        var pickerDiscovery = Slice(probe, "function Get-PickerWindow", "function Set-PickerFileName");
        var pickerInput = Slice(probe, "function Set-PickerFileName", "function Get-PickerSaveButton");
        var goneWait = Slice(probe, "function Wait-StatusGone", "function Get-NewTopLevelWindowEvidence");
        var saveStep = Slice(
            probe,
            "Invoke-Step \"Save a unique JSON through the transient FileSavePicker\"",
            "$status = $null");
        var openStep = Slice(
            probe,
            "Invoke-Step \"Invoke Open and prove Launcher acceptance from prompt banner closure\"",
            "Write-ResultAndExit -Status PASS");

        Assert.Contains("IsOwnedBy", pickerDiscovery, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow() -ne $PickerHwnd", pickerInput, StringComparison.Ordinal);
        Assert.Contains("AutomationElement]::FocusedElement", pickerInput, StringComparison.Ordinal);
        Assert.Contains("ReplaceFocusedText($FullPath)", pickerInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", pickerInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConvertFrom-Json -ErrorAction Stop", saveStep, StringComparison.Ordinal);
        Assert.Contains("$script:saveInvokedAt = [Diagnostics.Stopwatch]::StartNew()", saveStep, StringComparison.Ordinal);
        Assert.True(
            saveStep.IndexOf("$script:saveInvokedAt =", StringComparison.Ordinal) <
            saveStep.IndexOf("Invoke-Element $saveButton", StringComparison.Ordinal));
        Assert.DoesNotContain(".Restart()", probe, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Id $AppPid", goneWait, StringComparison.Ordinal);
        var goneWaitCatchStart = goneWait.IndexOf("catch {", StringComparison.Ordinal);
        var goneWaitCatchEnd = goneWait.IndexOf("Start-Sleep", goneWaitCatchStart, StringComparison.Ordinal);
        Assert.True(goneWaitCatchStart >= 0 && goneWaitCatchEnd > goneWaitCatchStart);
        Assert.DoesNotContain("return", goneWait[goneWaitCatchStart..goneWaitCatchEnd], StringComparison.Ordinal);
        Assert.Contains("$script:saveInvokedAt.ElapsedMilliseconds -ge 2800", openStep, StringComparison.Ordinal);
        Assert.Contains("openDisabledOrGoneImmediately", openStep, StringComparison.Ordinal);
        Assert.Contains("matchingNewTopLevelWindowsAfterOpen", openStep, StringComparison.Ordinal);
        Assert.Contains("-ExpectedFileName", openStep, StringComparison.Ordinal);
    }

    private static string ReadSmokeScript() =>
        File.ReadAllText(RepositoryPaths.FromRoot("scripts", "Run-UiSmoke.ps1"));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing marker: {startMarker}");
        Assert.True(end > start, $"Missing marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}

namespace CoursePlanner.Tests;

public sealed class UiMotionAndMaterialArchitectureTests
{
    [Fact]
    public void PeriodTimePickersReserveReadableWidthAndReflowOnNarrowWindows()
    {
        var picker = Read("CoursePlanner", "Controls", "CompactTimePicker.cs");
        var semestersXaml = Read("CoursePlanner", "Pages", "SemestersPage.xaml");
        var semestersCode = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");

        Assert.Contains("public const double MinimumDisplayWidth = 108;", picker);
        Assert.Contains("MinWidth = MinimumDisplayWidth;", picker);
        Assert.Contains("x:Name=\"PeriodNumberEditor\"", semestersXaml);
        Assert.Contains("x:Name=\"PeriodActionBar\"", semestersXaml);
        Assert.Contains("PeriodEditorStackBreakpoint = 900;", semestersCode);
        Assert.Contains("ApplyPeriodEditorLayout(responsiveWidth < PeriodEditorStackBreakpoint);", semestersCode);
        Assert.Contains("PeriodEditorGrid.ColumnDefinitions.Clear();", semestersCode);
        Assert.Contains("Grid.SetRow(PeriodStartPicker, 1);", semestersCode);
        Assert.Contains("Grid.SetColumn(PeriodEndPicker, 1);", semestersCode);
    }

    [Fact]
    public void NavigationAndCustomMotionUseTheCentralAnimationLayer()
    {
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("UISettings", animation);
        Assert.Contains("AnimationsEnabled", animation);
        Assert.Contains("SuppressNavigationTransitionInfo", animation);
        Assert.Contains("EntranceNavigationTransitionInfo", animation);
        Assert.DoesNotContain("PaneThemeTransition", animation);
        Assert.DoesNotContain("ContentThemeTransition", animation);
        Assert.Contains("ScalarTransition", animation);
        Assert.Contains("Vector3Transition", animation);
        Assert.Contains("AppAnimationProfile.Interactive", animation);
        Assert.DoesNotContain("AppAnimationLayer.SetProfile(tab, AppAnimationProfile.Interactive)", mainWindow);
        Assert.Contains("AppAnimationLayer.ConfigureFrame(RootFrame)", mainWindow);
        Assert.Contains("AppAnimationLayer.Navigate(RootFrame, pageType, _services)", mainWindow);
        Assert.Contains("AnimationsEnabledChanged", animation);
        Assert.Contains("flyout.Opening += Flyout_Opening", animation);
        Assert.Contains("flyout.Closing += Flyout_Closing", animation);
        Assert.Contains("OwnedTransitions", animation);
        Assert.Contains("RefreshVersion", animation);
    }

    [Fact]
    public void PagesDoNotStackEntranceAnimationsOnFrameNavigation()
    {
        foreach (var page in new[]
                 {
                     "CourseLibraryPage.xaml",
                     "LabelsPage.xaml",
                     "PlansPage.xaml",
                     "SemestersPage.xaml",
                     "SettingsPage.xaml"
                 })
        {
            var xaml = Read("CoursePlanner", "Pages", page);
            Assert.DoesNotContain("EntranceThemeTransition", xaml);
            Assert.DoesNotContain("Grid.ChildrenTransitions", xaml);
        }
    }

    [Fact]
    public void LongLivedWindowsUseMicaAndModalDialogsKeepNativeMaterial()
    {
        var toolWindow = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");
        var toolWindowCode = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml");
        var appStyles = Read("CoursePlanner", "Styles", "AppStyles.xaml");
        var materials = Read("CoursePlanner", "Styles", "MaterialResources.xaml");
        var materialLayer = Read("CoursePlanner", "Services", "AppMaterialLayer.cs");
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");

        Assert.DoesNotContain("<MicaBackdrop", toolWindow);
        Assert.Contains("SystemBackdrop = new MicaBackdrop", toolWindowCode);
        Assert.DoesNotContain("DesktopAcrylicBackdrop", toolWindow);
        Assert.DoesNotContain("AppMaterialSmokeBrush", materials);
        Assert.DoesNotContain("SystemBackdrop = new DesktopAcrylicBackdrop", materialLayer);
        Assert.Contains("AppTransientFlyoutPresenterStyle", materialLayer);
        Assert.DoesNotContain("CreateTransientFlyoutPresenterStyle", materialLayer);
        Assert.DoesNotContain("TransientFlyout,", materialLayer);
        Assert.Contains("AppMaterialSurface.Dialog", coordinator);
        Assert.DoesNotContain("ApplySurface(dialog, AppMaterialSurface.TransientFlyout)", coordinator);
        Assert.Contains("services:AppMaterialLayer.Surface=\"Page\"", mainWindow);
        Assert.DoesNotContain("Value=\"TransientFlyout\"", appStyles);
    }

    [Fact]
    public void PlannerUsesNativePaneAndContentProfiles()
    {
        var xaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var code = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");

        Assert.Contains("AppAnimationLayer.Profile=\"PaneFromLeft\"", xaml);
        Assert.Contains("AppAnimationLayer.Profile=\"PaneFromRight\"", xaml);
        Assert.Contains("AppAnimationLayer.SetProfile(host, AppAnimationProfile.ContentRefresh)", code);
        Assert.Contains("x:Name=\"TimetableFrame\"", xaml);
        Assert.Contains("AppAnimationLayer.RefreshContent(TimetableHost, RenderCenterCore)", code);
        Assert.Contains("AppContentDirection", animation);
        Assert.Contains("NavigateDirectional", animation);
        Assert.Contains("SlideNavigationTransitionInfo", animation);
        Assert.Contains("SlideNavigationTransitionEffect.FromRight", animation);
        Assert.Contains("SlideNavigationTransitionEffect.FromLeft", animation);
        Assert.Contains("_pendingCenterRefreshDirection", code);
        Assert.Contains("_renderTargetTimetableHost", code);
        Assert.Contains("CreateTimetableHost()", code);
        Assert.Contains("TimetableTransitionPage.QueueContent(nextHost)", code);
        Assert.Contains("MatchTimetableTransitionGeometry(_activeTimetableHost, nextHost)", code);
        Assert.Contains("ChangeWeek(-1, AppContentDirection.Backward)", code);
        Assert.Contains("ChangeWeek(1, AppContentDirection.Forward)", code);
        Assert.Contains("AppAnimationLayer.NavigateDirectional(", code);
        Assert.DoesNotContain("RefreshDirectionalContentAsync", animation);
        Assert.DoesNotContain("DirectionalTravelDistance", animation);
        Assert.Contains("? PanePresentation.Overlay", code);
        Assert.DoesNotContain("PlayWeekCardJumpAnimationAsync", code);
    }

    [Fact]
    public void LongLivedMaterialsUseFluentLayersAndBrandSemanticColors()
    {
        var materials = Read("CoursePlanner", "Styles", "MaterialResources.xaml");
        var controls = Read("CoursePlanner", "Styles", "ControlStateResources.xaml");
        var domain = Read("CoursePlanner", "Styles", "DomainColorResources.xaml");
        var materialLayer = Read("CoursePlanner", "Services", "AppMaterialLayer.cs");
        var app = Read("CoursePlanner", "App.xaml");

        Assert.Contains("AppMaterialSurface.Chrome => new(\"AppMaterialChromeBrush\"", materialLayer);
        Assert.Contains("AppMaterialSurface.Page => new(\"AppMaterialPageBrush\"", materialLayer);
        Assert.Contains("AppMaterialSurface.DockedPane => new(\"AppMaterialDockedPaneBrush\"", materialLayer);
        Assert.Contains("AppMaterialSurface.Card => new(\"AppMaterialCardBrush\"", materialLayer);
        Assert.Contains("AppMaterialSurface.OverlayPane => new(\"AppMaterialOverlayPaneBrush\"", materialLayer);
        Assert.Equal(2, materials.Split("<AcrylicBrush", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, materials.Split("x:Key=\"AppMaterialOverlayPaneBrush\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("AppMaterialFlyoutBrush", materials);
        Assert.Contains("NavigationViewContentBackground\" ResourceKey=\"AppMaterialPageBrush", materials);
        Assert.Contains("<Color x:Key=\"SystemAccentColor\">#00857A</Color>", app);
        Assert.Contains("<Color x:Key=\"SystemAccentColor\">#72DED0</Color>", app);
        Assert.Contains("AccentFillColorDefaultBrush\" Color=\"#00857A", app);
        Assert.Contains("AccentFillColorDefaultBrush\" Color=\"#72DED0", app);
        Assert.DoesNotContain("SystemAccentColor", controls);
        Assert.Contains("AppPickerSelectedBrush\" Color=\"#00857A", controls);
        Assert.Contains("AppPickerSelectedBrush\" Color=\"#72DED0", controls);
        Assert.Contains("AppPickerSelectedTextBrush\" Color=\"#0B2622", controls);
        Assert.Contains("AppControlHoverBrush\" Color=\"#09000000", controls);
        Assert.Contains("AppControlHoverBrush\" Color=\"#16FFFFFF", controls);
        Assert.Equal(3, controls.Split("x:Key=\"ButtonBackgroundPointerOver\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, controls.Split("x:Key=\"AppBarButtonBackgroundPointerOver\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"NavigationViewItemBackgroundPointerOver\"", controls);
        Assert.Contains("AppStatusCriticalBrush\" Color=\"#A4262C", domain);
        Assert.Contains("AppStatusCautionBrush\" Color=\"#724E00", domain);
        Assert.Contains("AppStatusCriticalBrush\" Color=\"#FFBAB5", domain);
    }

    [Fact]
    public void NavigationRailKeepsTheBrandTintInCompactAndExpandedStates()
    {
        var materials = Read("CoursePlanner", "Styles", "MaterialResources.xaml");
        const string defaultAlias = "NavigationViewDefaultPaneBackground\" ResourceKey=\"AppMaterialNavigationRailBrush";
        const string expandedAlias = "NavigationViewExpandedPaneBackground\" ResourceKey=\"AppMaterialNavigationRailBrush";
        const string topAlias = "NavigationViewTopPaneBackground\" ResourceKey=\"AppMaterialNavigationRailBrush";

        Assert.Contains("AppMaterialNavigationRailBrush\" Color=\"#E4F3ED", materials);
        Assert.Contains("AppMaterialNavigationRailBrush\" Color=\"#1B2A26", materials);
        Assert.Equal(2, materials.Split(defaultAlias, StringSplitOptions.None).Length - 1);
        Assert.Equal(2, materials.Split(expandedAlias, StringSplitOptions.None).Length - 1);
        Assert.Equal(2, materials.Split(topAlias, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("NavigationViewExpandedPaneBackground\" ResourceKey=\"AppMaterialOverlayPaneBrush", materials);
        Assert.Equal(2, materials.Split("NavigationViewContentBackground\" ResourceKey=\"AppMaterialPageBrush", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void NativeAndExportFallbacksStayAlignedWithSemanticThemeColors()
    {
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml.cs");
        var exportFactory = Read("CoursePlanner", "Services", "TimetableExportRequestFactory.cs");
        var exportContracts = Read("CoursePlanner.Export", "TimetableExportContracts.cs");

        Assert.Contains("ColorHelper.FromArgb(0xFF, 0xF5, 0xF7, 0xF6)", mainWindow);
        Assert.Contains("ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)", mainWindow);
        Assert.Contains("ColorHelper.FromArgb(0xFF, 0xC5, 0xCE, 0xCB)", mainWindow);
        Assert.Contains("ColorHelper.FromArgb(0xFF, 0x57, 0x5F, 0x5C)", mainWindow);
        Assert.Contains("ColorHelper.FromArgb(0xE8, 0x1B, 0x1F, 0x1E)", mainWindow);
        Assert.Contains("ColorHelper.FromArgb(0xE8, 0xF1, 0xF4, 0xF3)", mainWindow);
        Assert.Contains("Color.FromArgb(255, 0x1E, 0x23, 0x21)", exportFactory);
        Assert.Contains("Color.FromArgb(255, 0xF8, 0xFB, 0xFA)", exportFactory);
        Assert.Contains("SecondaryText = TimetableExportColor.FromHex(\"#575F5C\")", exportContracts);
        Assert.Contains("PrimaryText = TimetableExportColor.FromHex(\"#F5F7F6\")", exportContracts);
        Assert.Contains("SecondaryText = TimetableExportColor.FromHex(\"#C5CECB\")", exportContracts);
        Assert.Contains("MatrixCardBackground = TimetableExportColor.FromHex(\"#2D312F\")", exportContracts);
    }

    [Fact]
    public void RepeatedContentAndLayoutChangesUseReplayableCentralMotion()
    {
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var styles = Read("CoursePlanner", "Styles", "AppStyles.xaml");
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var presenter = Read("CoursePlanner", "Controls", "MeetingTimesPresenter.cs");
        var editor = Read("CoursePlanner", "Controls", "MeetingTimesEditor.cs");

        Assert.Contains("ElementCompositionPreview.GetElementVisual(host)", animation);
        Assert.Contains("CreateScalarKeyFrameAnimation()", animation);
        Assert.Contains("visual.StartAnimation(nameof(Visual.Opacity), animation)", animation);
        Assert.DoesNotContain("DispatcherQueuePriority.Low", animation);
        Assert.Contains("Property=\"services:AppAnimationLayer.Profile\" Value=\"DynamicChildren\"", styles);
        Assert.Contains("ActualThemeChanged += (_, _) => Rebuild();", presenter);
        Assert.Contains("ActualThemeChanged += (_, _) => RebuildRows();", editor);

        var dockedStart = planner.IndexOf("if (presentation == PanePresentation.Docked)", StringComparison.Ordinal);
        var overlayStart = planner.IndexOf("CaptureColumnWidth(column", dockedStart, StringComparison.Ordinal);
        var dockedCode = planner[dockedStart..overlayStart];
        Assert.True(
            dockedCode.IndexOf("column.Width = new GridLength(storedWidth)", StringComparison.Ordinal) <
            dockedCode.IndexOf("pane.Visibility = Visibility.Visible", StringComparison.Ordinal));

        var overlayCode = planner[overlayStart..planner.IndexOf("private static void CaptureDockedPaneWidth", overlayStart, StringComparison.Ordinal)];
        Assert.True(
            overlayCode.IndexOf("pane.Width = overlayWidth", StringComparison.Ordinal) <
            overlayCode.IndexOf("pane.Visibility = Visibility.Visible", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannerSidePanesUseReplayableCentralOpenCloseAndReflowMotion()
    {
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var plannerXaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");

        Assert.Contains("public static long PreparePaneEntrance(", animation);
        Assert.Contains("public static async Task<bool> PlayPreparedPaneEntranceAsync(", animation);
        Assert.Contains("public static async Task<bool> PlayPaneExitThenAsync(", animation);
        Assert.Contains("public static bool CancelPendingPaneExit(", animation);
        Assert.Contains("public static bool CompletePendingPaneExit(", animation);
        Assert.Contains("PaneAnimationVisual", animation);
        Assert.Contains("PaneVersion", animation);
        Assert.DoesNotContain("CompositionTarget.Rendering", animation);
        Assert.Contains("new Vector3Transition", animation);
        Assert.Contains("new ScalarTransition", animation);
        Assert.Contains("if (!AnimationsEnabled || !pane.IsLoaded)", animation);
        Assert.Contains("private async Task OpenDetailsAsync(Action beginEdit)", planner);
        Assert.Contains("AppAnimationLayer.CancelPendingPaneExit(DetailPane);", planner);
        Assert.Contains("AppAnimationLayer.CompletePendingPaneExit(DetailPane);", planner);
        Assert.Contains("AppAnimationLayer.PreparePaneEntrance(DetailPane)", planner);
        Assert.Contains("AppAnimationLayer.PlayPreparedPaneEntranceAsync(DetailPane", planner);
        Assert.Contains("AppAnimationLayer.PlayPaneExitThenAsync(DetailPane", planner);
        Assert.Contains("if (AppAnimationLayer.CancelPendingPaneExit(LibraryPane))", planner);
        Assert.Contains("AppAnimationLayer.CompletePendingPaneExit(LibraryPane);", planner);
        Assert.Contains("AppAnimationLayer.PreparePaneEntrance(LibraryPane)", planner);
        Assert.Contains("AppAnimationLayer.PlayPreparedPaneEntranceAsync(LibraryPane", planner);
        Assert.Contains("await CloseLibraryAsync();", planner);
        Assert.Contains("private async Task CloseLibraryAsync()", planner);
        Assert.Contains("AppAnimationLayer.PlayPaneExitThenAsync(", planner);
        Assert.Contains("LibraryPane,", planner);
        Assert.Contains("private readonly PlannerPaneStateCoordinator _paneState = new();", planner);
        Assert.Contains("private async Task<bool> SwitchOverlayAsync(", planner);
        Assert.Contains("private async Task CompleteSuppressedPaneRestorationAsync(", planner);
        Assert.Contains("AppAnimationLayer.CancelPreparedPaneEntrance", planner);
        Assert.Contains("_paneState.IsSuppressed(PlannerPaneKind.Library)", planner);
        Assert.Contains("_paneState.IsSuppressed(PlannerPaneKind.Detail)", planner);
        Assert.Contains("AppAnimationLayer.RefreshContent(DetailContentHost", planner);
        Assert.Contains("AppAnimationLayer.PlayResponsiveWidthReflow(", planner);
        Assert.Contains("ArmCenterReflow(", planner);
        Assert.Contains("enabled: ShouldDockLibraryPane()", planner);
        Assert.Contains("enabled: ShouldDockDetailPane()", planner);
        Assert.Contains("Grid.GetColumnSpan(LibraryPane) == 1", planner);
        Assert.Contains("Grid.GetColumnSpan(DetailPane) == 1", planner);
        Assert.Contains("anchor: AppHorizontalAnchor.Right", planner);
        Assert.Contains("anchor: AppHorizontalAnchor.Left", planner);
        Assert.Contains("anchor == AppHorizontalAnchor.Left ? 0 : (float)currentWidth", animation);
        Assert.Contains("CreateVector3KeyFrameAnimation()", animation);
        Assert.Contains("visual.StartAnimation(nameof(Visual.Scale), animation)", animation);
        Assert.Contains("private async Task<bool> OpenLibraryCourseDetailsAsync(", planner);
        Assert.Contains("ShowLibraryContextMenu(course, e.Position, e.Target);", planner);
        Assert.DoesNotContain("SelectLibraryCourseAsync", planner);
        Assert.Contains("x:Name=\"DetailContentHost\"", plannerXaml);
    }

    [Fact]
    public void DetailPaneMotionUsesFluentCompositionEasingInsteadOfImplicitLinearTransitions()
    {
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var paneMotionStart = animation.IndexOf(
            "public static long PreparePaneEntrance(",
            StringComparison.Ordinal);
        var paneMotionEnd = animation.IndexOf(
            "public static void ConfigureFlyout(",
            paneMotionStart,
            StringComparison.Ordinal);
        Assert.True(paneMotionStart >= 0 && paneMotionEnd > paneMotionStart);

        var paneMotion = animation[paneMotionStart..paneMotionEnd];
        Assert.Contains("CreateScopedBatch(CompositionBatchTypes.Animation)", animation);
        Assert.Contains("CreateCubicBezierEasingFunction", animation);
        Assert.Contains("CreateTimer()", animation);
        Assert.Contains("translationAnimation.Target = \"Translation.X\"", animation);
        Assert.Contains("pane.StartAnimation(translationAnimation)", animation);
        Assert.Contains("visual.StartAnimation(nameof(Visual.Opacity), opacityAnimation)", animation);
        Assert.Contains("PaneEntranceDuration", animation);
        Assert.Contains("PaneExitDuration", animation);
        Assert.Contains("SetIsTranslationEnabled(pane, true)", paneMotion);
        Assert.DoesNotContain("new Vector3Transition", paneMotion);
        Assert.DoesNotContain("new ScalarTransition", paneMotion);
        Assert.DoesNotContain("Task.Delay(FastDuration", paneMotion);
    }

    [Fact]
    public void PlannerEdgeOverlaysShareAFlatBorderlessSurface()
    {
        var material = Read("CoursePlanner", "Services", "AppMaterialLayer.cs");
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");

        Assert.Contains(
            "AppMaterialSurface.OverlayPane => new(\"AppMaterialOverlayPaneBrush\", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.Layer)",
            material);
        Assert.Equal(2, planner.Split("overlaySurface: AppMaterialSurface.OverlayPane", StringSplitOptions.None).Length - 1);
        Assert.Contains("ApplyPaneMaterial(pane, presentation == PanePresentation.Overlay, overlaySurface)", planner);
        Assert.Contains("AppMaterialLayer.SetElevation(pane, AppMaterialElevation.Layer);", planner);
        Assert.DoesNotContain("AppMaterialElevation.Flyout", planner);
        Assert.Contains("if (!usesThemeShadow)", material);
        Assert.Contains("Flat layer/card surfaces have no ThemeShadow", material);
    }

    [Fact]
    public void NumberBoxAlignmentSurvivesFocusAndTextEntry()
    {
        var assist = Read("CoursePlanner", "Controls", "NumberBoxAssist.cs");
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml");

        Assert.Contains("inputBox.TextAlignment = GetTextAlignment(numberBox);", assist);
        Assert.Contains("inputBox.HorizontalContentAlignment = HorizontalAlignment.Stretch;", assist);
        Assert.Contains("inputBox.VerticalContentAlignment = VerticalAlignment.Center;", assist);
        Assert.Contains("numberBox.GotFocus += NumberBox_GotFocus;", assist);
        Assert.Contains("FindDescendant<FrameworkElement>(inputBox, \"DeleteButton\")", assist);
        Assert.Contains("deleteButton.MinWidth = 0;", assist);
        Assert.Contains("deleteButton.MaxWidth = 0;", assist);
        Assert.Contains("deleteButton.Width = 0;", assist);
        Assert.Contains("deleteButton.IsHitTestVisible = false;", assist);
        Assert.Contains("DispatcherQueuePriority.High", assist);
        Assert.Contains("Padding=\"8,5,8,6\"", planner);
        Assert.DoesNotContain("<ControlTemplate", planner);
    }

    [Fact]
    public void BrandAndStatusColorsMeetWcagContrastAcrossCourseStates()
    {
        var lightCourseBackgrounds = new[]
        {
            "#E7F1ED", "#D6E7E0", "#DFF4E7", "#F8E2E2",
            "#F7EDCF", "#CBE8D5", "#EDCACA", "#E9DDAF"
        };
        var darkCourseBackgrounds = new[]
        {
            "#313D3A", "#3B4B47", "#204336", "#4D3136",
            "#4A4129", "#2A5946", "#653E45", "#615332"
        };

        AssertMinimumContrast(
            new[] { "#A4262C", "#724E00" },
            lightCourseBackgrounds,
            4.5);
        AssertMinimumContrast(
            new[] { "#FFBAB5", "#F4C76E", "#72DED0" },
            darkCourseBackgrounds,
            4.5);
        AssertMinimumContrast(new[] { "#00857A" }, new[] { "#FFFFFF", "#000000" }, 4.5);
    }

    [Fact]
    public void ThemeAndMotionPoliciesUseDesktopWindowMessageFallbacks()
    {
        var source = Read("CoursePlanner", "Services", "ThemeService.cs");
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("ActualThemeChanged", source);
        Assert.DoesNotContain("HighContrastChanged +=", source);
        Assert.Contains("WmSettingChange", mainWindow);
        Assert.Contains("WmThemeChanged", mainWindow);
        Assert.Contains("_services.Theme.RefreshTheme()", mainWindow);
        Assert.Contains("AppAnimationLayer.RefreshPolicy()", mainWindow);
    }

    [Fact]
    public void RegistrationOrderWindowAppliesThemeBeforeCreatingItsBackdrop()
    {
        var xaml = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");
        var code = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");

        Assert.DoesNotContain("<Window.SystemBackdrop>", xaml);
        Assert.Contains("SystemBackdrop = new MicaBackdrop", code);
        var applyTheme = code.IndexOf("ApplyTheme(_theme.RequestedTheme);", StringComparison.Ordinal);
        var createBackdrop = code.IndexOf("SystemBackdrop = new MicaBackdrop", StringComparison.Ordinal);
        Assert.True(applyTheme >= 0 && createBackdrop > applyTheme);
    }

    [Fact]
    public void RegistrationOrderWindowAnimatesButtonCloseBeforeBackdropTeardown()
    {
        var xaml = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");
        var code = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        var service = Read("CoursePlanner", "Services", "RegistrationOrderWindowService.cs");
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");

        Assert.Contains("AppWindow.Closing += AppWindow_Closing;", code);
        Assert.Contains("private void AppWindow_Closing(", code);
        Assert.Contains("sender.Hide();", code);
        Assert.Contains("SetWindowOwner(windowHandle, mainWindowHandle);", code);
        Assert.Contains("GwlHwndParent", code);
        Assert.Contains("SetWindowLongPtr(", code);
        Assert.Contains("SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);", code);
        Assert.Contains("_presenter.IsResizable = true;", code);
        Assert.Contains("x:Name=\"AnimatedContentRoot\"", xaml);
        Assert.Contains("internal Task CloseAnimatedAsync()", code);
        Assert.Contains("await AppAnimationLayer.PlayToolWindowExitThenAsync(", code);
        Assert.Contains("AnimatedContentRoot,", code);
        Assert.DoesNotContain("AnimateWindow(", code);
        Assert.Contains("public static async Task PlayToolWindowExitThenAsync(", animation);
        Assert.Contains("visual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);", animation);
        Assert.DoesNotContain("ToolWindowExitTravelDistance", animation);
        Assert.DoesNotContain("root.StartAnimation(translationAnimation);", animation);
        Assert.Contains("CreateScopedBatch(CompositionBatchTypes.Animation)", animation);
        Assert.Contains("private async void CloseButton_Click", code);
        Assert.Contains("await CloseAnimatedAsync();", code);
        Assert.Contains("public async Task ToggleAsync(string planId)", service);
        Assert.Contains("await window.CloseAnimatedAsync();", service);
        Assert.Equal(2, planner.Split("await registrationOrders.ToggleAsync(plan.PlanId);", StringSplitOptions.None).Length - 1);
        Assert.Contains("internal void HideAndClose()", code);
        Assert.Contains("AppWindow.Hide();", code);
        Assert.Contains("PrepareForClose();", code);
        Assert.Contains("_persistence.Flush(CurrentPlan(), PersistOrder);", code);
        Assert.Contains("_persistence.Close(CurrentPlan(), PersistOrder);", code);
        Assert.Contains("_lifetime.CompleteClose();", code);
        Assert.Contains("window?.HideAndClose();", service);
        Assert.Contains("AppWindow.Closing -= AppWindow_Closing;", code);
    }

    [Fact]
    public void RegistrationOrderWindowReprojectsRowsAfterDocumentRollback()
    {
        var code = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");

        Assert.Contains("_documents.Changed += Documents_Changed;", code);
        Assert.Contains("_documents.RolledBack += Documents_Changed;", code);
        Assert.Contains("_documents.Changed -= Documents_Changed;", code);
        Assert.Contains("_documents.RolledBack -= Documents_Changed;", code);

        var handlerStart = code.IndexOf(
            "private void Documents_Changed(object? sender, EventArgs e)",
            StringComparison.Ordinal);
        var handlerEnd = code.IndexOf(
            "private void Localization_LanguageChanged",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        var handler = code[handlerStart..handlerEnd];
        Assert.Contains("RunOnWindowThread", handler);
        Assert.Contains("var plan = CurrentPlan();", handler);
        Assert.Contains("if (plan is null)", handler);
        Assert.Contains("HideAndClose();", handler);
        Assert.Contains("_persistence.CanRetainPending(", handler);
        Assert.Contains("ReplaceRows(CurrentAnalysis(retainPendingOrder ? displayedOrder : null));", handler);
    }

    [Fact]
    public void RegistrationOrderSynchronousEnqueueFallbackDoesNotReenterRowsProjection()
    {
        var code = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        var queueStart = code.IndexOf("private void QueuePersist()", StringComparison.Ordinal);
        var queueEnd = code.IndexOf(
            "private void ExecuteQueuedPersist(",
            queueStart,
            StringComparison.Ordinal);

        Assert.True(queueStart >= 0 && queueEnd > queueStart);
        var queue = code[queueStart..queueEnd];
        var enqueue = queue.IndexOf("DispatcherQueue.TryEnqueue(", StringComparison.Ordinal);
        var deferredRefresh = queue.IndexOf(
            "ExecuteQueuedPersist(ticket.Value, refreshRows: true)",
            StringComparison.Ordinal);
        var synchronousFallback = queue.IndexOf(
            "ExecuteQueuedPersist(ticket.Value, refreshRows: false)",
            StringComparison.Ordinal);

        Assert.True(enqueue >= 0 && deferredRefresh > enqueue);
        Assert.True(synchronousFallback > deferredRefresh);
    }

    [Fact]
    public void RegistrationOrderToolWindowUsesHiddenMinimizeStateInsteadOfIconicToolWindow()
    {
        var window = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        var service = Read("CoursePlanner", "Services", "RegistrationOrderWindowService.cs");

        Assert.Contains("_presenter.IsMinimizable = false;", window);
        Assert.Contains("internal bool IsMinimized { get; private set; }", window);
        Assert.Contains("IsMinimized = true;", window);
        Assert.Contains("AppWindow.Hide();", window);
        Assert.DoesNotContain("MinimizeButton_Click(object sender, RoutedEventArgs e) => _presenter.Minimize()", window);
        Assert.Contains("if (window.IsMinimized)", service);
        Assert.Contains("window.BringToFront();", service);
    }

    [Fact]
    public void PlannerCommandsToggleLibraryAndRegistrationOrderSurfaces()
    {
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var service = Read("CoursePlanner", "Services", "RegistrationOrderWindowService.cs");

        Assert.Contains("private async void ToggleLibrary_Click", planner);
        Assert.Contains("await CloseLibraryAsync();", planner);
        Assert.Contains("ViewModel.IsLibraryOpen = true;", planner);
        Assert.Contains("public bool IsOpenFor(string planId)", service);
        Assert.Contains("public async Task ToggleAsync(string planId)", service);
        Assert.Contains("if (IsOpenFor(planId))", service);
        Assert.Contains("registrationOrders.IsOpenFor(plan.PlanId)", planner);
        Assert.Equal(2, planner.Split("await registrationOrders.ToggleAsync(plan.PlanId);", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RegistrationOrderWindowRemembersSessionBoundsAndAnimatesSizeReset()
    {
        var applicationServices = Read("CoursePlanner", "Services", "ApplicationServices.cs");
        var service = Read("CoursePlanner", "Services", "RegistrationOrderWindowService.cs");
        var state = Read("CoursePlanner", "Services", "ToolWindowPlacementState.cs");
        var xaml = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");
        var code = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");

        Assert.Contains("RegistrationOrderWindowPlacement = new ToolWindowPlacementState();", applicationServices);
        Assert.Contains("RegistrationOrderWindowPlacement", applicationServices);
        Assert.Contains("ToolWindowPlacementState placement", service);
        Assert.Contains("_placement", service);
        Assert.DoesNotContain("ApplicationData", state);
        Assert.DoesNotContain("LocalSettings", state);
        Assert.Contains("_placement.TryGet(out var remembered)", code);
        Assert.Contains("_placement.Remember(CurrentBounds())", code);
        Assert.Contains("ToolWindowPlacementState.FitWithinWorkArea", code);

        var resetButton = xaml.IndexOf("x:Name=\"ResetWindowSizeButton\"", StringComparison.Ordinal);
        var pinButton = xaml.IndexOf("x:Name=\"PinButton\"", StringComparison.Ordinal);
        Assert.True(resetButton >= 0 && pinButton > resetButton);
        Assert.Contains("AutomationProperties.AutomationId=\"RegistrationOrderResetSizeButton\"", xaml);
        Assert.Contains("IsEnabled=\"False\"", xaml);
        Assert.DoesNotContain("ResetWindowPosition", xaml);
        Assert.DoesNotContain("ResetWindowPosition", code);
        Assert.Contains("ResetWindowSizeButton_Click", code);
        Assert.Contains("WindowRoot.SizeChanged += WindowRoot_SizeChanged;", code);
        Assert.Contains("ResetWindowSizeButton.IsEnabled", code);
        Assert.Contains("AppAnimationLayer.PlayToolWindowSizeReflow(", code);
        Assert.Contains("public static void PlayToolWindowSizeReflow(", animation);
        Assert.Contains("CreateVector3KeyFrameAnimation()", animation);
        Assert.Contains("SystemUiSettings.AnimationsEnabled", animation);
    }

    [Fact]
    public void PlannerToolbarAndCustomTabRailFollowComparisonAndOverflowRules()
    {
        var plannerXaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var plannerCode = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var shellXaml = Read("CoursePlanner", "MainWindow.xaml");
        var shellCode = Read("CoursePlanner", "MainWindow.xaml.cs");
        var registrationOrderXaml = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml");

        Assert.True(
            plannerXaml.IndexOf("x:Name=\"RegistrationOrderButton\"", StringComparison.Ordinal) <
            plannerXaml.IndexOf("x:Name=\"CompareButton\"", StringComparison.Ordinal));
        Assert.True(
            plannerCode.IndexOf("_toolbar.AddCommand(RegistrationOrderButton", StringComparison.Ordinal) <
            plannerCode.IndexOf("_toolbar.AddCommand(CompareButton", StringComparison.Ordinal));
        Assert.Contains("CompareButton.IsEnabled = !busy && ViewModel.CanOpenSelectedComparison;", plannerCode);
        Assert.Contains("if (!ViewModel.CanOpenSelectedComparison)", plannerCode);

        Assert.Contains("x:Name=\"ShellPlanTabScrollViewer\"", shellXaml);
        Assert.Contains("x:Name=\"ShellPlanTabStripHost\"", shellXaml);
        Assert.Contains("ColumnDefinitions=\"Auto,*\"", shellXaml);
        Assert.Contains("ColumnDefinitions=\"Auto,Auto\"", shellXaml);
        Assert.Contains("Grid.Column=\"1\"", shellXaml);
        Assert.True(
            shellXaml.IndexOf("</ScrollViewer>", StringComparison.Ordinal) <
            shellXaml.IndexOf("x:Name=\"ShellAddPlanTabButton\"", StringComparison.Ordinal));
        Assert.Contains("PlanTabMinimumWidth", shellCode);
        Assert.Contains("PlanTabMaximumWidth", shellCode);
        Assert.Contains("UpdatePlanTabLayout", shellCode);
        Assert.Contains("ShellPlanTabStripHost.MaxWidth = availableWidth;", shellCode);
        Assert.Contains("var navigationInset = RootNavigation.CompactPaneLength;", shellCode);
        Assert.Contains("ShellPlanTabScrollViewer.MaxWidth = tabViewportWidth;", shellCode);
        Assert.Contains("CalculatePlanTabWidth(tabs.Count, tabViewportWidth)", shellCode);
        Assert.Contains("UpdatePlanTabCloseButton", shellCode);

        var resetButtonStart = registrationOrderXaml.IndexOf("x:Name=\"ResetWindowSizeButton\"", StringComparison.Ordinal);
        var resetButtonEnd = registrationOrderXaml.IndexOf("</Button>", resetButtonStart, StringComparison.Ordinal);
        var resetButton = registrationOrderXaml[resetButtonStart..resetButtonEnd];
        Assert.Contains("Background=\"Transparent\"", resetButton);
    }

    [Fact]
    public void PlanTabClosesCommitLayoutImmediatelyAndUseCompositionFlipReflow()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");
        var closeSequence = Read("CoursePlanner", "Services", "PlanTabCloseSequenceState.cs");

        Assert.Contains("PlanTabCloseCommitDelay", shell);
        Assert.Contains("var previousPositions = CapturePlanTabHorizontalPositions();", shell);
        Assert.Contains("ShellPlanTabItems.Children.Remove(sourceTab);", shell);
        Assert.Contains("UpdatePlanTabLayout();", shell);
        Assert.Contains("ShellPlanTabStripHost.UpdateLayout();", shell);
        Assert.Contains("PlayPlanTabReflow(previousPositions);", shell);
        Assert.Contains("AppAnimationLayer.PlayHorizontalReflow(element, previousX - currentX);", shell);
        Assert.DoesNotContain("_planTabFrozenWidth", shell);
        Assert.DoesNotContain("_planTabCloseBurstActive", shell);
        Assert.DoesNotContain("_planTabLayoutDeferred", shell);
        Assert.Contains("animation.Target = \"Translation.X\";", animation);
        Assert.Contains("this.StartingValue + layoutDelta", animation);
        Assert.Contains("target.StartAnimation(animation);", animation);
        Assert.Contains("FinishHorizontalReflowAfterDurationAsync", animation);
        Assert.Contains("RestartPlanTabCloseCommitTimer();", shell);
        Assert.Contains("ShellPlanTabs_PointerExited", shell);
        Assert.Contains("CommitPendingPlanTabCloses();", shell);
        Assert.Contains("await ClosePlanTabAsync(plan, tab);", shell);
        Assert.Contains("_planTabCloseCommitTimer.Tick += (_, _) => FlushDeferredPlanTabClosePersistence();", shell);
        Assert.DoesNotContain("_planTabCloseCommitTimer.Tick += (_, _) => CommitPendingPlanTabCloses();", shell);
        Assert.Contains("_planTabCloseSequence.BeginStep", shell);
        Assert.Contains("ApplyPlanTabCloseSequenceMargin();", shell);
        Assert.Contains("_planTabCloseSequence.IsActive", shell);
        Assert.Contains("? _planTabCloseSequence.TabWidth", shell);
        Assert.Contains("TrailingReserve += TabWidth;", closeSequence);
        Assert.Contains("PlanTabCloseLayoutMode", closeSequence);
        Assert.Contains("UpdateLayoutAfterClose", closeSequence);
        Assert.Contains("singleTabWidthTotal < tabViewportWidth", closeSequence);
        Assert.Contains("singleTabWidthTotal <= anchorDistanceFromLeft", closeSequence);
        Assert.Contains("CalculatePlanTabWidth(tabCount: 1, tabViewportWidth)", shell);
        Assert.Contains("if (closeLayoutMode == PlanTabCloseLayoutMode.MaximumWidth)", shell);
        Assert.Contains("ReleasePlanTabCloseSequenceLock();", shell);
        Assert.Contains("PlanTabCloseLayoutMode.FillLeft", closeSequence);
        Assert.Contains("IsRightToLeftHandoff", closeSequence);
        Assert.Contains("deferReflowForHandoff", closeSequence);
        Assert.Contains("AlignReplacementToAnchor", closeSequence);
        Assert.Contains("closeLayoutMode != PlanTabCloseLayoutMode.MaximumWidth", shell);
        Assert.Contains("stationaryReplacementTab", shell);
    }

    [Fact]
    public void PlanTabControlsAreNativeTitleBarPassthroughRegions()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("InputNonClientPointerSource.GetForWindowId(AppWindow.Id)", shell);
        Assert.Contains("GetPhysicalBounds(ShellPlanTabScrollViewer, scale)", shell);
        Assert.Contains("GetPhysicalBounds(ShellAddPlanTabButton, scale)", shell);
        Assert.Contains("SetRegionRects(NonClientRegionKind.Passthrough, passthroughRegions)", shell);
        Assert.Contains("ClearRegionRects(NonClientRegionKind.Passthrough)", shell);
        Assert.Contains("ShellPlanTabStripHost.SizeChanged += (_, _) => QueueTitleBarInteractiveRegionUpdate();", shell);
        Assert.Contains("ShellPlanTabs.Visibility != Visibility.Visible", shell);
        Assert.Contains("XamlRoot.RasterizationScale", shell);
    }

    [Fact]
    public void ContinuousCustomMotionRunsOnTheSystemCompositorWithoutFrameTimers()
    {
        var animation = Read("CoursePlanner", "Services", "AppAnimationLayer.cs");

        Assert.Contains("ElementCompositionPreview.GetElementVisual", animation);
        Assert.Contains("CreateScalarKeyFrameAnimation()", animation);
        Assert.Contains("CreateVector3KeyFrameAnimation()", animation);
        Assert.Contains("StartAnimation", animation);
        Assert.DoesNotContain("CompositionTarget.Rendering", animation);
        Assert.DoesNotContain("Thread.Sleep", animation);
        Assert.DoesNotContain("System.Timers.Timer", animation);
    }

    [Fact]
    public void ConsecutivePlanTabClosesUseIncrementalRemovalInsteadOfRebuildingEveryTab()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");
        var closeStart = shell.IndexOf("private async Task ClosePlanTabAsync", StringComparison.Ordinal);
        var closeEnd = shell.IndexOf("private async void ShellPlanTab_PointerReleased", closeStart, StringComparison.Ordinal);
        var closeMethod = shell[closeStart..closeEnd];

        var replacementPreflight = closeMethod.IndexOf("ValidateLastPlanTabReplacement(plan)", StringComparison.Ordinal);
        var incrementalRemove = closeMethod.IndexOf("RemovePlanTabIncrementally(liveSourceTab);", StringComparison.Ordinal);
        var persistedClose = closeMethod.IndexOf("_plannerViewModel.ClosePlanTab(plan, persist: false);", StringComparison.Ordinal);
        Assert.True(replacementPreflight >= 0 && replacementPreflight < incrementalRemove);
        Assert.True(incrementalRemove >= 0 && persistedClose > incrementalRemove);
        Assert.Contains("_plannerViewModel.ClosePlanTab(plan, persist: false);", closeMethod);
        Assert.Contains("_plannerViewModel.PersistPlanTabState();", shell);
        Assert.Contains("if (_planTabIncrementalCloseInProgress)", shell);
        Assert.Contains("TrySynchronizePlanTabsAfterIncrementalClose", shell);
        Assert.Contains("BuildPlanTabSignature()", shell);
        Assert.Contains("_lastTabSignature = BuildPlanTabSignature();", shell);
    }

    [Fact]
    public void LastPlanTabCloseOpensAReplacementWithoutContainerScaleAnimation()
    {
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("UpdatePlanTabCloseButton(replacementTab, pointerOver: true);", shell);
        Assert.DoesNotContain("AppAnimationLayer.SetProfile(tab, AppAnimationProfile.Interactive);", shell);

        var closeStart = shell.IndexOf("private async Task ClosePlanTabAsync", StringComparison.Ordinal);
        var closeEnd = shell.IndexOf("private async void ShellPlanTab_PointerReleased", closeStart, StringComparison.Ordinal);
        var closeMethod = shell[closeStart..closeEnd];
        var lastTabBranch = closeMethod.IndexOf("if (replacingLastPlanTab)", StringComparison.Ordinal);
        var clearPendingSave = closeMethod.IndexOf("_planTabClosePersistencePending = false;", StringComparison.Ordinal);
        var replaceAtomically = closeMethod.IndexOf("_plannerViewModel.TryReplaceLastPlanTab(", StringComparison.Ordinal);
        Assert.True(lastTabBranch >= 0 && clearPendingSave > lastTabBranch && replaceAtomically > clearPendingSave);
        Assert.Contains("_plannerViewModel.TryReplaceLastPlanTab(", closeMethod);
        Assert.DoesNotContain("if (_plannerViewModel.OpenPlans.Count == 0)", closeMethod);
    }

    [Fact]
    public void DisabledButtonsKeepNativeHoverWithoutLosingDisabledSemantics()
    {
        var layer = Read("CoursePlanner", "Services", "DisabledButtonHoverLayer.cs");
        var mainWindow = Read("CoursePlanner", "MainWindow.xaml.cs");
        var toolWindow = Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml.cs");

        Assert.Contains("List<ButtonBase> _buttonCache", layer);
        Assert.Contains("_buttonCacheDirty && Environment.TickCount64 - _lastButtonCacheRefresh >= 250", layer);
        Assert.Contains("_root.LayoutUpdated += Root_LayoutUpdated", layer);
        Assert.Contains("CollectButtons(_root)", layer);
        Assert.Contains("GetOpenPopupsForXamlRoot", layer);
        Assert.Contains("button.TransformToVisual(_root)", layer);
        Assert.Contains("QueueVisuals(previousVisual, _hoverVisual)", layer);
        Assert.Contains("DispatcherQueuePriority.Low", layer);
        Assert.Contains("TimeSpan.FromMilliseconds(50)", layer);
        Assert.Contains("ScreenToClient", layer);
        Assert.Contains("ContentPresenter? FindContentPresenter", layer);
        Assert.Contains("AppControlHoverBrush", layer);
        Assert.Contains("_presenter.BorderBrush = _borderBrush", layer);
        Assert.Contains("_presenter.Foreground = _foreground", layer);
        Assert.Contains("ClearValue(ContentPresenter.BackgroundProperty)", layer);
        Assert.Contains("ClearValue(ContentPresenter.BorderBrushProperty)", layer);
        Assert.Contains("ClearValue(ContentPresenter.ForegroundProperty)", layer);
        Assert.Contains("ToolTipService.GetToolTip(Button)", layer);
        Assert.Contains("_toolTip.PlacementTarget = Button", layer);
        Assert.Contains("_toolTip.IsOpen = true", layer);
        Assert.Contains("_toolTip.IsOpen = false", layer);
        Assert.DoesNotContain("IsEnabled = true", layer);
        Assert.DoesNotContain("Click +=", layer);
        Assert.Contains("DisabledButtonHoverLayer.Attach(this, ShellRoot);", mainWindow);
        Assert.Contains("DisabledButtonHoverLayer.Attach(this, WindowRoot);", toolWindow);
        Assert.Contains("DisabledButtonHoverLayer.Detach(WindowRoot);", toolWindow);
        Assert.Contains("PinFillIcon.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed", toolWindow);
        Assert.DoesNotContain("Symbol.UnPin", toolWindow);
        Assert.Contains("Glyph=\"&#xE840;\"", Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml"));
        Assert.Contains("Glyph=\"&#xE842;\"", Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml"));
        Assert.Contains("ToggleButtonBackgroundChecked\" ResourceKey=\"ControlFillColorTransparentBrush", Read("CoursePlanner", "Windows", "RegistrationOrderWindow.xaml"));
    }

    [Fact]
    public void CourseLibraryStatusIconsScaleNativeGlyphsInsteadOfClippingThem()
    {
        var tree = Read("CoursePlanner", "Controls", "CourseLibraryTree.cs");

        Assert.Contains("var statusIndicator = new Viewbox", tree);
        Assert.Contains("Stretch = Stretch.Uniform", tree);
        Assert.Contains("StretchDirection = StretchDirection.DownOnly", tree);
        Assert.Contains("Width = NativeStatusIconSize", tree);
        Assert.Contains("Height = NativeStatusIconSize", tree);
        Assert.DoesNotContain("var statusIndicator = new Border", tree);
    }

    [Fact]
    public void ComparisonAndShellNavigationExposeNonColorStateAndStayCentralized()
    {
        var planner = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");
        var plans = Read("CoursePlanner", "Pages", "PlansPage.xaml.cs");
        var tree = Read("CoursePlanner", "Controls", "CourseLibraryTree.cs");
        var shell = Read("CoursePlanner", "MainWindow.xaml.cs");

        Assert.Contains("DifferenceAdded", planner);
        Assert.Contains("DifferenceRemoved", planner);
        Assert.Contains("DifferenceModified", planner);
        Assert.Contains("SetHelpText(block, differenceLabel)", planner);
        Assert.Contains("CoursePlanStatusIndicator", tree);
        Assert.Contains("SymbolForStatus", tree);
        Assert.Contains("_services!.Navigation.RequestPlanner()", plans);
        Assert.DoesNotContain("Frame.Navigate", plans);
        Assert.Contains("_navigationRequestVersion", shell);
        Assert.Contains("isSelected && AppBrushes.IsHighContrast", shell);
        Assert.Contains("? new Thickness(2)", shell);
        Assert.DoesNotContain("new Thickness(0, 0, 0, 2)", shell);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(RepositoryPaths.FromRoot(segments));

    private static void AssertMinimumContrast(
        IEnumerable<string> foregrounds,
        IEnumerable<string> backgrounds,
        double minimum)
    {
        foreach (var foreground in foregrounds)
            foreach (var background in backgrounds)
            {
                var ratio = ContrastRatio(foreground, background);
                Assert.True(
                    ratio >= minimum,
                    $"{foreground} on {background} has contrast {ratio:0.00}:1; expected at least {minimum:0.0}:1.");
            }
    }

    private static double ContrastRatio(string first, string second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(string value)
    {
        var hex = value.TrimStart('#');
        var channels = new[] { 0, 2, 4 }
            .Select(index => Convert.ToInt32(hex.Substring(index, 2), 16) / 255d)
            .Select(channel => channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4))
            .ToArray();
        return (0.2126 * channels[0]) + (0.7152 * channels[1]) + (0.0722 * channels[2]);
    }
}

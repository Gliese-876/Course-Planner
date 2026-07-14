using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CoursePlanner.Core;
using CoursePlanner.Pages;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CoursePlanner;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly record struct PlanTabMetrics(decimal TotalCredits, int CourseCount, int ConflictSlots);
    private sealed record PlanTabPresentation(SelectionPlan Plan, PlanTabMetrics Metrics, string Title);

    private const double NavigationRailWidthAllowanceDip = 72;
    private const double MinimumWindowWidthDip = TwoPaneLayoutService.CompactBreakpoint + NavigationRailWidthAllowanceDip;
    private const double MinimumWindowHeightDip = 360;
    private const uint MinimumWindowSubclassId = 1;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmSettingChange = 0x001A;
    private const uint WmSysColorChange = 0x0015;
    private const uint WmThemeChanged = 0x031A;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcDestroy = 0x0082;
    private const uint SwpNoSize = 0x0001;
    private const double PlanTabHeight = 36;
    private const double PlanTabMinimumWidth = 72;
    private const double PlanTabMaximumWidth = 240;
    private const double PlanTabCloseButtonCollapseWidth = 144;
    private const double PlanTabIconSize = 18;
    private const double PlanTabIconSourceSize = 20;
    private const double PlanTabTitleFontSize = 13;
    private const double PlanTabCloseButtonSize = 26;
    private const double PlanTabCloseIconFontSize = 13;
    private static readonly TimeSpan PlanTabCloseCommitDelay = TimeSpan.FromMilliseconds(250);
    private readonly ApplicationServices _services;
    private readonly PlannerViewModel _plannerViewModel;
    private readonly SubclassProc _windowSubclassProc;
    private readonly DispatcherQueueTimer _planTabCloseCommitTimer;
    private readonly PlanTabCloseSequenceState _planTabCloseSequence = new();
    private readonly WindowCloseGuardState _windowCloseGuardState = new();
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly Dictionary<string, PlanTabMetrics> _planTabMetrics = new(StringComparer.Ordinal);
    private IntPtr _windowHandle;
    private bool _syncingTabs;
    private bool _syncingNavigation;
    private string _currentNavigationTag = "planner";
    private string _lastTabSignature = "";
    private string? _lastSelectedPlanId;
    private bool _planTabLayoutQueued;
    private bool _bringSelectedPlanTabIntoView;
    private bool _planTabIncrementalCloseInProgress;
    private bool _planTabClosePersistencePending;
    private bool _titleBarInteractiveRegionUpdateQueued;
    private bool _systemSettingsRefreshQueued;
    private Thickness _planTabCloseBaseItemsMargin;
    private long _navigationRequestVersion;

    public MainWindow(ApplicationServices services)
    {
        _services = services;
        _plannerViewModel = services.Planner;
        InitializeComponent();
        _planTabCloseCommitTimer = DispatcherQueue.CreateTimer();
        _planTabCloseCommitTimer.Interval = PlanTabCloseCommitDelay;
        _planTabCloseCommitTimer.IsRepeating = false;
        _planTabCloseCommitTimer.Tick += (_, _) => FlushDeferredPlanTabClosePersistence();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ShellTitleBar);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _services.Theme.ThemeChanged += Theme_ThemeChanged;
        _services.Theme.AttachWindow(this);
        _services.Localization.LanguageChanged += Localization_LanguageChanged;
        _services.BackgroundOperations.Changed += BackgroundOperations_Changed;
        _windowSubclassProc = WindowSubclassProc;
        InstallMinimumWindowSizeConstraint();
        AppTypography.Apply(RootNavigation);
        AppAnimationLayer.ConfigureFrame(RootFrame);
        DisabledButtonHoverLayer.Attach(this, ShellRoot);
        RootFrame.SizeChanged += (_, _) => UpdateShellChrome(_currentNavigationTag);
        ShellPlanTabs.Loaded += (_, _) => QueueTitleBarInteractiveRegionUpdate();
        ShellPlanTabs.SizeChanged += (_, _) =>
        {
            QueuePlanTabLayout();
            QueueTitleBarInteractiveRegionUpdate();
        };
        ShellPlanTabStripHost.SizeChanged += (_, _) => QueueTitleBarInteractiveRegionUpdate();
        ShellPlanTabs.PointerExited += ShellPlanTabs_PointerExited;
        ShellRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ShellRoot_KeyDown), true);
        ShellRoot.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(ShellRoot_KeyUp), true);
        Activated += MainWindow_Activated;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
        Closed += (_, _) =>
        {
            _planTabCloseCommitTimer.Stop();
            FlushDeferredPlanTabClosePersistence();
            _services.BackgroundOperations.Changed -= BackgroundOperations_Changed;
        };

        AppWindow.SetIcon("Assets/AppIcon.ico");

        ApplyText();
        _services.Documents.Changed += (_, _) =>
        {
            _planTabMetrics.Clear();
            ApplyText();
            BuildPlanTabs();
        };
        _services.Documents.RolledBack += (_, _) =>
        {
            _planTabMetrics.Clear();
            BuildPlanTabs();
        };
        _services.Navigation.SemestersRequested += (_, _) => NavigateToSemesters();
        _services.Navigation.PlannerRequested += (_, _) => NavigateToPlanner();
        _plannerViewModel.PropertyChanged += (_, _) => BuildPlanTabs();
        _syncingNavigation = true;
        RootNavigation.SelectedItem = PlannerItem;
        _syncingNavigation = false;
        NavigateToPage("planner");
        BuildPlanTabs();
        ApplyBackgroundOperationState();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= MainWindow_Closed;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var closeRequest = _windowCloseGuardState.InterceptClose();
        args.Cancel = closeRequest.Cancel;
        if (!closeRequest.StartResolution)
            return;

        try
        {
            if (_services.BackgroundOperations.IsBusy)
            {
                await ShowBackgroundOperationCloseBlockedAsync();
                _windowCloseGuardState.RejectResolution();
                return;
            }

            if (!await GuardCurrentPageAsync())
            {
                _windowCloseGuardState.RejectResolution();
                return;
            }

            if (_services.BackgroundOperations.IsBusy)
            {
                await ShowBackgroundOperationCloseBlockedAsync();
                _windowCloseGuardState.RejectResolution();
                return;
            }

            // The close timer and deactivation are not guaranteed to run before
            // an immediate native close. Flush only after every close guard has
            // approved, but before releasing the one final Close() path.
            CommitPendingPlanTabCloses();
            if (_windowCloseGuardState.ApproveResolution())
                Close();
        }
        catch
        {
            _windowCloseGuardState.RejectResolution();
            throw;
        }
    }

    private async Task ShowBackgroundOperationCloseBlockedAsync()
    {
        var operation = _services.BackgroundOperations.Message;
        if (string.IsNullOrWhiteSpace(operation))
            operation = _services.Localization.Localizer["BackgroundOperation"];

        await ShowMessageAsync(
            _services.Localization.Localizer["CloseBlockedByBackgroundOperationTitle"],
            string.Format(
                _services.Localization.Localizer["CloseBlockedByBackgroundOperationMessageFormat"],
                operation));
    }

    public bool TryShowRuntimeOperationError()
    {
        try
        {
            if (!ShellRoot.IsLoaded || ShellRoot.XamlRoot is null)
                return false;

            RuntimeOperationErrorBar.Title = _services.Localization.Localizer["RuntimeOperationErrorTitle"];
            RuntimeOperationErrorBar.Message = _services.Localization.Localizer["RuntimeOperationErrorMessage"];
            RuntimeOperationErrorBar.Severity = InfoBarSeverity.Error;
            UpdateRuntimeOperationRecoveryAction();
            RuntimeOperationErrorBar.IsOpen = true;
            return true;
        }
        catch (Exception presentationException) when (!RuntimeOperationExceptionPolicy.IsFatal(presentationException))
        {
            return false;
        }
    }

    private void RuntimeOperationRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequiresRuntimeOperationRecovery)
        {
            UpdateRuntimeOperationRecoveryAction();
            return;
        }

        RuntimeOperationRecoveryButton.IsEnabled = false;
        try
        {
            _services.Documents.ReloadFromRepository();
        }
        catch (Exception exception) when (IsRecoverableDocumentRecoveryFailure(exception))
        {
            RuntimeOperationErrorBar.Title = _services.Localization.Localizer["RuntimeOperationErrorTitle"];
            RuntimeOperationErrorBar.Message = exception.Message;
            RuntimeOperationErrorBar.Severity = InfoBarSeverity.Error;
            RuntimeOperationErrorBar.IsOpen = true;
            UpdateRuntimeOperationRecoveryAction();
            return;
        }

        _services.Localization.RefreshLanguage();
        _services.Theme.RefreshTheme();
        ApplyText();
        RuntimeOperationErrorBar.IsOpen = false;
        UpdateRuntimeOperationRecoveryAction();
    }

    private bool RequiresRuntimeOperationRecovery =>
        _services.Documents.IsStorageConsistencyUnknown ||
        _services.Documents.IsSessionConsistencyUnknown;

    private void UpdateRuntimeOperationRecoveryAction()
    {
        var requiresRecovery = RequiresRuntimeOperationRecovery;
        var retryText = _services.Localization.Localizer["Retry"];
        RuntimeOperationRecoveryButton.Content = retryText;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RuntimeOperationRecoveryButton,
            retryText);
        RuntimeOperationRecoveryButton.Visibility = requiresRecovery
            ? Visibility.Visible
            : Visibility.Collapsed;
        RuntimeOperationRecoveryButton.IsEnabled = requiresRecovery;
        RuntimeOperationErrorBar.IsClosable = !requiresRecovery;
    }

    private static bool IsRecoverableDocumentRecoveryFailure(Exception exception) =>
        !RuntimeOperationExceptionPolicy.IsFatal(exception) &&
        (RuntimeOperationExceptionPolicy.IsRecoverable(exception) ||
         exception is DocumentRestoreCompensationException or
                      DocumentSessionConsistencyException or
                      DocumentSessionRollbackException);

    public void ActivateFromRedirectedLaunch()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void BackgroundOperations_Changed(object? sender, EventArgs e) =>
        ApplyBackgroundOperationState();

    private void ApplyBackgroundOperationState()
    {
        var isInteractive = !_services.BackgroundOperations.IsBusy;
        RootFrame.IsEnabled = isInteractive;
        ShellPlanTabs.IsHitTestVisible = isInteractive;
        ShellAddPlanTabButton.IsEnabled = isInteractive;
        PlannerItem.IsEnabled = isInteractive;
        SemestersItem.IsEnabled = isInteractive;
        LibraryItem.IsEnabled = isInteractive;
        LabelsItem.IsEnabled = isInteractive;
        PlansItem.IsEnabled = isInteractive;
        SettingsItem.IsEnabled = isInteractive;
    }

    private void InstallMinimumWindowSizeConstraint()
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_windowHandle == IntPtr.Zero)
            return;

        SetWindowSubclass(_windowHandle, _windowSubclassProc, new UIntPtr(MinimumWindowSubclassId), UIntPtr.Zero);
        Closed += (_, _) => RemoveMinimumWindowSizeConstraint();
    }

    private void RemoveMinimumWindowSizeConstraint()
    {
        if (_windowHandle == IntPtr.Zero)
            return;

        RemoveWindowSubclass(_windowHandle, _windowSubclassProc, new UIntPtr(MinimumWindowSubclassId));
        _windowHandle = IntPtr.Zero;
    }

    private IntPtr WindowSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            minMaxInfo.MinTrackSize.X = DipToWindowPixels(hwnd, MinimumWindowWidthDip);
            minMaxInfo.MinTrackSize.Y = DipToWindowPixels(hwnd, MinimumWindowHeightDip);
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmWindowPosChanging)
        {
            var windowPos = Marshal.PtrToStructure<WindowPos>(lParam);
            if ((windowPos.Flags & SwpNoSize) == 0)
            {
                var minimumWidth = DipToWindowPixels(hwnd, MinimumWindowWidthDip);
                var minimumHeight = DipToWindowPixels(hwnd, MinimumWindowHeightDip);
                windowPos.Width = Math.Max(windowPos.Width, minimumWidth);
                windowPos.Height = Math.Max(windowPos.Height, minimumHeight);
                Marshal.StructureToPtr(windowPos, lParam, false);
            }
        }

        if (message is WmSettingChange or WmSysColorChange or WmThemeChanged)
            QueueSystemSettingsRefresh();

        if (message == WmNcDestroy)
            RemoveMinimumWindowSizeConstraint();

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void QueueSystemSettingsRefresh()
    {
        if (_systemSettingsRefreshQueued)
            return;

        _systemSettingsRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _systemSettingsRefreshQueued = false;
                _services.Theme.RefreshTheme();
                AppAnimationLayer.RefreshPolicy();
            }))
        {
            _systemSettingsRefreshQueued = false;
        }
    }

    private static int DipToWindowPixels(IntPtr hwnd, double value)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        return (int)Math.Ceiling(value * dpi / 96.0);
    }

    private void Theme_ThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        ConfigureNativeTitleBar(e.ResolvedTheme);
        _lastTabSignature = "";
        BuildPlanTabs();
    }

    private void ConfigureNativeTitleBar(ResolvedThemeMode resolvedTheme)
    {
        var foregroundFallback = resolvedTheme == ResolvedThemeMode.Dark
            ? ColorHelper.FromArgb(0xFF, 0xF5, 0xF7, 0xF6)
            : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        var secondaryFallback = resolvedTheme == ResolvedThemeMode.Dark
            ? ColorHelper.FromArgb(0xFF, 0xC5, 0xCE, 0xCB)
            : ColorHelper.FromArgb(0xFF, 0x57, 0x5F, 0x5C);
        var hoverFallback = resolvedTheme == ResolvedThemeMode.Dark
            ? ColorHelper.FromArgb(0x19, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0, 0, 0);
        var pressedFallback = resolvedTheme == ResolvedThemeMode.Dark
            ? ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x20, 0, 0, 0);
        var backgroundFallback = resolvedTheme == ResolvedThemeMode.Dark
            ? ColorHelper.FromArgb(0xE8, 0x1B, 0x1F, 0x1E)
            : ColorHelper.FromArgb(0xE8, 0xF1, 0xF4, 0xF3);
        var background = AppMaterialLayer.Color(AppMaterialSurface.Chrome, resolvedTheme, backgroundFallback);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = AppMaterialLayer.Color(AppColorRole.TextPrimary, resolvedTheme, foregroundFallback);
        titleBar.ButtonInactiveForegroundColor = AppMaterialLayer.Color(AppColorRole.TextSecondary, resolvedTheme, secondaryFallback);
        titleBar.ButtonHoverBackgroundColor = AppMaterialLayer.Color(AppColorRole.TitleBarHover, resolvedTheme, hoverFallback);
        titleBar.ButtonHoverForegroundColor = AppMaterialLayer.Color(AppColorRole.InteractiveText, resolvedTheme, foregroundFallback);
        titleBar.ButtonPressedBackgroundColor = AppMaterialLayer.Color(AppColorRole.TitleBarPressed, resolvedTheme, pressedFallback);
        titleBar.ButtonPressedForegroundColor = AppMaterialLayer.Color(AppColorRole.InteractiveText, resolvedTheme, foregroundFallback);
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        ApplyText();
        _lastTabSignature = "";
        BuildPlanTabs();
    }

    private void ApplyText()
    {
        PlannerItem.Content = _services.Localization.Localizer["Planner"];
        SemestersItem.Content = _services.Localization.Localizer["Semesters"];
        LibraryItem.Content = _services.Localization.Localizer["CourseLibrary"];
        LabelsItem.Content = _services.Localization.Localizer["LabelManagement"];
        PlansItem.Content = _services.Localization.Localizer["Plans"];
        SettingsItem.Content = _services.Localization.Localizer["Settings"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlannerItem, _services.Localization.Localizer["Planner"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SemestersItem, _services.Localization.Localizer["Semesters"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LibraryItem, _services.Localization.Localizer["CourseLibrary"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LabelsItem, _services.Localization.Localizer["LabelManagement"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlansItem, _services.Localization.Localizer["Plans"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsItem, _services.Localization.Localizer["Settings"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShellAddPlanTabButton, _services.Localization.Localizer["NewPlan"]);
        ToolTipService.SetToolTip(ShellAddPlanTabButton, _services.Localization.Localizer["NewPlan"]);
        Title = _services.Localization.Localizer["AppTitle"];
        UpdateRuntimeOperationRecoveryAction();
        UpdateShellChrome(_currentNavigationTag);
    }

    private async void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNavigation)
            return;
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var requestVersion = ++_navigationRequestVersion;
        var tag = item.Tag?.ToString() ?? "planner";
        if (string.Equals(_currentNavigationTag, tag, StringComparison.Ordinal))
            return;

        var canLeave = await GuardCurrentPageAsync();
        if (requestVersion != _navigationRequestVersion)
            return;
        if (canLeave)
        {
            NavigateToPage(tag);
            return;
        }

        _syncingNavigation = true;
        RootNavigation.SelectedItem = NavigationItemForTag(_currentNavigationTag);
        _syncingNavigation = false;
    }

    private NavigationViewItem NavigationItemForTag(string? tag) => tag switch
    {
        "settings" => SettingsItem,
        "semesters" => SemestersItem,
        "library" => LibraryItem,
        "labels" => LabelsItem,
        "plans" => PlansItem,
        _ => PlannerItem
    };

    public async void NavigateToSemesters() =>
        await NavigateToRequestedPageAsync("semesters");

    public async void NavigateToPlanner() =>
        await NavigateToRequestedPageAsync("planner");

    private async Task NavigateToRequestedPageAsync(string navigationTag)
    {
        var requestVersion = ++_navigationRequestVersion;
        if (string.Equals(_currentNavigationTag, navigationTag, StringComparison.Ordinal))
            return;

        if (!await GuardCurrentPageAsync())
            return;

        if (requestVersion != _navigationRequestVersion)
            return;

        _syncingNavigation = true;
        RootNavigation.SelectedItem = NavigationItemForTag(navigationTag);
        _syncingNavigation = false;
        NavigateToPage(navigationTag);
    }

    private void NavigateToPage(string navigationTag)
    {
        _currentNavigationTag = navigationTag;
        UpdatePlanTabsVisibility(navigationTag);

        var pageType = navigationTag switch
        {
            "settings" => typeof(SettingsPage),
            "semesters" => typeof(SemestersPage),
            "library" => typeof(CourseLibraryPage),
            "labels" => typeof(LabelsPage),
            "plans" => typeof(PlansPage),
            _ => typeof(PlannerPage)
        };

        AppAnimationLayer.Navigate(RootFrame, pageType, _services);
    }

    private void UpdatePlanTabsVisibility(string? navigationTag)
    {
        UpdateShellChrome(navigationTag);
    }

    private void UpdateShellChrome(string? navigationTag)
    {
        var tag = navigationTag ?? _currentNavigationTag;
        var isPlanner = tag == "planner";
        ShellPlanTabs.Visibility = isPlanner ? Visibility.Visible : Visibility.Collapsed;
        ShellAppBrand.Visibility = isPlanner ? Visibility.Collapsed : Visibility.Visible;
        ShellAppTitleText.Text = isPlanner ? "" : ShellTitleForNavigation(tag);
        QueueTitleBarInteractiveRegionUpdate();
    }

    private void QueueTitleBarInteractiveRegionUpdate()
    {
        if (_titleBarInteractiveRegionUpdateQueued)
            return;

        _titleBarInteractiveRegionUpdateQueued = true;
        if (DispatcherQueue.TryEnqueue(() =>
            {
                _titleBarInteractiveRegionUpdateQueued = false;
                UpdateTitleBarInteractiveRegions();
            }))
        {
            return;
        }

        _titleBarInteractiveRegionUpdateQueued = false;
    }

    private void UpdateTitleBarInteractiveRegions()
    {
        if (!ExtendsContentIntoTitleBar ||
            ShellPlanTabs.Visibility != Visibility.Visible ||
            ShellPlanTabs.XamlRoot is null)
        {
            _nonClientPointerSource.ClearRegionRects(NonClientRegionKind.Passthrough);
            return;
        }

        var scale = ShellPlanTabs.XamlRoot.RasterizationScale;
        var passthroughRegions = new[]
            {
                GetPhysicalBounds(ShellPlanTabScrollViewer, scale),
                GetPhysicalBounds(ShellAddPlanTabButton, scale)
            }
            .Where(region => region.Width > 0 && region.Height > 0)
            .ToArray();
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRegions);
    }

    private static Windows.Graphics.RectInt32 GetPhysicalBounds(FrameworkElement element, double scale)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return default;

        var transform = element.TransformToVisual(null);
        var bounds = transform.TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new Windows.Graphics.RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    private string ShellTitleForNavigation(string? navigationTag)
    {
        if (!UsesCompactPageTitle())
            return _services.Localization.Localizer["AppTitle"];

        return navigationTag switch
        {
            "settings" => _services.Localization.Localizer["Settings"],
            "semesters" => _services.Localization.Localizer["Semesters"],
            "library" => _services.Localization.Localizer["CourseLibrary"],
            "labels" => _services.Localization.Localizer["LabelManagement"],
            "plans" => _services.Localization.Localizer["Plans"],
            _ => _services.Localization.Localizer["AppTitle"]
        };
    }

    private bool UsesCompactPageTitle()
    {
        var width = RootFrame.ActualWidth > 0
            ? RootFrame.ActualWidth
            : ShellRoot.ActualWidth;
        return width > 0 && width < TwoPaneLayoutService.CompactBreakpoint;
    }

    private void BuildPlanTabs()
    {
        var selectedPlanId = _plannerViewModel.CurrentPlan?.PlanId;
        if (_planTabIncrementalCloseInProgress)
            return;

        var presentations = BuildPlanTabPresentations();
        var tabSignature = BuildPlanTabSignature(presentations);
        if (string.Equals(tabSignature, _lastTabSignature, StringComparison.Ordinal))
        {
            SyncSelectedPlanTab(selectedPlanId);
            return;
        }

        EndPlanTabCloseSequence(animate: false);

        _syncingTabs = true;
        try
        {
            ShellPlanTabItems.Children.Clear();
            for (var index = 0; index < presentations.Count; index++)
            {
                var presentation = presentations[index];
                var plan = presentation.Plan;
                var selectedForComparison = _plannerViewModel.IsPlanSelectedForComparison(plan);
                var item = CreatePlanTabItem(
                    plan,
                    presentation.Title,
                    selectedForComparison,
                    plan.PlanId == selectedPlanId);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, PlanTabAutomationId(plan));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, presentation.Title);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetPositionInSet(item, index + 1);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetSizeOfSet(item, _plannerViewModel.OpenPlans.Count);
                item.ContextFlyout = CreatePlanTabMenu(plan);
                item.PointerEntered += ShellPlanTab_PointerEntered;
                item.PointerExited += ShellPlanTab_PointerExited;
                item.PointerReleased += ShellPlanTab_PointerReleased;
                item.Click += ShellPlanTab_Click;
                ShellPlanTabItems.Children.Add(item);
            }
            _lastTabSignature = tabSignature;
            QueuePlanTabLayout(bringSelectedIntoView: true);
        }
        finally
        {
            _syncingTabs = false;
        }
    }

    private List<PlanTabPresentation> BuildPlanTabPresentations()
    {
        var openIds = _plannerViewModel.OpenPlans
            .Select(plan => plan.PlanId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stalePlanId in _planTabMetrics.Keys.Where(id => !openIds.Contains(id)).ToArray())
            _planTabMetrics.Remove(stalePlanId);

        return _plannerViewModel.OpenPlans
            .Select(plan =>
            {
                var metrics = GetPlanTabMetrics(plan);
                return new PlanTabPresentation(plan, metrics, PlanTabTitle(plan, metrics));
            })
            .ToList();
    }

    private PlanTabMetrics GetPlanTabMetrics(SelectionPlan plan)
    {
        if (_planTabMetrics.TryGetValue(plan.PlanId, out var cached))
            return cached;

        var library = _services.Documents.Document.CourseLibrary;
        var courses = PlanCourseResolver.Courses(plan, library).ToList();
        var semester = _services.Documents.Document.Semesters.FirstOrDefault(candidate =>
            string.Equals(candidate.SemesterId, plan.SemesterId, StringComparison.Ordinal));
        var metrics = new PlanTabMetrics(
            SelectionPlanMetrics.TotalCredits(courses),
            SelectionPlanMetrics.CourseCount(plan),
            semester is null ? 0 : TimetableConflictService.CountConflictSlots(courses, semester));
        _planTabMetrics[plan.PlanId] = metrics;
        return metrics;
    }

    private string BuildPlanTabSignature() =>
        BuildPlanTabSignature(BuildPlanTabPresentations());

    private string BuildPlanTabSignature(IReadOnlyList<PlanTabPresentation> presentations)
    {
        var signature = new StringBuilder();
        foreach (var presentation in presentations)
        {
            AppendSignatureValue(signature, presentation.Plan.PlanId);
            AppendSignatureValue(signature, presentation.Plan.PlanName);
            AppendSignatureValue(signature, presentation.Title);
            AppendSignatureValue(signature, presentation.Metrics.TotalCredits.ToString(CultureInfo.InvariantCulture));
            signature.Append(presentation.Metrics.CourseCount).Append(';');
            signature.Append(presentation.Metrics.ConflictSlots).Append(';');
        }
        signature.Append('|');
        foreach (var selectedPlanId in _plannerViewModel.SelectedComparisonPlanIds)
            AppendSignatureValue(signature, selectedPlanId);
        return signature.ToString();
    }

    private static void AppendSignatureValue(StringBuilder signature, string value) =>
        signature.Append(value.Length).Append(':').Append(value).Append(';');

    private Button CreatePlanTabItem(
        SelectionPlan plan,
        string planTitle,
        bool selectedForComparison,
        bool isSelected)
    {
        var tab = new Button
        {
            Height = PlanTabHeight,
            MinWidth = PlanTabMinimumWidth,
            MaxWidth = PlanTabMaximumWidth,
            Padding = new Thickness(12, 0, 6, 0),
            Tag = plan,
            Background = AppBrushes.Transparent(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsTabStop = true
        };
        var content = new Grid
        {
            ColumnSpacing = 8
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = CreatePlanTabIcon(selectedForComparison ? Symbol.Accept : Symbol.Document);
        content.Children.Add(icon);

        var title = new TextBlock
        {
            Text = planTitle,
            Style = AppTypography.TextStyle(AppTextRole.Body),
            FontSize = PlanTabTitleFontSize,
            LineHeight = AppTypography.LineHeight(AppTextRole.Body, PlanTabTitleFontSize),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextLineBounds = TextLineBounds.Full,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1
        };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);

        var closeButton = new Button
        {
            Width = PlanTabCloseButtonSize,
            Height = PlanTabCloseButtonSize,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = AppBrushes.Transparent(),
            BorderThickness = new Thickness(0),
            Content = new FontIcon
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = PlanTabCloseIconFontSize,
                Glyph = "\uE711"
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(closeButton, PlanTabCloseAutomationId(plan));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            closeButton,
            $"{_services.Localization.Localizer["ClosePlanTab"]} {plan.PlanName}");
        ToolTipService.SetToolTip(closeButton, _services.Localization.Localizer["ClosePlanTab"]);
        closeButton.PointerPressed += (_, e) => e.Handled = true;
        closeButton.PointerReleased += (_, e) => e.Handled = true;
        closeButton.Click += async (_, _) => await ClosePlanTabAsync(plan, tab);
        Grid.SetColumn(closeButton, 2);
        content.Children.Add(closeButton);

        tab.Content = content;
        ApplyPlanTabVisual(tab, isSelected);
        return tab;
    }

    private static string PlanTabAutomationId(SelectionPlan plan) =>
        $"ShellPlanTab_{plan.PlanId}";

    private static string PlanTabCloseAutomationId(SelectionPlan plan) =>
        $"ShellPlanTabClose_{plan.PlanId}";

    private void ShellPlanTab_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button tab)
        {
            ApplyPlanTabHoverVisual(tab);
            UpdatePlanTabCloseButton(tab, pointerOver: true);
        }
    }

    private void ShellPlanTab_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button tab)
        {
            ApplyPlanTabVisual(tab, IsCurrentPlanTab(tab));
            UpdatePlanTabCloseButton(tab, pointerOver: false);
        }
    }


    private static Viewbox CreatePlanTabIcon(Symbol symbol) =>
        new()
        {
            Width = PlanTabIconSize,
            Height = PlanTabIconSize,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new SymbolIcon
            {
                Symbol = symbol,
                Width = PlanTabIconSourceSize,
                Height = PlanTabIconSourceSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    private string PlanTabTitle(SelectionPlan plan, PlanTabMetrics metrics)
    {
        var title = $"{plan.PlanName}  {metrics.TotalCredits:0.#} {_services.Localization.Localizer["CreditsShort"]}";
        return metrics.ConflictSlots > 0
            ? $"{title}  {string.Format(_services.Localization.Localizer["PlanCreditsIncludeConflicts"], metrics.ConflictSlots)}"
            : title;
    }

    private void SyncSelectedPlanTab(string? selectedPlanId)
    {
        var selectionChanged = !string.Equals(_lastSelectedPlanId, selectedPlanId, StringComparison.Ordinal);
        _syncingTabs = true;
        try
        {
            foreach (var item in ShellPlanTabItems.Children.OfType<Button>())
            {
                var isSelected = item.Tag is SelectionPlan plan &&
                                 !string.IsNullOrWhiteSpace(selectedPlanId) &&
                                 string.Equals(plan.PlanId, selectedPlanId, StringComparison.Ordinal);
                ApplyPlanTabVisual(item, isSelected);
                UpdatePlanTabCloseButton(item, pointerOver: false);
            }
        }
        finally
        {
            _syncingTabs = false;
        }

        _lastSelectedPlanId = selectedPlanId;
        if (selectionChanged)
            QueuePlanTabLayout(bringSelectedIntoView: true);
    }

    private void QueuePlanTabLayout(bool bringSelectedIntoView = false)
    {
        _bringSelectedPlanTabIntoView |= bringSelectedIntoView;
        if (_planTabLayoutQueued)
            return;

        _planTabLayoutQueued = true;
        if (DispatcherQueue.TryEnqueue(() =>
            {
                _planTabLayoutQueued = false;
                var bringSelected = _bringSelectedPlanTabIntoView;
                _bringSelectedPlanTabIntoView = false;
                UpdatePlanTabLayout();
                QueueTitleBarInteractiveRegionUpdate();
                if (bringSelected)
                    BringSelectedPlanTabIntoView();
            }))
        {
            return;
        }

        _planTabLayoutQueued = false;
        _bringSelectedPlanTabIntoView = false;
    }

    private void RestartPlanTabCloseCommitTimer()
    {
        _planTabCloseCommitTimer.Stop();
        _planTabCloseCommitTimer.Interval = PlanTabCloseCommitDelay;
        _planTabCloseCommitTimer.Start();
    }

    private void CommitPendingPlanTabCloses()
    {
        _planTabCloseCommitTimer.Stop();
        EndPlanTabCloseSequence();
        FlushDeferredPlanTabClosePersistence();
    }

    private void ShellPlanTabs_PointerExited(object sender, PointerRoutedEventArgs e) =>
        CommitPendingPlanTabCloses();

    private bool TrySynchronizePlanTabsAfterIncrementalClose(string? selectedPlanId)
    {
        var tabs = ShellPlanTabItems.Children.OfType<Button>().ToList();
        if (tabs.Count != _plannerViewModel.OpenPlans.Count)
            return false;

        for (var index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].Tag is not SelectionPlan tabPlan ||
                !string.Equals(tabPlan.PlanId, _plannerViewModel.OpenPlans[index].PlanId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            var plan = (SelectionPlan)tab.Tag;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tab, PlanTabAutomationId(plan));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetPositionInSet(tab, index + 1);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetSizeOfSet(tab, tabs.Count);
            ApplyPlanTabVisual(
                tab,
                !string.IsNullOrWhiteSpace(selectedPlanId) &&
                string.Equals(plan.PlanId, selectedPlanId, StringComparison.Ordinal));
        }

        _lastSelectedPlanId = selectedPlanId;
        _lastTabSignature = BuildPlanTabSignature();
        return true;
    }

    private void RemovePlanTabIncrementally(Button sourceTab)
    {
        var tabs = ShellPlanTabItems.Children.OfType<Button>().ToList();
        var sourceIndex = tabs.IndexOf(sourceTab);
        if (sourceIndex < 0)
            return;

        if (_planTabCloseSequence.IsActive &&
            sourceIndex != _planTabCloseSequence.ExpectedSourceIndex(tabs.Count))
        {
            EndPlanTabCloseSequence();
            tabs = ShellPlanTabItems.Children.OfType<Button>().ToList();
            sourceIndex = tabs.IndexOf(sourceTab);
            if (sourceIndex < 0)
                return;
        }

        UpdatePlanTabCloseButton(sourceTab, pointerOver: true);
        ShellPlanTabStripHost.UpdateLayout();
        var tabWidth = sourceTab.ActualWidth > 0 ? sourceTab.ActualWidth : sourceTab.Width;
        tabWidth = Math.Clamp(tabWidth, PlanTabMinimumWidth, PlanTabMaximumWidth);
        var anchorX = PlanTabCloseButtonHorizontalPosition(sourceTab);
        if (!double.IsFinite(anchorX))
            anchorX = PlanTabHorizontalPosition(sourceTab) + tabWidth - (PlanTabCloseButtonSize / 2);
        var closeTrailingInset = Math.Max(
            0,
            PlanTabHorizontalPosition(sourceTab) + tabWidth - anchorX);

        if (!_planTabCloseSequence.IsActive)
            _planTabCloseBaseItemsMargin = ShellPlanTabItems.Margin;

        var closeStep = _planTabCloseSequence.BeginStep(
            sourceIndex,
            tabs.Count,
            tabWidth,
            anchorX);
        var previousPositions = CapturePlanTabHorizontalPositions();
        ApplyPlanTabCloseSequenceMargin();
        ShellPlanTabStripHost.UpdateLayout();
        sourceTab.PointerEntered -= ShellPlanTab_PointerEntered;
        sourceTab.PointerExited -= ShellPlanTab_PointerExited;
        sourceTab.PointerReleased -= ShellPlanTab_PointerReleased;
        sourceTab.Click -= ShellPlanTab_Click;
        ShellPlanTabItems.Children.Remove(sourceTab);
        var closeLayoutMode = PlanTabCloseLayoutMode.Frozen;
        if (ShellPlanTabItems.Children.Count > 0 &&
            TryGetPlanTabLayoutMetrics(out _, out var tabViewportWidth))
        {
            var anchorDistanceFromLeft =
                _planTabCloseSequence.AnchorX - PlanTabHorizontalPosition(ShellPlanTabScrollViewer);
            var singleTabWidth = CalculatePlanTabWidth(tabCount: 1, tabViewportWidth);
            closeLayoutMode = _planTabCloseSequence.UpdateLayoutAfterClose(
                    sourceWasRightmost: sourceIndex == tabs.Count - 1,
                    remainingTabCount: ShellPlanTabItems.Children.Count,
                    singleTabWidth,
                    tabViewportWidth,
                    anchorDistanceFromLeft,
                    closeTrailingInset,
                    deferReflowForHandoff: closeStep.IsRightToLeftHandoff);
            if (closeLayoutMode == PlanTabCloseLayoutMode.MaximumWidth)
            {
                ReleasePlanTabCloseSequenceLock();
            }
            else if (closeLayoutMode != PlanTabCloseLayoutMode.Frozen)
            {
                ApplyPlanTabCloseSequenceMargin();
            }
        }
        UpdatePlanTabLayout();
        ShellPlanTabStripHost.UpdateLayout();
        Button? stationaryReplacementTab = null;
        if (ShellPlanTabItems.Children.Count > 0)
        {
            var replacementIndex = Math.Clamp(sourceIndex, 0, ShellPlanTabItems.Children.Count - 1);
            if (ShellPlanTabItems.Children[replacementIndex] is Button replacementTab)
            {
                UpdatePlanTabCloseButton(replacementTab, pointerOver: true);
                ShellPlanTabStripHost.UpdateLayout();
                if (closeLayoutMode != PlanTabCloseLayoutMode.MaximumWidth)
                    stationaryReplacementTab = replacementTab;
                if (closeStep.Direction == PlanTabCloseFillDirection.Left &&
                    closeLayoutMode != PlanTabCloseLayoutMode.MaximumWidth)
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var replacementCloseX = PlanTabCloseButtonHorizontalPosition(replacementTab);
                        if (!double.IsFinite(replacementCloseX))
                            break;

                        var adjustment = _planTabCloseSequence.AlignReplacementToAnchor(replacementCloseX);
                        if (Math.Abs(adjustment) < 0.5)
                            break;

                        ApplyPlanTabCloseSequenceMargin();
                        ShellPlanTabStripHost.UpdateLayout();
                    }
                }
            }
        }
        PlayPlanTabReflow(previousPositions, stationaryReplacementTab);
        QueueTitleBarInteractiveRegionUpdate();
    }

    private void ApplyPlanTabCloseSequenceMargin()
    {
        if (!_planTabCloseSequence.IsActive)
            return;

        ShellPlanTabItems.Margin = new Thickness(
            _planTabCloseBaseItemsMargin.Left + _planTabCloseSequence.LeadingInset,
            _planTabCloseBaseItemsMargin.Top,
            _planTabCloseBaseItemsMargin.Right + _planTabCloseSequence.TrailingReserve,
            _planTabCloseBaseItemsMargin.Bottom);
    }

    private void EndPlanTabCloseSequence(bool animate = true)
    {
        if (!_planTabCloseSequence.IsActive)
            return;

        var previousPositions = animate
            ? CapturePlanTabHorizontalPositions()
            : null;
        ReleasePlanTabCloseSequenceLock();
        UpdatePlanTabLayout();
        ShellPlanTabStripHost.UpdateLayout();
        if (previousPositions is not null)
            PlayPlanTabReflow(previousPositions);
        QueueTitleBarInteractiveRegionUpdate();
    }

    private void ReleasePlanTabCloseSequenceLock()
    {
        ShellPlanTabItems.Margin = _planTabCloseBaseItemsMargin;
        _planTabCloseSequence.Reset();
    }

    private Dictionary<FrameworkElement, double> CapturePlanTabHorizontalPositions()
    {
        var positions = ShellPlanTabItems.Children
            .OfType<Button>()
            .Cast<FrameworkElement>()
            .Append(ShellAddPlanTabButton)
            .ToDictionary(element => element, PlanTabHorizontalPosition);
        return positions;
    }

    private double PlanTabHorizontalPosition(FrameworkElement element) =>
        element.TransformToVisual(ShellPlanTabStripHost)
            .TransformPoint(new Windows.Foundation.Point())
            .X;

    private double PlanTabCloseButtonHorizontalPosition(Button tab)
    {
        var closeButton = PlanTabCloseButton(tab);
        if (closeButton is null || closeButton.ActualWidth <= 0)
            return double.NaN;

        return closeButton.TransformToVisual(ShellPlanTabStripHost)
                   .TransformPoint(new Windows.Foundation.Point())
                   .X +
               (closeButton.ActualWidth / 2);
    }

    private void PlayPlanTabReflow(
        IReadOnlyDictionary<FrameworkElement, double> previousPositions,
        FrameworkElement? stationaryElement = null)
    {
        foreach (var (element, previousX) in previousPositions)
        {
            if (!element.IsLoaded ||
                (element is Button tab && !ShellPlanTabItems.Children.Contains(tab)))
            {
                continue;
            }

            if (ReferenceEquals(element, stationaryElement))
            {
                AppAnimationLayer.PlayHorizontalReflow(element, 0);
                continue;
            }

            var currentX = PlanTabHorizontalPosition(element);
            AppAnimationLayer.PlayHorizontalReflow(element, previousX - currentX);
        }
    }

    private void FlushDeferredPlanTabClosePersistence()
    {
        if (!_planTabClosePersistencePending)
            return;

        _planTabClosePersistencePending = false;
        _plannerViewModel.PersistPlanTabState();
    }

    private void UpdatePlanTabLayout()
    {
        if (!TryGetPlanTabLayoutMetrics(out var availableWidth, out var tabViewportWidth))
            return;

        ShellPlanTabStripHost.MaxWidth = availableWidth;
        ShellPlanTabScrollViewer.MaxWidth = tabViewportWidth;
        var tabs = ShellPlanTabItems.Children.OfType<Button>().ToList();
        if (tabs.Count == 0)
            return;

        var tabWidth = _planTabCloseSequence.IsActive
            ? _planTabCloseSequence.TabWidth
            : CalculatePlanTabWidth(tabs.Count, tabViewportWidth);
        foreach (var tab in tabs)
        {
            tab.Width = tabWidth;
            UpdatePlanTabCloseButton(tab, pointerOver: false);
        }
    }

    private static double CalculatePlanTabWidth(int tabCount, double tabViewportWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tabCount, 0);
        var desiredTabStripWidth = Math.Min(tabViewportWidth, tabCount * PlanTabMaximumWidth);
        return Math.Clamp(
            desiredTabStripWidth / tabCount,
            PlanTabMinimumWidth,
            PlanTabMaximumWidth);
    }

    private bool TryGetPlanTabLayoutMetrics(out double availableWidth, out double tabViewportWidth)
    {
        var navigationInset = RootNavigation.CompactPaneLength;
        availableWidth = Math.Max(
            0,
            ShellPlanTabs.ActualWidth -
            ShellPlanTabs.Padding.Left -
            ShellPlanTabs.Padding.Right -
            navigationInset);
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            tabViewportWidth = 0;
            return false;
        }

        var addButtonWidth = ShellAddPlanTabButton.ActualWidth > 0
            ? ShellAddPlanTabButton.ActualWidth
            : ShellAddPlanTabButton.Width;
        tabViewportWidth = Math.Max(
            0,
            availableWidth - addButtonWidth - ShellPlanTabStripHost.ColumnSpacing);
        return double.IsFinite(tabViewportWidth) && tabViewportWidth > 0;
    }

    private void UpdatePlanTabCloseButton(Button tab, bool pointerOver)
    {
        var closeButton = PlanTabCloseButton(tab);
        if (closeButton is null)
            return;

        var compact = !double.IsNaN(tab.Width) && tab.Width < PlanTabCloseButtonCollapseWidth;
        closeButton.Visibility = compact && !pointerOver && !IsCurrentPlanTab(tab)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static Button? PlanTabCloseButton(Button tab) =>
        tab.Content is Grid content
            ? content.Children.OfType<Button>().FirstOrDefault()
            : null;

    private void BringSelectedPlanTabIntoView()
    {
        var selected = ShellPlanTabItems.Children
            .OfType<Button>()
            .FirstOrDefault(IsCurrentPlanTab);
        selected?.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            HorizontalAlignmentRatio = 0.5
        });
    }

    private void ApplyPlanTabVisual(Button tab, bool isSelected)
    {
        tab.Background = isSelected
            ? AppMaterialLayer.Brush(tab, AppColorRole.ShellTabSelected, Colors.Transparent)
            : AppMaterialLayer.Brush(tab, AppColorRole.ShellTabRest, Colors.Transparent);
        var showHighContrastSelection = isSelected && AppBrushes.IsHighContrast;
        tab.BorderBrush = showHighContrastSelection
            ? AppMaterialLayer.Brush(tab, AppColorRole.StatusCurrent, Colors.Transparent)
            : AppBrushes.Transparent();
        tab.BorderThickness = showHighContrastSelection
            ? new Thickness(2)
            : new Thickness(0);
        UpdatePlanTabAutomation(tab, isSelected);
    }

    private void UpdatePlanTabAutomation(Button tab, bool isSelected) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(
            tab, isSelected ? _services.Localization.Localizer["CurrentPlan"] : "");

    private void ApplyPlanTabHoverVisual(Button tab)
    {
        if (IsCurrentPlanTab(tab))
            return;

        tab.Background = AppMaterialLayer.Brush(tab, AppColorRole.ShellTabHover, Colors.Transparent);
    }

    private bool IsCurrentPlanTab(Button tab) =>
        tab.Tag is SelectionPlan plan &&
        !string.IsNullOrWhiteSpace(_plannerViewModel.CurrentPlan?.PlanId) &&
        string.Equals(plan.PlanId, _plannerViewModel.CurrentPlan.PlanId, StringComparison.Ordinal);

    private MenuFlyout CreatePlanTabMenu(SelectionPlan plan)
    {
        var menu = AppMaterialLayer.CreateTransientMenuFlyout();
        menu.Items.Add(MenuItem(_services.Localization.Localizer["Copy"], Symbol.Copy, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            if (!_plannerViewModel.TryCopyPlan(plan, out _, out var validation))
            {
                await ShowMessageAsync(
                    _services.Localization.Localizer["ValidationFailed"],
                    _services.Localization.Localizer.ValidationSummary(validation.Errors));
            }
        }));
        menu.Items.Add(MenuItem(_services.Localization.Localizer["Rename"], Symbol.Edit, async (_, _) => await RenamePlanAsync(plan)));
        menu.Items.Add(MenuItem(_services.Localization.Localizer["ClearPlan"], Symbol.Clear, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            if (await ConfirmAsync(_services.Localization.Localizer["ClearPlan"], string.Format(_services.Localization.Localizer["ClearPlanConfirm"], plan.PlanName)))
            {
                var validation = _plannerViewModel.ClearPlan(plan);
                if (!validation.IsValid)
                {
                    await ShowMessageAsync(
                        _services.Localization.Localizer["ValidationFailed"],
                        _services.Localization.Localizer.ValidationSummary(validation.Errors));
                }
            }
        }));
        menu.Items.Add(MenuItem(_services.Localization.Localizer["Delete"], Symbol.Delete, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            if (await ConfirmAsync(_services.Localization.Localizer["Delete"], string.Format(_services.Localization.Localizer["DeletePlanConfirm"], plan.PlanName)))
            {
                var validation = _plannerViewModel.DeletePlan(plan);
                if (!validation.IsValid)
                {
                    await ShowMessageAsync(
                        _services.Localization.Localizer["ValidationFailed"],
                        _services.Localization.Localizer.ValidationSummary(validation.Errors));
                }
            }
        }));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, Symbol symbol, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(symbol) };
        item.Click += click;
        return item;
    }

    private async void ShellAddPlanTabButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingPlanTabCloses();
        await CreatePlanTabAsync();
    }

    private async Task CreatePlanTabAsync()
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        if (!_plannerViewModel.TryCreatePlanFromTab(out _, out var validation))
        {
            await ShowMessageAsync(
                _services.Localization.Localizer["ValidationFailed"],
                _services.Localization.Localizer.ValidationSummary(validation.Errors));
        }
    }

    private async Task ClosePlanTabAsync(SelectionPlan plan, Button? sourceTab = null)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var replacingLastPlanTab = _plannerViewModel.OpenPlans.Count == 1;
        if (replacingLastPlanTab)
        {
            var replacementValidation = _plannerViewModel.ValidateLastPlanTabReplacement(plan);
            if (!replacementValidation.IsValid)
            {
                await ShowMessageAsync(
                    _services.Localization.Localizer["ValidationFailed"],
                    _services.Localization.Localizer.ValidationSummary(replacementValidation.Errors));
                return;
            }
        }

        var liveSourceTab = ShellPlanTabItems.Children
            .OfType<Button>()
            .FirstOrDefault(tab =>
                tab.Tag is SelectionPlan tabPlan &&
                string.Equals(tabPlan.PlanId, plan.PlanId, StringComparison.Ordinal)) ?? sourceTab;
        if (liveSourceTab is not null)
            RemovePlanTabIncrementally(liveSourceTab);

        if (replacingLastPlanTab)
        {
            _planTabClosePersistencePending = false;
            _planTabCloseCommitTimer.Stop();
            EndPlanTabCloseSequence(animate: false);
            _lastTabSignature = "";
            if (!_plannerViewModel.TryReplaceLastPlanTab(plan, out _, out var validation))
            {
                BuildPlanTabs();
                await ShowMessageAsync(
                    _services.Localization.Localizer["ValidationFailed"],
                    _services.Localization.Localizer.ValidationSummary(validation.Errors));
            }
            return;
        }

        _planTabIncrementalCloseInProgress = true;
        try
        {
            _plannerViewModel.ClosePlanTab(plan, persist: false);
            _planTabClosePersistencePending = true;
        }
        finally
        {
            _planTabIncrementalCloseInProgress = false;
        }

        if (!TrySynchronizePlanTabsAfterIncrementalClose(_plannerViewModel.CurrentPlan?.PlanId))
            BuildPlanTabs();

        RestartPlanTabCloseCommitTimer();
    }

    private async void ShellPlanTab_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_syncingTabs || sender is not Button { Tag: SelectionPlan plan } tab)
            return;

        var updateKind = e.GetCurrentPoint(tab).Properties.PointerUpdateKind;
        if (updateKind == Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased)
        {
            await ClosePlanTabAsync(plan, tab);
            e.Handled = true;
        }
    }

    private async void ShellPlanTab_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs || sender is not Button { Tag: SelectionPlan plan })
            return;

        await ActivatePlanTabAsync(plan);
    }

    private async Task ActivatePlanTabAsync(SelectionPlan plan)
    {
        CommitPendingPlanTabCloses();

        var comparisonModifierPressed = IsKeyDown(VirtualKey.Control);
        _plannerViewModel.SetComparisonModifierPressed(comparisonModifierPressed);
        if (comparisonModifierPressed)
        {
            _plannerViewModel.ToggleComparisonPlanSelection(plan);
            SyncSelectedPlanTab(_plannerViewModel.CurrentPlan?.PlanId);
            return;
        }

        if (!await ConfirmUnsavedCourseEditAsync())
        {
            SyncSelectedPlanTab(_plannerViewModel.CurrentPlan?.PlanId);
            return;
        }

        _plannerViewModel.CurrentPlan = plan;
        _plannerViewModel.ClearComparisonPlanSelection();
        SyncSelectedPlanTab(plan.PlanId);
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ShellRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
            _plannerViewModel.SetComparisonModifierPressed(true);
    }

    private void ShellRoot_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
            _plannerViewModel.SetComparisonModifierPressed(false);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _plannerViewModel.SetComparisonModifierPressed(false);
            CommitPendingPlanTabCloses();
        }
    }

    private async Task RenamePlanAsync(SelectionPlan plan)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var box = new TextBox
        {
            Text = plan.PlanName,
            Header = _services.Localization.Localizer["Name"],
            MaxLength = WindowsFileNameRules.MaxComponentLength
        };
        var dialog = new ContentDialog
        {
            XamlRoot = ShellRoot.XamlRoot,
            Title = _services.Localization.Localizer["Rename"],
            Content = box,
            PrimaryButtonText = _services.Localization.Localizer["Save"],
            CloseButtonText = _services.Localization.Localizer["Cancel"]
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;

        var validation = _plannerViewModel.RenamePlan(plan, box.Text);
        if (!validation.IsValid)
            await ShowMessageAsync(_services.Localization.Localizer["CannotRenamePlan"], _services.Localization.Localizer.ValidationSummary(validation.Errors));
    }

    private Task<bool> GuardCurrentPageAsync() =>
        RootFrame.Content switch
        {
            PlannerPage plannerPage => plannerPage.ConfirmLeavingCourseEditAsync(),
            CourseLibraryPage libraryPage => libraryPage.ConfirmLeavingCourseEditAsync(),
            _ => ConfirmDetachedPlannerEditAsync()
        };

    private async Task<bool> ConfirmUnsavedCourseEditAsync() =>
        await GuardCurrentPageAsync();

    private async Task<bool> ConfirmDetachedPlannerEditAsync()
    {
        if (!_plannerViewModel.HasUnsavedCourseEdit)
            return true;

        var dialog = new ContentDialog
        {
            XamlRoot = ShellRoot.XamlRoot,
            Title = _services.Localization.Localizer["UnsavedChanges"],
            Content = new TextBlock
            {
                Text = _services.Localization.Localizer["SaveCourseEditBeforeChangingDetails"],
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _services.Localization.Localizer["Save"],
            SecondaryButtonText = _services.Localization.Localizer["DontSave"],
            CloseButtonText = _services.Localization.Localizer["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await ContentDialogCoordinator.ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
            return await SaveActiveCourseEditAsync();
        if (result == ContentDialogResult.Secondary)
        {
            _plannerViewModel.DiscardActiveCourseEdit();
            return true;
        }
        return false;
    }

    private async Task<bool> SaveActiveCourseEditAsync()
    {
        var result = _plannerViewModel.SaveActiveCourseEdit();
        if (!result.IsValid)
        {
            await ShowMessageAsync(_services.Localization.Localizer["ValidationFailed"], _services.Localization.Localizer.ValidationSummary(result.Errors));
            return false;
        }
        if (result.RequiresForce)
        {
            if (!await ConfirmAsync(_services.Localization.Localizer["OutOfRange"], _services.Localization.Localizer.ValidationSummary(result.Warnings) + " " + _services.Localization.Localizer["SaveAnyway"]))
                return false;
            result = _plannerViewModel.SaveActiveCourseEdit(forceOutOfRange: true);
            if (!result.IsValid)
            {
                await ShowMessageAsync(_services.Localization.Localizer["ValidationFailed"], _services.Localization.Localizer.ValidationSummary(result.Errors));
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellRoot.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = _services.Localization.Localizer["OK"],
            CloseButtonText = _services.Localization.Localizer["Cancel"]
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = ShellRoot.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = _services.Localization.Localizer["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc subclassProc, UIntPtr subclassId, UIntPtr refData);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc subclassProc, UIntPtr subclassId);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }
}

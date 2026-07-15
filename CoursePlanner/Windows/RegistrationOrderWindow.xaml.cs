using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CoursePlanner.Core;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CoursePlanner.ToolWindows;

public sealed class RegistrationOrderRow : INotifyPropertyChanged
{
    private int _position;

    public RegistrationOrderRow(RegistrationPriorityAnalysis analysis, AppLocalizer text)
    {
        SnapshotId = analysis.SnapshotId;
        Position = analysis.CurrentOrder;
        CourseName = analysis.Course?.CourseName ?? text["RegistrationPriorityMissingReference"];
        Teacher = analysis.Course?.Teacher ?? "";
        CapacityText = CapacityTextFor(analysis, text);
        ToolTipText = string.Join(
            " · ",
            new[] { CourseName, Teacher, CapacityText }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        AutomationId = $"RegistrationOrderRow_{SnapshotId}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SnapshotId { get; }
    public string CourseName { get; }
    public string Teacher { get; }
    public string CapacityText { get; }
    public string ToolTipText { get; }
    public string AutomationId { get; }

    public int Position
    {
        get => _position;
        set
        {
            if (_position == value)
                return;

            _position = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        }
    }

    public string AutomationName => $"{Position}. {ToolTipText}";

    private static string CapacityTextFor(RegistrationPriorityAnalysis analysis, AppLocalizer text)
    {
        if (analysis.Course is null)
            return "–";

        if (analysis.Course.EnrolledCount is not { } enrolled || analysis.Course.Capacity is not { } capacity)
            return "–/–";

        return string.Format(
            CultureInfo.CurrentCulture,
            text["RegistrationCapacityCompactFormat"],
            enrolled,
            capacity);
    }

}

public sealed partial class RegistrationOrderWindow : Window
{
    private const double WindowWidthDip = 280;
    private const double WindowHeightDip = 300;
    private const double MinimumWindowWidthDip = 260;
    private const double MinimumWindowHeightDip = 220;
    private const double MaximumWindowWidthDip = 480;
    private const double MaximumWindowHeightDip = 520;
    private const double PlacementMarginDip = 16;
    private const double PlacementTopOffsetDip = 52;
    private static readonly TimeSpan ResizeLayoutTimeout = TimeSpan.FromMilliseconds(500);
    private readonly DocumentSession _documents;
    private readonly LocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly PlannerViewModel _planner;
    private bool _interactionEnabled = true;
    private readonly ToolWindowPlacementState _placement;
    private readonly string _planId;
    private readonly OverlappedPresenter _presenter;
    private readonly RegistrationOrderPersistenceCoordinator _persistence = new();
    private readonly RegistrationOrderWindowLifetime _lifetime = new();
    private SizeInt32 _defaultWindowSize;
    private bool _isReplacingRows;
    private Task? _closeAnimationTask;

    public RegistrationOrderWindow(
        DocumentSession documents,
        LocalizationService localization,
        ThemeService theme,
        PlannerViewModel planner,
        ToolWindowPlacementState placement,
        string planId,
        nint placementAnchorWindowHandle)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        _documents = documents;
        _localization = localization;
        _theme = theme;
        _planner = planner;
        _placement = placement;
        _planId = planId;

        InitializeComponent();
        DisabledButtonHoverLayer.Attach(this, WindowRoot);
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyTheme(_theme.RequestedTheme);
        SystemBackdrop = new MicaBackdrop
        {
            Kind = MicaKind.Base
        };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _presenter = OverlappedPresenter.Create();
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = true;
        _presenter.IsResizable = true;
        var scale = DpiScale(windowHandle);
        _presenter.PreferredMinimumWidth = PhysicalPixels(MinimumWindowWidthDip, scale);
        _presenter.PreferredMinimumHeight = PhysicalPixels(MinimumWindowHeightDip, scale);
        _presenter.PreferredMaximumWidth = PhysicalPixels(MaximumWindowWidthDip, scale);
        _presenter.PreferredMaximumHeight = PhysicalPixels(MaximumWindowHeightDip, scale);
        AppWindow.SetPresenter(_presenter);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        WindowRoot.SizeChanged += WindowRoot_SizeChanged;
        ResizeAndPlace(placementAnchorWindowHandle);
        ApplyText();
        ReplaceRows(CurrentAnalysis());
        Rows.CollectionChanged += Rows_CollectionChanged;

        _documents.Changed += Documents_Changed;
        _documents.RolledBack += Documents_Changed;
        _localization.LanguageChanged += Localization_LanguageChanged;
        _theme.ThemeChanged += Theme_ThemeChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => TearDown();
    }

    public string PlanId => _planId;

    internal bool IsMinimized =>
        _presenter.State == OverlappedPresenterState.Minimized;

    public ObservableCollection<RegistrationOrderRow> Rows { get; } = new();

    public IReadOnlyList<string> OrderedSnapshotIds =>
        Rows.Select(row => row.SnapshotId).ToList();

    public void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized)
            _presenter.Restore();
        AppWindow.Show(true);
        AppWindow.MoveInZOrderAtTop();
    }

    internal void HideAndClose()
    {
        if (_lifetime.IsClosed)
            return;

        PrepareForClose();
        try
        {
            RememberPlacement();
            AppWindow.Hide();
            Close();
        }
        catch
        {
            if (!_lifetime.IsClosed)
            {
                _lifetime.CancelClose();
                UpdateState();
            }

            throw;
        }
    }

    internal Task CloseAnimatedAsync()
    {
        if (_lifetime.IsClosed)
            return Task.CompletedTask;

        return _closeAnimationTask ??= CloseAnimatedTrackedAsync();
    }

    private async Task CloseAnimatedTrackedAsync()
    {
        try
        {
            await CloseAnimatedCoreAsync();
        }
        finally
        {
            if (!_lifetime.IsClosed)
                _closeAnimationTask = null;
        }
    }

    private async Task CloseAnimatedCoreAsync()
    {
        await Task.Yield();
        if (_lifetime.IsClosed)
            return;

        PrepareForClose();
        try
        {
            RememberPlacement();
            await AppAnimationLayer.PlayToolWindowExitThenAsync(
                AnimatedContentRoot,
                () =>
                {
                    if (!_lifetime.IsClosed)
                        AppWindow.Hide();
                });
            if (!_lifetime.IsClosed)
                Close();
        }
        catch
        {
            if (!_lifetime.IsClosed)
            {
                _lifetime.CancelClose();
                UpdateState();
            }

            throw;
        }
    }

    private void PrepareForClose()
    {
        if (!_lifetime.TryBeginClose())
            return;

        try
        {
            UpdateState();
            _persistence.Flush(CurrentPlan(), PersistOrder);
        }
        catch
        {
            _lifetime.CancelClose();
            UpdateState();
            throw;
        }
    }

    private SelectionPlan? CurrentPlan() =>
        _documents.Document.Plans.FirstOrDefault(plan =>
            string.Equals(plan.PlanId, _planId, StringComparison.Ordinal));

    private IReadOnlyList<RegistrationPriorityAnalysis> CurrentAnalysis(
        IReadOnlyList<string>? currentSnapshotOrder = null)
    {
        var plan = CurrentPlan();
        return plan is null
            ? Array.Empty<RegistrationPriorityAnalysis>()
            : RegistrationPriorityService.Analyze(
                plan,
                _documents.Document.CourseLibrary,
                currentSnapshotOrder);
    }

    private void ApplyText()
    {
        var text = _localization.Localizer;
        var planName = CurrentPlan()?.PlanName ?? "";
        Title = string.Format(
            CultureInfo.CurrentCulture,
            text["RegistrationOrderDialogTitleFormat"],
            planName);
        WindowTitleText.Text = text["RegistrationOrder"];
        PlanNameText.Text = string.IsNullOrWhiteSpace(planName) ? "" : $"· {planName}";
        SmartSortButtonText.Text = text["SmartSort"];
        EmptyStateText.Text = text["RegistrationOrderEmptyTitle"];
        AutomationProperties.SetName(RegistrationOrderList, text["RegistrationOrder"]);
        AutomationProperties.SetHelpText(RegistrationOrderList, text["RegistrationOrderListHelp"]);
        ToolTipService.SetToolTip(RegistrationOrderList, text["RegistrationOrderListHelp"]);
        SetAccessibleLabel(SmartSortButton, text["SmartSort"], text["RegistrationSmartSortHint"]);
        SetAccessibleLabel(ResetWindowSizeButton, text["ResetWindowSize"], text["ResetWindowSize"]);
        SetAccessibleLabel(MinimizeButton, text["MinimizeWindow"], text["MinimizeWindow"]);
        SetAccessibleLabel(CloseButton, text["CloseWindow"], text["CloseWindow"]);
        UpdatePinText();
    }

    private static void SetAccessibleLabel(FrameworkElement element, string name, string toolTip)
    {
        AutomationProperties.SetName(element, name);
        AutomationProperties.SetHelpText(element, toolTip);
        ToolTipService.SetToolTip(element, toolTip);
    }

    private void SmartSortButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_lifetime.AcceptsInteraction)
            return;

        var plan = CurrentPlan();
        if (plan is null)
        {
            HideAndClose();
            return;
        }

        var recommendation = RegistrationPriorityService.Recommend(
            plan,
            _documents.Document.CourseLibrary,
            OrderedSnapshotIds);
        ReplaceRows(recommendation);
        _persistence.PersistImmediately(plan, OrderedSnapshotIds, PersistOrder);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _presenter.IsAlwaysOnTop = PinButton.IsChecked == true;
        UpdatePinText();
    }

    private async void ResetWindowSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_lifetime.AcceptsInteraction || !ResetWindowSizeButton.IsEnabled)
            return;

        ResetWindowSizeButton.IsEnabled = false;
        var previousWidth = AnimatedContentRoot.ActualWidth;
        var previousHeight = AnimatedContentRoot.ActualHeight;
        var currentPosition = AppWindow.Position;
        var currentSize = await ResizeToDefaultWindowAsync();
        if (!_lifetime.AcceptsInteraction)
            return;

        AppWindow.Move(currentPosition);

        AppAnimationLayer.PlayToolWindowSizeReflow(
            AnimatedContentRoot,
            previousWidth,
            previousHeight,
            currentSize.Width,
            currentSize.Height);
        RememberPlacement();
        UpdateResetWindowSizeButtonState();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_lifetime.AcceptsInteraction || IsMinimized)
            return;

        RememberPlacement();
        _presenter.Minimize();
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseAnimatedAsync();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            PrepareForClose();
            RememberPlacement();
            // User closes have already animated and hidden the complete HWND.
            // Forced teardown still reaches this path directly.
            sender.Hide();
        }
        catch
        {
            args.Cancel = true;
            if (!_lifetime.IsClosed)
            {
                _lifetime.CancelClose();
                UpdateState();
            }

            throw;
        }
    }

    private void CaptionIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement icon)
            return;

        var lineHeight = AppTypography.LineHeight(AppTextRole.Caption);
        icon.RenderTransform = new TranslateTransform
        {
            Y = AppTypography.IconAlignmentOffset(AppTextRole.Caption, lineHeight)
        };
    }

    private void UpdatePinText()
    {
        var isPinned = PinButton.IsChecked == true;
        var label = _localization.Localizer[isPinned ? "UnpinWindow" : "PinWindow"];
        PinFillIcon.Visibility = isPinned ? Visibility.Visible : Visibility.Collapsed;
        PinIcon.Visibility = Visibility.Visible;
        AutomationProperties.SetName(PinButton, label);
        AutomationProperties.SetHelpText(PinButton, label);
        ToolTipService.SetToolTip(PinButton, label);
    }

    private void MoveUpAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        MoveSelectedRow(-1);
        args.Handled = true;
    }

    private void MoveDownAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        MoveSelectedRow(1);
        args.Handled = true;
    }

    private void MoveSelectedRow(int offset)
    {
        if (!_lifetime.AcceptsInteraction)
            return;

        if (RegistrationOrderList.SelectedItem is not RegistrationOrderRow selected)
            return;

        var currentIndex = Rows.IndexOf(selected);
        var targetIndex = currentIndex + offset;
        if (targetIndex < 0 || targetIndex >= Rows.Count)
            return;

        Rows.Move(currentIndex, targetIndex);
        RegistrationOrderList.SelectedItem = selected;
        RegistrationOrderList.ScrollIntoView(selected);
        RegistrationOrderList.Focus(FocusState.Keyboard);
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshPositions();
        UpdateState();
        QueuePersist();
    }

    private void QueuePersist()
    {
        if (_isReplacingRows || !_lifetime.AcceptsInteraction)
            return;

        var plan = CurrentPlan();
        if (plan is null)
            return;

        var ticket = _persistence.Queue(plan, OrderedSnapshotIds);
        if (ticket is null)
            return;

        if (WindowRoot.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => ExecuteQueuedPersist(ticket.Value, refreshRows: true)))
            return;

        // TryEnqueue can fail while the dispatcher is shutting down. Persist the
        // captured order, but do not Clear/Add Rows from inside CollectionChanged;
        // doing so would violate ObservableCollection's reentrancy contract.
        ExecuteQueuedPersist(ticket.Value, refreshRows: false);
    }

    private void ExecuteQueuedPersist(
        RegistrationOrderPersistenceTicket ticket,
        bool refreshRows)
    {
        var handled = _persistence.ExecuteScheduled(ticket, CurrentPlan(), PersistOrder);
        if (handled &&
            refreshRows &&
            _lifetime.AcceptsInteraction &&
            CurrentPlan() is not null)
        {
            ReplaceRows(CurrentAnalysis());
        }
    }

    private void PersistOrder(IReadOnlyList<string> orderedSnapshotIds) =>
        _planner.PersistRegistrationOrder(_planId, orderedSnapshotIds, notify: false);

    private void ReplaceRows(IEnumerable<RegistrationPriorityAnalysis> analyses)
    {
        var selectedSnapshotId = (RegistrationOrderList.SelectedItem as RegistrationOrderRow)?.SnapshotId;
        var rows = analyses
            .OrderBy(item => item.CurrentOrder)
            .Select(item => new RegistrationOrderRow(item, _localization.Localizer))
            .ToList();

        _isReplacingRows = true;
        try
        {
            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);
        }
        finally
        {
            _isReplacingRows = false;
        }

        RefreshPositions();
        UpdateState();
        if (!string.IsNullOrWhiteSpace(selectedSnapshotId))
        {
            RegistrationOrderList.SelectedItem = Rows.FirstOrDefault(row =>
                string.Equals(row.SnapshotId, selectedSnapshotId, StringComparison.Ordinal));
        }
    }

    private void RefreshPositions()
    {
        for (var index = 0; index < Rows.Count; index++)
            Rows[index].Position = index + 1;
    }

    private void UpdateState()
    {
        var acceptsInteraction = _interactionEnabled && _lifetime.AcceptsInteraction;
        SmartSortButton.IsEnabled = acceptsInteraction && Rows.Count > 1;
        RegistrationOrderList.IsEnabled = acceptsInteraction;
        RegistrationOrderList.Visibility = Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetInteractionEnabled(bool enabled) => RunOnWindowThread(() =>
    {
        _interactionEnabled = enabled;
        UpdateState();
    });

    private void Documents_Changed(object? sender, EventArgs e) => RunOnWindowThread(() =>
    {
        var plan = CurrentPlan();
        if (plan is null)
        {
            HideAndClose();
            return;
        }

        var displayedOrder = OrderedSnapshotIds;
        var retainPendingOrder = _persistence.CanRetainPending(
            plan,
            plan.Snapshots
                .Where(snapshot => !snapshot.IsLocked)
                .Select(snapshot => snapshot.SnapshotId)
                .ToList());
        if (!retainPendingOrder)
            _persistence.DiscardPending();

        ApplyText();
        ReplaceRows(CurrentAnalysis(retainPendingOrder ? displayedOrder : null));
    });

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e) => RunOnWindowThread(() =>
    {
        var plan = CurrentPlan();
        var displayedOrder = OrderedSnapshotIds;
        var retainPendingOrder = plan is not null && _persistence.CanRetainPending(
            plan,
            plan.Snapshots
                .Where(snapshot => !snapshot.IsLocked)
                .Select(snapshot => snapshot.SnapshotId)
                .ToList());
        if (!retainPendingOrder)
            _persistence.DiscardPending();

        ApplyText();
        ReplaceRows(CurrentAnalysis(retainPendingOrder ? displayedOrder : null));
    });

    private void Theme_ThemeChanged(object? sender, ThemeChangedEventArgs e) =>
        RunOnWindowThread(() => ApplyTheme(e.RequestedTheme));

    private void RunOnWindowThread(Action action)
    {
        if (_lifetime.IsClosed)
            return;
        if (WindowRoot.DispatcherQueue.HasThreadAccess)
            action();
        else
            _ = WindowRoot.DispatcherQueue.TryEnqueue(() => _lifetime.TryRunDeferred(action));
    }

    private void ApplyTheme(ThemeMode requestedTheme)
    {
        WindowRoot.RequestedTheme = requestedTheme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        AppMaterialLayer.RefreshTree(WindowRoot);
    }

    private void WindowRoot_SizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateResetWindowSizeButtonState();

    private void UpdateResetWindowSizeButtonState()
    {
        if (_lifetime.IsClosed)
            return;

        ResetWindowSizeButton.IsEnabled = !IsDefaultWindowSize(AppWindow.Size);
    }

    private bool IsDefaultWindowSize(SizeInt32 size) =>
        Math.Abs(size.Width - _defaultWindowSize.Width) <= 1 &&
        Math.Abs(size.Height - _defaultWindowSize.Height) <= 1;

    private async Task<(double Width, double Height)> ResizeToDefaultWindowAsync()
    {
        var completion = new TaskCompletionSource<(double Width, double Height)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SizeChangedEventHandler? sizeChanged = null;
        sizeChanged = (sender, args) =>
        {
            if (IsDefaultWindowSize(AppWindow.Size))
                completion.TrySetResult((args.NewSize.Width, args.NewSize.Height));
        };

        var timeout = WindowRoot.DispatcherQueue.CreateTimer();
        void TimeoutElapsed(DispatcherQueueTimer sender, object args) =>
            completion.TrySetResult((WindowRoot.ActualWidth, WindowRoot.ActualHeight));

        try
        {
            WindowRoot.SizeChanged += sizeChanged;
            timeout.Interval = ResizeLayoutTimeout;
            timeout.IsRepeating = false;
            timeout.Tick += TimeoutElapsed;
            timeout.Start();
            AppWindow.Resize(_defaultWindowSize);
            return await completion.Task;
        }
        finally
        {
            timeout.Stop();
            timeout.Tick -= TimeoutElapsed;
            WindowRoot.SizeChanged -= sizeChanged;
        }
    }

    private void ResizeAndPlace(nint placementAnchorWindowHandle)
    {
        ResizeClientToDefault();
        _defaultWindowSize = AppWindow.Size;

        if (_placement.TryGet(out var remembered) && TryRestoreRememberedBounds(remembered))
            return;

        if (placementAnchorWindowHandle == 0)
            return;

        var mainWindowId = Win32Interop.GetWindowIdFromWindow(placementAnchorWindowHandle);
        var mainAppWindow = AppWindow.GetFromWindowId(mainWindowId);
        var workArea = DisplayArea.GetFromWindowId(mainWindowId, DisplayAreaFallback.Nearest)?.WorkArea;
        if (mainAppWindow is null || workArea is null)
            return;

        var scale = DpiScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var margin = (int)Math.Round(PlacementMarginDip * scale);
        var topOffset = (int)Math.Round(PlacementTopOffsetDip * scale);
        var x = mainAppWindow.Position.X + mainAppWindow.Size.Width - AppWindow.Size.Width - margin;
        var y = mainAppWindow.Position.Y + topOffset;
        x = Math.Clamp(x, workArea.Value.X, workArea.Value.X + workArea.Value.Width - AppWindow.Size.Width);
        y = Math.Clamp(y, workArea.Value.Y, workArea.Value.Y + workArea.Value.Height - AppWindow.Size.Height);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void ResizeClientToDefault()
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = DpiScale(windowHandle);
        AppWindow.ResizeClient(new SizeInt32(
            PhysicalPixels(WindowWidthDip, scale),
            PhysicalPixels(WindowHeightDip, scale)));
    }

    private bool TryRestoreRememberedBounds(ToolWindowBounds remembered)
    {
        var displayArea = DisplayArea.GetFromPoint(
            new PointInt32(remembered.X, remembered.Y),
            DisplayAreaFallback.Nearest);
        if (displayArea is null)
            return false;

        var area = displayArea.WorkArea;
        var fitted = ToolWindowPlacementState.FitWithinWorkArea(
            remembered,
            new ToolWindowWorkArea(area.X, area.Y, area.Width, area.Height));
        AppWindow.Resize(new SizeInt32(fitted.Width, fitted.Height));
        var actualSize = AppWindow.Size;
        var visible = ToolWindowPlacementState.FitWithinWorkArea(
            new ToolWindowBounds(fitted.X, fitted.Y, actualSize.Width, actualSize.Height),
            new ToolWindowWorkArea(area.X, area.Y, area.Width, area.Height));
        AppWindow.Move(new PointInt32(visible.X, visible.Y));
        return true;
    }

    private ToolWindowBounds CurrentBounds()
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new ToolWindowBounds(position.X, position.Y, size.Width, size.Height);
    }

    private void RememberPlacement()
    {
        if (_lifetime.IsClosed || AppWindow.Size.Width <= 0 || AppWindow.Size.Height <= 0)
            return;

        _placement.Remember(CurrentBounds());
    }

    private void TearDown()
    {
        if (_lifetime.IsClosed)
            return;

        // Gate every delayed UI callback immediately. Persistence has its own
        // captured snapshot and is flushed directly below, so this cannot drop it.
        _lifetime.CompleteClose();
        try
        {
            // AppWindow.Closing normally flushes first. This is the final guard for
            // forced teardown paths that can reach Closed without our close helpers.
            _persistence.Close(CurrentPlan(), PersistOrder);
        }
        finally
        {
            Rows.CollectionChanged -= Rows_CollectionChanged;
            _documents.Changed -= Documents_Changed;
            _documents.RolledBack -= Documents_Changed;
            _localization.LanguageChanged -= Localization_LanguageChanged;
            _theme.ThemeChanged -= Theme_ThemeChanged;
            AppWindow.Closing -= AppWindow_Closing;
            WindowRoot.SizeChanged -= WindowRoot_SizeChanged;
            DisabledButtonHoverLayer.Detach(WindowRoot);
        }
    }

    private static double DpiScale(nint windowHandle)
    {
        var dpi = GetDpiForWindow(windowHandle);
        return (dpi == 0 ? 96d : dpi) / 96d;
    }

    private static int PhysicalPixels(double dip, double scale) =>
        Math.Max(1, (int)Math.Round(dip * scale));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}

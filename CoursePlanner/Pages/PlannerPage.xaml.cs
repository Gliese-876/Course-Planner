using CoursePlanner.Core;
using CoursePlanner.Controls;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace CoursePlanner.Pages;

public sealed partial class PlannerPage : Page
{
    private const double ToolbarIconSize = 16;
    private const double MediumBreakpoint = 760;
    private const double LargeBreakpoint = 1040;
    private const double LibraryWideWidth = 280;
    private const double DetailWideWidth = 320;
    private const double LibraryMinWidth = 220;
    private const double LibraryMaxWidth = 560;
    private const double DetailMinWidth = 260;
    private const double DetailMaxWidth = 520;
    private const double PaneDividerWidth = 1;
    private const double TimetablePeriodColumnWidth = 88;
    private const double TimetablePeriodRowHeight = 58;
    private const double TimetableHeaderRowHeight = 46;
    private const double TimetableComparisonDividerWidth = 44;
    private const double TimetableComparisonSwapButtonSize = 36;
    private const double TimetableScrollbarGutter = 8;

    private bool _loadingDetails;
    private bool _historyCommandInProgress;
    private bool _synchronizingWeekNumberBox;
    private Task<bool>? _courseEditLeaveOperation;
    private int _detailLoadVersion;
    private string _lastCenterRenderSignature = "";
    private string _lastLibraryRenderSignature = "";
    private double _libraryColumnWidth = LibraryWideWidth;
    private double _detailColumnWidth = DetailWideWidth;
    private readonly PlannerPaneStateCoordinator _paneState = new();
    private AppContentDirection? _pendingCenterRefreshDirection;
    private Grid _activeTimetableHost = null!;
    private Grid? _renderTargetTimetableHost;
    private SizeChangedEventHandler? _centerReflowHandler;
    private int _lastSemesterOverviewColumnCount = -1;
    private DateHeaderTextMode? _lastDateHeaderTextMode;
    private readonly List<(TextBlock Text, DateOnly Date)> _dateHeaderTexts = new();
    private readonly List<CourseBlockLayoutRegistration> _courseBlockLayouts = new();
    private ResponsiveToolbarController _toolbar = null!;
    private bool _timetableLayoutRefreshQueued;
    private Grid? _comparisonGrid;
    private ColumnDefinition? _comparisonBaseColumn;
    private ColumnDefinition? _comparisonDividerColumn;
    private ColumnDefinition? _comparisonCurrentColumn;
    private Button? _comparisonSwapButton;
    private readonly Dictionary<TimetableSlot, List<Button>> _comparisonBlocksBySlot = new();
    private ApplicationServices? _services;
    private readonly TransientNotificationLifetime _statusLifetime = new();
    private long _statusGeneration;
    private bool _statusCloseInProgress;
    private bool _committingStatusClose;
    private string? _statusOpenPath;

    private enum PanePresentation
    {
        Hidden,
        Docked,
        Overlay
    }

    private enum WeekGridRole
    {
        Standard,
        CompareBase,
        CompareCurrent
    }

    private enum DateHeaderTextMode
    {
        Short,
        Medium,
        Long
    }

    private sealed class WeekGridOptions
    {
        public Grid Host { get; init; } = null!;
        public SelectionPlan Plan { get; init; } = null!;
        public IReadOnlyList<SlotDifference> Differences { get; init; } = Array.Empty<SlotDifference>();
        public IReadOnlyList<TimetableCourseBlock>? CourseBlocks { get; init; }
        public TimetableUiMaterializationDecision? MaterializationDecision { get; init; }
        public WeekGridRole Role { get; init; }
        public bool AllowBlankSlotCreate { get; init; }
    }

    private sealed class DenseTimetableItem
    {
        public string DisplayText { get; init; } = "";
        public PlanCourseSnapshot? Snapshot { get; init; }

        public override string ToString() => DisplayText;
    }

    private sealed class CourseBlockLayoutRegistration
    {
        public Border ContentHost { get; init; } = null!;
        public TextBlock Title { get; init; } = null!;
        public FrameworkElement? DetailClip { get; init; }
        public IReadOnlyList<TextBlock> DetailTexts { get; init; } = Array.Empty<TextBlock>();

        public void Refresh()
        {
            if (ContentHost.ActualWidth <= 0 || ContentHost.ActualHeight <= 0)
                return;

            UpdateCourseBlockTextLayout(
                new Size(ContentHost.ActualWidth, ContentHost.ActualHeight),
                ContentHost,
                Title,
                DetailClip,
            DetailTexts);
        }
    }

    private sealed class CourseBlockVisualState
    {
        public Border Visual { get; init; } = null!;
        public Brush NormalBackground { get; init; } = null!;
        public Brush HoverBackground { get; init; } = null!;
    }

    private sealed class PlanPickerItem
    {
        public SelectionPlan Plan { get; init; } = new();
        public string Summary { get; init; } = "";
    }

    private sealed class BulkTargetIssue
    {
        public SelectionPlan Plan { get; init; } = new();
        public Semester Semester { get; init; } = new();
        public bool RequiresSemesterCopy { get; init; }
        public bool Duplicate { get; init; }
        public List<CourseOffering> Conflicts { get; init; } = new();
    }

    private sealed class BulkPlanDecisionControls
    {
        public BulkTargetIssue Issue { get; init; } = new();
        public ComboBox? DuplicateBox { get; init; }
        public ComboBox? ConflictBox { get; init; }

        public BulkAddPlanDecision ToDecision() => new()
        {
            Plan = Issue.Plan,
            DuplicateResolution = DuplicateBox?.SelectedIndex == 1
                ? DuplicateResolution.ReplaceExisting
                : DuplicateResolution.SkipExisting,
            ConflictResolution = ConflictBox?.SelectedIndex switch
            {
                0 => ConflictResolution.Cancel,
                2 => ConflictResolution.RemoveConflictingThenAdd,
                _ => ConflictResolution.KeepConflict
            }
        };
    }

    public PlannerPage()
    {
        InitializeComponent();
        CreditsBox.Maximum = (double)CourseNumericRules.MaximumCredits;
        EnrolledBox.Maximum = CourseNumericRules.MaximumPeopleCount;
        CapacityBox.Maximum = CourseNumericRules.MaximumPeopleCount;
        _activeTimetableHost = CreateTimetableHost();
        TimetableFrame.Content = new TimetableTransitionPage
        {
            Content = _activeTimetableHost
        };
        ConfigureToolbar();
        AppTypography.Apply(this);
        LibraryTree.AllowCourseDrag = true;
        LibraryTree.CourseInvoked += LibraryTree_CourseInvoked;
        LibraryTree.CourseContextRequested += LibraryTree_CourseContextRequested;
        LibraryTree.CourseStatusDotTapped += LibraryTree_CourseStatusDotTapped;
        Loaded += (_, _) =>
        {
            _toolbar.QueueLayout();
            UpdateResponsiveColumns(ActualWidth);
            QueueTimetableLayoutRefresh();
        };
        Unloaded += PlannerPage_Unloaded;
        LibraryPane.SizeChanged += (_, args) => CaptureDockedPaneWidth(LibraryPane, args.NewSize.Width, ref _libraryColumnWidth, LibraryMinWidth, LibraryMaxWidth);
        DetailPane.SizeChanged += (_, args) => CaptureDockedPaneWidth(DetailPane, args.NewSize.Width, ref _detailColumnWidth, DetailMinWidth, DetailMaxWidth);
        AddKeyboardAccelerators();
    }

    public PlannerViewModel ViewModel { get; private set; } = null!;

    private DocumentSession Documents => _services!.Documents;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("PlannerPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Planner;
        DataContext = ViewModel;
        ViewModel.IsLibraryOpen = true;
        ViewModel.AllSemesters = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Documents.Changed += Documents_Changed;
        Documents.StateAccepted += Documents_HistoryStateChanged;
        Documents.RolledBack += Documents_HistoryStateChanged;
        services.BackgroundOperations.Changed += BackgroundOperations_Changed;
        services.Theme.ThemeChanged += Theme_ThemeChanged;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        ApplyText();
        SyncFilterFields();
        RefreshVisuals();
        LoadActiveCourseEditToFieldsCore();
    }

    private CourseDisplayFormatter Display => new(ViewModel.T);

    public Task<bool> ConfirmLeavingCourseEditAsync()
    {
        if (_courseEditLeaveOperation is not null)
            return _courseEditLeaveOperation;

        var operation = CourseEditLeaveGuard.TryLeaveAsync(
            ViewModel.ActiveEdit is not null,
            ViewModel.HasUnsavedCourseEdit,
            PromptCourseEditLeaveChoiceAsync,
            SaveCurrentCourseEditAsync,
            DiscardCurrentCourseEdit);
        _courseEditLeaveOperation = operation;
        return AwaitCourseEditLeaveAndClearAsync(operation);
    }

    private async Task<bool> AwaitCourseEditLeaveAndClearAsync(Task<bool> operation)
    {
        try
        {
            return await operation;
        }
        finally
        {
            if (ReferenceEquals(_courseEditLeaveOperation, operation))
                _courseEditLeaveOperation = null;
        }
    }

    private void ConfigureToolbar()
    {
        NewCourseButtonIcon.Content = AppCommandIcons.NewCourseToolbar(ToolbarIconSize);
        NewPlanButtonIcon.Content = AppCommandIcons.NewPlanToolbar(ToolbarIconSize);

        _toolbar = new ResponsiveToolbarController(PlannerCommandBarHost, ToolbarMoreButton, CreateTransientMenuFlyout);
        _toolbar.AddCommand(ToggleLibraryButton, ToggleLibraryButtonText, "CourseLibrary", () => new SymbolIcon(Symbol.Library), ToggleLibrary_Click, group: 0, collapseOrder: 100);
        _toolbar.AddCommand(NewCourseButton, NewCourseButtonText, "NewCourse", AppCommandIcons.NewCourse, NewCourse_Click, group: 1, collapseOrder: 90);
        _toolbar.AddCommand(NewPlanButton, NewPlanButtonText, "NewPlan", AppCommandIcons.NewPlan, NewPlan_Click, group: 1, collapseOrder: 80);
        _toolbar.AddCommand(OpenPlanButton, OpenPlanButtonText, "OpenPlan", () => new SymbolIcon(Symbol.OpenFile), OpenPlan_Click, group: 1, collapseOrder: 70);
        _toolbar.AddCommand(WeekViewButton, WeekViewButtonText, "Week", () => new SymbolIcon(Symbol.Calendar), WeekView_Click, group: 2, collapseOrder: 60);
        _toolbar.AddCommand(SemesterViewButton, SemesterViewButtonText, "SemesterOverview", () => new SymbolIcon(Symbol.View), SemesterView_Click, group: 2, collapseOrder: 50);
        _toolbar.AddCommand(RegistrationOrderButton, RegistrationOrderButtonText, "RegistrationOrder", () => new SymbolIcon(Symbol.Sort), RegistrationOrder_Click, group: 2, collapseOrder: 35);
        _toolbar.AddCommand(CompareButton, CompareButtonText, "Compare", () => new SymbolIcon(Symbol.Switch), Compare_Click, group: 2, collapseOrder: 40);
        _toolbar.AddCommand(ImportButton, ImportButtonText, "Import", () => new SymbolIcon(Symbol.Download), Import_Click, group: 3, collapseOrder: 30);
        _toolbar.AddCommand(ExportButton, ExportButtonText, "Export", () => new SymbolIcon(Symbol.Save), Export_Click, group: 3, collapseOrder: 20);
        _toolbar.AddCommand(UndoButton, UndoButtonText, "Undo", () => new SymbolIcon(Symbol.Undo), Undo_Click, group: 4, collapseOrder: 10);
        _toolbar.AddCommand(RedoButton, RedoButtonText, "Redo", () => new SymbolIcon(Symbol.Redo), Redo_Click, group: 4, collapseOrder: 0);
        _toolbar.AddSeparator(LibraryCommandSeparator, group: 0);
        _toolbar.AddSeparator(PlanCommandSeparator, group: 1);
        _toolbar.AddSeparator(ViewCommandSeparator, group: 2);
        _toolbar.AddSeparator(ExchangeCommandSeparator, group: 3);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLibraryFilterProperty(e.PropertyName))
            SyncFilterFields();
        RefreshVisuals();
    }

    private void Documents_Changed(object? sender, EventArgs e)
    {
        ApplyText();
        RefreshVisuals();
    }

    private void Documents_HistoryStateChanged(object? sender, EventArgs e) =>
        ApplyHistoryCommandState();

    private void PlannerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _statusGeneration++;
        _statusLifetime.Dispose();
        AppAnimationLayer.CancelTransientBannerMotion(StatusBar);
        LibraryTree.CourseInvoked -= LibraryTree_CourseInvoked;
        LibraryTree.CourseContextRequested -= LibraryTree_CourseContextRequested;
        LibraryTree.CourseStatusDotTapped -= LibraryTree_CourseStatusDotTapped;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Documents.Changed -= Documents_Changed;
        Documents.StateAccepted -= Documents_HistoryStateChanged;
        Documents.RolledBack -= Documents_HistoryStateChanged;
        _services!.BackgroundOperations.Changed -= BackgroundOperations_Changed;
        _services!.Theme.ThemeChanged -= Theme_ThemeChanged;
        _services!.Localization.LanguageChanged -= Localization_LanguageChanged;
        DisarmCenterReflow();
        AppAnimationLayer.CancelResponsiveWidthReflow(CenterPane);
        AppAnimationLayer.CompletePendingPaneExit(LibraryPane);
        AppAnimationLayer.CompletePendingPaneExit(DetailPane);
        _paneState.Clear();
        Unloaded -= PlannerPage_Unloaded;
    }

    private void Theme_ThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        _lastCenterRenderSignature = "";
        _lastLibraryRenderSignature = "";
        RefreshVisuals();
        UpdateColorIndicator();
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        ApplyText();
        _lastCenterRenderSignature = "";
        _lastLibraryRenderSignature = "";
        RefreshVisuals();
        UpdateColorIndicator();
    }

    private void AddKeyboardAccelerators()
    {
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators[^1].Invoked += (_, args) =>
        {
            CourseFilters.FocusSearch(FocusState.Keyboard);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators[^1].Invoked += async (_, args) =>
        {
            if (!CanExecuteHistoryCommand(redo: false))
                return;
            args.Handled = true;
            await ExecuteHistoryCommandAsync(redo: false);
        };
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Y, Modifiers = VirtualKeyModifiers.Control });
        KeyboardAccelerators[^1].Invoked += async (_, args) =>
        {
            if (!CanExecuteHistoryCommand(redo: true))
                return;
            args.Handled = true;
            await ExecuteHistoryCommandAsync(redo: true);
        };
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Z, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift });
        KeyboardAccelerators[^1].Invoked += async (_, args) =>
        {
            if (!CanExecuteHistoryCommand(redo: true))
                return;
            args.Handled = true;
            await ExecuteHistoryCommandAsync(redo: true);
        };
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.Delete });
        KeyboardAccelerators[^1].Invoked += async (_, args) =>
        {
            await DeleteSelectedAsync();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(new KeyboardAccelerator { Key = VirtualKey.F2 });
        KeyboardAccelerators[^1].Invoked += async (_, args) =>
        {
            await RenameCurrentContextAsync();
            args.Handled = true;
        };
    }

    private void ApplyText()
    {
        var t = ViewModel.T;
        LibraryTitle.Text = t["CourseLibrary"];
        DetailTitle.Text = t["Details"];
        CourseFilters.ApplyText(key => t[key]);
        _toolbar.ApplyText(key => t[key]);
        SaveCourseEditButton.Content = t["Save"];
        DiscardCourseEditButton.Content = t["Discard"];
        CourseNameBox.Header = t["Name"];
        TeacherBox.Header = t["Teacher"];
        LocationBox.Header = t["Location"];
        CreditsBox.Header = t["Credits"];
        EnrolledBox.Header = t["Enrolled"];
        CapacityBox.Header = t["Capacity"];
        GroupTypeBox.Header = t["GroupType"];
        StudyTypeBox.Header = t["StudyType"];
        LabelsBox.Header = t["Labels"];
        MeetingsBox.ApplyText(t);
        NotesBox.Header = t["Notes"];
        CourseColorBox.Header = t["CourseColor"];
        RecommendCourseColorButton.Content = t["RecommendedColor"];
        StatusOpenButton.Content = t["Open"];
        ToolTipService.SetToolTip(CloseLibraryButton, t["CloseCourseLibrary"]);
        ToolTipService.SetToolTip(CloseDetailButton, t["CloseDetails"]);
        ToolTipService.SetToolTip(PreviousWeekButton, t["PreviousWeek"]);
        ToolTipService.SetToolTip(NextWeekButton, t["NextWeek"]);
        ToolTipService.SetToolTip(SwapCompareButton, t["SwapComparisonDirection"]);
        WeekSelectorPrefixText.Text = t["WeekSelectorPrefix"];
        WeekSelectorSuffixText.Text = t["WeekSelectorSuffix"];
        WeekSelectorPrefixText.Visibility = string.IsNullOrWhiteSpace(WeekSelectorPrefixText.Text) ? Visibility.Collapsed : Visibility.Visible;
        WeekSelectorSuffixText.Visibility = string.IsNullOrWhiteSpace(WeekSelectorSuffixText.Text) ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PreviousWeekButton, t["PreviousWeek"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NextWeekButton, t["NextWeek"]);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SwapCompareButton, t["SwapComparisonDirection"]);
        SaveCourseEditButton.Content = t["Save"];
    }

    private static bool IsLibraryFilterProperty(string? propertyName) =>
        propertyName is nameof(PlannerViewModel.SearchText)
            or nameof(PlannerViewModel.LabelFilterText)
            or nameof(PlannerViewModel.GroupFilterText)
            or nameof(PlannerViewModel.StudyFilterText)
            or nameof(PlannerViewModel.TeacherFilterText)
            or nameof(PlannerViewModel.LocationFilterText);

    private void SyncFilterFields()
    {
        CourseFilters.SetValues(
            ViewModel.SearchText,
            ViewModel.LabelFilterText,
            ViewModel.GroupFilterText,
            ViewModel.StudyFilterText,
            ViewModel.TeacherFilterText,
            ViewModel.LocationFilterText);
    }

    private void RefreshVisuals()
    {
        BuildLibraryTree();
        WeekTitleText.Text = BuildWeekTitleText();
        SynchronizeWeekNumberBox();
        ApplyWeekHeaderState();
        ApplyCommandReadOnlyState();
        RenderCenterIfNeeded();
        if (ActualWidth > 0)
            UpdateResponsiveColumns(ActualWidth);
    }

    private void SynchronizeWeekNumberBox()
    {
        var maximumWeek = Math.Max(1, ViewModel.CurrentSemester?.WeekCount ?? 1);
        _synchronizingWeekNumberBox = true;
        try
        {
            WeekNumberBox.Maximum = maximumWeek;
            WeekNumberBox.Value = ViewModel.CurrentWeek;
        }
        finally
        {
            _synchronizingWeekNumberBox = false;
        }
    }

    private void ApplyWeekHeaderState()
    {
        var isWeekGridView = ViewModel.ViewMode is PlannerViewMode.Week or PlannerViewMode.Comparison;
        PreviousWeekButton.Visibility = isWeekGridView ? Visibility.Visible : Visibility.Collapsed;
        WeekSelectorHost.Visibility = isWeekGridView ? Visibility.Visible : Visibility.Collapsed;
        NextWeekButton.Visibility = isWeekGridView ? Visibility.Visible : Visibility.Collapsed;
        SwapCompareButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyCommandReadOnlyState()
    {
        var editablePlanner = ViewModel.ViewMode == PlannerViewMode.Week;
        var busy = _services?.BackgroundOperations.IsBusy == true;
        ToggleLibraryButton.IsEnabled = editablePlanner;
        NewCourseButton.IsEnabled = editablePlanner;
        RegistrationOrderButton.IsEnabled = !busy &&
                                            ViewModel.CurrentPlan?.Snapshots.Any(snapshot => !snapshot.IsLocked) == true;
        CompareButton.IsEnabled = !busy && ViewModel.CanOpenSelectedComparison;
        ImportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        ApplyHistoryCommandState();
        _toolbar.QueueLayout();
    }

    private void BackgroundOperations_Changed(object? sender, EventArgs e)
    {
        ApplyCommandReadOnlyState();
        if (_services?.BackgroundOperations.IsBusy == true)
        {
            ShowStatus(
                _services.BackgroundOperations.Message,
                InfoBarSeverity.Informational);
        }
    }

    private static MenuFlyoutItem MenuItem(string text, Symbol symbol, RoutedEventHandler click)
    {
        return MenuItem(text, new SymbolIcon(symbol), click);
    }

    private static MenuFlyoutItem MenuItem(string text, IconElement icon, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = icon };
        item.Click += click;
        return item;
    }

    private static MenuFlyout CreateTransientMenuFlyout() =>
        AppMaterialLayer.CreateTransientMenuFlyout();

    private void BuildLibraryTree()
    {
        var signature = CourseLibraryRenderSignature.Build(
            ViewModel.T.ResolvedLanguage,
            ViewModel.CurrentSemester,
            ViewModel.CurrentPlan,
            ViewModel.CurrentWeek,
            Documents.Document.CourseLibrary,
            ViewModel.LibraryGroups);
        if (string.Equals(signature, _lastLibraryRenderSignature, StringComparison.Ordinal))
            return;

        LibraryTree.CourseStatusAutomationName = ViewModel.T["CourseStatusInCurrentPlan"];
        LibraryTree.EmptyText = Documents.Document.CourseLibrary.Count == 0
            ? ViewModel.T["NoCoursesInLibrary"]
            : ViewModel.T["NoMatchingCourses"];
        var statusIndex = CourseLibraryStatusService.CreateIndex(
            ViewModel.CurrentPlan,
            ViewModel.CurrentSemester,
            ViewModel.CurrentWeek,
            Documents.Document.CourseLibrary,
            key => ViewModel.T[key]);
        LibraryTree.SetGroups(CourseLibraryTreeRenderModelBuilder.Build(
            ViewModel.LibraryGroups,
            ViewModel.T,
            Display.CourseListSummary,
            statusIndex.Resolve));
        _lastLibraryRenderSignature = signature;
    }

    private void RenderCenterIfNeeded(bool force = false)
    {
        var signature = BuildCenterRenderSignature();
        if (!force && string.Equals(signature, _lastCenterRenderSignature, StringComparison.Ordinal))
            return;

        _lastCenterRenderSignature = signature;
        RenderCenter();
        ApplyResponsiveDateHeaders(TimetableScrollViewer.ActualWidth, force: true);
        QueueTimetableLayoutRefresh();
    }

    private string BuildCenterRenderSignature()
        => TimetableRenderSignatureService.Build(
            ViewModel.CurrentSemester,
            ViewModel.CurrentPlan,
            ViewModel.BaseComparePlan,
            ViewModel.ViewMode,
            ViewModel.CurrentWeek,
            ActualTheme.ToString(),
            Documents.Document.CourseLibrary);

    private void RenderCenter()
    {
        if (_pendingCenterRefreshDirection is not { } direction)
        {
            AppAnimationLayer.RefreshContent(TimetableHost, RenderCenterCore);
            return;
        }

        _pendingCenterRefreshDirection = null;
        var nextHost = CreateTimetableHost();
        _renderTargetTimetableHost = nextHost;
        try
        {
            RenderCenterCore();
            ApplyResponsiveDateHeaders(TimetableScrollViewer.ActualWidth, force: true);
        }
        finally
        {
            _renderTargetTimetableHost = null;
        }

        MatchTimetableTransitionGeometry(_activeTimetableHost, nextHost);
        _activeTimetableHost = nextHost;
        var contentToken = TimetableTransitionPage.QueueContent(nextHost);
        if (!AppAnimationLayer.NavigateDirectional(
            TimetableFrame,
            typeof(TimetableTransitionPage),
            contentToken,
            direction))
        {
            TimetableTransitionPage.CancelQueuedContent(contentToken);
            TimetableFrame.Content = new TimetableTransitionPage
            {
                Content = nextHost
            };
        }
        QueueTimetableLayoutRefresh();
    }

    private static void MatchTimetableTransitionGeometry(Grid current, Grid next)
    {
        var width = current.ActualWidth > 0
            ? current.ActualWidth
            : current.Width;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return;

        next.Width = width;
        next.MinWidth = current.MinWidth;
    }

    private Grid TimetableHost =>
        _renderTargetTimetableHost ?? _activeTimetableHost;

    private Grid CreateTimetableHost()
    {
        var host = new Grid
        {
            AllowDrop = true,
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(host, "TimetableHost");
        AppAnimationLayer.SetProfile(host, AppAnimationProfile.ContentRefresh);
        host.DragOver += TimetableHost_DragOver;
        host.Drop += TimetableHost_Drop;
        return host;
    }

    private void RenderCenterCore()
    {
        _dateHeaderTexts.Clear();
        _lastDateHeaderTextMode = null;
        _courseBlockLayouts.Clear();
        TimetableHost.Children.Clear();
        TimetableHost.RowDefinitions.Clear();
        TimetableHost.ColumnDefinitions.Clear();
        TimetableHost.MinWidth = 0;
        TimetableHost.Width = double.NaN;
        TimetableHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        ClearComparisonLayoutReferences();

        if (ViewModel.CurrentSemester is null || ViewModel.CurrentPlan is null)
        {
            RenderEmptyState();
            return;
        }

        if (ViewModel.ViewMode == PlannerViewMode.SemesterOverview)
            RenderSemesterOverview();
        else if (ViewModel.ViewMode == PlannerViewMode.Comparison && ViewModel.BaseComparePlan is not null)
            RenderComparisonView();
        else
            RenderWeekGrid(new WeekGridOptions
            {
                Host = TimetableHost,
                Plan = ViewModel.CurrentPlan!,
                Role = WeekGridRole.Standard,
                AllowBlankSlotCreate = true
            });
    }

    private void ClearComparisonLayoutReferences()
    {
        _comparisonGrid = null;
        _comparisonBaseColumn = null;
        _comparisonDividerColumn = null;
        _comparisonCurrentColumn = null;
        _comparisonSwapButton = null;
        _comparisonBlocksBySlot.Clear();
    }

    private void RenderEmptyState()
    {
        TimetableHost.RowDefinitions.Add(new RowDefinition());
        TimetableHost.ColumnDefinitions.Add(new ColumnDefinition());
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = ViewModel.CurrentSemester is null ? ViewModel.T["NoSemesters"] : ViewModel.T["NoOpenPlans"],
            Style = AppTypography.TextStyle(AppTextRole.Subtitle)
        });
        if (ViewModel.CurrentSemester is null)
        {
            var semesterButton = new Button { Content = ViewModel.T["Semesters"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(semesterButton, "OpenSemestersFromPlannerButton");
            semesterButton.Click += (_, _) =>
            {
                _services!.Navigation.RequestSemesters();
            };
            panel.Children.Add(semesterButton);
        }
        else
        {
            var button = new Button { Content = ViewModel.T["NewPlan"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "NewPlanFromEmptyStateButton");
            button.Click += NewPlan_Click;
            panel.Children.Add(button);
            var openButton = new Button { Content = ViewModel.T["OpenPlan"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(openButton, "OpenPlanFromEmptyStateButton");
            openButton.Click += OpenPlan_Click;
            panel.Children.Add(openButton);
        }
        TimetableHost.Children.Add(panel);
    }

    private void RenderSemesterOverview()
    {
        var semester = ViewModel.CurrentSemester!;
        var weekdayOrder = SemesterRules.GetWeekdayOrder(semester.WeekStartDay).ToList();
        var overviewSlots = TimetableRenderModelService.BuildOverviewCourseBySlot(
            PlanCourseResolver.Courses(ViewModel.CurrentPlan!, Documents.Document.CourseLibrary),
            semester,
            maximumPeriod: 8);
        var overviewSlotsByWeek = overviewSlots
            .GroupBy(pair => pair.Key.Week)
            .ToDictionary(group => group.Key, group => group.ToList());
        var columns = ResolveSemesterOverviewColumnCount(TimetableScrollViewer.ActualWidth);
        _lastSemesterOverviewColumnCount = columns;
        for (var i = 0; i < columns; i++)
            TimetableHost.ColumnDefinitions.Add(new ColumnDefinition());
        var rows = (int)Math.Ceiling(semester.WeekCount / (double)columns);
        for (var i = 0; i < rows; i++)
            TimetableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var week = 1; week <= semester.WeekCount; week++)
        {
            var card = new Button
            {
                Margin = new Thickness(6),
                Padding = new Thickness(10),
                MinHeight = 160,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            ApplySemesterOverviewCardResources(card);
            AppMaterialLayer.ApplySurface(card, AppMaterialSurface.SemesterOverviewCard);
            var grid = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
            grid.Children.Add(new TextBlock
            {
                Text = string.Format(ViewModel.T["ExportWeekHeadingFormat"], week),
                Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
            });
            var mini = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            for (var c = 0; c < 7; c++)
                mini.ColumnDefinitions.Add(new ColumnDefinition());
            for (var r = 0; r < Math.Min(semester.PeriodSchedule.Count, 8); r++)
                mini.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            if (overviewSlotsByWeek.TryGetValue(week, out var occupiedSlots))
            {
                foreach (var (slot, course) in occupiedSlots)
                {
                    var cell = new Border
                    {
                        Margin = new Thickness(1),
                        Background = AppBrushes.FromHex(CourseDecorativeColor(course)),
                        CornerRadius = new CornerRadius(2)
                    };
                    Grid.SetColumn(cell, weekdayOrder.IndexOf(slot.Weekday));
                    Grid.SetRow(cell, slot.Period - 1);
                    mini.Children.Add(cell);
                }
            }
            Grid.SetRow(mini, 1);
            grid.Children.Add(mini);
            card.Content = grid;
            var capturedWeek = week;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, $"SemesterWeekCard{capturedWeek}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, string.Format(ViewModel.T["ExportWeekHeadingFormat"], capturedWeek));
            card.Click += async (_, _) =>
            {
                if (!await ConfirmUnsavedCourseEditAsync())
                    return;
                ViewModel.CurrentWeek = capturedWeek;
                ViewModel.ViewMode = PlannerViewMode.Week;
                RefreshVisuals();
            };
            ToolTipService.SetToolTip(card, SemesterRules.WeekRangeText(semester, week));
            Grid.SetColumn(card, (week - 1) % columns);
            Grid.SetRow(card, (week - 1) / columns);
            TimetableHost.Children.Add(card);
        }
    }

    private void ApplySemesterOverviewCardResources(Button card)
    {
        var normal = AppMaterialLayer.Brush(card, AppMaterialSurface.SemesterOverviewCard, Colors.Transparent);
        var hover = RoleBrush(AppColorRole.SemesterOverviewCardHover, Colors.Transparent);
        var pressed = RoleBrush(AppColorRole.SemesterOverviewCardPressed, Colors.Transparent);
        var stroke = AppMaterialLayer.Brush(card, "AppMaterialCardStrokeBrush", Colors.Transparent);

        card.Resources["ButtonBackground"] = normal;
        card.Resources["ButtonBackgroundPointerOver"] = hover;
        card.Resources["ButtonBackgroundPressed"] = pressed;
        card.Resources["ButtonBorderBrush"] = stroke;
        card.Resources["ButtonBorderBrushPointerOver"] = stroke;
        card.Resources["ButtonBorderBrushPressed"] = stroke;
    }

    private void RenderComparisonView()
    {
        var basePlan = ViewModel.BaseComparePlan!;
        var currentPlan = ViewModel.CurrentPlan!;
        var differences = ViewModel.GetCurrentDifferences();
        var semester = ViewModel.CurrentSemester!;
        var baseBlocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            PlanCourseResolver.Courses(basePlan, Documents.Document.CourseLibrary),
            semester,
            ViewModel.CurrentWeek,
            differences,
            includeConflictLayout: false,
            includeInactiveMeetings: true);
        var currentBlocks = TimetableRenderModelService.BuildWeekCourseBlocks(
            PlanCourseResolver.Courses(currentPlan, Documents.Document.CourseLibrary),
            semester,
            ViewModel.CurrentWeek,
            differences,
            includeInactiveMeetings: true);
        var materialization = TimetableUiMaterializationPolicy.Evaluate(
            semester.PeriodSchedule.Count,
            baseBlocks.Count,
            currentBlocks.Count);
        _comparisonBlocksBySlot.Clear();

        TimetableHost.ColumnDefinitions.Add(new ColumnDefinition());
        TimetableHost.RowDefinitions.Add(new RowDefinition());
        TimetableHost.MinWidth = 0;
        TimetableHost.HorizontalAlignment = HorizontalAlignment.Stretch;

        var baseColumn = new ColumnDefinition();
        var dividerColumn = new ColumnDefinition();
        var currentColumn = new ColumnDefinition();
        var comparisonGrid = new Grid
        {
            ColumnDefinitions =
            {
                baseColumn,
                dividerColumn,
                currentColumn
            }
        };
        _comparisonGrid = comparisonGrid;
        _comparisonBaseColumn = baseColumn;
        _comparisonDividerColumn = dividerColumn;
        _comparisonCurrentColumn = currentColumn;

        AddComparisonPane(
            comparisonGrid,
            0,
            basePlan,
            WeekGridRole.CompareBase,
            differences,
            baseBlocks,
            materialization);
        AddComparisonDivider(comparisonGrid);
        AddComparisonPane(
            comparisonGrid,
            2,
            currentPlan,
            WeekGridRole.CompareCurrent,
            differences,
            currentBlocks,
            materialization);
        TimetableHost.Children.Add(comparisonGrid);
        ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
    }

    private void AddComparisonPane(
        Grid comparisonGrid,
        int column,
        SelectionPlan plan,
        WeekGridRole role,
        IReadOnlyList<SlotDifference> differences,
        IReadOnlyList<TimetableCourseBlock> courseBlocks,
        TimetableUiMaterializationDecision materialization)
    {
        var pane = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition()
            },
            Background = RoleBrush(AppColorRole.TimetableBackground, Colors.Transparent)
        };

        var header = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = plan.PlanName,
                        Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
        AppMaterialLayer.ApplySurface(header, AppMaterialSurface.CommandBar);
        pane.Children.Add(header);

        var weekGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(weekGrid, 1);
        pane.Children.Add(weekGrid);

        RenderWeekGrid(new WeekGridOptions
        {
            Host = weekGrid,
            Plan = plan,
            Differences = differences,
            CourseBlocks = courseBlocks,
            MaterializationDecision = materialization,
            Role = role,
            AllowBlankSlotCreate = false
        });

        Grid.SetColumn(pane, column);
        comparisonGrid.Children.Add(pane);
    }

    private void AddComparisonDivider(Grid comparisonGrid)
    {
        var divider = new Grid
        {
            Background = RoleBrush(AppColorRole.TimetableBackground, Colors.Transparent)
        };
        var dividerLine = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AppMaterialLayer.ApplySurface(dividerLine, AppMaterialSurface.Divider);
        divider.Children.Add(dividerLine);
        var swapButton = new Button
        {
            Width = TimetableComparisonSwapButtonSize,
            Height = TimetableComparisonSwapButtonSize,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = RoleBrush(AppColorRole.TimetableBackground, Colors.Transparent),
            BorderBrush = SurfaceBrush(AppMaterialSurface.Divider, Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Content = new SymbolIcon(Symbol.Switch)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(swapButton, "InlineSwapCompareButton");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swapButton, ViewModel.T["SwapComparisonDirection"]);
        ToolTipService.SetToolTip(swapButton, ViewModel.T["SwapComparisonDirection"]);
        swapButton.Click += SwapCompare_Click;
        _comparisonSwapButton = swapButton;
        UpdateComparisonSwapButtonPosition();
        divider.Children.Add(swapButton);

        Grid.SetColumn(divider, 1);
        comparisonGrid.Children.Add(divider);
    }

    private void RenderWeekGrid(WeekGridOptions options)
    {
        var host = options.Host;
        var semester = ViewModel.CurrentSemester!;
        var weekdayOrder = SemesterRules.GetWeekdayOrder(semester.WeekStartDay).ToList();
        var periodRows = semester.PeriodSchedule.OrderBy(x => x.Period).ToList();
        var courseBlocks = options.CourseBlocks ?? TimetableRenderModelService.BuildWeekCourseBlocks(
            PlanCourseResolver.Courses(options.Plan, Documents.Document.CourseLibrary),
            semester,
            ViewModel.CurrentWeek,
            options.Differences,
            includeConflictLayout: options.Role != WeekGridRole.CompareBase,
            includeInactiveMeetings: true);
        var materialization = options.MaterializationDecision ?? TimetableUiMaterializationPolicy.Evaluate(
            periodRows.Count,
            courseBlocks.Count);
        if (materialization.Mode == TimetableUiPresentationMode.VirtualizedList)
        {
            RenderDenseWeekList(options, courseBlocks, materialization);
            if (ReferenceEquals(host, TimetableHost))
                ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
            return;
        }

        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimetablePeriodColumnWidth) });
        foreach (var _ in weekdayOrder)
        {
            var column = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            host.ColumnDefinitions.Add(column);
        }
        host.MinWidth = 0;
        host.Width = double.NaN;
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TimetableHeaderRowHeight) });
        foreach (var _ in periodRows)
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TimetablePeriodRowHeight) });

        AddCell(host, 0, 0, "", true);
        var dates = SemesterRules.GetWeekDates(semester, ViewModel.CurrentWeek);
        for (var c = 0; c < weekdayOrder.Count; c++)
        {
            var date = dates[c];
            var header = new StackPanel { Spacing = 1, Padding = new Thickness(6) };
            header.Children.Add(new TextBlock
            {
                Text = WeekdayText(weekdayOrder[c]),
                Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            header.Children.Add(CreateResponsiveDateText(date));
            if (SemesterRules.IsOutsideSemester(semester, date))
                header.Children.Add(new TextBlock
                {
                    Text = date < semester.StartDate ? ViewModel.T["BeforeSemester"] : ViewModel.T["AfterSemester"],
                    Style = AppTypography.TextStyle(AppTextRole.Caption),
                    FontSize = 12,
                    Foreground = RoleBrush(AppColorRole.TextSecondary, Colors.Gray),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                });
            AddElementCell(host, c + 1, 0, header, SemesterRules.IsOutsideSemester(semester, date));
        }

        for (var periodIndex = 0; periodIndex < periodRows.Count; periodIndex++)
        {
            var period = periodRows[periodIndex];
            var rowIndex = periodIndex + 1;
            var panel = new StackPanel { Padding = new Thickness(6), Spacing = 1 };
            panel.Children.Add(new TextBlock
            {
                Text = period.Period.ToString(),
                Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{period.Start:HH\\:mm}-{period.End:HH\\:mm}",
                Style = AppTypography.TextStyle(AppTextRole.Caption),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = RoleBrush(AppColorRole.TextSecondary, Colors.Gray)
            });
            AddElementCell(host, 0, rowIndex, panel, false);
            for (var c = 0; c < weekdayOrder.Count; c++)
                AddBlankSlotCell(host, c + 1, rowIndex, weekdayOrder[c], period.Period, options.AllowBlankSlotCreate);
        }

        var snapshotsByOfferingId = options.Plan.Snapshots
            .GroupBy(x => x.CourseOfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var courseBlock in courseBlocks)
        {
            if (!snapshotsByOfferingId.TryGetValue(courseBlock.Course.OfferingId, out var snapshot))
                continue;

            var column = weekdayOrder.IndexOf(courseBlock.Slot.Weekday);
            if (column < 0)
                continue;

            var block = CreateCourseBlock(snapshot, options.Role, courseBlock);
            Grid.SetColumn(block, column + 1);
            Grid.SetRow(block, courseBlock.StartPeriod);
            Grid.SetRowSpan(block, courseBlock.EndPeriod - courseBlock.StartPeriod + 1);
            options.Host.Children.Add(block);
        }

        if (ReferenceEquals(host, TimetableHost))
            ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
    }

    private void RenderDenseWeekList(
        WeekGridOptions options,
        IReadOnlyList<TimetableCourseBlock> courseBlocks,
        TimetableUiMaterializationDecision materialization)
    {
        var host = options.Host;
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        host.ColumnDefinitions.Add(new ColumnDefinition());
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition());
        host.MinWidth = 0;
        host.Width = double.NaN;
        host.HorizontalAlignment = HorizontalAlignment.Stretch;

        var headerContent = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = ViewModel.T["TimetableDenseModeTitle"],
                    Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
                },
                new TextBlock
                {
                    Text = string.Format(
                        ViewModel.T["TimetableDenseModeMessage"],
                        courseBlocks.Count,
                        materialization.EstimatedVisualElementCount),
                    Style = AppTypography.TextStyle(AppTextRole.Caption),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        var header = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Child = headerContent
        };
        AppMaterialLayer.ApplySurface(header, AppMaterialSurface.CommandBar);
        host.Children.Add(header);

        var snapshotsByOfferingId = options.Plan.Snapshots
            .GroupBy(snapshot => snapshot.CourseOfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var denseItems = new List<DenseTimetableItem>(courseBlocks.Count);
        foreach (var block in courseBlocks)
        {
            snapshotsByOfferingId.TryGetValue(block.Course.OfferingId, out var snapshot);
            var difference = ComparisonDifferenceLabel(block.Difference);
            var schedule = $"{WeekdayText(block.Slot.Weekday)} · " +
                           string.Format(
                               ViewModel.T["MeetingPeriodsFormat"],
                               block.StartPeriod,
                               block.EndPeriod);
            var metadata = Display.CourseMetadataLine(block.Course);
            var details = new[] { difference, schedule, metadata }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            denseItems.Add(new DenseTimetableItem
            {
                DisplayText = $"{CourseBlockTitle(block.Course, block.IsInRequestedWeek)} · {string.Join(" · ", details)}",
                Snapshot = snapshot
            });
        }

        var list = new ListView
        {
            ItemsSource = denseItems,
            DisplayMemberPath = nameof(DenseTimetableItem.DisplayText),
            IsItemClickEnabled = options.Role == WeekGridRole.Standard,
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(list, "DenseTimetableList");
        AutomationProperties.SetName(list, ViewModel.T["TimetableDenseModeTitle"]);
        if (options.Role == WeekGridRole.Standard)
        {
            list.ItemClick += async (_, args) =>
            {
                if (args.ClickedItem is not DenseTimetableItem { Snapshot: { } snapshot })
                    return;
                if (!await ConfirmUnsavedCourseEditAsync())
                    return;
                await OpenDetailsAsync(() => ViewModel.BeginEditPlanSnapshot(snapshot));
            };
        }

        Grid.SetRow(list, 1);
        host.Children.Add(list);
    }

    private Button CreateCourseBlock(PlanCourseSnapshot snapshot, WeekGridRole role, TimetableCourseBlock courseBlock)
    {
        var course = courseBlock.Course;
        var diff = courseBlock.Difference;
        var slot = courseBlock.Slot;
        var conflictIndex = courseBlock.ConflictIndex;
        var conflictCount = courseBlock.ConflictCount;
        var decorativeColor = CourseDecorativeColor(course);
        var isLocked = snapshot.IsLocked;
        var isInRequestedWeek = courseBlock.IsInRequestedWeek;
        var background = CourseBlockBackgroundBrush(diff, role, isLocked, isInRequestedWeek);
        var hoverBackground = CourseBlockHoverBackgroundBrush(diff, role, isLocked, isInRequestedWeek);
        var leftMargin = conflictCount > 1 ? 4 + (conflictIndex * 10) : 4;
        var rightMargin = conflictCount > 1 ? 4 + ((conflictCount - conflictIndex - 1) * 10) : 4;
        var isConflict = courseBlock.HasConflictInRequestedWeek;
        var differenceLabel = ComparisonDifferenceLabel(diff);
        var foreground = isLocked
            ? RoleBrush(AppColorRole.TextSecondary, Colors.DimGray)
            : RoleBrush(AppColorRole.TextPrimary, Colors.Black);
        var secondaryForeground = RoleBrush(AppColorRole.TextSecondary, Colors.Gray);
        var courseBlockCornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        var block = new Button
        {
            Margin = new Thickness(leftMargin, 4, rightMargin, 4),
            CornerRadius = courseBlockCornerRadius,
            Background = AppBrushes.Transparent(),
            BorderBrush = AppBrushes.Transparent(),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            UseLayoutRounding = true
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(block, role == WeekGridRole.Standard ? "CourseBlock" : $"CourseBlock{role}");
        var stateLabels = new List<string>();
        if (isLocked)
            stateLabels.Add(ViewModel.T["CourseLocked"]);
        if (!isInRequestedWeek)
            stateLabels.Add(ViewModel.T["CourseNotThisWeek"]);
        if (differenceLabel is not null)
            stateLabels.Add(differenceLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            block,
            stateLabels.Count == 0
                ? course.CourseName
                : $"{course.CourseName}, {string.Join(", ", stateLabels)}");
        if (stateLabels.Count > 0)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(block, string.Join(", ", stateLabels));
        var layout = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(5) },
                new ColumnDefinition()
            }
        };
        layout.Children.Add(new Border
        {
            Background = isLocked || !isInRequestedWeek
                ? RoleBrush(AppColorRole.TextSecondary, Colors.Gray)
                : AppBrushes.FromHex(decorativeColor),
            CornerRadius = new CornerRadius(courseBlockCornerRadius.TopLeft, 0, 0, courseBlockCornerRadius.BottomLeft)
        });
        var content = new StackPanel { Spacing = 2 };
        var title = CreateCourseBlockText(
            differenceLabel is null
                ? CourseBlockTitle(course, isInRequestedWeek)
                : $"{differenceLabel}: {CourseBlockTitle(course, isInRequestedWeek)}",
            foreground,
            bold: true);
        content.Children.Add(title);

        var details = new StackPanel { Spacing = 2 };
        var detailTexts = new List<TextBlock>();
        AddCourseBlockDetail(details, detailTexts, course.Teacher, secondaryForeground);
        AddCourseBlockDetail(details, detailTexts, course.Location, secondaryForeground);
        AddCourseBlockDetail(details, detailTexts, CourseDerivedValues.CapacityText(course), CapacityBrush(course));
        if (isConflict)
            AddCourseBlockDetail(details, detailTexts, ViewModel.T["Conflict"], RoleBrush(AppColorRole.StatusCritical, Colors.IndianRed));
        FrameworkElement? detailClip = null;
        if (detailTexts.Count > 0)
        {
            detailClip = details;
            content.Children.Add(detailClip);
        }

        var contentHost = new Border
        {
            Padding = new Thickness(7, 6, 7, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = content
        };
        var layoutRegistration = new CourseBlockLayoutRegistration
        {
            ContentHost = contentHost,
            Title = title,
            DetailClip = detailClip,
            DetailTexts = detailTexts
        };
        _courseBlockLayouts.Add(layoutRegistration);
        contentHost.Loaded += (_, _) => QueueTimetableLayoutRefresh();
        contentHost.SizeChanged += (_, _) => layoutRegistration.Refresh();
        Grid.SetColumn(contentHost, 1);
        layout.Children.Add(contentHost);
        var visual = new Border
        {
            Background = background,
            BorderBrush = AppBrushes.Transparent(),
            BorderThickness = new Thickness(0),
            CornerRadius = courseBlockCornerRadius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = layout
        };
        block.Tag = new CourseBlockVisualState
        {
            Visual = visual,
            NormalBackground = background,
            HoverBackground = hoverBackground
        };
        block.Content = visual;
        block.PointerEntered += (_, _) => SetCourseBlockHover(block, isHovering: true);
        block.PointerExited += (_, _) => SetCourseBlockHover(block, isHovering: false);
        block.GotFocus += (_, _) => SetCourseBlockHover(block, isHovering: true);
        block.LostFocus += (_, _) => SetCourseBlockHover(block, isHovering: false);
        if (role is (WeekGridRole.CompareBase or WeekGridRole.CompareCurrent) && diff?.Kind == DifferenceKind.Replaced)
            RegisterComparisonBlock(slot, block);

        block.Click += async (_, _) =>
        {
            if (role == WeekGridRole.Standard)
            {
                if (!await ConfirmUnsavedCourseEditAsync())
                    return;
                await OpenDetailsAsync(() => ViewModel.BeginEditPlanSnapshot(snapshot));
            }
            else if (diff?.Kind == DifferenceKind.Replaced)
            {
                await FlashComparisonCounterpartsAsync(slot, block);
            }
        };
        block.DoubleTapped += (_, _) =>
        {
            if (role == WeekGridRole.Standard)
                CourseNameBox.Focus(FocusState.Programmatic);
        };
        if (role == WeekGridRole.Standard)
            block.RightTapped += (_, e) =>
            {
                ShowCourseContextMenu(block, snapshot, e.GetPosition(block));
                e.Handled = true;
            };
        var tooltip = Display.CourseTooltipText(course);
        if (!isInRequestedWeek)
            tooltip = $"{ViewModel.T["CourseNotThisWeek"]}: {tooltip}";
        if (isLocked)
            tooltip = $"{ViewModel.T["CourseLocked"]}: {tooltip}";
        if (differenceLabel is not null)
            tooltip = $"{differenceLabel}: {tooltip}";
        if (diff?.Kind == DifferenceKind.Replaced && diff.BaseCourse is not null && diff.BaseCourse.OfferingId != course.OfferingId)
        {
            var baseMetadata = Display.CourseMetadataLine(diff.BaseCourse);
            var baseLine = string.IsNullOrWhiteSpace(baseMetadata)
                ? diff.BaseCourse.CourseName
                : $"{diff.BaseCourse.CourseName} / {baseMetadata}";
            tooltip += $"{Environment.NewLine}{ViewModel.T["BaseCourse"]}: {baseLine}";
        }
        ToolTipService.SetToolTip(block, tooltip);
        return block;
    }

    private string? ComparisonDifferenceLabel(SlotDifference? difference) =>
        difference?.Kind switch
        {
            DifferenceKind.Added => ViewModel.T["DifferenceAdded"],
            DifferenceKind.Removed => ViewModel.T["DifferenceRemoved"],
            DifferenceKind.Replaced => ViewModel.T["DifferenceModified"],
            _ => null
        };

    private void RegisterComparisonBlock(TimetableSlot slot, Button block)
    {
        if (!_comparisonBlocksBySlot.TryGetValue(slot, out var blocks))
        {
            blocks = new List<Button>();
            _comparisonBlocksBySlot[slot] = blocks;
        }

        blocks.Add(block);
    }

    private async Task FlashComparisonCounterpartsAsync(TimetableSlot slot, Button source)
    {
        if (!_comparisonBlocksBySlot.TryGetValue(slot, out var blocks))
            return;

        var targets = blocks.Where(block => !ReferenceEquals(block, source)).ToList();
        if (targets.Count == 0)
            return;

        await Task.WhenAll(targets.Select(PlayCourseBlockFlashAsync));
    }

    private async Task PlayCourseBlockFlashAsync(Button block)
    {
        var visual = CourseBlockVisual(block);
        if (visual is null)
            return;

        var highlightBrush = RoleBrush(AppColorRole.FlashBorder, Colors.Gold);
        await AppAnimationLayer.PlayAttentionAsync(
            visual,
            highlightBrush,
            new Thickness(3));
    }

    private static void SetCourseBlockHover(Button block, bool isHovering)
    {
        if (block.Tag is not CourseBlockVisualState state)
            return;

        state.Visual.Background = isHovering ? state.HoverBackground : state.NormalBackground;
    }

    private static Border? CourseBlockVisual(Button block) =>
        block.Tag is CourseBlockVisualState state ? state.Visual : block.Tag as Border;

    private static TextBlock CreateCourseBlockText(string text, Brush foreground, bool bold)
    {
        return new TextBlock
        {
            Text = text,
            Tag = text,
            FontFamily = bold ? AppTypography.CourseBlockBoldFontFamily : AppTypography.CourseBlockFontFamily,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = foreground,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.WordEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextLineBounds = TextLineBounds.Full,
            MaxLines = 1
        };
    }

    private static void AddCourseBlockDetail(Panel panel, ICollection<TextBlock> detailTexts, string text, Brush foreground)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var detail = CreateCourseBlockText(text, foreground, bold: false);
        detailTexts.Add(detail);
        panel.Children.Add(detail);
    }

    private static void UpdateCourseBlockTextLayout(
        Size size,
        Border contentHost,
        TextBlock title,
        FrameworkElement? detailClip,
        IReadOnlyList<TextBlock> detailTexts)
    {
        var ultraCompact = size.Width < 56;
        var compact = ultraCompact || size.Height < 72 || size.Width < 104;
        var titleSize = ultraCompact ? 10 : compact ? 11 : 13;
        var detailSize = ultraCompact ? 9 : compact ? 10 : 12;
        var titleLineHeight = AppTypography.CourseBlockLineHeight(titleSize, bold: true);
        var detailLineHeight = AppTypography.CourseBlockLineHeight(detailSize, bold: false);
        var targetPadding = new Thickness(size.Width < 72 ? 4 : 7, compact ? 4 : 6, size.Width < 72 ? 4 : 7, compact ? 4 : 6);
        var availableHeight = Math.Max(0, size.Height - targetPadding.Top - targetPadding.Bottom);
        var availableTextWidth = Math.Max(0, size.Width - targetPadding.Left - targetPadding.Right);

        if (contentHost.Padding != targetPadding)
            contentHost.Padding = targetPadding;

        if (availableHeight < titleLineHeight || availableTextWidth < 18)
        {
            title.Visibility = Visibility.Visible;
            title.FontSize = titleSize;
            title.LineHeight = titleLineHeight;
            title.MaxLines = 1;
            title.Text = CourseBlockRawText(title);
            if (detailClip is not null)
                detailClip.Visibility = Visibility.Collapsed;
            foreach (var detail in detailTexts)
                detail.Visibility = Visibility.Collapsed;
            return;
        }

        title.Visibility = Visibility.Visible;
        title.FontSize = titleSize;
        title.LineHeight = titleLineHeight;
        foreach (var detail in detailTexts)
        {
            detail.FontSize = detailSize;
            detail.LineHeight = detailLineHeight;
        }

        var titleLineCapacity = Math.Max(1, (int)Math.Floor(availableHeight / titleLineHeight));
        var titleLinesNeeded = MeasureCourseBlockTextLines(title, availableTextWidth, titleLineHeight);
        var titleLines = Math.Min(titleLinesNeeded, titleLineCapacity);
        var detailLayout = CalculateCourseBlockDetailLayout(availableHeight, titleLines, titleLineHeight, detailLineHeight, detailTexts.Count);

        title.MaxLines = Math.Max(1, titleLines);
        title.Text = FormatCourseBlockText(title, availableTextWidth, title.MaxLines);
        if (detailClip is not null)
        {
            detailClip.Visibility = detailLayout.VisibleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < detailTexts.Count; i++)
            {
                detailTexts[i].Visibility = i < detailLayout.VisibleCount ? Visibility.Visible : Visibility.Collapsed;
                detailTexts[i].MaxLines = i < detailLayout.VisibleCount ? detailLayout.MaxLinesPerDetail[i] : 1;
                detailTexts[i].Text = FormatCourseBlockText(detailTexts[i], availableTextWidth, detailTexts[i].MaxLines);
            }
        }
    }

    private static int MeasureCourseBlockTextLines(TextBlock text, double availableWidth, double lineHeight)
    {
        if (availableWidth <= 0 || lineHeight <= 0)
            return 1;

        var lines = TextRules.WrapTextWithAsciiHyphenation(
            CourseBlockRawText(text),
            64,
            candidate => FitsCourseBlockLine(text, candidate, availableWidth));

        return Math.Max(1, lines.Count);
    }

    private static string FormatCourseBlockText(TextBlock text, double availableWidth, int maxLines)
    {
        var lines = TextRules.WrapTextWithAsciiHyphenation(
            CourseBlockRawText(text),
            maxLines,
            candidate => FitsCourseBlockLine(text, candidate, availableWidth));

        return string.Join(Environment.NewLine, lines);
    }

    private static bool FitsCourseBlockLine(TextBlock text, string candidate, double availableWidth)
    {
        var originalText = text.Text;
        var originalWrapping = text.TextWrapping;
        var originalMaxLines = text.MaxLines;

        text.Text = candidate;
        text.TextWrapping = TextWrapping.NoWrap;
        text.MaxLines = 1;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var fits = text.DesiredSize.Width <= availableWidth + 0.5;
        text.Text = originalText;
        text.TextWrapping = originalWrapping;
        text.MaxLines = originalMaxLines;
        return fits;
    }

    private static string CourseBlockRawText(TextBlock text) => text.Tag as string ?? text.Text;

    private static CourseBlockDetailLayout CalculateCourseBlockDetailLayout(
        double availableHeight,
        int titleLines,
        double titleLineHeight,
        double detailLineHeight,
        int detailCount)
    {
        if (detailCount == 0)
            return new CourseBlockDetailLayout(0, Array.Empty<int>());

        const double stackSpacing = 2;
        var remainingHeight = availableHeight - (titleLines * titleLineHeight) - stackSpacing;
        if (remainingHeight < detailLineHeight)
            return new CourseBlockDetailLayout(0, Array.Empty<int>());

        var visibleCount = 0;
        for (var count = 1; count <= detailCount; count++)
        {
            var minimumHeight = (count * detailLineHeight) + ((count - 1) * stackSpacing);
            if (minimumHeight <= remainingHeight)
                visibleCount = count;
        }

        if (visibleCount == 0)
            return new CourseBlockDetailLayout(0, Array.Empty<int>());

        var lineBudget = Math.Max(visibleCount, (int)Math.Floor((remainingHeight - ((visibleCount - 1) * stackSpacing)) / detailLineHeight));
        var maxLinesPerDetail = new int[visibleCount];
        for (var i = 0; i < visibleCount; i++)
            maxLinesPerDetail[i] = 1;

        var extraLines = lineBudget - visibleCount;
        var index = 0;
        var lineLimit = Math.Min(4, Math.Max(1, lineBudget));
        while (extraLines > 0 && visibleCount > 0)
        {
            if (maxLinesPerDetail[index] < lineLimit)
            {
                maxLinesPerDetail[index]++;
                extraLines--;
            }

            index = (index + 1) % visibleCount;
            if (maxLinesPerDetail.All(lines => lines >= lineLimit))
                break;
        }

        return new CourseBlockDetailLayout(visibleCount, maxLinesPerDetail);
    }

    private readonly record struct CourseBlockDetailLayout(int VisibleCount, int[] MaxLinesPerDetail);

    private void AddBlankSlotCell(Grid host, int column, int row, int weekday, int period, bool allowCreate)
    {
        var border = AddCell(host, column, row, "", false);
        if (!allowCreate)
            return;

        border.Tapped += async (_, _) =>
        {
            if (await ConfirmUnsavedCourseEditAsync())
            {
                ViewModel.SelectedCourse = null;
                ViewModel.DiscardActiveCourseEdit();
                LoadActiveCourseEditToFieldsCore();
            }
        };
        border.DoubleTapped += async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            await OpenDetailsAsync(() => ViewModel.BeginNewCourseEdit(weekday, period, addToCurrentPlan: true));
        };
        border.RightTapped += (_, e) =>
        {
            var menu = CreateTransientMenuFlyout();
            menu.Items.Add(MenuItem(ViewModel.T["NewCourse"], AppCommandIcons.NewCourse(), async (_, _) =>
            {
                if (!await ConfirmUnsavedCourseEditAsync())
                    return;
                await OpenDetailsAsync(() => ViewModel.BeginNewCourseEdit(weekday, period, addToCurrentPlan: true));
            }));
            menu.ShowAt(border, e.GetPosition(border));
        };
    }

    private Border AddCell(Grid host, int column, int row, string text, bool header)
    {
        var textBlock = new TextBlock { Text = text, Padding = new Thickness(6) };
        return AddElementCell(host, column, row, textBlock, header);
    }

    private Border AddElementCell(Grid host, int column, int row, UIElement content, bool shaded)
    {
        var border = new Border
        {
            BorderBrush = AppMaterialLayer.Brush(AppMaterialSurface.Divider, Colors.Transparent),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = shaded
                ? RoleBrush(AppColorRole.TimetableHeader, Colors.Transparent)
                : AppBrushes.Transparent(),
            Child = content
        };
        Grid.SetColumn(border, column);
        Grid.SetRow(border, row);
        host.Children.Add(border);
        return border;
    }

    private void ShowCourseContextMenu(FrameworkElement target, PlanCourseSnapshot snapshot, Windows.Foundation.Point point)
    {
        var menu = CreateTransientMenuFlyout();
        menu.Items.Add(MenuItem(ViewModel.T["Details"], Symbol.Edit, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            await OpenDetailsAsync(() => ViewModel.BeginEditPlanSnapshot(snapshot));
        }));
        var course = PlanCourseResolver.CourseForSnapshot(snapshot, Documents.Document.CourseLibrary);
        menu.Items.Add(MenuItem(
            ViewModel.T[snapshot.IsLocked ? "UnlockCourse" : "LockCourse"],
            new FontIcon { Glyph = "\uE72E" },
            async (_, _) => await ToggleCourseLockAsync(snapshot, course?.CourseName ?? snapshot.CourseOfferingId)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(ViewModel.T["Delete"], Symbol.Delete, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            var courseName = course?.CourseName ?? snapshot.CourseOfferingId;
            if (await ConfirmAsync(ViewModel.T["Delete"], string.Format(ViewModel.T["RemoveFromCurrentPlanConfirm"], courseName)))
                ViewModel.RemoveCourseFromCurrentPlan(snapshot.CourseOfferingId);
        }));
        menu.ShowAt(target, point);
    }

    private async Task ToggleCourseLockAsync(PlanCourseSnapshot snapshot, string courseName)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var liveSnapshot = ViewModel.CurrentPlan?.Snapshots.FirstOrDefault(candidate =>
            string.Equals(candidate.CourseOfferingId, snapshot.CourseOfferingId, StringComparison.Ordinal));
        if (liveSnapshot is null)
            return;

        var isLocking = !liveSnapshot.IsLocked;
        if (!ViewModel.SetCurrentPlanCourseLocked(liveSnapshot.CourseOfferingId, isLocking))
            return;

        ShowStatus(
            string.Format(
                ViewModel.T[isLocking ? "CourseLockedStatusFormat" : "CourseUnlockedStatusFormat"],
                courseName),
            InfoBarSeverity.Success);
    }

    private async Task OpenDetailsAsync(Action beginEdit)
    {
        ArgumentNullException.ThrowIfNull(beginEdit);

        // Reopening during the exit interval must invalidate that exit's
        // delayed finalizer before it can discard this new edit and collapse
        // the pane. The animation layer owns the versioning and restoration.
        AppAnimationLayer.CancelPendingPaneExit(DetailPane);
        var paneToSuspend = _paneState.PlanActivation(
            PlannerPaneKind.Detail,
            ShouldUseExclusiveOverlayMode(),
            ViewModel.IsLibraryOpen,
            ViewModel.IsDetailOpen);
        var alreadyVisible = ViewModel.IsDetailOpen &&
                             DetailPane.Visibility == Visibility.Visible &&
                             paneToSuspend is null;
        if (alreadyVisible)
        {
            AppAnimationLayer.RefreshContent(DetailContentHost, () =>
            {
                beginEdit();
                LoadActiveCourseEditToFieldsCore();
            });
            return;
        }

        ArmCenterReflow(
            enabled: ShouldDockDetailPane(),
            expectShrink: true,
            anchor: AppHorizontalAnchor.Left);
        var transitionVersion = AppAnimationLayer.PreparePaneEntrance(DetailPane);
        beginEdit();
        LoadActiveCourseEditToFieldsCore();
        if (!await SwitchOverlayAsync(PlannerPaneKind.Detail, paneToSuspend))
        {
            AppAnimationLayer.CancelPreparedPaneEntrance(DetailPane);
            return;
        }

        await AppAnimationLayer.PlayPreparedPaneEntranceAsync(DetailPane, transitionVersion);
    }

    private async Task<bool> SwitchOverlayAsync(
        PlannerPaneKind target,
        PlannerPaneKind? paneToSuspend)
    {
        if (paneToSuspend is not { } suspended)
            return true;

        var suspendedPane = PaneFor(suspended);
        if (suspendedPane.Visibility != Visibility.Visible ||
            Grid.GetColumnSpan(suspendedPane) == 1)
        {
            _paneState.CommitActivation(target, suspended);
            UpdateResponsiveColumns(ActualWidth);
            return true;
        }

        return await AppAnimationLayer.PlayPaneExitThenAsync(suspendedPane, () =>
        {
            _paneState.CommitActivation(target, suspended);
            UpdateResponsiveColumns(ActualWidth);
        });
    }

    private FrameworkElement PaneFor(PlannerPaneKind pane) =>
        pane == PlannerPaneKind.Library ? LibraryPane : DetailPane;

    private void LoadActiveCourseEditToFieldsCore()
    {
        _loadingDetails = true;
        var loadVersion = ++_detailLoadVersion;
        var course = ViewModel.ActiveEdit?.Course ?? ViewModel.SelectedCourse;
        ClearDetailInfo();
        SaveCourseEditButton.Content = ViewModel.T["Save"];
        var hasActiveEdit = ViewModel.ActiveEdit is not null;
        SaveCourseEditButton.Visibility = hasActiveEdit ? Visibility.Visible : Visibility.Collapsed;
        DiscardCourseEditButton.Visibility = hasActiveEdit ? Visibility.Visible : Visibility.Collapsed;
        if (course is null)
        {
            foreach (var box in new[] { CourseNameBox, TeacherBox, LocationBox, GroupTypeBox, StudyTypeBox, LabelsBox, CourseColorBox, NotesBox })
                box.Text = "";
            MeetingsBox.SetMeetings([]);
            CreditsBox.Value = double.NaN;
            EnrolledBox.Value = double.NaN;
            CapacityBox.Value = double.NaN;
            UpdateColorIndicator();
            _ = ReleaseDetailLoadingAfterPendingControlEventsAsync(loadVersion);
            return;
        }

        LoadCourseEditModel(CourseEditMapper.FromCourse(course, ViewModel.T));
        var notices = new List<DetailNotice>();
        if (ViewModel.HasUnsavedCourseEdit)
            notices.Add(new DetailNotice(ViewModel.T["UnsavedChanges"], ViewModel.T["UnsavedCourseEditReminder"], InfoBarSeverity.Warning));
        ShowDetailInfo(notices);
        UpdateColorIndicator();
        _ = ReleaseDetailLoadingAfterPendingControlEventsAsync(loadVersion);
    }

    private async Task ReleaseDetailLoadingAfterPendingControlEventsAsync(int loadVersion)
    {
        await Task.Yield();
        if (_detailLoadVersion == loadVersion)
            _loadingDetails = false;
    }

    private readonly record struct DetailNotice(string Title, string Message, InfoBarSeverity Severity);

    private void ClearDetailInfo()
    {
        DetailInfoBar.Title = "";
        DetailInfoBar.Message = "";
        DetailInfoBar.IsOpen = false;
    }

    private void ShowDetailInfo(IReadOnlyList<DetailNotice> notices)
    {
        if (notices.Count == 0)
        {
            ClearDetailInfo();
            return;
        }

        var severity = notices.Any(x => x.Severity == InfoBarSeverity.Error)
            ? InfoBarSeverity.Error
            : notices.Any(x => x.Severity == InfoBarSeverity.Warning)
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Informational;
        var single = notices.Count == 1;
        DetailInfoBar.Title = single ? notices[0].Title : ViewModel.T["DetailNotices"];
        DetailInfoBar.Message = single
            ? notices[0].Message
            : string.Join(Environment.NewLine, notices.Select(x => $"{x.Title}: {x.Message}"));
        DetailInfoBar.Severity = severity;
        DetailInfoBar.IsOpen = true;
    }

    private void ShowDetailInfo(string title, string message, InfoBarSeverity severity)
    {
        ShowDetailInfo(new[] { new DetailNotice(title, message, severity) });
    }

    private void UpdateCourseEditFromFields()
    {
        if (_loadingDetails || ViewModel.ActiveEdit is null)
            return;
        var numeric = CourseEditNumericInput.Map(CreditsBox.Value, EnrolledBox.Value, CapacityBox.Value);
        if (!numeric.IsValid)
        {
            ShowDetailInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(numeric.Errors), InfoBarSeverity.Error);
            return;
        }

        var model = ReadCourseEditModel(ViewModel.ActiveEdit.Course.SemesterId, numeric.Value!);
        ViewModel.UpdateActiveCourseEdit(course => CourseEditMapper.ApplyToCourse(course, model, ViewModel.T));
        ShowDetailInfo(ViewModel.HasUnsavedCourseEdit
            ? new[] { new DetailNotice(ViewModel.T["UnsavedChanges"], ViewModel.T["UnsavedCourseEditReminder"], InfoBarSeverity.Warning) }
            : Array.Empty<DetailNotice>());
    }

    private void LoadCourseEditModel(CourseEditModel model)
    {
        CourseNameBox.Text = model.CourseName;
        TeacherBox.Text = model.Teacher;
        LocationBox.Text = model.Location;
        CreditsBox.Value = (double)model.Credits;
        EnrolledBox.Value = model.EnrolledCount ?? double.NaN;
        CapacityBox.Value = model.Capacity ?? double.NaN;
        GroupTypeBox.Text = model.CourseGroupType;
        StudyTypeBox.Text = model.StudyType;
        LabelsBox.Text = model.LabelsText;
        var editSemester = ViewModel.Semesters.FirstOrDefault(semester =>
            string.Equals(semester.SemesterId, model.SemesterId, StringComparison.Ordinal));
        MeetingsBox.MaxPeriod = Math.Max(1, editSemester?.PeriodSchedule.Count ?? 20);
        MeetingsBox.WeekCount = Math.Max(1, editSemester?.WeekCount ?? 16);
        MeetingsBox.SetMeetings(model.Meetings);
        CourseColorBox.Text = model.Color;
        NotesBox.Text = model.Notes;
    }

    private CourseEditModel ReadCourseEditModel(string semesterId, CourseEditNumericValues numeric) => new()
    {
        SemesterId = semesterId,
        CourseName = CourseNameBox.Text,
        Teacher = TeacherBox.Text,
        Location = LocationBox.Text,
        Credits = numeric.Credits,
        EnrolledCount = numeric.EnrolledCount,
        Capacity = numeric.Capacity,
        CourseGroupType = GroupTypeBox.Text,
        StudyType = StudyTypeBox.Text,
        LabelsText = LabelsBox.Text,
        Meetings = MeetingsBox.GetMeetings().ToList(),
        Color = CourseColorBox.Text,
        Notes = NotesBox.Text
    };

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = ViewModel.T["OK"],
            CloseButtonText = ViewModel.T["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<bool> AddCourseInteractiveAsync(CourseOffering course)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return false;

        if (ViewModel.CurrentPlan is null || ViewModel.CurrentSemester is null)
            return false;

        if (course.SemesterId != ViewModel.CurrentSemester.SemesterId)
        {
            var crossSemesterDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["CopyToCurrentSemester"],
                Content = string.Format(ViewModel.T["CopyTo"], ViewModel.CurrentSemester.SemesterName) + $": {course.CourseName}",
                PrimaryButtonText = ViewModel.T["CopyAndAdd"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Primary
            };
            if (await ContentDialogCoordinator.ShowAsync(crossSemesterDialog) != ContentDialogResult.Primary)
                return false;
        }

        var duplicateResolution = DuplicateResolution.SkipExisting;
        if (ViewModel.CurrentPlan.Snapshots.Any(x => string.Equals(x.CourseOfferingId, course.OfferingId, StringComparison.Ordinal)))
        {
            var duplicateDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["DuplicateCourse"],
                Content = string.Format(ViewModel.T["AlreadyInCurrentPlan"], course.CourseName),
                PrimaryButtonText = ViewModel.T["ReplaceExisting"],
                SecondaryButtonText = ViewModel.T["Skip"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Secondary
            };
            var duplicateChoice = await ContentDialogCoordinator.ShowAsync(duplicateDialog);
            if (duplicateChoice == ContentDialogResult.Primary)
                duplicateResolution = DuplicateResolution.ReplaceExisting;
            else
                return false;
        }

        var conflictResolution = ConflictResolution.KeepConflict;
        var conflicts = PlannerDomainService.FindConflicts(ViewModel.CurrentPlan, course, ViewModel.CurrentSemester, Documents.Document.CourseLibrary).ToList();
        if (conflicts.Count > 0)
        {
            var conflictDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["Conflict"],
                Content = string.Format(ViewModel.T["ConflictsWith"], string.Join(", ", conflicts.Select(x => x.CourseName))),
                PrimaryButtonText = ViewModel.T["KeepConflict"],
                SecondaryButtonText = ViewModel.T["RemoveConflictsThenAdd"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Close
            };
            var conflictChoice = await ContentDialogCoordinator.ShowAsync(conflictDialog);
            if (conflictChoice == ContentDialogResult.Primary)
                conflictResolution = ConflictResolution.KeepConflict;
            else if (conflictChoice == ContentDialogResult.Secondary)
                conflictResolution = ConflictResolution.RemoveConflictingThenAdd;
            else
                return false;
        }

        var result = ViewModel.AddCourseToCurrentPlan(course, duplicateResolution, conflictResolution);
        if (!result.Validation.IsValid)
        {
            await ShowValidationAsync(ViewModel.T["ValidationFailed"], result.Validation);
            return false;
        }
        if (result.Cancelled)
            return false;
        if (result.ConflictingCourses.Count > 0 && conflictResolution == ConflictResolution.KeepConflict)
            ShowStatus($"{ViewModel.T["Conflict"]}: {string.Join(", ", result.ConflictingCourses.Select(x => x.CourseName))}", InfoBarSeverity.Warning);
        else if (result.Added)
            ShowStatus(string.Format(ViewModel.T["AddedCourse"], course.CourseName), InfoBarSeverity.Success);
        return result.Added || result.ReplacedDuplicate;
    }

    private async Task RenamePlanAsync(SelectionPlan plan)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var box = new TextBox
        {
            Text = plan.PlanName,
            Header = ViewModel.T["Name"],
            MaxLength = WindowsFileNameRules.MaxComponentLength
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["Rename"],
            Content = box,
            PrimaryButtonText = ViewModel.T["Save"],
            CloseButtonText = ViewModel.T["Cancel"]
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            var validation = ViewModel.RenamePlan(plan, box.Text);
            if (!validation.IsValid)
                await ShowValidationAsync(ViewModel.T["CannotRenamePlan"], validation);
        }
    }

    private async Task ShowValidationAsync(string title, ValidationResult validation) =>
        await ShowMessageAsync(title, ViewModel.T.ValidationSummary(validation.Errors));

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = ViewModel.T["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private async Task DeleteSelectedAsync()
    {
        if (ViewModel.ActiveEdit is not null)
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
        }

        if (ViewModel.SelectedCourse is CourseOffering course && await ConfirmAsync(ViewModel.T["Delete"], string.Format(ViewModel.T["DeleteFromLibraryConfirm"], course.CourseName)))
            ViewModel.DeleteLibraryCourse(course);
    }

    private async Task RenameCurrentContextAsync()
    {
        if (ViewModel.SelectedCourse is CourseOffering course)
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            await OpenDetailsAsync(() => ViewModel.BeginEditLibraryCourse(course));
            CourseNameBox.Focus(FocusState.Programmatic);
            CourseNameBox.SelectAll();
            return;
        }

        if (ViewModel.CurrentPlan is not null)
            await RenamePlanAsync(ViewModel.CurrentPlan);
    }

    private string BuildWeekTitleText()
    {
        if (ViewModel.ViewMode == PlannerViewMode.SemesterOverview &&
            ViewModel.CurrentSemester is not null &&
            ViewModel.CurrentPlan is not null)
        {
            var semester = ViewModel.CurrentSemester;
            return string.Format(
                ViewModel.T["SemesterOverviewTitleFormat"],
                semester.SemesterName,
                ViewModel.CurrentPlan.PlanName,
                semester.WeekCount,
                DateDisplay.Date(semester.StartDate),
                DateDisplay.Date(semester.EndDate));
        }

        var title = ViewModel.CurrentSemester is null
            ? ""
            : string.Format(ViewModel.T["WeekTitleFormat"], SemesterRules.WeekRangeText(ViewModel.CurrentSemester, ViewModel.CurrentWeek));
        return ViewModel.ViewMode == PlannerViewMode.Comparison && ViewModel.BaseComparePlan is not null && ViewModel.CurrentPlan is not null
            ? string.Format(ViewModel.T["ComparisonWeekTitleFormat"], ViewModel.BaseComparePlan.PlanName, ViewModel.CurrentPlan.PlanName, title)
            : title;
    }

    private TextBlock CreateResponsiveDateText(DateOnly date)
    {
        var text = new TextBlock
        {
            Style = AppTypography.TextStyle(AppTextRole.Caption),
            FontSize = 12,
            Foreground = RoleBrush(AppColorRole.TextSecondary, Colors.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = FormatDateHeader(date, ResolveDateHeaderTextMode(TimetableScrollViewer.ActualWidth))
        };
        _dateHeaderTexts.Add((text, date));
        return text;
    }

    private void ApplyResponsiveDateHeaders(double viewportWidth, bool force = false)
    {
        if (_dateHeaderTexts.Count == 0)
            return;

        var mode = ResolveDateHeaderTextMode(viewportWidth);
        if (!force && _lastDateHeaderTextMode == mode)
            return;

        foreach (var (text, date) in _dateHeaderTexts)
            text.Text = FormatDateHeader(date, mode);
        _lastDateHeaderTextMode = mode;
    }

    private DateHeaderTextMode ResolveDateHeaderTextMode(double viewportWidth)
    {
        var availableWidth = EffectiveTimetableContentWidth(viewportWidth);
        var dayColumns = ViewModel.ViewMode == PlannerViewMode.Comparison ? 14 : 7;
        var periodColumns = ViewModel.ViewMode == PlannerViewMode.Comparison ? 2 : 1;
        var dividerWidth = ViewModel.ViewMode == PlannerViewMode.Comparison ? TimetableComparisonDividerWidth : 0;
        var dayWidth = (availableWidth - (periodColumns * TimetablePeriodColumnWidth) - dividerWidth) / dayColumns;
        return dayWidth switch
        {
            < 78 => DateHeaderTextMode.Short,
            < 112 => DateHeaderTextMode.Medium,
            _ => DateHeaderTextMode.Long
        };
    }

    private double EffectiveTimetableContentWidth(double viewportWidth)
    {
        var width = ResolveTimetableContentViewportWidth(viewportWidth);
        if (width <= 0 && !double.IsNaN(TimetableHost.Width))
            width = TimetableHost.Width;
        return ViewModel.ViewMode == PlannerViewMode.Comparison
            ? Math.Max(TimetableComparisonDividerWidth, width)
            : Math.Max(0, width);
    }

    private static string FormatDateHeader(DateOnly date, DateHeaderTextMode mode) =>
        mode switch
        {
            DateHeaderTextMode.Short => DateDisplay.ShortMonthDay(date),
            DateHeaderTextMode.Medium => DateDisplay.MonthDay(date),
            _ => DateDisplay.Date(date)
        };

    private static string CourseDecorativeColor(CourseOffering course) => course.Color;

    private string CourseBlockTitle(CourseOffering course, bool isInRequestedWeek) =>
        isInRequestedWeek
            ? course.CourseName
            : $"[{ViewModel.T["CourseNotThisWeekTitlePrefix"]}] {course.CourseName}";

    private Brush CourseBlockBackgroundBrush(
        SlotDifference? diff,
        WeekGridRole role,
        bool isLocked,
        bool isInRequestedWeek)
    {
        if (isLocked)
            return RoleBrush(AppColorRole.CourseBlockLocked, Colors.Gray);
        if (!isInRequestedWeek)
            return RoleBrush(AppColorRole.CourseBlockOutOfWeek, Colors.LightGray);
        if (role == WeekGridRole.Standard || diff is null)
            return RoleBrush(AppColorRole.CourseBlock, Colors.Transparent);

        return diff.Kind switch
        {
            DifferenceKind.Added => RoleBrush(AppColorRole.CourseBlockAdded, Colors.Transparent),
            DifferenceKind.Removed => RoleBrush(AppColorRole.CourseBlockRemoved, Colors.Transparent),
            DifferenceKind.Replaced => RoleBrush(AppColorRole.CourseBlockModified, Colors.Transparent),
            _ => RoleBrush(AppColorRole.CourseBlock, Colors.Transparent)
        };
    }

    private Brush CourseBlockHoverBackgroundBrush(
        SlotDifference? diff,
        WeekGridRole role,
        bool isLocked,
        bool isInRequestedWeek)
    {
        if (isLocked)
            return RoleBrush(AppColorRole.CourseBlockLockedHover, Colors.DarkGray);
        if (!isInRequestedWeek)
            return RoleBrush(AppColorRole.CourseBlockOutOfWeekHover, Colors.Gray);
        if (role == WeekGridRole.Standard || diff is null)
            return RoleBrush(AppColorRole.CourseBlockHover, Colors.Transparent);

        return diff.Kind switch
        {
            DifferenceKind.Added => RoleBrush(AppColorRole.CourseBlockAddedHover, Colors.Transparent),
            DifferenceKind.Removed => RoleBrush(AppColorRole.CourseBlockRemovedHover, Colors.Transparent),
            DifferenceKind.Replaced => RoleBrush(AppColorRole.CourseBlockModifiedHover, Colors.Transparent),
            _ => RoleBrush(AppColorRole.CourseBlockHover, Colors.Transparent)
        };
    }

    private Brush CapacityBrush(CourseOffering course)
    {
        if (course.EnrolledCount is not { } enrolled || course.Capacity is not { } capacity || capacity <= 0)
            return RoleBrush(AppColorRole.TextSecondary, Colors.Gray);
        if (enrolled >= capacity)
            return RoleBrush(AppColorRole.StatusCritical, Colors.IndianRed);
        if (enrolled / (double)capacity >= 0.9)
            return RoleBrush(AppColorRole.StatusCaution, Colors.Goldenrod);
        return RoleBrush(AppColorRole.TextSecondary, Colors.Gray);
    }

    private Brush RoleBrush(AppColorRole role, Color fallback) =>
        AppMaterialLayer.Brush(this, role, fallback);

    private Brush SurfaceBrush(AppMaterialSurface surface, Color fallback) =>
        AppMaterialLayer.Brush(this, surface, fallback);

    private string WeekdayText(int weekday) => weekday switch
    {
        1 => ViewModel.T["MondayShort"],
        2 => ViewModel.T["TuesdayShort"],
        3 => ViewModel.T["WednesdayShort"],
        4 => ViewModel.T["ThursdayShort"],
        5 => ViewModel.T["FridayShort"],
        6 => ViewModel.T["SaturdayShort"],
        7 => ViewModel.T["SundayShort"],
        _ => throw new ArgumentOutOfRangeException(nameof(weekday), weekday, null)
    };

    private async void NewCourse_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        await OpenDetailsAsync(() => ViewModel.BeginNewCourseEdit());
    }

    private async void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        if (!ViewModel.TryCreatePlan(name: null, out _, out var validation))
            await ShowValidationAsync(ViewModel.T["ValidationFailed"], validation);
    }

    private async void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        var items = ViewModel.AllPlans
            .Where(x => x.SemesterId == ViewModel.CurrentSemester?.SemesterId)
            .OrderByDescending(x => x.ModifiedAt)
            .Select(x => new PlanPickerItem
            {
                Plan = x,
                Summary = string.Format(
                    ViewModel.T["PlanPickerSummary"],
                    x.PlanName,
                    $"{SelectionPlanMetrics.TotalCredits(x, Documents.Document.CourseLibrary):0.#}",
                    SelectionPlanMetrics.CourseCount(x),
                    DateDisplay.LocalDateTime(x.ModifiedAt))
            })
            .ToList();
        var list = new ListView
        {
            ItemsSource = items,
            DisplayMemberPath = nameof(PlanPickerItem.Summary),
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 260
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["OpenPlan"],
            Content = list,
            PrimaryButtonText = ViewModel.T["Open"],
            CloseButtonText = ViewModel.T["Cancel"]
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary &&
            list.SelectedItem is PlanPickerItem item &&
            !ViewModel.TryOpenPlan(item.Plan, out var validation))
        {
            await ShowValidationAsync(ViewModel.T["ValidationFailed"], validation);
        }
    }

    private async void RegistrationOrder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentPlan is not { } plan)
            return;

        var registrationOrders = _services!.RegistrationOrders;
        if (registrationOrders.IsOpenFor(plan.PlanId))
        {
            await registrationOrders.ToggleAsync(plan.PlanId);
            return;
        }

        if (!await ConfirmUnsavedCourseEditAsync() || !plan.Snapshots.Any(snapshot => !snapshot.IsLocked))
            return;

        await registrationOrders.ToggleAsync(plan.PlanId);
    }

    private async void WeekView_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        ViewModel.ViewMode = PlannerViewMode.Week;
        RefreshVisuals();
    }

    private async void SemesterView_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        ViewModel.ViewMode = PlannerViewMode.SemesterOverview;
        RefreshVisuals();
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpenSelectedComparison)
            return;
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        if (ViewModel.OpenSelectedComparison())
            RefreshVisuals();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        await _services!.ImportExport.ImportAsync(this, ShowStatus);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        await _services!.ImportExport.ExportAsync(this, ShowStatus);
    }

    private void ShowStatus(
        string message,
        InfoBarSeverity severity,
        string? openPath = null)
    {
        if (!IsLoaded || _services is null)
            return;

        var generation = ++_statusGeneration;
        var expiryToken = _statusLifetime.Restart();
        _statusCloseInProgress = false;
        _statusOpenPath = NormalizeOpenFilePath(openPath);

        StatusBar.Title = null;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusOpenButton.IsEnabled = true;
        StatusOpenButton.Visibility = _statusOpenPath is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutomationProperties.SetLiveSetting(StatusBar, AutomationLiveSetting.Polite);

        var entranceVersion = AppAnimationLayer.PrepareTransientBannerEntrance(StatusBar);
        StatusBar.Visibility = Visibility.Visible;
        StatusBar.IsOpen = true;
        _ = RunStatusBannerLifetimeAsync(generation, entranceVersion, expiryToken);
    }

    private async Task RunStatusBannerLifetimeAsync(
        long generation,
        long entranceVersion,
        CancellationToken expiryToken)
    {
        var expiry = _statusLifetime.WaitForExpiryAsync(expiryToken);
        try
        {
            await AppAnimationLayer.PlayPreparedTransientBannerEntranceAsync(
                StatusBar,
                entranceVersion);

            if (!await expiry ||
                expiryToken.IsCancellationRequested ||
                !_statusLifetime.IsCurrent(expiryToken) ||
                generation != _statusGeneration)
            {
                return;
            }

            RequestStatusBarClose(generation);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            // Composition failures must not fault an unobserved lifetime task or
            // leave the notification permanently open.
            RequestStatusBarClose(generation);
        }
    }

    private void StatusBar_Closing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (_committingStatusClose)
            return;

        args.Cancel = true;
        var generation = _statusGeneration;
        if (!sender.DispatcherQueue.TryEnqueue(() => RequestStatusBarClose(generation)))
        {
            // If the dispatcher is already shutting down, let WinUI finish its
            // native close instead of leaving a cancelled close with no queued
            // custom exit to commit it.
            args.Cancel = false;
        }
    }

    private async void RequestStatusBarClose(long generation)
    {
        if (generation != _statusGeneration ||
            _statusCloseInProgress ||
            !StatusBar.IsOpen)
        {
            return;
        }

        _statusCloseInProgress = true;
        _statusLifetime.Cancel();
        StatusOpenButton.IsEnabled = false;
        try
        {
            await AppAnimationLayer.PlayTransientBannerExitThenAsync(
                StatusBar,
                () => CommitStatusBarClose(generation));
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            // A failed compositor exit should degrade to an immediate close,
            // never escape this async-void event path or strand the InfoBar.
            CommitStatusBarClose(generation);
        }
    }

    private void CommitStatusBarClose(long generation)
    {
        if (generation != _statusGeneration || !_statusCloseInProgress)
            return;

        _committingStatusClose = true;
        try
        {
            StatusBar.IsOpen = false;
        }
        finally
        {
            _committingStatusClose = false;
        }
    }

    private void StatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.IsOpen)
            return;

        sender.Visibility = Visibility.Collapsed;
        _statusCloseInProgress = false;
        _statusOpenPath = null;
        StatusOpenButton.IsEnabled = true;
        StatusOpenButton.Visibility = Visibility.Collapsed;
    }

    private async void StatusOpenButton_Click(object sender, RoutedEventArgs e)
    {
        var generation = _statusGeneration;
        var path = _statusOpenPath;
        if (path is null || !StatusOpenButton.IsEnabled || _statusCloseInProgress)
            return;

        StatusOpenButton.IsEnabled = false;
        bool launched;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            launched = await Launcher.LaunchFileAsync(file);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            launched = false;
        }

        if (generation != _statusGeneration ||
            !StatusBar.IsOpen ||
            _statusCloseInProgress ||
            !string.Equals(_statusOpenPath, path, StringComparison.Ordinal))
            return;

        if (launched)
        {
            RequestStatusBarClose(generation);
            return;
        }

        ShowStatus(ViewModel.T["OpenFileFailed"], InfoBarSeverity.Error);
    }

    private static string? NormalizeOpenFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await ExecuteHistoryCommandAsync(redo: false);

    private async void Redo_Click(object sender, RoutedEventArgs e) => await ExecuteHistoryCommandAsync(redo: true);

    private async Task ExecuteHistoryCommandAsync(bool redo)
    {
        if (!CanExecuteHistoryCommand(redo))
            return;

        _historyCommandInProgress = true;
        ApplyHistoryCommandState();
        try
        {
            if (!await ConfirmLeavingCourseEditAsync())
                return;

            if (!IsHistoryCommandAvailable(redo))
                return;

            if (redo)
                Documents.Redo();
            else
                Documents.Undo();
        }
        finally
        {
            _historyCommandInProgress = false;
            ApplyHistoryCommandState();
        }
    }

    private bool CanExecuteHistoryCommand(bool redo) =>
        !_historyCommandInProgress && IsHistoryCommandAvailable(redo);

    private bool IsHistoryCommandAvailable(bool redo) =>
        _services is not null &&
        !_services.BackgroundOperations.IsBusy &&
        (redo ? Documents.UndoRedo.CanRedo : Documents.UndoRedo.CanUndo);

    private void ApplyHistoryCommandState()
    {
        UndoButton.IsEnabled = CanExecuteHistoryCommand(redo: false);
        RedoButton.IsEnabled = CanExecuteHistoryCommand(redo: true);
        _toolbar.QueueLayout();
    }

    private async void ToggleLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (AppAnimationLayer.CancelPendingPaneExit(LibraryPane))
            return;

        if (ViewModel.IsLibraryOpen && LibraryPane.Visibility == Visibility.Visible)
        {
            await CloseLibraryAsync();
            return;
        }

        await OpenLibraryAsync();
    }

    private async Task OpenLibraryAsync()
    {
        var paneToSuspend = _paneState.PlanActivation(
            PlannerPaneKind.Library,
            ShouldUseExclusiveOverlayMode(),
            ViewModel.IsLibraryOpen,
            ViewModel.IsDetailOpen);
        ArmCenterReflow(
            enabled: ShouldDockLibraryPane(),
            expectShrink: true,
            anchor: AppHorizontalAnchor.Right);
        var transitionVersion = AppAnimationLayer.PreparePaneEntrance(LibraryPane);
        ViewModel.IsLibraryOpen = true;
        if (!await SwitchOverlayAsync(PlannerPaneKind.Library, paneToSuspend))
        {
            AppAnimationLayer.CancelPreparedPaneEntrance(LibraryPane);
            return;
        }

        await AppAnimationLayer.PlayPreparedPaneEntranceAsync(LibraryPane, transitionVersion);
    }

    private async void CloseLibrary_Click(object sender, RoutedEventArgs e)
    {
        await CloseLibraryAsync();
    }

    private async Task CloseLibraryAsync()
    {
        if (!ViewModel.IsLibraryOpen && LibraryPane.Visibility != Visibility.Visible)
            return;

        var paneToRestore = _paneState.PlanRestoreAfterClose(PlannerPaneKind.Library);
        var restoreVersion = 0L;
        if (paneToRestore is { } restore)
        {
            var restorePane = PaneFor(restore);
            AppAnimationLayer.CancelPendingPaneExit(restorePane);
            restoreVersion = AppAnimationLayer.PreparePaneEntrance(restorePane);
        }

        ArmCenterReflow(
            enabled: LibraryPane.Visibility == Visibility.Visible &&
                     Grid.GetColumnSpan(LibraryPane) == 1,
            expectShrink: false,
            anchor: AppHorizontalAnchor.Right);
        var closed = await AppAnimationLayer.PlayPaneExitThenAsync(
            LibraryPane,
            () =>
            {
                _paneState.CommitClose(PlannerPaneKind.Library);
                ViewModel.IsLibraryOpen = false;
            });
        await CompleteSuppressedPaneRestorationAsync(paneToRestore, restoreVersion, closed);
    }

    private async void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var paneToRestore = _paneState.PlanRestoreAfterClose(PlannerPaneKind.Detail);
        var restoreVersion = 0L;
        if (paneToRestore is { } restore)
        {
            var restorePane = PaneFor(restore);
            AppAnimationLayer.CancelPendingPaneExit(restorePane);
            restoreVersion = AppAnimationLayer.PreparePaneEntrance(restorePane);
        }

        ArmCenterReflow(
            enabled: DetailPane.Visibility == Visibility.Visible &&
                     Grid.GetColumnSpan(DetailPane) == 1,
            expectShrink: false,
            anchor: AppHorizontalAnchor.Left);
        var closed = await AppAnimationLayer.PlayPaneExitThenAsync(DetailPane, () =>
        {
            _paneState.CommitClose(PlannerPaneKind.Detail);
            if (ViewModel.ActiveEdit is not null)
                ViewModel.DiscardActiveCourseEdit();
            ViewModel.IsDetailOpen = false;
        });
        await CompleteSuppressedPaneRestorationAsync(paneToRestore, restoreVersion, closed);
    }

    private async Task CompleteSuppressedPaneRestorationAsync(
        PlannerPaneKind? paneToRestore,
        long restoreVersion,
        bool completed)
    {
        if (paneToRestore is not { } restore)
            return;

        var restorePane = PaneFor(restore);
        if (!completed)
        {
            AppAnimationLayer.CancelPreparedPaneEntrance(restorePane);
            return;
        }

        await AppAnimationLayer.PlayPreparedPaneEntranceAsync(restorePane, restoreVersion);
    }

    private void PreviousWeek_Click(object sender, RoutedEventArgs e)
    {
        ChangeWeek(-1, AppContentDirection.Backward);
    }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        ChangeWeek(1, AppContentDirection.Forward);
    }

    private void ChangeWeek(int delta, AppContentDirection direction)
    {
        var maximumWeek = Math.Max(1, ViewModel.CurrentSemester?.WeekCount ?? 1);
        var targetWeek = Math.Clamp(ViewModel.CurrentWeek + delta, 1, maximumWeek);
        if (targetWeek == ViewModel.CurrentWeek)
            return;

        _pendingCenterRefreshDirection = direction;
        ViewModel.CurrentWeek = targetWeek;
    }

    private void WeekNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_synchronizingWeekNumberBox)
            return;

        var maximumWeek = Math.Max(1, ViewModel.CurrentSemester?.WeekCount ?? 1);
        var targetWeek = WeekNumberInput.Normalize(
            args.NewValue,
            ViewModel.CurrentWeek,
            maximumWeek);
        try
        {
            if (targetWeek != ViewModel.CurrentWeek)
            {
                _pendingCenterRefreshDirection = targetWeek > ViewModel.CurrentWeek
                    ? AppContentDirection.Forward
                    : AppContentDirection.Backward;
                ViewModel.CurrentWeek = targetWeek;
            }
        }
        finally
        {
            // NumberBox accepts fractional, empty, and out-of-range text. Keep
            // its visible value aligned even when normalization leaves the
            // view-model on the same week and no property notification fires.
            SynchronizeWeekNumberBox();
        }
    }

    private async void SwapCompare_Click(object sender, RoutedEventArgs e)
    {
        await SwapCompareAsync();
    }

    private async Task SwapCompareAsync()
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        ViewModel.SwapComparison();
        RefreshVisuals();
    }

    private void CourseFilters_SearchTextChanged(object? sender, CourseLibrarySearchChangedEventArgs e)
    {
        ViewModel.SearchText = e.Text;
        CourseFilters.SetSearchSuggestions(ViewModel.LibraryCourses.Select(x => x.CourseName).Take(8));
    }

    private void CourseFilters_SearchSubmitted(object? sender, CourseLibrarySearchSubmittedEventArgs e)
    {
        ViewModel.SearchText = e.QueryText;
        LibraryTree.Focus(FocusState.Programmatic);
    }

    private void CourseFilters_FilterTextChanged(object? sender, EventArgs e)
    {
        var values = CourseFilters.Values;
        ViewModel.LabelFilterText = values.LabelText;
        ViewModel.GroupFilterText = values.GroupText;
        ViewModel.StudyFilterText = values.StudyText;
        ViewModel.TeacherFilterText = values.TeacherText;
        ViewModel.LocationFilterText = values.LocationText;
    }

    private async Task<bool> OpenLibraryCourseDetailsAsync(CourseOffering course)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return false;

        await OpenDetailsAsync(() => ViewModel.BeginEditLibraryCourse(course));
        return true;
    }

    private CourseOffering? ResolveTreeCourse(CourseLibraryCourseEventArgs e) =>
        ViewModel.LibraryCourses.FirstOrDefault(x => string.Equals(x.OfferingId, e.OfferingId, StringComparison.Ordinal))
        ?? Documents.Document.CourseLibrary.FirstOrDefault(x => string.Equals(x.OfferingId, e.OfferingId, StringComparison.Ordinal));

    private async void LibraryTree_CourseInvoked(object? sender, CourseLibraryCourseEventArgs e)
    {
        if (ResolveTreeCourse(e) is { } course)
            await OpenLibraryCourseDetailsAsync(course);
    }

    private async void LibraryTree_CourseContextRequested(object? sender, CourseLibraryCourseEventArgs e)
    {
        if (ResolveTreeCourse(e) is not { } course ||
            !await ConfirmUnsavedCourseEditAsync())
            return;
        ShowLibraryContextMenu(course, e.Position, e.Target);
    }

    private async void LibraryTree_CourseStatusDotTapped(object? sender, CourseLibraryCourseEventArgs e)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;
        if (ResolveTreeCourse(e) is { } course &&
            await ConfirmAsync(ViewModel.T["Delete"], string.Format(ViewModel.T["RemoveFromCurrentPlanConfirm"], course.CourseName)))
        {
            ViewModel.RemoveCourseFromCurrentPlan(course.OfferingId);
        }
    }

    private void ShowLibraryContextMenu(
        CourseOffering course,
        Windows.Foundation.Point point,
        FrameworkElement target)
    {
        var menu = CreateTransientMenuFlyout();
        var addMenu = new MenuFlyoutSubItem { Text = ViewModel.T["AddMenu"], Icon = new SymbolIcon(Symbol.Add) };
        addMenu.Items.Add(MenuItem(ViewModel.T["CurrentPlan"], Symbol.Add, async (_, _) => await AddCourseInteractiveAsync(course)));
        addMenu.Items.Add(MenuItem(ViewModel.T["AllOpenPlans"], Symbol.Library, async (_, _) => await AddCourseToOpenPlansAsync(course)));
        addMenu.Items.Add(MenuItem(ViewModel.T["OtherPlans"], Symbol.Find, async (_, _) => await AddCourseToSelectedPlansAsync(course)));
        menu.Items.Add(addMenu);
        var currentSnapshot = ViewModel.CurrentPlan?.Snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.CourseOfferingId, course.OfferingId, StringComparison.Ordinal));
        if (currentSnapshot is not null)
        {
            menu.Items.Add(MenuItem(
                ViewModel.T[currentSnapshot.IsLocked ? "UnlockCourse" : "LockCourse"],
                new FontIcon { Glyph = "\uE72E" },
                async (_, _) => await ToggleCourseLockAsync(currentSnapshot, course.CourseName)));
        }
        menu.Items.Add(MenuItem(ViewModel.T["Details"], Symbol.Edit, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            await OpenDetailsAsync(() => ViewModel.BeginEditLibraryCourse(course));
        }));
        var deleteMenu = new MenuFlyoutSubItem { Text = ViewModel.T["Delete"], Icon = new SymbolIcon(Symbol.Delete) };
        if (ViewModel.CurrentPlan?.Snapshots.Any(x => string.Equals(x.CourseOfferingId, course.OfferingId, StringComparison.Ordinal)) == true)
        {
            deleteMenu.Items.Add(MenuItem(ViewModel.T["RemoveFromCurrent"], Symbol.Remove, async (_, _) =>
            {
                if (!await ConfirmUnsavedCourseEditAsync())
                    return;
                if (await ConfirmAsync(ViewModel.T["Delete"], string.Format(ViewModel.T["RemoveFromCurrentPlanConfirm"], course.CourseName)))
                    ViewModel.RemoveCourseFromCurrentPlan(course.OfferingId);
            }));
        }
        deleteMenu.Items.Add(MenuItem(ViewModel.T["DeleteFromLibrary"], Symbol.Delete, async (_, _) =>
        {
            if (!await ConfirmUnsavedCourseEditAsync())
                return;
            if (await ConfirmAsync(ViewModel.T["Delete"], string.Format(ViewModel.T["DeleteFromLibraryConfirm"], course.CourseName)))
                ViewModel.DeleteLibraryCourse(course);
        }));
        menu.Items.Add(deleteMenu);
        menu.ShowAt(target, point);
    }

    private async Task AddCourseToOpenPlansAsync(CourseOffering course)
    {
        await AddCourseToPlansInteractiveAsync(course, ViewModel.OpenPlans);
    }

    private async Task AddCourseToSelectedPlansAsync(CourseOffering course)
    {
        var searchBox = new AutoSuggestBox { PlaceholderText = ViewModel.T["SearchPlans"], QueryIcon = new SymbolIcon(Symbol.Find) };
        var list = new ListView
        {
            ItemsSource = ViewModel.AllPlans,
            DisplayMemberPath = "PlanName",
            SelectionMode = ListViewSelectionMode.Multiple,
            MinHeight = 260
        };
        searchBox.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var query = TextRules.TruncateUtf16(searchBox.Text, PlannerDataLimits.MaxTextFieldLength);
                if (!string.Equals(query, searchBox.Text, StringComparison.Ordinal))
                    searchBox.Text = query;
                list.ItemsSource = ViewModel.AllPlans
                    .Where(x => x.PlanName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
            }
        };
        var panel = new StackPanel { Spacing = 8, Children = { searchBox, list } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["SelectTargetPlans"],
            Content = panel,
            PrimaryButtonText = ViewModel.T["Add"],
            CloseButtonText = ViewModel.T["Cancel"]
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;

        await AddCourseToPlansInteractiveAsync(course, list.SelectedItems.OfType<SelectionPlan>().ToList());
    }

    private async Task AddCourseToPlansInteractiveAsync(CourseOffering course, IEnumerable<SelectionPlan> targetPlans)
    {
        if (!await ConfirmUnsavedCourseEditAsync())
            return;

        var targets = targetPlans.DistinctBy(x => x.PlanId).ToList();
        if (targets.Count == 0)
            return;

        var issues = AnalyzeBulkAddIssues(course, targets);
        var decisions = await PromptBulkAddDecisionsAsync(course, issues);
        if (decisions is null)
            return;

        var result = ViewModel.AddCourseToPlans(decisions, course);
        var parts = new List<string>
        {
            string.Format(ViewModel.T["AddedToPlans"], result.Added, result.TargetCount)
        };
        if (!result.Validation.IsValid)
            parts.Add(ViewModel.T.ValidationSummary(result.Validation.Errors));
        if (result.ReplacedDuplicates > 0)
            parts.Add(string.Format(ViewModel.T["ReplacedDuplicates"], result.ReplacedDuplicates));
        if (result.Skipped > 0)
            parts.Add(string.Format(ViewModel.T["SkippedCount"], result.Skipped));
        if (result.Cancelled > 0)
            parts.Add(string.Format(ViewModel.T["CancelledConflicted"], result.Cancelled));
        if (result.ConflictingCoursesRemoved > 0)
            parts.Add(string.Format(ViewModel.T["RemovedConflicting"], result.ConflictingCoursesRemoved));

        var severity = !result.Validation.IsValid
            ? InfoBarSeverity.Error
            : result.Cancelled > 0 || result.ConflictPlans > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
        ShowStatus($"{course.CourseName}: {string.Join("; ", parts)}.", severity);
        RefreshVisuals();
    }

    private List<BulkTargetIssue> AnalyzeBulkAddIssues(CourseOffering course, IEnumerable<SelectionPlan> targetPlans)
    {
        var issues = new List<BulkTargetIssue>();
        foreach (var plan in targetPlans)
        {
            var semester = Documents.Document.Semesters.FirstOrDefault(x => x.SemesterId == plan.SemesterId);
            if (semester is null)
                continue;

            var source = PreviewCourseForSemester(course, semester);
            issues.Add(new BulkTargetIssue
            {
                Plan = plan,
                Semester = semester,
                RequiresSemesterCopy = course.SemesterId != semester.SemesterId,
                Duplicate = plan.Snapshots.Any(x => string.Equals(x.CourseOfferingId, source.OfferingId, StringComparison.Ordinal)),
                Conflicts = PlannerDomainService.FindConflicts(plan, source, semester, Documents.Document.CourseLibrary).ToList()
            });
        }

        return issues;
    }

    private static CourseOffering PreviewCourseForSemester(CourseOffering course, Semester semester) =>
        course.SemesterId == semester.SemesterId
            ? course
            : PlannerDomainService.CopyCourseToSemester(course, semester.SemesterId, 0);

    private async Task<IReadOnlyList<BulkAddPlanDecision>?> PromptBulkAddDecisionsAsync(CourseOffering course, IReadOnlyList<BulkTargetIssue> issues)
    {
        var hasDuplicate = issues.Any(x => x.Duplicate);
        var hasConflict = issues.Any(x => x.Conflicts.Count > 0);
        var hasCrossSemester = issues.Any(x => x.RequiresSemesterCopy);
        if (!hasDuplicate && !hasConflict && !hasCrossSemester)
            return issues.Select(x => new BulkAddPlanDecision { Plan = x.Plan }).ToList();

        var applyAllDuplicateBox = new WheelSafeComboBox
        {
            Header = ViewModel.T["ApplyDuplicateHandlingToAll"],
            ItemsSource = new[] { ViewModel.T["PerPlan"], ViewModel.T["SkipExistingAll"], ViewModel.T["ReplaceExistingAll"] },
            SelectedIndex = 0,
            Visibility = hasDuplicate ? Visibility.Visible : Visibility.Collapsed
        };
        var applyAllConflictBox = new WheelSafeComboBox
        {
            Header = ViewModel.T["ApplyConflictHandlingToAll"],
            ItemsSource = new[] { ViewModel.T["PerPlan"], ViewModel.T["CancelConflictedPlans"], ViewModel.T["AddAndKeepConflicts"], ViewModel.T["RemoveConflictsThenAdd"] },
            SelectedIndex = 0,
            Visibility = hasConflict ? Visibility.Visible : Visibility.Collapsed
        };

        var controls = new List<BulkPlanDecisionControls>();
        var perPlanPanel = new StackPanel { Spacing = 10 };
        foreach (var issue in issues.Where(x => x.Duplicate || x.Conflicts.Count > 0 || x.RequiresSemesterCopy))
        {
            var tags = new List<string>();
            if (issue.RequiresSemesterCopy)
                tags.Add(string.Format(ViewModel.T["CopyTo"], issue.Semester.SemesterName));
            if (issue.Duplicate)
                tags.Add(ViewModel.T["Duplicate"]);
            if (issue.Conflicts.Count > 0)
                tags.Add(string.Format(ViewModel.T["Conflicts"], string.Join(", ", issue.Conflicts.Select(c => c.CourseName))));

            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = $"{issue.Plan.PlanName}: {string.Join("; ", tags)}",
                TextWrapping = TextWrapping.Wrap,
                Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
            });

            ComboBox? duplicateDecision = null;
            if (issue.Duplicate)
            {
                duplicateDecision = new WheelSafeComboBox
                {
                    Header = ViewModel.T["Duplicate"],
                    ItemsSource = new[] { ViewModel.T["SkipExisting"], ViewModel.T["ReplaceExistingCourse"] },
                    SelectedIndex = 0
                };
                row.Children.Add(duplicateDecision);
            }

            ComboBox? conflictDecision = null;
            if (issue.Conflicts.Count > 0)
            {
                conflictDecision = new WheelSafeComboBox
                {
                    Header = ViewModel.T["Conflict"],
                    ItemsSource = new[] { ViewModel.T["CancelAddForThisPlan"], ViewModel.T["KeepConflict"], ViewModel.T["RemoveConflictsThenAdd"] },
                    SelectedIndex = 1
                };
                row.Children.Add(conflictDecision);
            }

            perPlanPanel.Children.Add(row);
            controls.Add(new BulkPlanDecisionControls
            {
                Issue = issue,
                DuplicateBox = duplicateDecision,
                ConflictBox = conflictDecision
            });
        }

        applyAllDuplicateBox.SelectionChanged += (_, _) =>
        {
            if (applyAllDuplicateBox.SelectedIndex == 0)
                return;
            foreach (var control in controls.Where(x => x.DuplicateBox is not null))
                control.DuplicateBox!.SelectedIndex = applyAllDuplicateBox.SelectedIndex - 1;
        };
        applyAllConflictBox.SelectionChanged += (_, _) =>
        {
            if (applyAllConflictBox.SelectedIndex == 0)
                return;
            foreach (var control in controls.Where(x => x.ConflictBox is not null))
                control.ConflictBox!.SelectedIndex = applyAllConflictBox.SelectedIndex - 1;
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = string.Format(ViewModel.T["BulkAddSummary"], course.CourseName, issues.Count), TextWrapping = TextWrapping.Wrap },
                applyAllDuplicateBox,
                applyAllConflictBox,
                new ScrollViewer
                {
                    MaxHeight = 260,
                    Content = perPlanPanel
                }
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["BulkAddCourse"],
            Content = panel,
            PrimaryButtonText = ViewModel.T["ApplyToAll"],
            CloseButtonText = ViewModel.T["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;

        var byPlan = controls.ToDictionary(x => x.Issue.Plan.PlanId, StringComparer.Ordinal);
        return issues.Select(issue => byPlan.TryGetValue(issue.Plan.PlanId, out var control)
                ? control.ToDecision()
                : new BulkAddPlanDecision { Plan = issue.Plan })
            .ToList();
    }

    private void TimetableHost_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel.ViewMode != PlannerViewMode.Week)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = ViewModel.T["AddToCurrentPlanCaption"];
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void TimetableHost_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.ViewMode != PlannerViewMode.Week)
            return;

        if (!e.DataView.Contains(StandardDataFormats.Text))
            return;

        var offeringId = await e.DataView.GetTextAsync();
        var course = Documents.Document.CourseLibrary.FirstOrDefault(x => x.OfferingId == offeringId);
        if (course is null)
            return;

        await AddCourseInteractiveAsync(course);
    }

    private void DetailField_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCourseEditFromFields();
        UpdateColorIndicator();
    }

    private void DetailMeetings_Changed(object sender, EventArgs e)
    {
        UpdateCourseEditFromFields();
    }

    private void DetailNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateCourseEditFromFields();

    private void RecommendCourseColor_Click(object sender, RoutedEventArgs e)
    {
        CourseColorBox.Text = CourseColorService.Generate(Documents.Document.CourseLibrary.Count);
    }

    private void UpdateColorIndicator()
    {
        var valid = CourseColorService.IsValidHex(CourseColorBox.Text);
        CourseColorSwatch.Background = valid ? AppBrushes.FromHex(CourseColorBox.Text) : AppBrushes.Transparent();
        var message = !valid
            ? ViewModel.T["EnterValidHexColorFirst"]
            : CourseColorService.ViolatesGeneratedColorGuidance(CourseColorBox.Text)
                ? ViewModel.T["CourseColorBrightnessWarning"]
                : "";
        var hasMessage = !string.IsNullOrWhiteSpace(message);
        CourseColorWarningIcon.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        CourseColorHintText.Text = message;
        CourseColorHintText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(CourseColorWarningIcon, hasMessage ? message : null);
    }

    private async void SaveCourseEdit_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveCurrentCourseEditAsync())
        {
            ShowStatus(ViewModel.T["Saved"], InfoBarSeverity.Success);
            LoadActiveCourseEditToFieldsCore();
        }
    }

    private async Task<bool> SaveCurrentCourseEditAsync()
    {
        if (ViewModel.ActiveEdit is not null)
        {
            var numeric = CourseEditNumericInput.Map(CreditsBox.Value, EnrolledBox.Value, CapacityBox.Value);
            if (!numeric.IsValid)
            {
                ShowDetailInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(numeric.Errors), InfoBarSeverity.Error);
                return false;
            }

            ViewModel.UpdateActiveCourseEdit(course => CourseEditMapper.ApplyToCourse(
                course,
                ReadCourseEditModel(course.SemesterId, numeric.Value!),
                ViewModel.T));
        }

        var result = ViewModel.SaveActiveCourseEdit();
        if (!result.IsValid)
        {
            DetailInfoBar.Title = ViewModel.T["ValidationFailed"];
            DetailInfoBar.Message = ViewModel.T.ValidationSummary(result.Errors);
            DetailInfoBar.Severity = InfoBarSeverity.Error;
            DetailInfoBar.IsOpen = true;
            return false;
        }
        if (result.RequiresForce)
        {
            var force = await ConfirmAsync(ViewModel.T["OutOfRange"], ViewModel.T.ValidationSummary(result.Warnings) + " " + ViewModel.T["SaveAnyway"]);
            if (!force)
                return false;
            result = ViewModel.SaveActiveCourseEdit(forceOutOfRange: true);
            if (!result.IsValid)
            {
                DetailInfoBar.Title = ViewModel.T["ValidationFailed"];
                DetailInfoBar.Message = ViewModel.T.ValidationSummary(result.Errors);
                DetailInfoBar.Severity = InfoBarSeverity.Error;
                DetailInfoBar.IsOpen = true;
                return false;
            }
        }

        return true;
    }

    private Task<bool> ConfirmUnsavedCourseEditAsync() => ConfirmLeavingCourseEditAsync();

    private async Task<CourseEditLeaveChoice> PromptCourseEditLeaveChoiceAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["UnsavedChanges"],
            Content = new TextBlock
            {
                Text = ViewModel.T["SaveCourseEditBeforeChangingDetails"],
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.T["Save"],
            SecondaryButtonText = ViewModel.T["DontSave"],
            CloseButtonText = ViewModel.T["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await ContentDialogCoordinator.ShowAsync(dialog);
        return result switch
        {
            ContentDialogResult.Primary => CourseEditLeaveChoice.Save,
            ContentDialogResult.Secondary => CourseEditLeaveChoice.Discard,
            _ => CourseEditLeaveChoice.Cancel
        };
    }

    private void DiscardCurrentCourseEdit()
    {
        ViewModel.DiscardActiveCourseEdit();
        LoadActiveCourseEditToFieldsCore();
    }

    private async void DiscardCourseEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveEdit is null)
            return;
        if (!ViewModel.HasUnsavedCourseEdit || await ConfirmAsync(ViewModel.T["Discard"], ViewModel.T["DiscardUnsavedChanges"]))
            DiscardCurrentCourseEdit();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveColumns(e.NewSize.Width);
    }

    private void TimetableScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyTimetableViewportWidth(e.NewSize.Width);
        ApplyResponsiveDateHeaders(e.NewSize.Width);
        UpdateSemesterOverviewColumnsIfNeeded(e.NewSize.Width);
        UpdateComparisonSwapButtonPosition();
        QueueTimetableLayoutRefresh();
    }

    private void TimetableScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateComparisonSwapButtonPosition();
    }

    private void ApplyTimetableViewportWidth(double viewportWidth)
    {
        if (ViewModel.ViewMode is not (PlannerViewMode.Week or PlannerViewMode.Comparison))
            return;

        var effectiveViewportWidth = ResolveTimetableContentViewportWidth(viewportWidth);
        if (effectiveViewportWidth <= 0)
            return;

        var targetWidth = ViewModel.ViewMode == PlannerViewMode.Comparison
            ? Math.Max(TimetableComparisonDividerWidth, effectiveViewportWidth)
            : Math.Max(TimetableHost.MinWidth, effectiveViewportWidth);
        if (ViewModel.ViewMode == PlannerViewMode.Comparison)
            targetWidth = ApplyComparisonPaneWidths(targetWidth);

        if (double.IsNaN(TimetableHost.Width) || Math.Abs(TimetableHost.Width - targetWidth) > 0.5)
        {
            TimetableHost.Width = targetWidth;
            TimetableHost.InvalidateMeasure();
            TimetableHost.InvalidateArrange();
            _comparisonGrid?.InvalidateMeasure();
            _comparisonGrid?.InvalidateArrange();
        }
    }

    private void QueueTimetableLayoutRefresh()
    {
        if (_services is null || _timetableLayoutRefreshQueued)
            return;

        _timetableLayoutRefreshQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
            TimetableHost.UpdateLayout();
            _comparisonGrid?.UpdateLayout();
            RefreshCourseBlockLayouts();

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _timetableLayoutRefreshQueued = false;
                ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
                TimetableHost.UpdateLayout();
                _comparisonGrid?.UpdateLayout();
                RefreshCourseBlockLayouts();
                UpdateComparisonSwapButtonPosition();
            });
        });
    }

    private void RefreshCourseBlockLayouts()
    {
        foreach (var registration in _courseBlockLayouts)
            registration.Refresh();
    }

    private double ResolveTimetableViewportWidth(double width)
    {
        if (TimetableScrollViewer.ViewportWidth > 0)
            return TimetableScrollViewer.ViewportWidth;
        if (width > 0)
            return width;
        if (TimetableScrollViewer.ActualWidth > 0)
            return TimetableScrollViewer.ActualWidth;
        return CenterPane.ActualWidth;
    }

    private double ResolveTimetableContentViewportWidth(double width)
    {
        var viewportWidth = ResolveTimetableViewportWidth(width);
        return Math.Max(0, viewportWidth - TimetableScrollbarGutter);
    }

    private double ApplyComparisonPaneWidths(double targetWidth)
    {
        if (_comparisonGrid is null ||
            _comparisonBaseColumn is null ||
            _comparisonDividerColumn is null ||
            _comparisonCurrentColumn is null)
            return targetWidth;

        var paneWidth = Math.Max(0, Math.Floor((targetWidth - TimetableComparisonDividerWidth) / 2));
        var dividerWidth = Math.Max(
            TimetableComparisonDividerWidth,
            targetWidth - (paneWidth * 2));
        var resolvedWidth = (paneWidth * 2) + dividerWidth;

        _comparisonBaseColumn.Width = new GridLength(paneWidth);
        _comparisonDividerColumn.Width = new GridLength(dividerWidth);
        _comparisonCurrentColumn.Width = new GridLength(paneWidth);
        _comparisonGrid.Width = resolvedWidth;
        _comparisonGrid.MinWidth = 0;
        _comparisonGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        UpdateComparisonSwapButtonPosition();
        return resolvedWidth;
    }

    private void UpdateComparisonSwapButtonPosition()
    {
        if (_comparisonSwapButton is null)
            return;

        var viewportHeight = TimetableScrollViewer.ViewportHeight > 0
            ? TimetableScrollViewer.ViewportHeight
            : TimetableScrollViewer.ActualHeight;
        if (viewportHeight <= 0)
            return;

        var y = Math.Max(0, TimetableScrollViewer.VerticalOffset + ((viewportHeight - TimetableComparisonSwapButtonSize) / 2));
        _comparisonSwapButton.Margin = new Thickness(0, y, 0, 0);
    }

    private void UpdateResponsiveColumns(double width)
    {
        width = ResolveResponsiveWidth(width);
        if (width <= 0)
            return;

        var centerOnlyMode = ViewModel.ViewMode is PlannerViewMode.SemesterOverview or PlannerViewMode.Comparison;
        _paneState.Reconcile(
            overlaysCanOverlap: !centerOnlyMode && width < MediumBreakpoint,
            libraryOpen: ViewModel.IsLibraryOpen,
            detailOpen: ViewModel.IsDetailOpen);
        var libraryPresentation = centerOnlyMode ||
                                  !ViewModel.IsLibraryOpen ||
                                  _paneState.IsSuppressed(PlannerPaneKind.Library)
            ? PanePresentation.Hidden
            : width < MediumBreakpoint
                ? PanePresentation.Overlay
                : PanePresentation.Docked;
        var detailPresentation = centerOnlyMode ||
                                 !ViewModel.IsDetailOpen ||
                                 _paneState.IsSuppressed(PlannerPaneKind.Detail)
            ? PanePresentation.Hidden
            : width < LargeBreakpoint
                ? PanePresentation.Overlay
                : PanePresentation.Docked;

        ApplyPanePresentation(
            LibraryPane,
            LibrarySplitter,
            columnIndex: 0,
            splitterColumnIndex: 1,
            defaultWidth: LibraryWideWidth,
            minWidth: LibraryMinWidth,
            maxWidth: LibraryMaxWidth,
            presentation: libraryPresentation,
            alignRight: false,
            effectiveWidth: width,
            zIndex: 20,
            overlaySurface: AppMaterialSurface.OverlayPane,
            storedWidth: ref _libraryColumnWidth);

        ApplyPanePresentation(
            DetailPane,
            DetailSplitter,
            columnIndex: 4,
            splitterColumnIndex: 3,
            defaultWidth: DetailWideWidth,
            minWidth: DetailMinWidth,
            maxWidth: DetailMaxWidth,
            presentation: detailPresentation,
            alignRight: true,
            effectiveWidth: width,
            zIndex: 30,
            overlaySurface: AppMaterialSurface.OverlayPane,
            storedWidth: ref _detailColumnWidth);

        LibrarySplitterLine.Visibility = LibrarySplitter.Visibility;
        DetailSplitterLine.Visibility = DetailSplitter.Visibility;
        ApplyTimetableViewportWidth(TimetableScrollViewer.ActualWidth);
        QueueTimetableLayoutRefresh();
    }

    private double ResolveResponsiveWidth(double width)
    {
        if (width > 0)
            return width;
        if (PlannerRoot.ActualWidth > 0)
            return PlannerRoot.ActualWidth;
        if (ActualWidth > 0)
            return ActualWidth;
        return XamlRoot?.Size.Width ?? 0;
    }

    private bool ShouldDockDetailPane()
    {
        if (ViewModel.ViewMode is PlannerViewMode.SemesterOverview or PlannerViewMode.Comparison)
            return false;

        return ResolveResponsiveWidth(ActualWidth) >= LargeBreakpoint;
    }

    private bool ShouldDockLibraryPane()
    {
        if (ViewModel.ViewMode is PlannerViewMode.SemesterOverview or PlannerViewMode.Comparison)
            return false;

        return ResolveResponsiveWidth(ActualWidth) >= MediumBreakpoint;
    }

    private bool ShouldUseExclusiveOverlayMode()
    {
        if (ViewModel.ViewMode is PlannerViewMode.SemesterOverview or PlannerViewMode.Comparison)
            return false;

        return ResolveResponsiveWidth(ActualWidth) < MediumBreakpoint;
    }

    private void ArmCenterReflow(
        bool enabled,
        bool expectShrink,
        AppHorizontalAnchor anchor)
    {
        DisarmCenterReflow();
        AppAnimationLayer.CancelResponsiveWidthReflow(CenterPane);
        var previousWidth = CenterPane.ActualWidth;
        if (!enabled || previousWidth <= 0)
            return;

        SizeChangedEventHandler? handler = null;
        handler = (sender, args) =>
        {
            var changedInExpectedDirection = expectShrink
                ? args.NewSize.Width < previousWidth - 4
                : args.NewSize.Width > previousWidth + 4;
            if (!changedInExpectedDirection)
                return;

            if (handler is not null)
                CenterPane.SizeChanged -= handler;
            if (ReferenceEquals(_centerReflowHandler, handler))
                _centerReflowHandler = null;
            AppAnimationLayer.PlayResponsiveWidthReflow(
                CenterPane,
                previousWidth,
                args.NewSize.Width,
                anchor);
        };

        _centerReflowHandler = handler;
        CenterPane.SizeChanged += handler;
    }

    private void DisarmCenterReflow()
    {
        if (_centerReflowHandler is null)
            return;

        CenterPane.SizeChanged -= _centerReflowHandler;
        _centerReflowHandler = null;
    }

    private void UpdateSemesterOverviewColumnsIfNeeded(double viewportWidth)
    {
        if (ViewModel.ViewMode != PlannerViewMode.SemesterOverview || ViewModel.CurrentSemester is null)
            return;

        var columns = ResolveSemesterOverviewColumnCount(viewportWidth);
        if (columns == _lastSemesterOverviewColumnCount)
            return;

        RenderCenterIfNeeded(force: true);
    }

    private int ResolveSemesterOverviewColumnCount(double viewportWidth)
    {
        var availableWidth = viewportWidth > 0 ? viewportWidth : TimetableScrollViewer.ActualWidth;
        if (availableWidth <= 0)
            availableWidth = CenterPane.ActualWidth;
        if (availableWidth <= 0)
            availableWidth = ResolveResponsiveWidth(ActualWidth);

        return Math.Clamp((int)Math.Floor(availableWidth / 310), 1, 4);
    }

    private void ApplyPanePresentation(
        FrameworkElement pane,
        FrameworkElement splitter,
        int columnIndex,
        int splitterColumnIndex,
        double defaultWidth,
        double minWidth,
        double maxWidth,
        PanePresentation presentation,
        bool alignRight,
        double effectiveWidth,
        int zIndex,
        AppMaterialSurface overlaySurface,
        ref double storedWidth)
    {
        var column = PlannerGrid.ColumnDefinitions[columnIndex];
        var splitterColumn = PlannerGrid.ColumnDefinitions[splitterColumnIndex];

        if (presentation == PanePresentation.Hidden)
        {
            pane.Visibility = Visibility.Collapsed;
            splitter.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(pane, 1);
            column.Width = new GridLength(0);
            column.MinWidth = 0;
            column.MaxWidth = double.PositiveInfinity;
            splitterColumn.Width = new GridLength(0);
            return;
        }

        pane.VerticalAlignment = VerticalAlignment.Stretch;
        ApplyPaneMaterial(pane, presentation == PanePresentation.Overlay, overlaySurface);

        if (presentation == PanePresentation.Docked)
        {
            storedWidth = ClampPaneWidth(storedWidth <= 0 ? defaultWidth : storedWidth, minWidth, maxWidth);
            Grid.SetColumn(pane, columnIndex);
            Grid.SetColumnSpan(pane, 1);
            pane.Width = double.NaN;
            pane.HorizontalAlignment = HorizontalAlignment.Stretch;
            AppMaterialLayer.SetElevation(pane, AppMaterialElevation.Layer);
            Canvas.SetZIndex(pane, 0);
            column.MinWidth = minWidth;
            column.MaxWidth = maxWidth;
            if (column.ActualWidth < 1 || column.Width.Value < 1)
                column.Width = new GridLength(storedWidth);
            splitter.Visibility = Visibility.Visible;
            splitterColumn.Width = new GridLength(PaneDividerWidth);
            // Restore the pane's geometry before making it visible. The
            // explicit Composition entrance needs the final column bounds,
            // not the zero-width column left by the previous close.
            pane.Visibility = Visibility.Visible;
            return;
        }

        CaptureColumnWidth(column, ref storedWidth, minWidth, maxWidth);
        column.Width = new GridLength(0);
        column.MinWidth = 0;
        column.MaxWidth = double.PositiveInfinity;
        splitter.Visibility = Visibility.Collapsed;
        splitterColumn.Width = new GridLength(0);

        var dockedWidth = ClampPaneWidth(storedWidth <= 0 ? defaultWidth : storedWidth, minWidth, maxWidth);
        var overlayWidth = Math.Min(dockedWidth, Math.Max(minWidth, effectiveWidth - 48));
        Grid.SetColumn(pane, 0);
        Grid.SetColumnSpan(pane, PlannerGrid.ColumnDefinitions.Count);
        pane.Width = overlayWidth;
        pane.HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        AppMaterialLayer.SetElevation(pane, AppMaterialElevation.Layer);
        Canvas.SetZIndex(pane, zIndex);
        pane.Visibility = Visibility.Visible;
    }

    private static void CaptureDockedPaneWidth(FrameworkElement pane, double width, ref double storedWidth, double minWidth, double maxWidth)
    {
        if (pane.Visibility != Visibility.Visible || Grid.GetColumnSpan(pane) != 1 || width <= 0)
            return;
        storedWidth = ClampPaneWidth(width, minWidth, maxWidth);
    }

    private static void CaptureColumnWidth(ColumnDefinition column, ref double storedWidth, double minWidth, double maxWidth)
    {
        var width = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return;
        storedWidth = ClampPaneWidth(width, minWidth, maxWidth);
    }

    private static double ClampPaneWidth(double width, double minWidth, double maxWidth) =>
        Math.Min(maxWidth, Math.Max(minWidth, width));

    private static void ApplyPaneMaterial(
        FrameworkElement pane,
        bool overlay,
        AppMaterialSurface overlaySurface)
    {
        AppMaterialLayer.ApplySurface(
            pane,
            overlay ? overlaySurface : AppMaterialSurface.DockedPane);
    }

}

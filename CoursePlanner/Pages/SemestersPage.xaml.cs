using CoursePlanner.Core;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CoursePlanner.Pages;

public sealed class PeriodListRow : ObservableObject
{
    public PeriodDefinition Period { get; init; } = new();
    public string PeriodText => Period.Period.ToString();
    public string TimeRange => $"{Period.Start:HH\\:mm}-{Period.End:HH\\:mm}";
}

public sealed partial class SemestersPage : Page
{
    private const double SidebarContentSpacing = 12;
    private const double PeriodEditorStackBreakpoint = 900;

    private bool _loading;
    private bool _syncingSemesterCalendar;
    private bool _deleteSemesterInProgress;
    private bool? _periodEditorIsStacked;
    private ApplicationServices? _services;
    private List<PeriodListRow> _periodRows = new();

    public SemestersPage()
    {
        InitializeComponent();
        SemesterNameBox.MaxLength = WindowsFileNameRules.MaxComponentLength;
        AppTypography.Apply(this);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Unloaded += SemestersPage_Unloaded;
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    private PlannerViewModel PlannerViewModel => _services!.Planner;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("SemestersPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Settings;
        DataContext = ViewModel;
        services.Documents.RolledBack += Documents_RolledBack;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        RefreshLocalizedControls();
    }

    private void SemestersPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            _services.Documents.RolledBack -= Documents_RolledBack;
            _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        }
        Unloaded -= SemestersPage_Unloaded;
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        RefreshLocalizedControls();

    private void Documents_RolledBack(object? sender, EventArgs e) =>
        RefreshLocalizedControls();

    private void RefreshLocalizedControls()
    {
        _loading = true;
        InitializeControls();
        LoadSettingsCore();
        _loading = false;
    }

    private void InitializeControls()
    {
        var t = ViewModel.T;
        PageTitle.Text = t["Semesters"];
        AddSemesterText.Text = t["Add"];
        SemesterCard.Header = t["SemesterManagement"];
        SemesterCard.Description = t["SemestersDescription"];
        PeriodScheduleCard.Header = t["PeriodSchedule"];
        PeriodScheduleCard.Description = t["PeriodScheduleDescription"];
        SemesterNameBox.Header = t["Name"];
        StartDatePicker.ApplyText(t, t["StartDate"]);
        EndDatePicker.ApplyText(t, t["EndDate"]);
        WeekCountBox.Header = t["Weeks"];
        WeekStartBox.Header = t["WeekStarts"];
        PeriodNumberHeader.Text = t["Period"];
        PeriodStartPicker.ApplyText(t, t["Start"]);
        PeriodEndPicker.ApplyText(t, t["End"]);
        SaveSemesterButton.Content = t["Save"];
        DeleteSemesterButton.Content = t["Delete"];
        ClearCoursesButton.Content = t["ClearCurrentSemesterCourses"];
        SavePeriodButton.Content = t["SavePeriod"];
        AddPeriodButton.Content = t["AddPeriod"];
        DeletePeriodButton.Content = t["DeletePeriod"];
        ResetPeriodsButton.Content = t["ResetDefault"];
        WeekStartBox.ItemsSource = new[] { t["WeekStartMonday"], t["WeekStartSunday"] };
        SemesterList.ItemsSource = ViewModel.Semesters;
        ReloadPeriodList();
    }

    private void ReloadPeriodList()
    {
        var selectedPeriod = ReadSelectedPeriodNumber();
        _periodRows = ViewModel.Periods
            .OrderBy(x => x.Period)
            .Select(x => new PeriodListRow { Period = x })
            .ToList();
        PeriodList.ItemsSource = _periodRows;
        if (selectedPeriod is not null)
            PeriodList.SelectedItem = _periodRows.FirstOrDefault(x => x.Period.Period == selectedPeriod.Value);
    }

    private void LoadSettings()
    {
        _loading = true;
        LoadSettingsCore();
        _loading = false;
    }

    private void LoadSettingsCore()
    {
        SemesterList.SelectedItem = ViewModel.SelectedSemester ?? ViewModel.Semesters.FirstOrDefault();
        LoadSemesterFields();
    }

    private void LoadSemesterFields()
    {
        var semester = ViewModel.SelectedSemester;
        if (semester is null)
        {
            LoadEmptySemesterFields();
            return;
        }
        UpdateSemesterActionState(hasSemester: true);
        ReloadPeriodList();
        SemesterNameBox.Text = semester.SemesterName;
        _syncingSemesterCalendar = true;
        StartDatePicker.Date = semester.StartDate;
        EndDatePicker.Date = semester.EndDate;
        WeekCountBox.Value = semester.WeekCount;
        WeekStartBox.SelectedIndex = semester.WeekStartDay == WeekStartDay.Monday ? 0 : 1;
        _syncingSemesterCalendar = false;
        SelectAndLoadPeriod((PeriodList.SelectedItem as PeriodListRow)?.Period.Period);
    }

    private void LoadEmptySemesterFields()
    {
        UpdateSemesterActionState(hasSemester: false);
        SemesterNameBox.Text = "";
        _syncingSemesterCalendar = true;
        var today = DateOnly.FromDateTime(DateTime.Today);
        StartDatePicker.Date = today;
        EndDatePicker.Date = today;
        WeekCountBox.Value = double.NaN;
        WeekStartBox.SelectedIndex = -1;
        _syncingSemesterCalendar = false;
        _periodRows.Clear();
        PeriodList.ItemsSource = _periodRows;
        LoadEmptyPeriodFields();
    }

    private void UpdateSemesterActionState(bool hasSemester)
    {
        SemesterNameBox.IsEnabled = hasSemester;
        StartDatePicker.IsEnabled = hasSemester;
        EndDatePicker.IsEnabled = hasSemester;
        WeekCountBox.IsEnabled = hasSemester;
        WeekStartBox.IsEnabled = hasSemester;
        SaveSemesterButton.IsEnabled = hasSemester;
        DeleteSemesterButton.IsEnabled = hasSemester && ViewModel.Semesters.Count > 1;
        ClearCoursesButton.IsEnabled = hasSemester;
        AddPeriodButton.IsEnabled = hasSemester;
        ResetPeriodsButton.IsEnabled = hasSemester;
    }

    private void LoadPeriodFields(PeriodDefinition period)
    {
        PeriodNumberText.Text = period.Period.ToString();
        PeriodStartPicker.Time = period.Start.ToTimeSpan();
        PeriodEndPicker.Time = period.End.ToTimeSpan();
        PeriodStartPicker.IsEnabled = true;
        PeriodEndPicker.IsEnabled = true;
        SavePeriodButton.IsEnabled = true;
        DeletePeriodButton.IsEnabled = ViewModel.Periods.Count > 1;
    }

    private void LoadEmptyPeriodFields()
    {
        PeriodNumberText.Text = "-";
        PeriodStartPicker.Time = new TimeSpan(8, 0, 0);
        PeriodEndPicker.Time = new TimeSpan(8, 45, 0);
        PeriodStartPicker.IsEnabled = false;
        PeriodEndPicker.IsEnabled = false;
        SavePeriodButton.IsEnabled = false;
        DeletePeriodButton.IsEnabled = false;
    }

    private void SelectAndLoadPeriod(int? preferredPeriodNumber = null)
    {
        var row = preferredPeriodNumber is null
            ? null
            : _periodRows.FirstOrDefault(x => x.Period.Period == preferredPeriodNumber.Value);

        PeriodList.SelectedItem = row;
        if (row is null)
            LoadEmptyPeriodFields();
        else
            LoadPeriodFields(row.Period);
    }

    private int? ReadSelectedPeriodNumber()
    {
        return (PeriodList.SelectedItem as PeriodListRow)?.Period.Period;
    }

    private void SemesterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        if (SemesterList.SelectedItem is Semester semester)
        {
            ViewModel.SelectedSemester = semester;
            PlannerViewModel.CurrentSemester = semester;
            LoadSemesterFields();
        }
    }

    private async void AddSemester_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryAddSemester(out var semester, out var validation) || semester is null)
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        _loading = true;
        ViewModel.Reload();
        var selected = ViewModel.Semesters.First(candidate =>
            string.Equals(candidate.SemesterId, semester.SemesterId, StringComparison.Ordinal));
        ViewModel.SelectedSemester = selected;
        SemesterList.SelectedItem = selected;
        _loading = false;
        PlannerViewModel.ReloadFromDocument();
        LoadSemesterFields();
    }

    private async void SaveSemester_Click(object sender, RoutedEventArgs e)
    {
        var result = ViewModel.SaveSelectedSemester(
            SemesterNameBox.Text,
            StartDatePicker.Date,
            EndDatePicker.Date,
            ReadWeekStartDay());
        if (!result.IsValid)
        {
            await ShowMessageAsync(ViewModel.T["CannotSaveSemester"], ViewModel.T.ValidationSummary(result.Errors));
            return;
        }
        PlannerViewModel.ReloadFromDocument();
        LoadSettings();
    }

    private WeekStartDay ReadWeekStartDay() => WeekStartBox.SelectedIndex switch
    {
        0 => WeekStartDay.Monday,
        1 => WeekStartDay.Sunday,
        _ => ViewModel.SelectedSemester?.WeekStartDay ?? WeekStartDay.Monday
    };

    private void SemesterDate_DateChanged(object? sender, EventArgs args)
    {
        if (_loading || _syncingSemesterCalendar)
            return;
        var start = StartDatePicker.Date;
        var end = EndDatePicker.Date;
        if (end < start)
            return;
        _syncingSemesterCalendar = true;
        WeekCountBox.Value = SemesterRules.CalculateWeekCount(start, end, ReadWeekStartDay());
        _syncingSemesterCalendar = false;
    }

    private void WeekCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _syncingSemesterCalendar || double.IsNaN(args.NewValue))
            return;
        var weekCount = Math.Max(1, (int)Math.Round(args.NewValue));
        ApplyProjectedSemesterCalendar(weekCount, ReadWeekStartDay());
    }

    private void WeekStartBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingSemesterCalendar || double.IsNaN(WeekCountBox.Value))
            return;
        var weekCount = Math.Max(1, (int)Math.Round(WeekCountBox.Value));
        ApplyProjectedSemesterCalendar(weekCount, ReadWeekStartDay());
    }

    private void ApplyProjectedSemesterCalendar(int requestedWeekCount, WeekStartDay weekStartDay)
    {
        var projection = SemesterRules.ProjectSupportedCalendarRange(
            StartDatePicker.Date,
            requestedWeekCount,
            weekStartDay);
        _syncingSemesterCalendar = true;
        WeekCountBox.Value = projection.WeekCount;
        EndDatePicker.Date = projection.EndDate;
        _syncingSemesterCalendar = false;
    }

    private async void DeleteSemester_Click(object sender, RoutedEventArgs e) => await DeleteSemesterAsync();

    private async Task DeleteSemesterAsync()
    {
        if (_deleteSemesterInProgress)
            return;

        _deleteSemesterInProgress = true;
        try
        {
            if (!await ConfirmAsync(ViewModel.T["DeleteSemester"], ViewModel.T["DeleteSemesterConfirm"]))
                return;

            try
            {
                if (!ViewModel.DeleteSelectedSemester())
                    return;
            }
            catch (SemesterDeletionBackupException)
            {
                await ShowMessageAsync(ViewModel.T["BackupFailed"], ViewModel.T["DeleteSemesterBackupFailed"]);
                return;
            }

            ViewModel.Reload();
            PlannerViewModel.ReloadFromDocument();
            RefreshLocalizedControls();
        }
        finally
        {
            _deleteSemesterInProgress = false;
        }
    }

    private async void ClearCourses_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(ViewModel.T["ClearCourses"], ViewModel.T["ClearCoursesConfirm"]))
        {
            if (ViewModel.ClearCurrentSemesterCourses())
                PlannerViewModel.ReloadFromDocument();
        }
    }

    private void PeriodList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeriodList.SelectedItem is PeriodListRow row)
            LoadPeriodFields(row.Period);
        else
            LoadEmptyPeriodFields();
    }

    private async void SavePeriod_Click(object sender, RoutedEventArgs e)
    {
        var periodNumber = ReadSelectedPeriodNumber();
        if (periodNumber is null)
            return;

        var start = TimeOnly.FromTimeSpan(PeriodStartPicker.Time);
        var end = TimeOnly.FromTimeSpan(PeriodEndPicker.Time);
        if (end <= start)
        {
            await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T["EndTimeAfterStart"]);
            return;
        }
        if (PeriodOverlapsAdjacent(periodNumber.Value, start, end))
        {
            await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T["Validation.PeriodTimeOverlap"]);
            return;
        }
        try
        {
            ViewModel.UpdatePeriodTime(periodNumber.Value, start, end);
        }
        catch (InvalidOperationException)
        {
            await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T["Validation.PeriodTimeOverlap"]);
            return;
        }
        PlannerViewModel.ReloadFromDocument();
        ReloadPeriodList();
        SelectAndLoadPeriod(periodNumber.Value);
    }

    private bool PeriodOverlapsAdjacent(int periodNumber, TimeOnly start, TimeOnly end)
    {
        var previous = ViewModel.Periods
            .Where(period => period.Period < periodNumber)
            .MaxBy(period => period.Period);
        var next = ViewModel.Periods
            .Where(period => period.Period > periodNumber)
            .MinBy(period => period.Period);
        return previous is not null && start < previous.End ||
               next is not null && end > next.Start;
    }

    private async void AddPeriod_Click(object sender, RoutedEventArgs e)
    {
        var validation = ViewModel.ValidateCanAddPeriod();
        if (!validation.IsValid)
        {
            await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        var selectedPeriod = ReadSelectedPeriodNumber();
        PeriodDefinition? period;
        try
        {
            period = ViewModel.AddPeriodAfter(selectedPeriod);
        }
        catch (InvalidOperationException)
        {
            await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T["PeriodScheduleCannotExtend"]);
            return;
        }
        PlannerViewModel.ReloadFromDocument();
        ReloadPeriodList();
        if (period is not null)
            SelectAndLoadPeriod(period.Period);
    }

    private async void DeletePeriod_Click(object sender, RoutedEventArgs e)
    {
        var periodNumber = ReadSelectedPeriodNumber();
        if (periodNumber is null)
            return;

        if (await ConfirmAsync(ViewModel.T["DeletePeriod"], string.Format(ViewModel.T["DeletePeriodConfirm"], periodNumber.Value)))
        {
            try
            {
                ViewModel.DeletePeriod(periodNumber.Value);
            }
            catch (PeriodScheduleCourseIdentityConflictException)
            {
                await ShowMessageAsync(
                    ViewModel.T["InvalidPeriod"],
                    ViewModel.T["Validation.CourseIdentityDuplicate"]);
                return;
            }
            catch (InvalidOperationException)
            {
                await ShowMessageAsync(ViewModel.T["InvalidPeriod"], ViewModel.T["Validation.PeriodScheduleRequired"]);
                return;
            }
            PlannerViewModel.ReloadFromDocument();
            ReloadPeriodList();
            var nextPeriod = ViewModel.Periods
                .OrderBy(x => x.Period)
                .FirstOrDefault(x => x.Period >= periodNumber.Value)
                ?? ViewModel.Periods.OrderByDescending(x => x.Period).FirstOrDefault();
            SelectAndLoadPeriod(nextPeriod?.Period);
        }
    }

    private async void ResetPeriods_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync(ViewModel.T["ResetPeriods"], ViewModel.T["ResetPeriodsConfirm"]))
        {
            try
            {
                ViewModel.ResetDefaultPeriods();
            }
            catch (PeriodScheduleCourseIdentityConflictException)
            {
                await ShowMessageAsync(
                    ViewModel.T["InvalidPeriod"],
                    ViewModel.T["Validation.CourseIdentityDuplicate"]);
                return;
            }
            PlannerViewModel.ReloadFromDocument();
            ReloadPeriodList();
            SelectAndLoadPeriod(1);
        }
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var responsiveWidth = TwoPaneLayoutService.ResolveWidth(this, width);
        var compact = responsiveWidth < TwoPaneLayoutService.CompactBreakpoint;
        PageTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TwoPaneLayoutService.Apply(this, RootGrid, SemesterListPane, responsiveWidth, columnSpacing: SidebarContentSpacing);
        TwoPaneLayoutService.SizeScrollableContent(ContentScrollViewer, ContentHost, ContentStack, responsiveWidth);
        ApplyPeriodEditorLayout(responsiveWidth < PeriodEditorStackBreakpoint);
    }

    private void ApplyPeriodEditorLayout(bool stacked)
    {
        if (_periodEditorIsStacked == stacked)
            return;

        _periodEditorIsStacked = stacked;
        PeriodEditorGrid.ColumnDefinitions.Clear();
        PeriodEditorGrid.RowDefinitions.Clear();

        if (stacked)
        {
            PeriodEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PeriodEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PeriodEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PeriodEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PeriodEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(PeriodNumberEditor, 0);
            Grid.SetColumn(PeriodNumberEditor, 0);
            Grid.SetColumnSpan(PeriodNumberEditor, 2);
            Grid.SetRow(PeriodStartPicker, 1);
            Grid.SetColumn(PeriodStartPicker, 0);
            Grid.SetColumnSpan(PeriodStartPicker, 1);
            Grid.SetRow(PeriodEndPicker, 1);
            Grid.SetColumn(PeriodEndPicker, 1);
            Grid.SetColumnSpan(PeriodEndPicker, 1);
            Grid.SetRow(PeriodActionBar, 2);
            Grid.SetColumn(PeriodActionBar, 0);
            Grid.SetColumnSpan(PeriodActionBar, 2);
            return;
        }

        PeriodEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        PeriodEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PeriodEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PeriodEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PeriodEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(PeriodNumberEditor, 0);
        Grid.SetColumn(PeriodNumberEditor, 0);
        Grid.SetColumnSpan(PeriodNumberEditor, 1);
        Grid.SetRow(PeriodStartPicker, 0);
        Grid.SetColumn(PeriodStartPicker, 1);
        Grid.SetColumnSpan(PeriodStartPicker, 1);
        Grid.SetRow(PeriodEndPicker, 0);
        Grid.SetColumn(PeriodEndPicker, 2);
        Grid.SetColumnSpan(PeriodEndPicker, 1);
        Grid.SetRow(PeriodActionBar, 1);
        Grid.SetColumn(PeriodActionBar, 0);
        Grid.SetColumnSpan(PeriodActionBar, 3);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = ViewModel.T["OK"],
            CloseButtonText = ViewModel.T["Cancel"]
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = ViewModel.T["OK"]
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }
}

using CoursePlanner.Core;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;

namespace CoursePlanner.Pages;

public sealed class PlanListRow : ObservableObject
{
    public SelectionPlan Plan { get; init; } = new();
    public string PlanName => Plan.PlanName;
    public int CourseCount => Plan.Snapshots.Count;
}

public sealed class PlanCourseRow : ObservableObject
{
    public string CourseName { get; init; } = "";
    public IReadOnlyList<MeetingDisplayPart> Meetings { get; init; } = Array.Empty<MeetingDisplayPart>();
}

public sealed partial class PlansPage : Page
{
    private bool _loading;
    private List<SelectionPlan> _visiblePlans = new();
    private ApplicationServices? _services;

    public PlansPage()
    {
        InitializeComponent();
        ManagementCommandBarStyle.Apply(PlanCommandBar);
        NewPlanButton.Icon = AppCommandIcons.NewPlan(22);
        AppTypography.Apply(this);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Unloaded += PlansPage_Unloaded;
    }

    public PlannerViewModel ViewModel { get; private set; } = null!;

    private DocumentSession Documents => _services!.Documents;

    private SelectionPlan? SelectedPlan => (PlanList.SelectedItem as PlanListRow)?.Plan;

    private CourseDisplayFormatter Display => new(ViewModel.T);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("PlansPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Planner;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Documents.Changed += Documents_Changed;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        ApplyText();
        LoadFilters();
        Refresh();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Documents_Changed(object? sender, EventArgs e) => Refresh();

    private void PlansPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Documents.Changed -= Documents_Changed;
        _services!.Localization.LanguageChanged -= Localization_LanguageChanged;
        Unloaded -= PlansPage_Unloaded;
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        var selectedSemesterId = (SemesterFilterBox.SelectedItem as Semester)?.SemesterId;
        ApplyText();
        LoadFilters(selectedSemesterId);
        Refresh();
    }

    private void ApplyText()
    {
        var t = ViewModel.T;
        PageTitle.Text = t["Plans"];
        SemesterFilterBox.Header = t["Semesters"];
        NewPlanButton.Label = t["NewPlan"];
        OpenPlanButton.Label = t["Open"];
        CopyPlanButton.Label = t["Copy"];
        RenamePlanButton.Label = t["Rename"];
        ClearPlanButton.Label = t["ClearPlan"];
        DeletePlanButton.Label = t["Delete"];
        PlanCourseList.Header = t["CourseLibrary"];
        UpdateSelectedPlan();
    }

    private void LoadFilters(string? selectedSemesterId = null)
    {
        _loading = true;
        var items = new List<object> { ViewModel.T["AllSemesters"] };
        items.AddRange(ViewModel.Semesters);
        SemesterFilterBox.ItemsSource = items;
        SemesterFilterBox.SelectedItem = selectedSemesterId is null
            ? items[0]
            : items.OfType<Semester>().FirstOrDefault(x => x.SemesterId == selectedSemesterId) ?? items[0];
        _loading = false;
    }

    private void Refresh()
    {
        var selectedId = SelectedPlan?.PlanId ?? ViewModel.CurrentPlan?.PlanId;
        var semester = SemesterFilterBox.SelectedItem as Semester;
        _visiblePlans = ViewModel.AllPlans
            .Where(x => semester is null || x.SemesterId == semester.SemesterId)
            .OrderBy(x => ViewModel.Semesters.FirstOrDefault(s => s.SemesterId == x.SemesterId)?.DisplayOrder ?? int.MaxValue)
            .ThenBy(x => x.DisplayOrder)
            .ThenBy(x => x.PlanName)
            .ToList();
        var rows = _visiblePlans.Select(plan => new PlanListRow { Plan = plan }).ToList();
        PlanList.ItemsSource = rows;
        PlanList.SelectedItem = rows.FirstOrDefault(x => x.Plan.PlanId == selectedId) ?? rows.FirstOrDefault();
        UpdateSelectedPlan();
    }

    private void SelectPlanById(string planId)
    {
        if (PlanList.ItemsSource is not IEnumerable<PlanListRow> rows)
            return;

        var row = rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Plan.PlanId, planId, StringComparison.Ordinal));
        if (row is not null)
            PlanList.SelectedItem = row;
    }

    private void UpdateSelectedPlan()
    {
        var plan = SelectedPlan;
        var hasPlan = plan is not null;
        OpenPlanButton.IsEnabled = hasPlan;
        CopyPlanButton.IsEnabled = hasPlan;
        RenamePlanButton.IsEnabled = hasPlan;
        ClearPlanButton.IsEnabled = hasPlan;
        DeletePlanButton.IsEnabled = hasPlan;

        if (plan is null)
        {
            SelectedPlanTitle.Text = ViewModel.T["NoPlanSelected"];
            SelectedPlanSemester.Text = "";
            SelectedPlanCredits.Text = "";
            SelectedPlanModified.Text = "";
            PlanCourseList.ItemsSource = null;
            return;
        }

        var semesterName = ViewModel.Semesters.FirstOrDefault(x => x.SemesterId == plan.SemesterId)?.SemesterName ?? "";
        SelectedPlanTitle.Text = plan.PlanName;
        SelectedPlanSemester.Text = semesterName;
        SelectedPlanCredits.Text = $"{SelectionPlanMetrics.TotalCredits(plan, Documents.Document.CourseLibrary):0.#} {ViewModel.T["CreditsShort"]} · {SelectionPlanMetrics.CourseCount(plan)} {ViewModel.T["Courses"]}";
        SelectedPlanModified.Text = DateDisplay.LocalDateTime(plan.ModifiedAt);
        PlanCourseList.ItemsSource = PlanCourseResolver.Courses(plan, Documents.Document.CourseLibrary)
            .Select(course => new PlanCourseRow
            {
                CourseName = course.CourseName,
                Meetings = Display.MeetingDetails(course)
            })
            .ToList();
    }

    private void SemesterFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
            Refresh();
    }

    private void PlanList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectedPlan();

    private async void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryCreatePlan(name: null, out var plan, out var validation) || plan is null)
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        Refresh();
        SelectPlanById(plan.PlanId);
    }

    private async void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is null)
            return;
        if (!ViewModel.TryOpenPlan(SelectedPlan, out var validation))
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        _services!.Navigation.RequestPlanner();
    }

    private async void CopyPlan_Click(object sender, RoutedEventArgs e)
    {
        var sourcePlan = SelectedPlan;
        if (sourcePlan is null)
            return;
        if (!ViewModel.TryCopyPlan(sourcePlan, out var copy, out var validation) || copy is null)
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        Refresh();
        SelectPlanById(copy.PlanId);
    }

    private async void RenamePlan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not null)
            await RenamePlanAsync(SelectedPlan);
    }

    private async void ClearPlan_Click(object sender, RoutedEventArgs e)
    {
        var plan = SelectedPlan;
        if (plan is null)
            return;
        if (!await ConfirmAsync(
                ViewModel.T["ClearPlan"],
                string.Format(ViewModel.T["ClearPlanConfirm"], plan.PlanName)))
        {
            return;
        }

        var validation = ViewModel.ClearPlan(plan);
        if (!validation.IsValid)
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        Refresh();
    }

    private async void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        var plan = SelectedPlan;
        if (plan is null)
            return;
        if (!await ConfirmAsync(
                ViewModel.T["Delete"],
                string.Format(ViewModel.T["DeletePlanConfirm"], plan.PlanName)))
        {
            return;
        }

        var validation = ViewModel.DeletePlan(plan);
        if (!validation.IsValid)
        {
            await ShowMessageAsync(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }
        Refresh();
    }

    private async Task RenamePlanAsync(SelectionPlan plan)
    {
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
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;

        var validation = ViewModel.RenamePlan(plan, box.Text);
        if (!validation.IsValid)
        {
            await ShowMessageAsync(ViewModel.T["CannotRenamePlan"], ViewModel.T.ValidationSummary(validation.Errors));
            return;
        }

        Refresh();
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
        TwoPaneLayoutService.Apply(this, RootGrid, PlanListPane, responsiveWidth);
        TwoPaneLayoutService.SizeScrollableContent(PlanContentScrollViewer, PlanContentHost, PlanContentStack, responsiveWidth);
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

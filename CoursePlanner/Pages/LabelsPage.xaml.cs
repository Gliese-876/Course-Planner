using CoursePlanner.Core;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CoursePlanner.Pages;

public sealed class LabelListRow
{
    public CourseLabel Label { get; init; } = new();
    public string Name => Label.Name;
}

public sealed class LabelGroup : List<LabelListRow>
{
    public LabelGroup(string key, IEnumerable<LabelListRow> rows) : base(rows)
    {
        Key = key;
    }

    public string Key { get; }
}

public sealed partial class LabelsPage : Page
{
    private const double SidebarContentSpacing = 12;

    private ApplicationServices? _services;
    private List<LabelListRow> _labelRows = new();
    private List<LabelGroup> _labelGroups = new();

    public LabelsPage()
    {
        InitializeComponent();
        AppTypography.Apply(this);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Unloaded += LabelsPage_Unloaded;
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("LabelsPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Settings;
        DataContext = ViewModel;
        services.Documents.RolledBack += Documents_RolledBack;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        RefreshLocalizedControls();
    }

    private void LabelsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            _services.Documents.RolledBack -= Documents_RolledBack;
            _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        }
        Unloaded -= LabelsPage_Unloaded;
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        RefreshLocalizedControls();

    private void Documents_RolledBack(object? sender, EventArgs e) =>
        RefreshLocalizedControls();

    private void RefreshLocalizedControls()
    {
        InitializeControls();
        LoadSettingsCore();
    }

    private void InitializeControls()
    {
        var t = ViewModel.T;
        PageTitle.Text = t["LabelManagement"];
        NewLabelText.Text = t["NewLabel"];
        LabelEditorCard.Header = t["LabelManagement"];
        LabelEditorCard.Description = t["LabelManagementDescription"];
        LabelNameBox.Header = t["LabelName"];
        LabelKindBox.Header = t["LabelKind"];
        MoveLabelUpButton.Content = t["MoveUp"];
        MoveLabelDownButton.Content = t["MoveDown"];
        SaveLabelButton.Content = t["SaveLabel"];
        DeleteLabelButton.Content = t["DeleteLabel"];
        LabelKindBox.ItemsSource = new[] { t["LabelKindOrdinary"], t["LabelKindCourseGroup"], t["LabelKindStudy"] };
        RefreshLabelRows();
    }

    private void LoadSettingsCore()
    {
        SelectLabelRow(ViewModel.SelectedLabel ?? ViewModel.Labels.FirstOrDefault());
        LoadLabelFields();
    }

    private void RefreshLabelRows()
    {
        _labelRows = ViewModel.Labels
            .Select(label => new LabelListRow
            {
                Label = label
            })
            .ToList();
        _labelGroups = new List<LabelGroup>
        {
            CreateLabelGroup(LabelKind.CourseGroupType, ViewModel.T["LabelKindCourseGroup"]),
            CreateLabelGroup(LabelKind.StudyType, ViewModel.T["LabelKindStudy"]),
            CreateLabelGroup(LabelKind.Ordinary, ViewModel.T["LabelKindOrdinary"])
        };
        LabelGroupsViewSource.Source = _labelGroups;
    }

    private LabelGroup CreateLabelGroup(LabelKind kind, string title) =>
        new(
            title,
            _labelRows
                .Where(row => row.Label.Kind == kind)
                .OrderBy(row => row.Label.DisplayOrder)
                .ThenBy(row => row.Label.Name, StringComparer.CurrentCultureIgnoreCase));

    private void SelectLabelRow(CourseLabel? label)
    {
        LabelList.SelectedItem = label is null
            ? null
            : _labelRows.FirstOrDefault(row => ReferenceEquals(row.Label, label) ||
                                               string.Equals(row.Label.Name, label.Name, StringComparison.OrdinalIgnoreCase) &&
                                               row.Label.Kind == label.Kind);
    }

    private void LoadLabelFields()
    {
        var label = ViewModel.SelectedLabel;
        LabelNameBox.Text = label?.Name ?? "";
        LabelKindBox.SelectedIndex = label?.Kind switch
        {
            LabelKind.CourseGroupType => 1,
            LabelKind.StudyType => 2,
            _ => 0
        };
    }

    private void LabelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LabelList.SelectedItem is LabelListRow row)
        {
            ViewModel.SelectedLabel = row.Label;
            LoadLabelFields();
        }
    }

    private void NewLabel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewLabelTemplate();
        LabelList.SelectedItem = null;
        LoadLabelFields();
    }

    private async void SaveLabel_Click(object sender, RoutedEventArgs e)
    {
        var kind = LabelKindBox.SelectedIndex switch
        {
            1 => LabelKind.CourseGroupType,
            2 => LabelKind.StudyType,
            _ => LabelKind.Ordinary
        };
        var result = ViewModel.UpsertLabel(LabelNameBox.Text, kind);
        if (!result.IsValid)
            await ShowMessageAsync(ViewModel.T["CannotSaveLabel"], ViewModel.T.ValidationSummary(result.Errors));
        RefreshLabelRows();
        SelectLabelRow(ViewModel.SelectedLabel);
    }

    private async void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedLabel is null)
            return;
        if (await ConfirmAsync(ViewModel.T["DeleteLabel"], string.Format(ViewModel.T["DeleteLabelConfirm"], ViewModel.SelectedLabel.Name)))
            ViewModel.DeleteSelectedLabel();
        RefreshLabelRows();
        SelectLabelRow(ViewModel.SelectedLabel);
    }

    private void MoveLabelUp_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.MoveSelectedLabel(-1);
        RefreshLabelRows();
        SelectLabelRow(ViewModel.SelectedLabel);
    }

    private void MoveLabelDown_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.MoveSelectedLabel(1);
        RefreshLabelRows();
        SelectLabelRow(ViewModel.SelectedLabel);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var responsiveWidth = TwoPaneLayoutService.ResolveWidth(this, width);
        var compact = responsiveWidth < TwoPaneLayoutService.CompactBreakpoint;
        PageTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TwoPaneLayoutService.Apply(this, RootGrid, LabelListPane, responsiveWidth, columnSpacing: SidebarContentSpacing);
        TwoPaneLayoutService.SizeScrollableContent(ContentScrollViewer, ContentHost, ContentStack, responsiveWidth);

        var compactActions = responsiveWidth < 900;
        LabelActionsPanel.Orientation = compactActions ? Orientation.Vertical : Orientation.Horizontal;
        LabelActionsPanel.HorizontalAlignment = compactActions ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;

        foreach (var button in LabelActionsPanel.Children.OfType<Button>())
            button.HorizontalAlignment = compactActions ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

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
}

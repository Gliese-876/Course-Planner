using CoursePlanner.Core;
using CoursePlanner.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Controls;

public sealed record CourseLibraryFilterValues(
    string SearchText,
    string LabelText,
    string GroupText,
    string StudyText,
    string TeacherText,
    string LocationText);

public sealed class CourseLibrarySearchChangedEventArgs : EventArgs
{
    public CourseLibrarySearchChangedEventArgs(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed class CourseLibrarySearchSubmittedEventArgs : EventArgs
{
    public CourseLibrarySearchSubmittedEventArgs(string queryText)
    {
        QueryText = queryText;
    }

    public string QueryText { get; }
}

public sealed class CourseLibraryFilterBar : UserControl
{
    private const double FilterButtonMinWidth = 68;
    private const double FilterButtonMinHeight = 32;
    private const double FilterIconSize = 16;
    private const double FilterTextFontSize = 13;

    private readonly AutoSuggestBox _searchBox = new()
    {
        QueryIcon = new SymbolIcon(Symbol.Find)
    };

    private readonly Button _filterButton = new()
    {
        MinWidth = FilterButtonMinWidth,
        MinHeight = FilterButtonMinHeight,
        Padding = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _filterButtonText = new();
    private readonly TextBox _labelFilterBox = new() { MaxLength = PlannerDataLimits.MaxTextFieldLength };
    private readonly TextBox _groupFilterBox = new() { MaxLength = PlannerDataLimits.MaxTextFieldLength };
    private readonly TextBox _studyFilterBox = new() { MaxLength = PlannerDataLimits.MaxTextFieldLength };
    private readonly TextBox _teacherFilterBox = new() { MaxLength = PlannerDataLimits.MaxTextFieldLength };
    private readonly TextBox _locationFilterBox = new() { MaxLength = PlannerDataLimits.MaxTextFieldLength };
    private bool _loading;

    public CourseLibraryFilterBar()
    {
        AppTypography.Apply(this);
        AppTypography.Apply(_filterButton);

        var root = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        _searchBox.TextChanged += SearchBox_TextChanged;
        _searchBox.QuerySubmitted += SearchBox_QuerySubmitted;
        root.Children.Add(_searchBox);

        _filterButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Viewbox
                {
                    Width = FilterIconSize,
                    Height = FilterIconSize,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new SymbolIcon(Symbol.Filter)
                },
                _filterButtonText
            }
        };
        _filterButtonText.FontSize = FilterTextFontSize;
        _filterButtonText.FontFamily = AppTypography.FontFamilyFor(AppTextRole.Body);
        _filterButtonText.LineHeight = AppTypography.LineHeight(AppTextRole.Body, FilterTextFontSize);
        _filterButtonText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _filterButtonText.TextLineBounds = TextLineBounds.Full;
        _filterButtonText.VerticalAlignment = VerticalAlignment.Center;
        var filterContent = new StackPanel
        {
            Width = 280,
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                _labelFilterBox,
                _groupFilterBox,
                _studyFilterBox,
                _teacherFilterBox,
                _locationFilterBox
            }
        };
        var filterFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = filterContent
        };
        AppMaterialLayer.ApplyTransientFlyout(filterFlyout);
        _filterButton.Flyout = filterFlyout;
        Grid.SetColumn(_filterButton, 1);
        root.Children.Add(_filterButton);

        _labelFilterBox.TextChanged += FilterBox_TextChanged;
        _groupFilterBox.TextChanged += FilterBox_TextChanged;
        _studyFilterBox.TextChanged += FilterBox_TextChanged;
        _teacherFilterBox.TextChanged += FilterBox_TextChanged;
        _locationFilterBox.TextChanged += FilterBox_TextChanged;

        Content = root;
    }

    public string SearchAutomationId
    {
        get => AutomationProperties.GetAutomationId(_searchBox);
        set => AutomationProperties.SetAutomationId(_searchBox, value);
    }

    public string FilterButtonAutomationId
    {
        get => AutomationProperties.GetAutomationId(_filterButton);
        set => AutomationProperties.SetAutomationId(_filterButton, value);
    }

    public string LabelFilterAutomationId
    {
        get => AutomationProperties.GetAutomationId(_labelFilterBox);
        set => AutomationProperties.SetAutomationId(_labelFilterBox, value);
    }

    public string GroupFilterAutomationId
    {
        get => AutomationProperties.GetAutomationId(_groupFilterBox);
        set => AutomationProperties.SetAutomationId(_groupFilterBox, value);
    }

    public string StudyFilterAutomationId
    {
        get => AutomationProperties.GetAutomationId(_studyFilterBox);
        set => AutomationProperties.SetAutomationId(_studyFilterBox, value);
    }

    public string TeacherFilterAutomationId
    {
        get => AutomationProperties.GetAutomationId(_teacherFilterBox);
        set => AutomationProperties.SetAutomationId(_teacherFilterBox, value);
    }

    public string LocationFilterAutomationId
    {
        get => AutomationProperties.GetAutomationId(_locationFilterBox);
        set => AutomationProperties.SetAutomationId(_locationFilterBox, value);
    }

    public CourseLibraryFilterValues Values => new(
        _searchBox.Text,
        _labelFilterBox.Text,
        _groupFilterBox.Text,
        _studyFilterBox.Text,
        _teacherFilterBox.Text,
        _locationFilterBox.Text);

    public event EventHandler<CourseLibrarySearchChangedEventArgs>? SearchTextChanged;
    public event EventHandler<CourseLibrarySearchSubmittedEventArgs>? SearchSubmitted;
    public event EventHandler? FilterTextChanged;

    public void ApplyText(Func<string, string> text)
    {
        var filters = text("Filters");
        _searchBox.PlaceholderText = text("Search");
        _filterButtonText.Text = filters;
        AutomationProperties.SetName(_filterButton, filters);
        ToolTipService.SetToolTip(_filterButton, filters);
        _labelFilterBox.Header = text("LabelFilter");
        _groupFilterBox.Header = text("GroupFilter");
        _studyFilterBox.Header = text("StudyFilter");
        _teacherFilterBox.Header = text("TeacherFilter");
        _locationFilterBox.Header = text("LocationFilter");
    }

    public void SetValues(
        string searchText,
        string labelText,
        string groupText,
        string studyText,
        string teacherText,
        string locationText)
    {
        _loading = true;
        SetTextIfChanged(_searchBox, Bound(searchText));
        SetTextIfChanged(_labelFilterBox, Bound(labelText));
        SetTextIfChanged(_groupFilterBox, Bound(groupText));
        SetTextIfChanged(_studyFilterBox, Bound(studyText));
        SetTextIfChanged(_teacherFilterBox, Bound(teacherText));
        SetTextIfChanged(_locationFilterBox, Bound(locationText));
        _loading = false;
    }

    public void SetSearchSuggestions(IEnumerable<string> suggestions)
    {
        _searchBox.ItemsSource = suggestions.ToList();
    }

    public void FocusSearch(FocusState focusState)
    {
        _searchBox.Focus(focusState);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_loading || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        var bounded = Bound(sender.Text);
        if (!string.Equals(sender.Text, bounded, StringComparison.Ordinal))
            sender.Text = bounded;
        SearchTextChanged?.Invoke(this, new CourseLibrarySearchChangedEventArgs(bounded));
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SearchSubmitted?.Invoke(this, new CourseLibrarySearchSubmittedEventArgs(Bound(args.QueryText)));
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            FilterTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetTextIfChanged(AutoSuggestBox box, string value)
    {
        if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            box.Text = value;
    }

    private static void SetTextIfChanged(TextBox box, string value)
    {
        if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            box.Text = value;
    }

    private static string Bound(string? value)
        => TextRules.TruncateUtf16(value, PlannerDataLimits.MaxTextFieldLength);
}

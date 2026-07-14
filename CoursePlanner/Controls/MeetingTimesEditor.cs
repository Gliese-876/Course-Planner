using CoursePlanner.Core;
using CoursePlanner.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Controls;

public sealed class MeetingTimesEditor : UserControl
{
    private sealed class WeekdayOption
    {
        public int Value { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    private sealed class ParityOption
    {
        public WeekParity Value { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    private readonly StackPanel _root = new() { Spacing = 8 };
    private readonly TextBlock _header = new();
    private readonly Button _addButton = new() { MinHeight = 30, Padding = new Thickness(10, 0, 10, 0) };
    private readonly StackPanel _rowsPanel = new() { Spacing = 8 };
    private readonly MeetingTimesEditorState _state = new();
    private AppLocalizer? _text;
    private bool _rebuilding;

    public MeetingTimesEditor()
    {
        AppTypography.Apply(this);
        _header.Style = AppTypography.TextStyle(AppTextRole.BodyStrong);
        AppTypography.Apply(_addButton);
        var headerRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        _header.VerticalAlignment = VerticalAlignment.Center;
        headerRow.Children.Add(_header);
        Grid.SetColumn(_addButton, 1);
        headerRow.Children.Add(_addButton);
        _addButton.Click += (_, _) => AddMeeting();

        _root.Children.Add(headerRow);
        _root.Children.Add(_rowsPanel);
        Content = _root;
        ActualThemeChanged += (_, _) => RebuildRows();
    }

    public int MaxPeriod
    {
        get => _state.MaxPeriod;
        set
        {
            var normalized = Math.Max(1, value);
            if (_state.MaxPeriod == normalized)
                return;

            var periodsChanged = _state.GetMeetings().Any(meeting =>
                meeting.StartPeriod > normalized || meeting.EndPeriod > normalized);
            _state.MaxPeriod = normalized;
            RebuildRows();
            if (periodsChanged)
                RaiseMeetingsChanged();
        }
    }

    public int WeekCount
    {
        get => _state.WeekCount;
        set => _state.WeekCount = value;
    }

    public event EventHandler? MeetingsChanged;

    public void ApplyText(AppLocalizer text)
    {
        _text = text;
        _header.Text = text["Meetings"];
        _addButton.Content = text["AddMeetingTime"];
        AutomationProperties.SetName(_addButton, text["AddMeetingTime"]);
        RebuildRows();
    }

    public void SetMeetings(IEnumerable<MeetingTime> meetings)
    {
        _state.SetMeetings(meetings);
        RebuildRows();
    }

    public IReadOnlyList<MeetingTime> GetMeetings() => _state.GetMeetings()
        .Select(meeting =>
        {
            meeting.Weeks = meeting.Weeks.Trim();
            return meeting;
        })
        .ToList();

    private CourseDisplayFormatter Formatter() =>
        new(_text ?? new AppLocalizer(LanguageMode.SimplifiedChinese));

    private void AddMeeting()
    {
        if (!_state.AddMeeting())
            return;
        RebuildRows();
        RaiseMeetingsChanged();
    }

    private void RemoveMeeting(int index)
    {
        _state.RemoveMeeting(index);
        RebuildRows();
        RaiseMeetingsChanged();
    }

    private void RebuildRows()
    {
        _rebuilding = true;
        _rowsPanel.Children.Clear();
        var meetings = _state.GetMeetings();
        _addButton.IsEnabled = meetings.Count < PlannerDataLimits.MaxMeetingsPerCourse;
        for (var index = 0; index < meetings.Count; index++)
            _rowsPanel.Children.Add(CreateRowEditor(meetings[index], index));
        _rebuilding = false;
    }

    private FrameworkElement CreateRowEditor(MeetingTime meeting, int index)
    {
        var text = _text;
        var border = new Border
        {
            BorderBrush = AppMaterialLayer.Brush(AppMaterialSurface.Divider, Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Background = AppBrushes.Transparent()
        };

        var stack = new StackPanel { Spacing = 8 };
        border.Child = stack;

        var titleRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = text is null ? $"Meeting {index + 1}" : string.Format(text["MeetingItemTitleFormat"], index + 1),
            Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
            VerticalAlignment = VerticalAlignment.Center
        });
        var removeButton = new Button
        {
            MinWidth = 32,
            Width = 32,
            Height = 30,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }
        };
        AppTypography.Apply(removeButton);
        AutomationProperties.SetName(removeButton, text?["RemoveMeetingTime"] ?? "Remove meeting time");
        removeButton.Click += (_, _) => RemoveMeeting(index);
        Grid.SetColumn(removeButton, 1);
        titleRow.Children.Add(removeButton);
        stack.Children.Add(titleRow);

        var weekdayBox = new WheelSafeComboBox
        {
            Header = text?["MeetingWeekday"] ?? "Weekday",
            ItemsSource = WeekdayOptions(),
            SelectedIndex = meeting.Weekday - 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(weekdayBox, RowAutomationId("Weekday", index));
        weekdayBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || weekdayBox.SelectedItem is not WeekdayOption option)
                return;
            _state.SetWeekday(index, option.Value);
            RaiseMeetingsChanged();
        };
        stack.Children.Add(weekdayBox);

        var periodRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        var startBox = CreatePeriodBox(text?["MeetingStartPeriod"] ?? "Start", meeting.StartPeriod, index, "Start");
        var endBox = CreatePeriodBox(text?["MeetingEndPeriod"] ?? "End", meeting.EndPeriod, index, "End");
        var syncingPeriods = false;
        startBox.ValueChanged += (_, _) =>
        {
            if (_rebuilding || syncingPeriods)
                return;
            _state.SetStartPeriod(index, ReadPeriod(startBox));
            var updated = _state.GetMeetings()[index];
            syncingPeriods = true;
            startBox.Value = updated.StartPeriod;
            endBox.Value = updated.EndPeriod;
            syncingPeriods = false;
            RaiseMeetingsChanged();
        };
        periodRow.Children.Add(startBox);

        endBox.ValueChanged += (_, _) =>
        {
            if (_rebuilding || syncingPeriods)
                return;
            _state.SetEndPeriod(index, ReadPeriod(endBox));
            var updated = _state.GetMeetings()[index];
            syncingPeriods = true;
            endBox.Value = updated.EndPeriod;
            syncingPeriods = false;
            RaiseMeetingsChanged();
        };
        Grid.SetColumn(endBox, 1);
        periodRow.Children.Add(endBox);
        stack.Children.Add(periodRow);

        var weekRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(120) }
            }
        };
        var weeksBox = new TextBox
        {
            Header = text?["MeetingWeeks"] ?? "Weeks",
            Text = meeting.Weeks,
            MaxLength = PlannerDataLimits.MaxMeetingWeeksLength,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(weeksBox, RowAutomationId("Weeks", index));
        weeksBox.TextChanged += (_, _) =>
        {
            if (_rebuilding)
                return;
            _state.SetWeeks(index, weeksBox.Text);
            RaiseMeetingsChanged();
        };
        weekRow.Children.Add(weeksBox);

        var parityBox = new WheelSafeComboBox
        {
            Header = text?["MeetingParity"] ?? "Parity",
            ItemsSource = ParityOptions(),
            SelectedIndex = meeting.WeekParity switch
            {
                WeekParity.Odd => 1,
                WeekParity.Even => 2,
                _ => 0
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(parityBox, RowAutomationId("Parity", index));
        parityBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || parityBox.SelectedItem is not ParityOption option)
                return;
            _state.SetParity(index, option.Value);
            RaiseMeetingsChanged();
        };
        Grid.SetColumn(parityBox, 1);
        weekRow.Children.Add(parityBox);
        stack.Children.Add(weekRow);

        return border;
    }

    private NumberBox CreatePeriodBox(string header, int value, int index, string slot)
    {
        var box = new NumberBox
        {
            Header = header,
            Minimum = 1,
            Maximum = Math.Max(1, MaxPeriod),
            Value = Math.Clamp(value, 1, Math.Max(1, MaxPeriod)),
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        AutomationProperties.SetAutomationId(box, RowAutomationId(slot, index));
        return box;
    }

    private IReadOnlyList<WeekdayOption> WeekdayOptions()
    {
        var formatter = Formatter();
        return Enumerable.Range(1, 7)
            .Select(day => new WeekdayOption { Value = day, Text = formatter.WeekdayShort(day) })
            .ToList();
    }

    private IReadOnlyList<ParityOption> ParityOptions()
    {
        var formatter = Formatter();
        return new[]
        {
            new ParityOption { Value = WeekParity.All, Text = formatter.ParityText(WeekParity.All) },
            new ParityOption { Value = WeekParity.Odd, Text = formatter.ParityText(WeekParity.Odd) },
            new ParityOption { Value = WeekParity.Even, Text = formatter.ParityText(WeekParity.Even) }
        };
    }

    private string RowAutomationId(string part, int index)
    {
        var rootId = AutomationProperties.GetAutomationId(this);
        return $"{(string.IsNullOrWhiteSpace(rootId) ? "MeetingTimesEditor" : rootId)}{part}{index + 1}";
    }

    private void RaiseMeetingsChanged()
    {
        if (!_rebuilding)
            MeetingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int ReadPeriod(NumberBox box) =>
        double.IsNaN(box.Value) ? 1 : Math.Max(1, (int)Math.Round(box.Value));

}

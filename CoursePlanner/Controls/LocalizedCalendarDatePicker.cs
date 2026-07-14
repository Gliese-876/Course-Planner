using CoursePlanner.Core;
using CoursePlanner.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Foundation;

namespace CoursePlanner.Controls;

public sealed class LocalizedCalendarDatePicker : UserControl
{
    private const int CalendarColumns = 7;
    private const int CalendarOptionColumns = 3;
    private const int CalendarOptionRows = 4;
    private const int CalendarYearPageSize = CalendarOptionColumns * CalendarOptionRows;
    private const int CalendarYearStart = CalendarDateMath.MinYear;
    private const int CalendarYearEnd = CalendarDateMath.MaxYear;
    private const double CalendarPanelWidth = 260;
    private const double CalendarPopupSize = CalendarPanelWidth + CalendarOuterPadding * 2;
    private const double CalendarPanelSpacing = 6;
    private const double CalendarOuterPadding = 8;
    private const double CalendarHeaderButtonSize = 28;
    private const int CalendarDateRows = 6;
    private const double CalendarDateCircleSize = 26;
    private const double CalendarSelectorHeight = 28;
    private const double CalendarCellMinWidth = 30;
    private const double CalendarGridSpacing = 2;

    private readonly StackPanel _root = new() { Spacing = 6 };
    private readonly TextBlock _header = new();
    private readonly Button _dateButton = new()
    {
        MinHeight = 36,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 4, 8, 4)
    };
    private readonly Flyout _flyout = new();

    private AppLocalizer? _text;
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _displayMonth;
    private CalendarViewMode _viewMode = CalendarViewMode.Date;
    private int _yearPageStart;
    private Button? _calendarJumpButton;
    private Button? _calendarPreviousButton;
    private Button? _calendarNextButton;
    private TextBlock? _calendarHeaderText;
    private Grid? _calendarBodyHost;

    public LocalizedCalendarDatePicker()
    {
        AppTypography.Apply(this);
        _header.Style = AppTypography.TextStyle(AppTextRole.BodyStrong);
        AppTypography.Apply(_dateButton);
        _date = CalendarDateMath.Clamp(_date);
        _displayMonth = CalendarDateMath.MonthStart(_date);
        AppMaterialLayer.ApplyTransientFlyout(_flyout);
        _dateButton.Click += (_, _) => OpenCalendar();
        _root.Children.Add(_header);
        _root.Children.Add(_dateButton);
        Content = _root;
        RefreshButtonContent();
    }

    public DateOnly Date
    {
        get => _date;
        set
        {
            _date = CalendarDateMath.Clamp(value);
            _displayMonth = CalendarDateMath.MonthStart(_date);
            RefreshButtonContent();
        }
    }

    public event EventHandler? DateChanged;

    public void ApplyText(AppLocalizer text, string header)
    {
        _text = text;
        _header.Text = header;
        AutomationProperties.SetName(this, header);
        RefreshButtonContent();
    }

    private void RefreshButtonContent()
    {
        _dateButton.Content = PickerDisplayContent.Create(
            PickerDisplayIcon.Calendar,
            DateDisplay.Date(_date));
        var rootAutomationId = AutomationProperties.GetAutomationId(this);
        if (!string.IsNullOrWhiteSpace(rootAutomationId))
            AutomationProperties.SetAutomationId(_dateButton, $"{rootAutomationId}Button");
        AutomationProperties.SetName(_dateButton, $"{_header.Text} {DateDisplay.Date(_date)}");
    }

    private void OpenCalendar()
    {
        _displayMonth = CalendarDateMath.MonthStart(_date);
        _viewMode = CalendarViewMode.Date;
        _yearPageStart = YearPageStart(_displayMonth.Year);
        AppMaterialLayer.ApplyTransientFlyout(_flyout);
        _flyout.Content = CreateCalendarSurface();
        _flyout.ShowAt(_dateButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Bottom,
            Position = new Point(_dateButton.ActualWidth / 2, 0)
        });
    }

    private FrameworkElement CreateCalendarSurface()
    {
        var panel = new Grid
        {
            RowSpacing = CalendarPanelSpacing,
            UseLayoutRounding = true,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        var header = CreateMonthHeader();
        Grid.SetRow(header, 0);
        panel.Children.Add(header);

        _calendarBodyHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AppAnimationLayer.SetProfile(_calendarBodyHost, AppAnimationProfile.ContentRefresh);
        _calendarBodyHost.Children.Add(CreateCalendarBody());
        Grid.SetRow(_calendarBodyHost, 1);
        panel.Children.Add(_calendarBodyHost);

        var footer = CreateCalendarFooter();
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);

        return new Border
        {
            Width = CalendarPopupSize,
            Height = CalendarPopupSize,
            Background = AppBrushes.Transparent(),
            Padding = new Thickness(CalendarOuterPadding),
            Child = panel
        };
    }

    private FrameworkElement CreateCalendarBody()
    {
        if (_viewMode == CalendarViewMode.Month)
            return CreateMonthSelectionGrid();

        if (_viewMode == CalendarViewMode.Year)
            return CreateYearSelectionGrid();

        var body = new Grid
        {
            RowSpacing = CalendarPanelSpacing,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };

        body.Children.Add(CreateWeekdayHeader());

        var dateGrid = CreateDateGrid();
        Grid.SetRow(dateGrid, 1);
        body.Children.Add(dateGrid);
        return body;
    }

    private Grid CreateMonthHeader()
    {
        var grid = new Grid
        {
            Height = CalendarSelectorHeight,
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var previous = CreateIconButton("\uE76B", Text("PreviousMonth", "Previous month"));
        previous.Click += (_, _) => MoveDisplay(-1);
        _calendarPreviousButton = previous;
        grid.Children.Add(previous);

        var title = CreateJumpButton();
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var next = CreateIconButton("\uE76C", Text("NextMonth", "Next month"));
        next.Click += (_, _) => MoveDisplay(1);
        _calendarNextButton = next;
        Grid.SetColumn(next, 2);
        grid.Children.Add(next);

        UpdateNavigationAvailability();

        return grid;
    }

    private Button CreateJumpButton()
    {
        var headerText = HeaderText();
        var monthHeaderText = new TextBlock
        {
            Text = headerText,
            Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
            VerticalAlignment = VerticalAlignment.Center
        };
        _calendarHeaderText = monthHeaderText;

        var hoverSurface = new Border
        {
            Background = AppBrushes.Transparent(),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = monthHeaderText
        };

        var button = new Button
        {
            Content = hoverSurface,
            MinHeight = CalendarHeaderButtonSize,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AppTypography.Apply(button);
        ApplyFlatHeaderButtonChrome(button);
        ApplyStableHeaderHover(button, hoverSurface);
        AutomationProperties.SetAutomationId(button, "CalendarJumpButton");
        AutomationProperties.SetName(button, headerText);
        button.Click += (_, _) => CycleViewMode();
        _calendarJumpButton = button;
        return button;
    }

    private Button CreateIconButton(string glyph, string automationName)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var hoverSurface = new Border
        {
            Width = CalendarHeaderButtonSize,
            Height = CalendarHeaderButtonSize,
            Background = AppBrushes.Transparent(),
            CornerRadius = new CornerRadius(4),
            Child = icon
        };

        var button = new Button
        {
            Content = hoverSurface,
            Width = CalendarHeaderButtonSize,
            Height = CalendarHeaderButtonSize,
            Padding = new Thickness(0)
        };
        AppTypography.Apply(button);
        ApplyFlatHeaderButtonChrome(button);
        ApplyStableHeaderHover(button, hoverSurface);
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static void ApplyFlatHeaderButtonChrome(Button button)
    {
        var transparent = AppBrushes.Transparent();
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = transparent;
        button.Resources["ButtonBackgroundPressed"] = transparent;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
    }

    private void ApplyStableHeaderHover(Button button, Border hoverSurface)
    {
        var transparent = AppBrushes.Transparent();
        button.PointerEntered += (_, _) =>
        {
            hoverSurface.Background = AppMaterialLayer.Brush(this, AppColorRole.CalendarHeaderHover, Colors.Transparent);
        };
        button.PointerExited += (_, _) =>
        {
            hoverSurface.Background = transparent;
        };
        button.PointerPressed += (_, _) =>
        {
            hoverSurface.Background = AppMaterialLayer.Brush(this, AppColorRole.CalendarHeaderPressed, Colors.Transparent);
        };
        button.PointerReleased += (_, _) =>
        {
            hoverSurface.Background = AppMaterialLayer.Brush(this, AppColorRole.CalendarHeaderHover, Colors.Transparent);
        };
    }

    private Grid CreateMonthSelectionGrid()
    {
        var grid = CreateOptionGrid();

        for (var month = 1; month <= 12; month++)
        {
            var option = CreateOptionButton(MonthOptionText(month), month == _displayMonth.Month);
            var optionMonth = month;
            option.Click += (_, _) => SelectDisplayMonth(optionMonth);
            AutomationProperties.SetAutomationId(option, $"CalendarMonth{month}");
            AutomationProperties.SetName(option, MonthOptionText(month));
            Grid.SetRow(option, (month - 1) / CalendarOptionColumns);
            Grid.SetColumn(option, (month - 1) % CalendarOptionColumns);
            grid.Children.Add(option);
        }

        return grid;
    }

    private Grid CreateYearSelectionGrid()
    {
        var grid = CreateOptionGrid();

        for (var index = 0; index < CalendarYearPageSize; index++)
        {
            var year = _yearPageStart + index;
            var option = CreateOptionButton(YearOptionText(year), year == _displayMonth.Year);
            option.IsEnabled = year >= CalendarYearStart && year <= CalendarYearEnd;
            option.Click += (_, _) => SelectDisplayYear(year);
            AutomationProperties.SetAutomationId(option, $"CalendarYear{year}");
            AutomationProperties.SetName(option, YearOptionText(year));
            Grid.SetRow(option, index / CalendarOptionColumns);
            Grid.SetColumn(option, index % CalendarOptionColumns);
            grid.Children.Add(option);
        }

        return grid;
    }

    private static Grid CreateOptionGrid()
    {
        var grid = new Grid
        {
            ColumnSpacing = CalendarGridSpacing,
            RowSpacing = CalendarGridSpacing,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        for (var column = 0; column < CalendarOptionColumns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < CalendarOptionRows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private Button CreateOptionButton(string text, bool selected)
    {
        var transparent = AppBrushes.Transparent();
        var selectedBackground = AppMaterialLayer.Brush(this, AppColorRole.PickerSelected, Colors.Teal);
        var selectedForeground = AppMaterialLayer.Brush(this, AppColorRole.PickerSelectedText, Colors.White);
        var hoverBackground = AppMaterialLayer.Brush(this, AppColorRole.PickerHover, Colors.Transparent);
        var pressedBackground = AppMaterialLayer.Brush(this, AppColorRole.PickerPressed, Colors.Transparent);
        var normalForeground = AppMaterialLayer.Brush(this, AppColorRole.TextPrimary, Colors.Black);
        var button = new Button
        {
            Content = text,
            MinHeight = 0,
            Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = selected ? selectedBackground : transparent,
            BorderBrush = selected ? selectedBackground : transparent,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Foreground = selected ? selectedForeground : normalForeground
        };
        AppTypography.Apply(button);
        button.Resources["ButtonBackground"] = selected ? selectedBackground : transparent;
        button.Resources["ButtonBackgroundPointerOver"] = selected ? selectedBackground : hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = selected ? selectedBackground : pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = selected ? selectedBackground : transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = selected ? selectedBackground : transparent;
        button.Resources["ButtonBorderBrushPressed"] = selected ? selectedBackground : transparent;
        button.Resources["ButtonForeground"] = selected ? selectedForeground : normalForeground;
        button.Resources["ButtonForegroundPointerOver"] = selected ? selectedForeground : normalForeground;
        button.Resources["ButtonForegroundPressed"] = selected ? selectedForeground : normalForeground;

        return button;
    }

    private Grid CreateWeekdayHeader()
    {
        var grid = CreateSevenColumnGrid(columnSpacing: 4);
        foreach (var (day, index) in WeekdayOrder().Select((day, index) => (day, index)))
        {
            var text = new TextBlock
            {
                Text = WeekdayName(day),
                Style = AppTypography.TextStyle(AppTextRole.Caption),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = AppTypography.FontFamilyFor(AppTextRole.BodyStrong),
                FontWeight = AppTypography.FontWeightFor(AppTextRole.BodyStrong),
                Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextSecondary, Colors.Gray),
                FontSize = 11
            };
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }

        return grid;
    }

    private Grid CreateDateGrid()
    {
        var grid = CreateSevenColumnGrid(columnSpacing: CalendarGridSpacing);
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.VerticalAlignment = VerticalAlignment.Stretch;
        grid.RowSpacing = CalendarGridSpacing;
        for (var row = 0; row < CalendarDateRows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        PopulateDateGrid(grid);
        return grid;
    }

    private void PopulateDateGrid(Grid grid)
    {
        grid.Children.Clear();
        var today = CalendarDateMath.Clamp(DateOnly.FromDateTime(DateTime.Today));
        foreach (var calendarCell in CalendarDateMath.CreateMonthGrid(_displayMonth, FirstWeekday()))
        {
            var cell = calendarCell.Date is { } date
                ? CreateDateButton(date, today, calendarCell.IsDisplayMonth)
                : CreateUnavailableDateButton(calendarCell.Index);

            Grid.SetRow(cell, calendarCell.Index / CalendarColumns);
            Grid.SetColumn(cell, calendarCell.Index % CalendarColumns);
            grid.Children.Add(cell);
        }
    }

    private Button CreateUnavailableDateButton(int index)
    {
        var button = new Button
        {
            Content = "",
            IsEnabled = false,
            MinWidth = CalendarCellMinWidth,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AppTypography.Apply(button);
        ApplyFlatButtonChrome(button);
        AutomationProperties.SetAutomationId(button, $"CalendarBoundaryCell{index:D2}");
        AutomationProperties.SetName(button, Text("OutOfRange", "Out of range"));
        return button;
    }

    private Button CreateDateButton(DateOnly date, DateOnly today, bool isDisplayMonth)
    {
        var selected = date == _date;
        var isToday = date == today;
        var circle = new Border
        {
            Width = CalendarDateCircleSize,
            Height = CalendarDateCircleSize,
            CornerRadius = new CornerRadius(CalendarDateCircleSize / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dayText = new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            Style = AppTypography.TextStyle(AppTextRole.Body),
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        circle.Child = dayText;

        var button = new Button
        {
            Content = circle,
            MinWidth = CalendarCellMinWidth,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = date
        };
        AppTypography.Apply(button);
        ApplyFlatButtonChrome(button);
        UpdateDateCircle(circle, dayText, selected, isToday, isDisplayMonth, hovering: false, pressed: false);

        AutomationProperties.SetName(button, DateDisplay.Date(date));
        AutomationProperties.SetAutomationId(button, $"CalendarDate{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
        button.PointerEntered += (_, _) => UpdateDateCircle(circle, dayText, selected, isToday, isDisplayMonth, hovering: true, pressed: false);
        button.PointerExited += (_, _) => UpdateDateCircle(circle, dayText, selected, isToday, isDisplayMonth, hovering: false, pressed: false);
        button.PointerPressed += (_, _) => UpdateDateCircle(circle, dayText, selected, isToday, isDisplayMonth, hovering: true, pressed: true);
        button.PointerReleased += (_, _) => UpdateDateCircle(circle, dayText, selected, isToday, isDisplayMonth, hovering: true, pressed: false);
        button.Click += DateButton_Click;
        return button;
    }

    private static void ApplyFlatButtonChrome(Button button)
    {
        var transparent = AppBrushes.Transparent();
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = transparent;
        button.Resources["ButtonBackgroundPressed"] = transparent;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
    }

    private void UpdateDateCircle(Border circle, TextBlock text, bool selected, bool today, bool isDisplayMonth, bool hovering, bool pressed)
    {
        if (selected)
        {
            circle.Background = AppMaterialLayer.Brush(this, AppColorRole.PickerSelected, Colors.DeepSkyBlue);
            circle.BorderBrush = AppBrushes.Transparent();
            circle.BorderThickness = new Thickness(0);
            text.Foreground = AppMaterialLayer.Brush(this, AppColorRole.PickerSelectedText, Colors.White);
            text.Opacity = 1;
            return;
        }

        circle.Background = pressed
            ? AppMaterialLayer.Brush(this, AppColorRole.CalendarDatePressed, Colors.Transparent)
            : hovering
                ? AppMaterialLayer.Brush(this, AppColorRole.CalendarDateHover, Colors.Transparent)
            : AppBrushes.Transparent();
        circle.BorderBrush = today
            ? AppMaterialLayer.Brush(this, AppColorRole.PickerSelected, Colors.Teal)
            : AppBrushes.Transparent();
        circle.BorderThickness = new Thickness(today ? 1 : 0);
        text.Foreground = isDisplayMonth || today
            ? AppMaterialLayer.Brush(this, AppColorRole.TextPrimary, Colors.Black)
            : AppMaterialLayer.Brush(this, AppColorRole.PickerMutedText, Colors.Gray);
        text.Opacity = 1;
    }

    private FrameworkElement CreateCalendarFooter()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var selectedDateText = new TextBlock
        {
            Text = DateDisplay.Date(_date),
            Style = AppTypography.TextStyle(AppTextRole.Body),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AppMaterialLayer.Brush(this, AppColorRole.PickerMutedText, Colors.Gray)
        };
        grid.Children.Add(selectedDateText);

        var today = new Button
        {
            Content = Text("Today", "Today"),
            MinHeight = CalendarSelectorHeight,
            Padding = new Thickness(10, 1, 10, 1)
        };
        AppTypography.Apply(today);
        today.Click += (_, _) => SelectDate(CalendarDateMath.Clamp(DateOnly.FromDateTime(DateTime.Today)));
        Grid.SetColumn(today, 1);
        grid.Children.Add(today);

        return grid;
    }

    private void MoveDisplay(int offset)
    {
        if (_viewMode == CalendarViewMode.Year)
        {
            _yearPageStart = Math.Clamp(
                _yearPageStart + offset * CalendarYearPageSize,
                YearPageStart(CalendarYearStart),
                YearPageStart(CalendarYearEnd));
        }
        else if (_viewMode == CalendarViewMode.Month)
        {
            _displayMonth = CalendarDateMath.MonthStart(CalendarDateMath.AddYearsClamped(_displayMonth, offset));
            _yearPageStart = YearPageStart(_displayMonth.Year);
        }
        else
        {
            _displayMonth = CalendarDateMath.MonthStart(CalendarDateMath.AddMonthsClamped(_displayMonth, offset));
            _yearPageStart = YearPageStart(_displayMonth.Year);
        }

        RefreshOpenCalendar();
    }

    private void SelectDisplayMonth(int month)
    {
        _displayMonth = new DateOnly(_displayMonth.Year, month, 1);
        _viewMode = CalendarViewMode.Date;
        RefreshOpenCalendar();
    }

    private void SelectDisplayYear(int year)
    {
        _displayMonth = new DateOnly(year, _displayMonth.Month, 1);
        _yearPageStart = YearPageStart(year);
        _viewMode = CalendarViewMode.Month;
        RefreshOpenCalendar();
    }

    private void CycleViewMode()
    {
        _viewMode = _viewMode switch
        {
            CalendarViewMode.Date => CalendarViewMode.Month,
            CalendarViewMode.Month => CalendarViewMode.Year,
            _ => CalendarViewMode.Date
        };

        if (_viewMode == CalendarViewMode.Year)
            _yearPageStart = YearPageStart(_displayMonth.Year);

        RefreshOpenCalendar();
    }

    private void RefreshOpenCalendar()
    {
        if (_calendarBodyHost is null)
        {
            _flyout.Content = CreateCalendarSurface();
            return;
        }

        var headerText = HeaderText();
        if (_calendarHeaderText is not null)
            _calendarHeaderText.Text = headerText;
        if (_calendarJumpButton is not null)
            AutomationProperties.SetName(_calendarJumpButton, headerText);
        UpdateNavigationAvailability();

        AppAnimationLayer.RefreshContent(_calendarBodyHost, () =>
        {
            _calendarBodyHost.Children.Clear();
            _calendarBodyHost.Children.Add(CreateCalendarBody());
        });
    }

    private void DateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateOnly selected })
            SelectDate(selected);
    }

    private void SelectDate(DateOnly selected)
    {
        selected = CalendarDateMath.Clamp(selected);
        if (selected == _date)
        {
            _flyout.Hide();
            return;
        }

        _date = selected;
        _displayMonth = CalendarDateMath.MonthStart(selected);
        RefreshButtonContent();
        _flyout.Hide();
        DateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Grid CreateSevenColumnGrid(double columnSpacing)
    {
        var grid = new Grid { ColumnSpacing = columnSpacing };
        for (var index = 0; index < CalendarColumns; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private IEnumerable<DayOfWeek> WeekdayOrder()
    {
        var firstDay = FirstWeekday();
        for (var offset = 0; offset < CalendarColumns; offset++)
            yield return (DayOfWeek)(((int)firstDay + offset) % CalendarColumns);
    }

    private DayOfWeek FirstWeekday() =>
        _text?.Culture.DateTimeFormat.FirstDayOfWeek ?? DayOfWeek.Monday;

    private bool CanMoveDisplay(int offset) =>
        _viewMode switch
        {
            CalendarViewMode.Year => offset < 0
                ? _yearPageStart > YearPageStart(CalendarYearStart)
                : _yearPageStart < YearPageStart(CalendarYearEnd),
            CalendarViewMode.Month => CalendarDateMath.CanAddYears(_displayMonth, offset),
            _ => CalendarDateMath.CanAddMonths(_displayMonth, offset)
        };

    private void UpdateNavigationAvailability()
    {
        if (_calendarPreviousButton is not null)
            _calendarPreviousButton.IsEnabled = CanMoveDisplay(-1);
        if (_calendarNextButton is not null)
            _calendarNextButton.IsEnabled = CanMoveDisplay(1);
    }

    private static int YearPageStart(int year)
    {
        var clampedYear = Math.Clamp(year, CalendarYearStart, CalendarYearEnd);
        var offset = clampedYear - CalendarYearStart;
        return CalendarYearStart + (offset / CalendarYearPageSize) * CalendarYearPageSize;
    }

    private string YearOptionText(int year) =>
        _text?.ResolvedLanguage == LanguageMode.SimplifiedChinese
            ? string.Format(CultureInfo.InvariantCulture, "{0} 年", year)
            : year.ToString(CultureInfo.InvariantCulture);

    private string MonthOptionText(int month)
    {
        var text = _text;
        if (text?.ResolvedLanguage == LanguageMode.SimplifiedChinese)
            return string.Format(CultureInfo.InvariantCulture, "{0} 月", month);

        return new DateTime(2000, month, 1).ToString("MMMM", text?.Culture ?? CultureInfo.InvariantCulture);
    }

    private string HeaderText()
    {
        if (_viewMode == CalendarViewMode.Year)
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1}",
                _yearPageStart,
                Math.Min(_yearPageStart + CalendarYearPageSize - 1, CalendarYearEnd));

        if (_viewMode == CalendarViewMode.Month)
            return YearOptionText(_displayMonth.Year);

        return _text?.ResolvedLanguage == LanguageMode.SimplifiedChinese
            ? string.Format(CultureInfo.InvariantCulture, "{0} 年 {1} 月", _displayMonth.Year, _displayMonth.Month)
            : _displayMonth.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", _text?.Culture ?? CultureInfo.InvariantCulture);
    }

    private string WeekdayName(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Monday => Text("MondayShort", "Mon"),
            DayOfWeek.Tuesday => Text("TuesdayShort", "Tue"),
            DayOfWeek.Wednesday => Text("WednesdayShort", "Wed"),
            DayOfWeek.Thursday => Text("ThursdayShort", "Thu"),
            DayOfWeek.Friday => Text("FridayShort", "Fri"),
            DayOfWeek.Saturday => Text("SaturdayShort", "Sat"),
            _ => Text("SundayShort", "Sun")
        };

    private string Text(string key, string fallback) =>
        _text is null ? fallback : _text[key];

    private enum CalendarViewMode
    {
        Date,
        Month,
        Year
    }
}

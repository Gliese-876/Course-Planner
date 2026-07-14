using CoursePlanner.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace CoursePlanner.Controls;

public sealed class CompactTimePicker : UserControl
{
    public const double MinimumDisplayWidth = 108;

    private const double ClockWidth = 260;
    private const double ClockHeight = 76;
    private const double ClockHorizontalPadding = 8;
    private const double TimePartColumnWidth = 106;
    private const double SeparatorColumnWidth = 28;
    private const double ActiveRegionWidth = 88;
    private const double ActiveRegionHeight = 64;
    private const double StepButtonSize = 34;

    private enum TimePart
    {
        Hour,
        Minute
    }

    private readonly StackPanel _root = new() { Spacing = 6 };
    private readonly TextBlock _header = new();
    private readonly Button _timeButton = new()
    {
        MinHeight = 36,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 4, 8, 4)
    };
    private readonly Flyout _flyout = new();

    private AppLocalizer? _text;
    private TimeSpan _time = new(8, 0, 0);
    private int _draftHour;
    private int _draftMinute;
    private TimePart _activePart = TimePart.Hour;
    private TimePart? _hoverPart;
    private TimePart? _pressedPart;
    private string _digitBuffer = string.Empty;
    private DateTime _lastDigitInputAt = DateTime.MinValue;
    private Button? _hourPartButton;
    private Button? _minutePartButton;
    private Border? _hourHighlight;
    private Border? _minuteHighlight;
    private TextBlock? _hourText;
    private TextBlock? _minuteText;

    public CompactTimePicker()
    {
        MinWidth = MinimumDisplayWidth;
        AppTypography.Apply(this);
        _header.Style = AppTypography.TextStyle(AppTextRole.BodyStrong);
        AppTypography.Apply(_timeButton);
        AppMaterialLayer.ApplyTransientFlyout(_flyout);
        _timeButton.Click += (_, _) => OpenTimePicker();
        _root.Children.Add(_header);
        _root.Children.Add(_timeButton);
        Content = _root;
        RefreshButtonContent();
    }

    public string Header
    {
        get => _header.Text;
        set
        {
            _header.Text = value;
            AutomationProperties.SetName(this, value);
            RefreshButtonContent();
        }
    }

    public TimeSpan Time
    {
        get => _time;
        set => SetTime(value.Hours, value.Minutes, raiseChanged: false);
    }

    public event EventHandler? TimeChanged;

    public void ApplyText(AppLocalizer text, string header)
    {
        _text = text;
        Header = header;
    }

    private void RefreshButtonContent()
    {
        _timeButton.Content = PickerDisplayContent.Create(
            PickerDisplayIcon.Clock,
            FormatTime(_time));
        AutomationProperties.SetName(_timeButton, $"{Header} {FormatTime(_time)}");
    }

    private void OpenTimePicker()
    {
        _draftHour = _time.Hours;
        _draftMinute = _time.Minutes;
        _activePart = TimePart.Hour;
        ResetDigitBuffer();
        AppMaterialLayer.ApplyTransientFlyout(_flyout);
        _flyout.Content = CreateTimePickerSurface();
        _flyout.ShowAt(_timeButton);
        DispatcherQueue.TryEnqueue(() => _hourPartButton?.Focus(FocusState.Programmatic));
    }

    private FrameworkElement CreateTimePickerSurface()
    {
        var panel = new Grid
        {
            Width = ClockWidth,
            RowSpacing = 4,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        var upperStepperRow = CreateStepperRow(isUpperRow: true);
        Grid.SetRow(upperStepperRow, 0);
        panel.Children.Add(upperStepperRow);

        var editor = CreateClockEditor();
        Grid.SetRow(editor, 1);
        panel.Children.Add(editor);

        var lowerStepperRow = CreateStepperRow(isUpperRow: false);
        Grid.SetRow(lowerStepperRow, 2);
        panel.Children.Add(lowerStepperRow);

        var footer = CreateFooter();
        Grid.SetRow(footer, 3);
        panel.Children.Add(footer);

        UpdateEditor();

        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = AppBrushes.Transparent(),
            Child = panel
        };
    }

    private Grid CreateStepperRow(bool isUpperRow)
    {
        var grid = CreateClockColumns();
        grid.Width = ClockWidth;
        grid.Height = 24;
        grid.Padding = new Thickness(ClockHorizontalPadding, 0, ClockHorizontalPadding, 0);
        grid.HorizontalAlignment = HorizontalAlignment.Center;
        var stepDirection = isUpperRow ? -1 : 1;

        var hour = CreateStepButton(TimePart.Hour, stepDirection, isUpperRow);
        Grid.SetColumn(hour, 0);
        grid.Children.Add(hour);

        var minute = CreateStepButton(TimePart.Minute, stepDirection, isUpperRow);
        Grid.SetColumn(minute, 2);
        grid.Children.Add(minute);

        return grid;
    }

    private FrameworkElement CreateStepButton(TimePart part, int stepDirection, bool isUpperRow)
    {
        var icon = new Polyline
        {
            Width = 20,
            Height = 14,
            Points = isUpperRow
                ? new PointCollection { new Point(3, 10), new Point(10, 3), new Point(17, 10) }
                : new PointCollection { new Point(3, 4), new Point(10, 11), new Point(17, 4) },
            Stroke = RoleBrush(AppColorRole.PickerMutedText, Colors.Gray),
            StrokeThickness = 2,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(icon, null);
        AutomationProperties.SetName(icon, string.Empty);

        var button = new RepeatButton
        {
            Width = StepButtonSize,
            Height = 24,
            Background = AppBrushes.Transparent(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon
        };
        button.Resources["RepeatButtonBackground"] = AppBrushes.Transparent();
        button.Resources["RepeatButtonBackgroundPointerOver"] = RoleBrush(AppColorRole.PickerHover, Colors.Transparent);
        button.Resources["RepeatButtonBackgroundPressed"] = RoleBrush(AppColorRole.PickerPressed, Colors.Transparent);
        button.Resources["RepeatButtonBorderBrush"] = AppBrushes.Transparent();
        button.Resources["RepeatButtonBorderBrushPointerOver"] = AppBrushes.Transparent();
        button.Resources["RepeatButtonBorderBrushPressed"] = AppBrushes.Transparent();
        AutomationProperties.SetAutomationId(
            button,
            $"TimePicker{part}{(isUpperRow ? "Previous" : "Next")}Button");
        AutomationProperties.SetName(button, $"{PartName(part)} {(stepDirection > 0 ? "+" : "-")}");
        ToolTipService.SetToolTip(button, null);
        button.Click += (_, _) => StepPart(part, stepDirection);
        return button;
    }

    private Grid CreateClockEditor()
    {
        var host = new Grid
        {
            Width = ClockWidth,
            Height = ClockHeight
        };
        host.PointerWheelChanged += (_, args) =>
        {
            var delta = args.GetCurrentPoint(host).Properties.MouseWheelDelta;
            if (delta == 0)
                return;

            StepPart(_activePart, delta > 0 ? -1 : 1);
            args.Handled = true;
        };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = RoleBrush(AppColorRole.PickerEditorStroke, Colors.Gray),
            Background = RoleBrush(AppColorRole.PickerEditor, Colors.Transparent)
        };
        host.Children.Add(frame);

        var grid = CreateClockColumns();
        grid.Padding = new Thickness(ClockHorizontalPadding, 5, ClockHorizontalPadding, 5);
        frame.Child = grid;

        _hourText = CreateTimePartText();
        var hourCell = CreateTimePartCell(TimePart.Hour, _hourText);
        Grid.SetColumn(hourCell, 0);
        grid.Children.Add(hourCell);

        var separator = new TextBlock
        {
            Text = ":",
            FontFamily = AppTypography.FontFamilyFor(AppTextRole.BodyStrong),
            FontSize = 32,
            FontWeight = AppTypography.FontWeightFor(AppTextRole.BodyStrong),
            LineHeight = AppTypography.LineHeight(AppTextRole.BodyStrong, 32),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextLineBounds = TextLineBounds.Full,
            Foreground = RoleBrush(AppColorRole.TextSecondary, Colors.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5)
        };
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);

        _minuteText = CreateTimePartText();
        var minuteCell = CreateTimePartCell(TimePart.Minute, _minuteText);
        Grid.SetColumn(minuteCell, 2);
        grid.Children.Add(minuteCell);

        var accent = new Border
        {
            Height = 2,
            Margin = new Thickness(1, 0, 1, 0),
            Background = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        host.Children.Add(accent);

        return host;
    }

    private static Grid CreateClockColumns()
    {
        return new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(TimePartColumnWidth) },
                new ColumnDefinition { Width = new GridLength(SeparatorColumnWidth) },
                new ColumnDefinition { Width = new GridLength(TimePartColumnWidth) }
            }
        };
    }

    private FrameworkElement CreateTimePartCell(TimePart part, TextBlock valueText)
    {
        var cell = new Grid
        {
            Width = TimePartColumnWidth,
            Height = ActiveRegionHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var highlight = new Border
        {
            Width = ActiveRegionWidth,
            Height = ActiveRegionHeight,
            CornerRadius = new CornerRadius(4),
            Background = RoleBrush(AppColorRole.PickerSoftSelected, Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        cell.Children.Add(highlight);

        var button = CreateTimePartButton(part, valueText);
        cell.Children.Add(button);

        if (part == TimePart.Hour)
        {
            _hourHighlight = highlight;
            _hourPartButton = button;
        }
        else
        {
            _minuteHighlight = highlight;
            _minutePartButton = button;
        }

        return cell;
    }

    private Button CreateTimePartButton(TimePart part, TextBlock valueText)
    {
        var button = CreateFlatButton(ActiveRegionWidth, ActiveRegionHeight);
        button.Content = valueText;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(button, null);
        AutomationProperties.SetAutomationId(button, part == TimePart.Hour ? "TimePickerHourWheel" : "TimePickerMinuteWheel");
        button.Click += (_, _) => SetActivePart(part);
        button.GotFocus += (_, _) => SetActivePart(part);
        button.KeyDown += (_, args) => HandleEditorKeyDown(part, args);
        button.PointerEntered += (_, _) =>
        {
            _hoverPart = part;
            UpdateEditor();
        };
        button.PointerExited += (_, _) =>
        {
            if (_hoverPart == part)
                _hoverPart = null;
            if (_pressedPart == part)
                _pressedPart = null;
            UpdateEditor();
        };
        button.PointerPressed += (_, _) =>
        {
            _pressedPart = part;
            SetActivePart(part);
            UpdateEditor();
        };
        button.PointerReleased += (_, _) =>
        {
            if (_pressedPart == part)
                _pressedPart = null;
            UpdateEditor();
        };
        button.PointerWheelChanged += (_, args) =>
        {
            SetActivePart(part);
            var delta = args.GetCurrentPoint(button).Properties.MouseWheelDelta;
            if (delta != 0)
                StepPart(part, delta > 0 ? -1 : 1);
            args.Handled = true;
        };
        return button;
    }

    private static TextBlock CreateTimePartText()
    {
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        AppTypography.ApplyTextMetrics(text, AppTextRole.ClockDigit);
        return text;
    }

    private static Button CreateFlatButton(double width, double height, bool isTabStop = true)
    {
        var transparent = AppBrushes.Transparent();
        return new Button
        {
            Width = width,
            Height = height,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = transparent,
            BorderBrush = transparent,
            BorderThickness = new Thickness(0),
            IsTabStop = isTabStop,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Resources =
            {
                ["ButtonBackground"] = transparent,
                ["ButtonBackgroundPointerOver"] = transparent,
                ["ButtonBackgroundPressed"] = transparent,
                ["ButtonBackgroundDisabled"] = transparent,
                ["ButtonBorderBrush"] = transparent,
                ["ButtonBorderBrushPointerOver"] = transparent,
                ["ButtonBorderBrushPressed"] = transparent,
                ["ButtonBorderBrushDisabled"] = transparent
            }
        };
    }

    private void HandleEditorKeyDown(TimePart part, KeyRoutedEventArgs args)
    {
        var digit = DigitFromKey(args.Key);
        if (digit is not null)
        {
            ApplyDigit(part, digit.Value);
            args.Handled = true;
            return;
        }

        switch (args.Key)
        {
            case VirtualKey.Up:
                StepPart(part, -1);
                args.Handled = true;
                break;
            case VirtualKey.Down:
                StepPart(part, 1);
                args.Handled = true;
                break;
            case VirtualKey.Left:
                FocusPart(TimePart.Hour);
                args.Handled = true;
                break;
            case VirtualKey.Right:
                FocusPart(TimePart.Minute);
                args.Handled = true;
                break;
            case VirtualKey.Enter:
                ApplyDraft();
                args.Handled = true;
                break;
            case VirtualKey.Escape:
                _flyout.Hide();
                args.Handled = true;
                break;
        }
    }

    private void StepPart(TimePart part, int direction)
    {
        SetActivePart(part);
        ResetDigitBuffer();
        if (part == TimePart.Hour)
            _draftHour = Wrap(_draftHour + direction, 24);
        else
            _draftMinute = Wrap(_draftMinute + direction, 60);

        UpdateEditor();
        FocusPart(part);
    }

    private void ApplyDigit(TimePart part, int digit)
    {
        SetActivePart(part);
        var now = DateTime.UtcNow;
        if ((now - _lastDigitInputAt).TotalMilliseconds > 1200)
            _digitBuffer = string.Empty;

        _lastDigitInputAt = now;
        _digitBuffer += digit.ToString(CultureInfo.InvariantCulture);
        if (_digitBuffer.Length > 2)
            _digitBuffer = _digitBuffer[^2..];

        var maximum = part == TimePart.Hour ? 23 : 59;
        if (int.TryParse(_digitBuffer, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= maximum)
        {
            SetDraftPart(part, value);
            if (_digitBuffer.Length == 2)
                ResetDigitBuffer();
        }
        else
        {
            _digitBuffer = digit.ToString(CultureInfo.InvariantCulture);
            SetDraftPart(part, digit);
        }

        UpdateEditor();
        FocusPart(part);
    }

    private void SetDraftPart(TimePart part, int value)
    {
        if (part == TimePart.Hour)
            _draftHour = Math.Clamp(value, 0, 23);
        else
            _draftMinute = Math.Clamp(value, 0, 59);
    }

    private void SetActivePart(TimePart part)
    {
        if (_activePart == part)
            return;

        _activePart = part;
        ResetDigitBuffer();
        UpdateEditor();
    }

    private void FocusPart(TimePart part)
    {
        var button = part == TimePart.Hour ? _hourPartButton : _minutePartButton;
        button?.Focus(FocusState.Programmatic);
    }

    private void UpdateEditor()
    {
        if (_hourText is not null)
            _hourText.Text = _draftHour.ToString("00", CultureInfo.InvariantCulture);
        if (_minuteText is not null)
            _minuteText.Text = _draftMinute.ToString("00", CultureInfo.InvariantCulture);

        UpdatePartButton(_hourPartButton, _hourHighlight, _hourText, TimePart.Hour, _draftHour);
        UpdatePartButton(_minutePartButton, _minuteHighlight, _minuteText, TimePart.Minute, _draftMinute);
    }

    private void UpdatePartButton(Button? button, Border? highlight, TextBlock? text, TimePart part, int value)
    {
        if (button is null || highlight is null || text is null)
            return;

        var active = _activePart == part;
        var hovered = _hoverPart == part;
        var pressed = _pressedPart == part;
        var highlightVisibility = active || hovered || pressed ? Visibility.Visible : Visibility.Collapsed;
        if (highlight.Visibility != highlightVisibility)
            highlight.Visibility = highlightVisibility;
        highlight.Opacity = 1;
        highlight.Background = pressed
            ? RoleBrush(AppColorRole.PickerPressed, Colors.Transparent)
            : active
                ? RoleBrush(AppColorRole.PickerSoftSelected, Colors.Transparent)
                : RoleBrush(AppColorRole.PickerHover, Colors.Transparent);
        highlight.BorderThickness = new Thickness(0);
        text.Foreground = active || hovered || pressed
            ? RoleBrush(AppColorRole.TextPrimary, Colors.White)
            : RoleBrush(AppColorRole.TextSecondary, Colors.LightGray);
        AutomationProperties.SetName(button, $"{PartName(part)} {value:00}");
    }

    private FrameworkElement CreateFooter()
    {
        var button = new Button
        {
            Content = Text("OK", "OK"),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinHeight = 30,
            Padding = new Thickness(12, 1, 12, 1),
            Margin = new Thickness(0, 4, 0, 0)
        };
        AppTypography.Apply(button);
        button.Background = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Foreground = RoleBrush(AppColorRole.PickerSelectedText, Colors.White);
        button.BorderBrush = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Resources["ButtonBackground"] = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Resources["ButtonBackgroundPointerOver"] = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Resources["ButtonBackgroundPressed"] = RoleBrush(AppColorRole.PickerPressed, Colors.DeepSkyBlue);
        button.Resources["ButtonBorderBrush"] = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Resources["ButtonBorderBrushPointerOver"] = RoleBrush(AppColorRole.PickerSelected, Colors.DeepSkyBlue);
        button.Resources["ButtonBorderBrushPressed"] = RoleBrush(AppColorRole.PickerPressed, Colors.DeepSkyBlue);
        button.Resources["ButtonForeground"] = RoleBrush(AppColorRole.PickerSelectedText, Colors.White);
        button.Resources["ButtonForegroundPointerOver"] = RoleBrush(AppColorRole.PickerSelectedText, Colors.White);
        button.Resources["ButtonForegroundPressed"] = RoleBrush(AppColorRole.PickerSelectedText, Colors.White);
        AutomationProperties.SetAutomationId(button, "TimePickerApplyButton");
        button.Click += (_, _) => ApplyDraft();
        return button;
    }

    private void ApplyDraft()
    {
        SetTime(_draftHour, _draftMinute, raiseChanged: true);
        _flyout.Hide();
    }

    private void SetTime(int hour, int minute, bool raiseChanged)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        var next = new TimeSpan(hour, minute, 0);
        var changed = next != _time;
        _time = next;
        RefreshButtonContent();

        if (changed && raiseChanged)
            TimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int? DigitFromKey(VirtualKey key)
    {
        var value = (int)key;
        if (value is >= 0x30 and <= 0x39)
            return value - 0x30;
        if (value is >= 0x60 and <= 0x69)
            return value - 0x60;
        return null;
    }

    private void ResetDigitBuffer()
    {
        _digitBuffer = string.Empty;
        _lastDigitInputAt = DateTime.MinValue;
    }

    private string PartName(TimePart part) =>
        part == TimePart.Hour ? Text("Hour", "Hour") : Text("Minute", "Minute");

    private static string FormatTime(TimeSpan time) =>
        $"{time.Hours:00}:{time.Minutes:00}";

    private static int Wrap(int value, int modulo) =>
        ((value % modulo) + modulo) % modulo;

    private string Text(string key, string fallback) =>
        _text is null ? fallback : _text[key];

    private Brush RoleBrush(AppColorRole role, Color fallback) =>
        AppMaterialLayer.Brush(this, role, fallback);

}

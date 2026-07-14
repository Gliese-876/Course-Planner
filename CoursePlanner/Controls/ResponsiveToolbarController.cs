using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CoursePlanner.Services;
using Windows.Foundation;

namespace CoursePlanner.Controls;

internal sealed class ResponsiveToolbarController
{
    private readonly FrameworkElement _host;
    private readonly Button _moreButton;
    private readonly Func<MenuFlyout> _createMenu;
    private readonly List<ToolbarCommand> _commands = new();
    private readonly List<ToolbarSeparator> _separators = new();
    private readonly List<ToolbarCommand> _hiddenCommands = new();
    private readonly Dictionary<FrameworkElement, double> _measuredWidths = new();
    private bool _layoutQueued;

    public ResponsiveToolbarController(FrameworkElement host, Button moreButton, Func<MenuFlyout> createMenu)
    {
        _host = host;
        _moreButton = moreButton;
        _createMenu = createMenu;
        _host.SizeChanged += (_, _) => QueueLayout();
        _moreButton.Click += MoreButton_Click;
    }

    public void AddCommand(
        Button button,
        TextBlock label,
        string textKey,
        Func<IconElement> iconFactory,
        RoutedEventHandler click,
        int group,
        int collapseOrder)
    {
        _commands.Add(new ToolbarCommand
        {
            Button = button,
            Label = label,
            TextKey = textKey,
            IconFactory = iconFactory,
            Click = click,
            Group = group,
            CollapseOrder = collapseOrder
        });
    }

    public void AddSeparator(FrameworkElement element, int group)
    {
        _separators.Add(new ToolbarSeparator { Element = element, Group = group });
    }

    public void ApplyText(Func<string, string> text)
    {
        foreach (var command in _commands)
        {
            command.Text = text(command.TextKey);
            command.Label.Text = command.Text;
            AutomationProperties.SetName(command.Button, command.Text);
            ToolTipService.SetToolTip(command.Button, command.Text);
        }

        AutomationProperties.SetName(_moreButton, text("More"));
        ToolTipService.SetToolTip(_moreButton, text("More"));
        _measuredWidths.Clear();
        QueueLayout();
    }

    public void QueueLayout()
    {
        if (_layoutQueued)
            return;

        _layoutQueued = true;
        if (_host.DispatcherQueue.TryEnqueue(() =>
        {
            _layoutQueued = false;
            UpdateLayout();
        }))
        {
            return;
        }

        _layoutQueued = false;
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        if (_commands.Count == 0)
            return;

        var availableWidth = _host.ActualWidth;
        if (availableWidth <= 0)
            return;

        var hidden = new HashSet<ToolbarCommand>();
        foreach (var command in _commands.OrderBy(command => command.CollapseOrder))
        {
            if (CalculateWidth(hidden) <= availableWidth)
                break;
            hidden.Add(command);
        }

        _hiddenCommands.Clear();
        _hiddenCommands.AddRange(_commands.Where(hidden.Contains));

        foreach (var command in _commands)
            command.Button.Visibility = hidden.Contains(command) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var separator in _separators)
        {
            separator.Element.Visibility = ShouldShowSeparator(separator.Group, hidden)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _moreButton.Visibility = _hiddenCommands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _moreButton.IsEnabled = _hiddenCommands.Count > 0;
    }

    private double CalculateWidth(IReadOnlySet<ToolbarCommand> hidden)
    {
        var width = 0d;

        foreach (var command in _commands)
        {
            if (!hidden.Contains(command))
                width += MeasureWidth(command.Button);
        }

        foreach (var separator in _separators)
        {
            if (ShouldShowSeparator(separator.Group, hidden))
                width += MeasureWidth(separator.Element);
        }

        if (hidden.Count > 0)
            width += MeasureWidth(_moreButton);

        return width;
    }

    private bool ShouldShowSeparator(int group, IReadOnlySet<ToolbarCommand> hidden)
    {
        var groupHasVisibleCommand = _commands.Any(command => command.Group == group && !hidden.Contains(command));
        if (!groupHasVisibleCommand)
            return false;

        return _commands.Any(command => command.Group > group && !hidden.Contains(command));
    }

    private double MeasureWidth(FrameworkElement element)
    {
        if (_measuredWidths.TryGetValue(element, out var width))
            return width;

        var originalVisibility = element.Visibility;
        try
        {
            element.Visibility = Visibility.Visible;
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Ceiling(Math.Max(1, element.DesiredSize.Width));
        }
        finally
        {
            element.Visibility = originalVisibility;
        }

        _measuredWidths[element] = width;
        return width;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hiddenCommands.Count == 0)
            return;

        var menu = _createMenu();
        var previousGroup = _hiddenCommands[0].Group;
        foreach (var command in _hiddenCommands)
        {
            if (menu.Items.Count > 0 && command.Group != previousGroup)
                menu.Items.Add(new MenuFlyoutSeparator());

            var icon = command.IconFactory();
            icon.RenderTransform = new TranslateTransform
            {
                Y = AppTypography.IconAlignmentOffset(AppTextRole.Body)
            };
            var item = new MenuFlyoutItem
            {
                Text = command.Text,
                Icon = icon,
                IsEnabled = command.Button.IsEnabled
            };
            item.Click += command.Click;
            menu.Items.Add(item);
            previousGroup = command.Group;
        }

        menu.ShowAt(_moreButton);
    }

    private sealed class ToolbarCommand
    {
        public Button Button { get; init; } = null!;
        public TextBlock Label { get; init; } = null!;
        public string TextKey { get; init; } = "";
        public string Text { get; set; } = "";
        public Func<IconElement> IconFactory { get; init; } = () => new SymbolIcon(Symbol.Document);
        public RoutedEventHandler Click { get; init; } = (_, _) => { };
        public int Group { get; init; }
        public int CollapseOrder { get; init; }
    }

    private sealed class ToolbarSeparator
    {
        public FrameworkElement Element { get; init; } = null!;
        public int Group { get; init; }
    }
}

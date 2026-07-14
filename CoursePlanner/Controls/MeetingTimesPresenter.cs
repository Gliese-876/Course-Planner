using CoursePlanner.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CoursePlanner.Controls;

public sealed class MeetingTimesPresenter : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<MeetingDisplayPart>),
            typeof(MeetingTimesPresenter),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private readonly StackPanel _panel = new() { Spacing = 8 };

    public MeetingTimesPresenter()
    {
        AppTypography.Apply(this);
        Content = _panel;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public IEnumerable<MeetingDisplayPart>? ItemsSource
    {
        get => (IEnumerable<MeetingDisplayPart>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is MeetingTimesPresenter presenter)
            presenter.Rebuild();
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        var items = ItemsSource?.ToList() ?? [];
        foreach (var item in items)
            _panel.Children.Add(CreateMeetingBlock(item));
    }

    private FrameworkElement CreateMeetingBlock(MeetingDisplayPart item)
    {
        var block = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 4)
        };
        AutomationProperties.SetName(block, item.AutomationText);

        block.Children.Add(new TextBlock
        {
            Text = item.Title,
            Style = AppTypography.TextStyle(AppTextRole.BodyStrong),
            Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextPrimary, Colors.Black)
        });

        foreach (var field in item.Fields)
            block.Children.Add(CreateFieldRow(field));

        return block;
    }

    private FrameworkElement CreateFieldRow(MeetingDisplayField field)
    {
        var row = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(72) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        row.Children.Add(new TextBlock
        {
            Text = field.Label,
            Style = AppTypography.TextStyle(AppTextRole.Body),
            Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextSecondary, Colors.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        });

        var value = new TextBlock
        {
            Text = field.Value,
            Style = AppTypography.TextStyle(AppTextRole.Body),
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppMaterialLayer.Brush(this, AppColorRole.TextPrimary, Colors.Black)
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

}

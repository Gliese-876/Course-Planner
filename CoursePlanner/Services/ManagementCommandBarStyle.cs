using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Services;

public static class ManagementCommandBarStyle
{
    private const double IconHeight = 18;
    private const double LabelFontSize = 14;

    public static void Apply(CommandBar commandBar)
    {
        commandBar.Resources["AppBarButtonContentHeight"] = IconHeight;
        commandBar.Loaded += ManagementCommandBar_Loaded;
    }

    private static void ManagementCommandBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CommandBar commandBar)
            return;

        ApplyToVisualTree(commandBar);
        commandBar.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyToVisualTree(commandBar));
    }

    private static void ApplyToVisualTree(DependencyObject root)
    {
        if (root is TextBlock { Name: "TextLabel" } label)
        {
            label.FontSize = LabelFontSize;
            label.FontFamily = AppTypography.FontFamilyFor(AppTextRole.Body);
            label.LineHeight = AppTypography.LineHeight(AppTextRole.Body, LabelFontSize);
            label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            label.TextLineBounds = TextLineBounds.Full;
        }

        if (root is Viewbox { Name: "ContentViewbox" } iconSlot)
            iconSlot.Height = IconHeight;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            ApplyToVisualTree(VisualTreeHelper.GetChild(root, index));
    }
}

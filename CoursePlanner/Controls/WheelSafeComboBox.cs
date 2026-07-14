using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CoursePlanner.Controls;

public sealed class WheelSafeComboBox : ComboBox
{
    private const double WheelStepDip = 48;

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsDropDownOpen)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        e.Handled = true;
        ForwardWheelToParentScrollViewer(delta);
    }

    private void ForwardWheelToParentScrollViewer(int delta)
    {
        if (delta == 0)
            return;

        var scrollViewer = FindParentScrollViewer(this);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.VerticalOffset - (delta / 120.0 * WheelStepDip),
            0,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(nextOffset - scrollViewer.VerticalOffset) > 0.1)
            scrollViewer.ChangeView(null, nextOffset, null, disableAnimation: true);
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject source)
    {
        for (var current = VisualTreeHelper.GetParent(source); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }
}

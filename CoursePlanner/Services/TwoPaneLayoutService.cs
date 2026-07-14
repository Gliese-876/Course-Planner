using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoursePlanner.Services;

public static class TwoPaneLayoutService
{
    public const double NavigationPaneWidth = 320;
    public const double CompactBreakpoint = 680;
    public const double ColumnSpacing = 20;
    public const double ScrollableContentSideInset = 20;
    public const double MaxScrollableContentWidth = 1080;
    public const double MinScrollableContentWidth = 320;

    public static double ResolveWidth(FrameworkElement page, double width)
    {
        if (width > 0)
            return width;
        if (page.ActualWidth > 0)
            return page.ActualWidth;
        return page.XamlRoot?.Size.Width ?? 0;
    }

    public static void Apply(
        FrameworkElement page,
        Grid rootGrid,
        UIElement navigationPane,
        double width,
        double paneWidth = NavigationPaneWidth,
        double columnSpacing = ColumnSpacing)
    {
        var responsiveWidth = ResolveWidth(page, width);
        var compact = responsiveWidth < CompactBreakpoint;

        rootGrid.ColumnDefinitions[0].Width = compact
            ? new GridLength(0)
            : new GridLength(paneWidth);
        rootGrid.ColumnSpacing = compact ? 0 : columnSpacing;
        navigationPane.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public static void SizeScrollableContent(
        ScrollViewer viewport,
        FrameworkElement content,
        double fallbackWidth,
        double maxWidth = MaxScrollableContentWidth) =>
        SizeScrollableContent(viewport, content, fallbackWidth, maxWidth, ScrollableContentSideInset);

    public static void SizeScrollableContent(
        ScrollViewer viewport,
        FrameworkElement centeringHost,
        FrameworkElement content,
        double fallbackWidth,
        double maxWidth = MaxScrollableContentWidth) =>
        SizeScrollableContent(viewport, centeringHost, content, fallbackWidth, maxWidth, ScrollableContentSideInset);

    public static void SizeScrollableContent(
        ScrollViewer viewport,
        FrameworkElement content,
        double fallbackWidth,
        double maxWidth,
        double sideInset)
    {
        var availableWidth = ResolveScrollableViewportWidth(viewport, fallbackWidth);
        var targetWidth = availableWidth - sideInset * 2;
        content.Width = Math.Max(
            MinScrollableContentWidth,
            Math.Min(maxWidth, Math.Max(0, targetWidth)));
    }

    public static void SizeScrollableContent(
        ScrollViewer viewport,
        FrameworkElement centeringHost,
        FrameworkElement content,
        double fallbackWidth,
        double maxWidth,
        double sideInset)
    {
        var availableWidth = ResolveScrollableViewportWidth(viewport, fallbackWidth);
        centeringHost.Width = availableWidth;

        var targetWidth = availableWidth - sideInset * 2;
        content.Width = Math.Max(
            MinScrollableContentWidth,
            Math.Min(maxWidth, Math.Max(0, targetWidth)));
    }

    private static double ResolveScrollableViewportWidth(ScrollViewer viewport, double fallbackWidth)
    {
        if (viewport.ViewportWidth > 0)
            return viewport.ViewportWidth;
        if (viewport.ActualWidth > 0)
            return viewport.ActualWidth;
        return Math.Max(0, fallbackWidth);
    }
}

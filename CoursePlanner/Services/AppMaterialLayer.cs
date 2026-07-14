using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;

namespace CoursePlanner.Services;

public enum AppMaterialSurface
{
    None,
    WindowBackdrop,
    Chrome,
    ShellTabRail,
    NavigationRail,
    Page,
    CommandBar,
    DockedPane,
    OverlayPane,
    Card,
    SemesterOverviewCard,
    Dialog,
    Divider,
    TimetableCanvas,
    TimetableHeader
}

public enum AppMaterialElevation
{
    None,
    Layer,
    Card,
    Flyout,
    Dialog
}

public enum AppColorRole
{
    TextPrimary,
    TextSecondary,
    InteractiveText,
    TitleBarHover,
    TitleBarPressed,
    ShellTabRest,
    ShellTabSelected,
    ShellTabHover,
    PickerEditor,
    PickerEditorStroke,
    PickerHover,
    PickerPressed,
    PickerSelected,
    PickerSelectedText,
    PickerSoftSelected,
    PickerMutedText,
    CalendarHeaderHover,
    CalendarHeaderPressed,
    CalendarDateHover,
    CalendarDatePressed,
    TimetableBackground,
    TimetableHeader,
    FlashBorder,
    StatusCritical,
    StatusCaution,
    StatusCurrent,
    CourseBlock,
    CourseBlockHover,
    CourseBlockAdded,
    CourseBlockRemoved,
    CourseBlockModified,
    CourseBlockAddedHover,
    CourseBlockRemovedHover,
    CourseBlockModifiedHover,
    SemesterOverviewCardHover,
    SemesterOverviewCardPressed
}

public static class AppMaterialLayer
{
    public static readonly DependencyProperty SurfaceProperty =
        DependencyProperty.RegisterAttached(
            "Surface",
            typeof(AppMaterialSurface),
            typeof(AppMaterialLayer),
            new PropertyMetadata(AppMaterialSurface.None, OnSurfaceChanged));

    private static readonly DependencyProperty ThemeHookedProperty =
        DependencyProperty.RegisterAttached(
            "ThemeHooked",
            typeof(bool),
            typeof(AppMaterialLayer),
            new PropertyMetadata(false));

    public static AppMaterialSurface GetSurface(DependencyObject element) =>
        (AppMaterialSurface)element.GetValue(SurfaceProperty);

    public static void SetSurface(DependencyObject element, AppMaterialSurface value) =>
        element.SetValue(SurfaceProperty, value);

    public static void ApplyTransientFlyout(Flyout flyout)
    {
        AppAnimationLayer.ConfigureFlyout(flyout);
        flyout.FlyoutPresenterStyle =
            (Style)Application.Current.Resources["AppTransientFlyoutPresenterStyle"];
    }

    public static MenuFlyout CreateTransientMenuFlyout()
    {
        var flyout = new MenuFlyout();
        AppAnimationLayer.ConfigureFlyout(flyout);
        return flyout;
    }

    public static Brush Brush(AppMaterialSurface surface, Color fallback)
    {
        var definition = Definition(surface);
        return Brush(definition.BackgroundBrushKey, fallback);
    }

    public static Brush Brush(FrameworkElement element, AppMaterialSurface surface, Color fallback)
    {
        var definition = Definition(surface);
        return Brush(element, definition.BackgroundBrushKey, fallback);
    }

    public static Brush Brush(string? resourceKey, Color fallback) =>
        string.IsNullOrWhiteSpace(resourceKey)
            ? new SolidColorBrush(fallback)
            : AppBrushes.Resource(resourceKey, fallback);

    public static Brush Brush(FrameworkElement element, string? resourceKey, Color fallback) =>
        string.IsNullOrWhiteSpace(resourceKey)
            ? new SolidColorBrush(fallback)
            : AppBrushes.Resource(element, resourceKey, fallback);

    public static Brush Brush(AppColorRole role, Color fallback) =>
        AppBrushes.Resource(ResourceKey(role), fallback);

    public static Brush Brush(FrameworkElement element, AppColorRole role, Color fallback) =>
        AppBrushes.Resource(element, ResourceKey(role), fallback);

    public static Color Color(AppColorRole role, Color fallback) =>
        AppBrushes.ColorResource(ResourceKey(role), fallback);

    public static Color Color(AppColorRole role, ResolvedThemeMode resolvedTheme, Color fallback) =>
        AppBrushes.ColorResource(ResourceKey(role), resolvedTheme, fallback);

    public static Color Color(AppMaterialSurface surface, Color fallback)
    {
        var key = Definition(surface).BackgroundBrushKey;
        return string.IsNullOrWhiteSpace(key)
            ? fallback
            : AppBrushes.ColorResource(key, fallback);
    }

    public static Color Color(AppMaterialSurface surface, ResolvedThemeMode resolvedTheme, Color fallback)
    {
        var key = Definition(surface).BackgroundBrushKey;
        return string.IsNullOrWhiteSpace(key)
            ? fallback
            : AppBrushes.ColorResource(key, resolvedTheme, fallback);
    }

    public static void ApplySurface(FrameworkElement element, AppMaterialSurface surface)
    {
        if (surface is AppMaterialSurface.None or AppMaterialSurface.Dialog)
            return;

        if (GetSurface(element) != surface)
        {
            element.SetValue(SurfaceProperty, surface);
            return;
        }

        EnsureThemeHook(element);

        var definition = Definition(surface);
        var background = Brush(element, definition.BackgroundBrushKey, Colors.Transparent);
        var border = Brush(element, definition.BorderBrushKey, Colors.Transparent);

        switch (element)
        {
            case Border borderElement:
                borderElement.Background = background;
                borderElement.BorderBrush = border;
                borderElement.BorderThickness = definition.BorderThickness;
                borderElement.CornerRadius = definition.CornerRadius;
                break;

            case Control control:
                control.Background = background;
                control.BorderBrush = border;
                control.BorderThickness = definition.BorderThickness;
                control.CornerRadius = definition.CornerRadius;
                break;

            case Panel panel:
                panel.Background = background;
                break;
        }

        SetElevation(element, definition.Elevation);
    }

    public static void SetElevation(FrameworkElement element, AppMaterialElevation elevation)
    {
        var usesThemeShadow = elevation is AppMaterialElevation.Flyout or AppMaterialElevation.Dialog;
        element.Shadow = usesThemeShadow ? new ThemeShadow() : null;

        // Writing UIElement.Translation activates a separate XAML translation
        // channel for the whole subtree. On a docked pane that channel can
        // corrupt descendant hit-test and UIA bounds even when only Z is set.
        // Flat layer/card surfaces have no ThemeShadow to project, so a Z
        // translation has no visual benefit and must not be enabled.
        if (!usesThemeShadow)
        {
            if (element.Translation.Z != 0)
            {
                element.Translation = new Vector3(
                    element.Translation.X,
                    element.Translation.Y,
                    0);
            }

            return;
        }

        var z = elevation switch
        {
            AppMaterialElevation.Flyout => 32,
            AppMaterialElevation.Dialog => 128,
            _ => 0
        };

        element.Translation = new Vector3(element.Translation.X, element.Translation.Y, z);
    }

    public static void RefreshTree(DependencyObject? root)
    {
        if (root is null)
            return;

        if (root is FrameworkElement element)
        {
            var surface = GetSurface(element);
            if (surface != AppMaterialSurface.None)
                ApplySurface(element, surface);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
            RefreshTree(VisualTreeHelper.GetChild(root, index));
    }

    private static void OnSurfaceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is FrameworkElement element && args.NewValue is AppMaterialSurface surface)
            ApplySurface(element, surface);
    }

    private static void EnsureThemeHook(FrameworkElement element)
    {
        if ((bool)element.GetValue(ThemeHookedProperty))
            return;

        element.SetValue(ThemeHookedProperty, true);
        element.Loaded += OnSurfaceLoaded;
        element.ActualThemeChanged += OnActualThemeChanged;
    }

    private static void OnSurfaceLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
            ApplySurface(element, GetSurface(element));
    }

    private static void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ApplySurface(sender, GetSurface(sender));

    private static MaterialSurfaceDefinition Definition(AppMaterialSurface surface) =>
        surface switch
        {
            AppMaterialSurface.WindowBackdrop => new("AppMaterialWindowBackdropBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.Chrome => new("AppMaterialChromeBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.ShellTabRail => new("AppShellTabRestBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.NavigationRail => new("AppMaterialNavigationRailBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.Page => new("AppMaterialPageBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.CommandBar => new("AppMaterialCommandBarBrush", "AppMaterialDividerBrush", new Thickness(0, 0, 0, 1), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.DockedPane => new("AppMaterialDockedPaneBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.OverlayPane => new("AppMaterialOverlayPaneBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.Layer),
            AppMaterialSurface.Card => new("AppMaterialCardBrush", null, new Thickness(0), new CornerRadius(6), AppMaterialElevation.None),
            AppMaterialSurface.SemesterOverviewCard => new("AppSemesterOverviewCardBrush", null, new Thickness(0), new CornerRadius(6), AppMaterialElevation.None),
            AppMaterialSurface.Dialog => new(null, null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.Divider => new("AppMaterialDividerBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.TimetableCanvas => new("AppTimetableBackgroundBrush", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None),
            AppMaterialSurface.TimetableHeader => new("AppTimetableHeaderBrush", "AppMaterialDividerBrush", new Thickness(0, 0, 1, 1), new CornerRadius(0), AppMaterialElevation.None),
            _ => new(null, null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.None)
        };

    private static string ResourceKey(AppColorRole role) =>
        role switch
        {
            AppColorRole.TextPrimary => "AppTextPrimaryBrush",
            AppColorRole.TextSecondary => "AppTextSecondaryBrush",
            AppColorRole.InteractiveText => "AppInteractiveTextBrush",
            AppColorRole.TitleBarHover => "AppTitleBarHoverBrush",
            AppColorRole.TitleBarPressed => "AppTitleBarPressedBrush",
            AppColorRole.ShellTabRest => "AppShellTabRestBrush",
            AppColorRole.ShellTabSelected => "AppShellTabSelectedBrush",
            AppColorRole.ShellTabHover => "AppShellTabHoverBrush",
            AppColorRole.PickerEditor => "AppPickerEditorBrush",
            AppColorRole.PickerEditorStroke => "AppPickerEditorStrokeBrush",
            AppColorRole.PickerHover => "AppPickerHoverBrush",
            AppColorRole.PickerPressed => "AppPickerPressedBrush",
            AppColorRole.PickerSelected => "AppPickerSelectedBrush",
            AppColorRole.PickerSelectedText => "AppPickerSelectedTextBrush",
            AppColorRole.PickerSoftSelected => "AppPickerSoftSelectedBrush",
            AppColorRole.PickerMutedText => "AppPickerMutedTextBrush",
            AppColorRole.CalendarHeaderHover => "AppCalendarHeaderHoverBrush",
            AppColorRole.CalendarHeaderPressed => "AppCalendarHeaderPressedBrush",
            AppColorRole.CalendarDateHover => "AppCalendarDateHoverBrush",
            AppColorRole.CalendarDatePressed => "AppCalendarDatePressedBrush",
            AppColorRole.TimetableBackground => "AppTimetableBackgroundBrush",
            AppColorRole.TimetableHeader => "AppTimetableHeaderBrush",
            AppColorRole.FlashBorder => "AppFlashBorderBrush",
            AppColorRole.StatusCritical => "AppStatusCriticalBrush",
            AppColorRole.StatusCaution => "AppStatusCautionBrush",
            AppColorRole.StatusCurrent => "AppStatusCurrentBrush",
            AppColorRole.CourseBlock => "AppCourseBlockBrush",
            AppColorRole.CourseBlockHover => "AppCourseBlockHoverBrush",
            AppColorRole.CourseBlockAdded => "AppCourseBlockAddedBrush",
            AppColorRole.CourseBlockRemoved => "AppCourseBlockRemovedBrush",
            AppColorRole.CourseBlockModified => "AppCourseBlockModifiedBrush",
            AppColorRole.CourseBlockAddedHover => "AppCourseBlockAddedHoverBrush",
            AppColorRole.CourseBlockRemovedHover => "AppCourseBlockRemovedHoverBrush",
            AppColorRole.CourseBlockModifiedHover => "AppCourseBlockModifiedHoverBrush",
            AppColorRole.SemesterOverviewCardHover => "AppSemesterOverviewCardHoverBrush",
            AppColorRole.SemesterOverviewCardPressed => "AppSemesterOverviewCardPressedBrush",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };

    private sealed record MaterialSurfaceDefinition(
        string? BackgroundBrushKey,
        string? BorderBrushKey,
        Thickness BorderThickness,
        CornerRadius CornerRadius,
        AppMaterialElevation Elevation);
}

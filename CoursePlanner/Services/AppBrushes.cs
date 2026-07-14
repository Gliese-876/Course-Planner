using CoursePlanner.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CoursePlanner.Services;

public static class AppBrushes
{
    private static readonly AccessibilitySettings Accessibility = new();
    private static ResolvedThemeMode _resolvedTheme = ResolvedThemeMode.Light;

    public static void UseResolvedTheme(ResolvedThemeMode resolvedTheme) =>
        _resolvedTheme = resolvedTheme;

    public static bool IsHighContrast => Accessibility.HighContrast;

    public static Brush Resource(string key) => Resource(key, Colors.Transparent);

    public static Brush Resource(string key, Color fallback)
    {
        if (TryGetResource(key, (FrameworkElement?)null, out var resource) && resource is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    public static Brush Resource(FrameworkElement element, string key, Color fallback)
    {
        if (TryGetResource(key, element, out var resource) && resource is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    public static Color ColorResource(string key, Color fallback)
    {
        if (TryGetResource(key, (FrameworkElement?)null, out var resource) && resource is SolidColorBrush brush)
            return brush.Color;
        return fallback;
    }

    public static Color ColorResource(string key, ResolvedThemeMode resolvedTheme, Color fallback)
    {
        if (TryGetResource(key, (FrameworkElement?)null, resolvedTheme, out var resource) && resource is SolidColorBrush brush)
            return brush.Color;
        return fallback;
    }

    public static Brush Transparent() => new SolidColorBrush(Colors.Transparent);

    public static Brush FromHex(string hex)
    {
        var (r, g, b) = CourseColorService.ParseRgb(hex);
        return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)r, (byte)g, (byte)b));
    }

    private static bool TryGetResource(string key, FrameworkElement? element, out object? resource)
    {
        var themeKey = ResolveThemeKey(element);

        return TryGetResource(key, themeKey, out resource);
    }

    private static bool TryGetResource(
        string key,
        FrameworkElement? element,
        ResolvedThemeMode resolvedTheme,
        out object? resource)
    {
        var themeKey = resolvedTheme == ResolvedThemeMode.Dark ? "Dark" : "Light";
        return TryGetResource(key, themeKey, out resource);
    }

    private static bool TryGetResource(string key, string themeKey, out object? resource)
    {

        var visited = new HashSet<ResourceDictionary>();
        if (TryGetThemeResource(Application.Current.Resources, themeKey, key, visited, out resource))
            return true;

        visited.Clear();
        return TryGetDictionaryResource(Application.Current.Resources, key, visited, out resource);
    }

    private static string ResolveThemeKey(FrameworkElement? _)
    {
        if (IsHighContrast)
            return "HighContrast";

        // Code-created controls often resolve their brushes before they are
        // attached to the visual tree. Their ActualTheme still reflects the
        // system theme at that point, so the app-wide resolved theme is the
        // authoritative source for all programmatic brush lookups.
        return _resolvedTheme == ResolvedThemeMode.Dark ? "Dark" : "Light";
    }

    private static bool TryGetThemeResource(
        ResourceDictionary dictionary,
        string themeKey,
        string key,
        ISet<ResourceDictionary> visited,
        out object? resource)
    {
        if (!visited.Add(dictionary))
        {
            resource = null;
            return false;
        }

        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themeDictionary) &&
            themeDictionary is ResourceDictionary themedResources &&
            TryGetDictionaryResource(themedResources, key, new HashSet<ResourceDictionary>(), out resource))
        {
            return true;
        }

        for (var index = dictionary.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var mergedDictionary = dictionary.MergedDictionaries[index];
            if (TryGetThemeResource(mergedDictionary, themeKey, key, visited, out resource))
                return true;
        }

        resource = null;
        return false;
    }

    private static bool TryGetDictionaryResource(
        ResourceDictionary dictionary,
        string key,
        ISet<ResourceDictionary> visited,
        out object? resource)
    {
        if (!visited.Add(dictionary))
        {
            resource = null;
            return false;
        }

        if (dictionary.TryGetValue(key, out resource))
            return true;

        for (var index = dictionary.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var mergedDictionary = dictionary.MergedDictionaries[index];
            if (TryGetDictionaryResource(mergedDictionary, key, visited, out resource))
                return true;
        }

        resource = null;
        return false;
    }
}

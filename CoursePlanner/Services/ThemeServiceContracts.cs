using CoursePlanner.Core;

namespace CoursePlanner.Services;

public enum ResolvedThemeMode
{
    Light,
    Dark
}

public sealed class ThemeChangedEventArgs : EventArgs
{
    public ThemeChangedEventArgs(
        ThemeMode requestedTheme,
        ResolvedThemeMode resolvedTheme,
        bool isHighContrast)
    {
        RequestedTheme = requestedTheme;
        ResolvedTheme = resolvedTheme;
        IsHighContrast = isHighContrast;
    }

    public ThemeMode RequestedTheme { get; }
    public ResolvedThemeMode ResolvedTheme { get; }
    public bool IsHighContrast { get; }
}

public interface IThemeService
{
    ThemeMode RequestedTheme { get; }
    ResolvedThemeMode ResolvedTheme { get; }
    bool IsHighContrast { get; }
    ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme);
    event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
    void ApplyTheme(ThemeMode theme);
    void RefreshTheme();
}

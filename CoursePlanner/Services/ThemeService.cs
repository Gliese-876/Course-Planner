using CoursePlanner.Core;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace CoursePlanner.Services;

public sealed class ThemeService : IThemeService
{
    private static readonly UISettings SystemUiSettings = new();
    private static readonly AccessibilitySettings SystemAccessibilitySettings = new();
    private readonly DocumentSession _session;
    private readonly Func<Window?> _windowAccessor;
    private FrameworkElement? _themeRoot;
    private ThemeMode? _lastNotifiedRequestedTheme;
    private ResolvedThemeMode? _lastNotifiedResolvedTheme;
    private bool? _lastNotifiedHighContrast;

    public ThemeService(DocumentSession session, Func<Window?> windowAccessor)
    {
        _session = session;
        _windowAccessor = windowAccessor;
    }

    public ThemeMode RequestedTheme => _session.Document.Settings.Theme;

    public ResolvedThemeMode ResolvedTheme => ResolveTheme(RequestedTheme);

    public bool IsHighContrast => SystemAccessibilitySettings.HighContrast;

    public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) => requestedTheme switch
    {
        ThemeMode.Light => ResolvedThemeMode.Light,
        ThemeMode.Dark => ResolvedThemeMode.Dark,
        _ => ResolveSystemTheme()
    };

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public void AttachWindow(Window window)
    {
        if (window.Content is FrameworkElement root)
            AttachRoot(root);
        ApplyThemeToWindow();
        NotifyThemeChanged(force: true);
    }

    public void ApplyTheme(ThemeMode theme)
    {
        if (_session.Document.Settings.Theme == theme)
        {
            ApplyThemeToWindow();
            NotifyThemeChanged(force: false);
            return;
        }

        _session.EnsureMutationAllowed();
        _session.Document.Settings.Theme = theme;
        _session.Save("settings.theme", notify: false);
        ApplyThemeToWindow();
        NotifyThemeChanged(force: true);
    }

    public void RefreshTheme()
    {
        ApplyThemeToWindow();
        NotifyThemeChanged(force: true);
    }

    public void ApplyThemeToWindow()
    {
        var root = ResolveRoot();
        if (root is null)
            return;

        // Update the code-created brush resolver before XAML propagates
        // ActualThemeChanged through the visual tree. Child controls can then
        // rebuild against the requested theme instead of briefly reusing the
        // previous theme's brush objects.
        AppBrushes.UseResolvedTheme(ResolvedTheme);
        root.RequestedTheme = RequestedTheme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private FrameworkElement? ResolveRoot()
    {
        if (_themeRoot is not null)
            return _themeRoot;

        var window = _windowAccessor();
        if (window?.Content is FrameworkElement root)
            AttachRoot(root);
        return _themeRoot;
    }

    private void AttachRoot(FrameworkElement root)
    {
        if (ReferenceEquals(_themeRoot, root))
            return;

        if (_themeRoot is not null)
            _themeRoot.ActualThemeChanged -= Root_ActualThemeChanged;

        _themeRoot = root;
        _themeRoot.ActualThemeChanged += Root_ActualThemeChanged;
        NotifyThemeChanged(force: true);
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) =>
        NotifyThemeChanged(force: false);

    private static ResolvedThemeMode ResolveSystemTheme()
    {
        var background = SystemUiSettings.GetColorValue(UIColorType.Background);
        var luminance = (0.2126 * background.R) + (0.7152 * background.G) + (0.0722 * background.B);
        return luminance < 128 ? ResolvedThemeMode.Dark : ResolvedThemeMode.Light;
    }

    private void NotifyThemeChanged(bool force)
    {
        var requested = RequestedTheme;
        var resolved = ResolveTheme(requested);
        var isHighContrast = IsHighContrast;
        if (!force &&
            _lastNotifiedRequestedTheme == requested &&
            _lastNotifiedResolvedTheme == resolved &&
            _lastNotifiedHighContrast == isHighContrast)
        {
            return;
        }

        _lastNotifiedRequestedTheme = requested;
        _lastNotifiedResolvedTheme = resolved;
        _lastNotifiedHighContrast = isHighContrast;
        AppBrushes.UseResolvedTheme(resolved);
        AppMaterialLayer.RefreshTree(_themeRoot);
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(requested, resolved, isHighContrast));
    }
}

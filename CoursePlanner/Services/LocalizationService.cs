using CoursePlanner.Core;
using System.Globalization;
#if WINDOWS
using Windows.Globalization;
#endif

namespace CoursePlanner.Services;

public sealed class LanguageChangedEventArgs : EventArgs
{
    public LanguageChangedEventArgs(LanguageMode requestedLanguage, AppLocalizer localizer)
    {
        RequestedLanguage = requestedLanguage;
        Localizer = localizer;
    }

    public LanguageMode RequestedLanguage { get; }
    public AppLocalizer Localizer { get; }
    public LanguageMode ResolvedLanguage => Localizer.ResolvedLanguage;
}

public sealed class LocalizationService
{
    private readonly DocumentSession _session;
    private readonly CultureInfo _followSystemCulture;

    public LocalizationService(DocumentSession session, CultureInfo? followSystemCulture = null)
    {
        _session = session;
        _followSystemCulture = followSystemCulture ?? CultureInfo.CurrentUICulture;
        Localizer = new AppLocalizer(session.Document.Settings.Language, _followSystemCulture);
        ApplyRuntimeLanguage(Localizer, session.Document.Settings.Language);
    }

    public AppLocalizer Localizer { get; private set; }

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    public void ApplyLanguage(LanguageMode language)
    {
        if (_session.Document.Settings.Language == language)
        {
            ApplyRuntimeLanguage(Localizer, language);
            NotifyLanguageChanged();
            return;
        }

        var localizer = new AppLocalizer(language, _followSystemCulture);
        _session.EnsureMutationAllowed();
        _session.Document.Settings.Language = language;
        _session.Save("settings.language", notify: false);
        Localizer = localizer;
        ApplyRuntimeLanguage(Localizer, language);
        NotifyLanguageChanged();
    }

    public void RefreshLanguage()
    {
        Localizer = new AppLocalizer(_session.Document.Settings.Language, _followSystemCulture);
        ApplyRuntimeLanguage(Localizer, _session.Document.Settings.Language);
        NotifyLanguageChanged();
    }

    private static void ApplyRuntimeLanguage(AppLocalizer localizer, LanguageMode requestedLanguage)
    {
        var culture = localizer.Culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
#if WINDOWS
        ApplicationLanguages.PrimaryLanguageOverride = requestedLanguage == LanguageMode.FollowSystem
            ? ""
            : localizer.PlatformLanguageTag;
#endif
    }

    private void NotifyLanguageChanged() =>
        LanguageChanged?.Invoke(this, new LanguageChangedEventArgs(_session.Document.Settings.Language, Localizer));
}

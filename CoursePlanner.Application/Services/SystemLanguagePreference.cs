using System.Globalization;

namespace CoursePlanner.Services;

public static class SystemLanguagePreference
{
    public static CultureInfo Resolve(
        IEnumerable<string>? preferredLanguageTags,
        CultureInfo fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        foreach (var tag in preferredLanguageTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            try
            {
                return CultureInfo.GetCultureInfo(tag);
            }
            catch (CultureNotFoundException)
            {
                // A malformed or newly introduced platform tag must not make
                // application startup fail. Try the next user preference.
            }
        }

        return fallback;
    }
}

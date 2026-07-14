using System.Globalization;
using System.Xml.Linq;
using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed class AppLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> KnownLabelKeys =
        PlannerLabels.BuiltIn
            .ToDictionary(label => label.Name, label => label.LocalizationKey, StringComparer.Ordinal)
            .Append(new KeyValuePair<string, string>(PlannerLabels.Uncategorized, "Uncategorized"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, string> _strings;

    public AppLocalizer(LanguageMode mode)
        : this(mode, CultureInfo.CurrentUICulture)
    {
    }

    public AppLocalizer(LanguageMode mode, CultureInfo followSystemCulture)
    {
        ArgumentNullException.ThrowIfNull(followSystemCulture);
        ResolvedLanguage = Resolve(mode, followSystemCulture);
        var resourceName = $"CoursePlanner.Localization.{ResourceLanguageTag}.Resources.resw";
        using var stream = typeof(AppLocalizer).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new FileNotFoundException("Localization catalog embedded resource was not found.", resourceName);
        var document = XDocument.Load(stream);
        _strings = document.Root?
                       .Elements("data")
                       .Select(element => new
                       {
                           Name = (string?)element.Attribute("name"),
                           Value = (string?)element.Element("value")
                       })
                       .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                       .ToDictionary(entry => entry.Name!, entry => entry.Value ?? "", StringComparer.Ordinal)
                   ?? throw new InvalidDataException($"Localization catalog is empty: {resourceName}");
    }

    public LanguageMode ResolvedLanguage { get; }

    public string ResourceLanguageTag => ResolvedLanguage switch
    {
        LanguageMode.SimplifiedChinese => "zh-Hans",
        LanguageMode.English => "en-US",
        _ => "en-US"
    };

    public string PlatformLanguageTag => ResolvedLanguage switch
    {
        LanguageMode.SimplifiedChinese => "zh-CN",
        LanguageMode.English => "en-US",
        _ => "en-US"
    };

    public CultureInfo Culture => CultureInfo.GetCultureInfo(PlatformLanguageTag);

    public string this[string key] =>
        _strings.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"Missing localized string: {key}");

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    public string ValidationSummary(IEnumerable<ValidationIssue> issues) =>
        string.Join(" ", issues.Select(ValidationMessage));

    public string ValidationMessage(ValidationIssue issue)
    {
        var key = issue.Code.Contains('.', StringComparison.Ordinal)
            ? issue.Code
            : $"Validation.{issue.Code}";
        return string.Format(CultureInfo.CurrentCulture, this[key], issue.Parameters.Cast<object>().ToArray());
    }

    public string LocalizeKnownLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return KnownLabelKeys.TryGetValue(trimmed, out var key) ? this[key] : trimmed;
    }

    public string CanonicalizeKnownLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        foreach (var (canonical, key) in KnownLabelKeys)
        {
            if (string.Equals(trimmed, canonical, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, this[key], StringComparison.OrdinalIgnoreCase))
                return key == "Uncategorized" ? "" : canonical;
        }

        return trimmed;
    }

    private static LanguageMode Resolve(LanguageMode mode, CultureInfo culture)
    {
        if (mode != LanguageMode.FollowSystem)
            return mode;

        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? LanguageMode.SimplifiedChinese
            : LanguageMode.English;
    }

}

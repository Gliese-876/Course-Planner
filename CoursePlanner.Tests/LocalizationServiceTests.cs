using System.Globalization;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(CultureSensitiveTestCollection.Name)]
public sealed class LocalizationServiceTests
{
    [Fact]
    public void ReturningToFollowSystemDoesNotFollowThePreviouslySelectedAppLanguage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"course-planner-localization-{Guid.NewGuid():N}");
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        try
        {
            var session = new DocumentSession(new SqliteAppRepository(directory));
            var localization = new LocalizationService(
                session,
                CultureInfo.GetCultureInfo("zh-CN"));

            Assert.Equal(LanguageMode.SimplifiedChinese, localization.Localizer.ResolvedLanguage);

            localization.ApplyLanguage(LanguageMode.English);
            Assert.Equal(LanguageMode.English, localization.Localizer.ResolvedLanguage);
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);

            localization.ApplyLanguage(LanguageMode.FollowSystem);

            Assert.Equal(LanguageMode.SimplifiedChinese, localization.Localizer.ResolvedLanguage);
            Assert.Equal("zh-CN", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(LanguageMode.FollowSystem, session.Document.Settings.Language);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultUiCulture;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("zh-CN", LanguageMode.SimplifiedChinese)]
    [InlineData("zh-TW", LanguageMode.SimplifiedChinese)]
    [InlineData("en-US", LanguageMode.English)]
    [InlineData("fr-FR", LanguageMode.English)]
    public void FollowSystemResolutionCanUseAnUnpollutedSystemCulture(
        string cultureName,
        LanguageMode expected)
    {
        var localizer = new AppLocalizer(
            LanguageMode.FollowSystem,
            CultureInfo.GetCultureInfo(cultureName));

        Assert.Equal(expected, localizer.ResolvedLanguage);
    }

    [Fact]
    public void SystemLanguagePreferenceUsesTheUserProfileInsteadOfAnAppRuntimeOverride()
    {
        var resolved = SystemLanguagePreference.Resolve(
            ["zh-CN", "en-US"],
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("zh-CN", resolved.Name);
    }

    [Fact]
    public void SystemLanguagePreferenceSkipsInvalidTagsAndHasASafeFallback()
    {
        var fallback = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal(
            "en-US",
            SystemLanguagePreference.Resolve(["not_a_language_tag", "en-US"], fallback).Name);
        Assert.Same(fallback, SystemLanguagePreference.Resolve([], fallback));
        Assert.Same(fallback, SystemLanguagePreference.Resolve(null, fallback));
    }
}

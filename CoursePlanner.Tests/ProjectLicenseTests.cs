using System.Xml.Linq;

namespace CoursePlanner.Tests;

public sealed class ProjectLicenseTests
{
    [Fact]
    public void RepositoryDeclaresStandardMitLicense()
    {
        var license = File.ReadAllText(ProjectFilePath("LICENSE"));

        Assert.StartsWith("MIT License", license);
        Assert.Contains("Copyright (c) 2026 Gliese-876", license);
        Assert.Contains("Permission is hereby granted, free of charge", license);
        Assert.Contains("The above copyright notice and this permission notice shall be included", license);
        Assert.Contains("THE SOFTWARE IS PROVIDED \"AS IS\"", license);
    }

    [Fact]
    public void AppPackagesLicenseAndPresentsItInsideSoftwareInformation()
    {
        var project = File.ReadAllText(ProjectFilePath("CoursePlanner", "CoursePlanner.csproj"));
        var manifest = File.ReadAllText(ProjectFilePath("CoursePlanner", "Package.appxmanifest"));
        var mainWindow = File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "SettingsPage.xaml"));
        var settingsCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "SettingsPage.xaml.cs"));

        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", project);
        Assert.Contains("<Content Include=\"..\\LICENSE\" Link=\"LICENSE\" />", project);
        Assert.DoesNotContain("<PRIResource Include=", project);
        Assert.Contains("<DisplayName>ms-resource:AppTitle</DisplayName>", manifest);
        Assert.Contains("DisplayName=\"ms-resource:AppTitle\"", manifest);
        Assert.Contains("Description=\"ms-resource:AppDescription\"", manifest);
        Assert.DoesNotContain("Title=\"Course Planner\"", mainWindow);
        Assert.Contains("x:Name=\"SoftwareInformationTitle\"", settingsXaml);
        Assert.Contains("x:Name=\"AppInformationCard\"", settingsXaml);
        Assert.Contains("x:Name=\"PublisherCard\"", settingsXaml);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenPublisherProfileButton\"", settingsXaml);
        Assert.DoesNotContain("<HyperlinkButton", settingsXaml);
        Assert.Contains("Click=\"OpenPublisherProfile_Click\"", settingsXaml);
        Assert.Contains("Launcher.LaunchUriAsync(new Uri(\"https://github.com/Gliese-876\"))", settingsCode);
        Assert.DoesNotContain("x:Name=\"AppPublisherText\"", settingsXaml);
        Assert.DoesNotContain("x:Name=\"LicensesTitle\"", settingsXaml);
        Assert.True(
            settingsXaml.IndexOf("x:Name=\"AppInformationCard\"", StringComparison.Ordinal) <
            settingsXaml.IndexOf("x:Name=\"PublisherCard\"", StringComparison.Ordinal));
        Assert.True(
            settingsXaml.IndexOf("x:Name=\"PublisherCard\"", StringComparison.Ordinal) <
            settingsXaml.IndexOf("x:Name=\"ProjectLicenseCard\"", StringComparison.Ordinal));
        Assert.Contains("AutomationProperties.AutomationId=\"ViewProjectLicenseButton\"", settingsXaml);
        Assert.Contains("x:Name=\"LicensesCard\"", settingsXaml);
        Assert.Contains("Package.Current.Id.Version", settingsCode);
        Assert.Contains("Package.Current.PublisherDisplayName", settingsCode);
        Assert.Contains("t[\"SoftwareInformation\"]", settingsCode);
        Assert.Contains("t[\"AppVersionFormat\"]", settingsCode);
        Assert.Contains("t[\"Publisher\"]", settingsCode);
        Assert.Contains("t[\"OpenGitHubProfile\"]", settingsCode);
        Assert.DoesNotContain("t[\"AppPublisherFormat\"]", settingsCode);
        Assert.Contains("ms-appx:///LICENSE", settingsCode);
        Assert.Contains("t[\"OpenSourceLicenseDescriptionFormat\"]", settingsCode);
        Assert.Contains("t[\"AppTitle\"]", settingsCode);
        Assert.DoesNotContain("MitLicenseOriginalTextNotice", settingsCode);
        Assert.Contains("t[\"ThirdPartyLicenses\"]", settingsCode);
    }

    [Theory]
    [InlineData("en-US", "App information", "Version {0}", "Publisher", "Open GitHub profile")]
    [InlineData("zh-Hans", "软件信息", "版本 {0}", "发布者", "打开 GitHub 主页")]
    public void SoftwareInformationLabelsAreLocalized(
        string language,
        string sectionTitle,
        string versionFormat,
        string publisher,
        string openGitHubProfile)
    {
        var resources = XDocument.Load(ProjectFilePath("CoursePlanner.Application", "Resources", language, "Resources.resw"));
        var values = resources.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value);

        Assert.Equal(sectionTitle, values["SoftwareInformation"]);
        Assert.Equal(versionFormat, values["AppVersionFormat"]);
        Assert.Equal(publisher, values["Publisher"]);
        Assert.Equal(openGitHubProfile, values["OpenGitHubProfile"]);
        Assert.DoesNotContain("AppPublisherFormat", values.Keys);
    }

    [Theory]
    [InlineData("en-US", "Course Planner", "MIT License", "{0} is open source under the MIT License", "MIT License")]
    [InlineData("zh-Hans", "选课助手", "MIT 许可证", "{0} 采用 MIT 许可证开源", "MIT 许可证")]
    public void ApplicationNameAndProjectLicenseStatementAreLocalized(
        string language,
        string appTitle,
        string licenseTitle,
        string descriptionFormat,
        string dialogTitle)
    {
        var resources = XDocument.Load(ProjectFilePath("CoursePlanner.Application", "Resources", language, "Resources.resw"));
        var values = resources.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value);

        Assert.Equal(appTitle, values["AppTitle"]);
        Assert.Equal(licenseTitle, values["OpenSourceLicense"]);
        Assert.Equal(descriptionFormat, values["OpenSourceLicenseDescriptionFormat"]);
        Assert.Equal(dialogTitle, values["MitLicenseTitle"]);

        var manifestResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Strings", language, "Resources.resw"));
        var manifestValues = manifestResources.Root!
            .Elements("data")
            .ToDictionary(
                element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value);
        Assert.Equal(appTitle, manifestValues["AppTitle"]);
        Assert.Equal(values["AppDescription"], manifestValues["AppDescription"]);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

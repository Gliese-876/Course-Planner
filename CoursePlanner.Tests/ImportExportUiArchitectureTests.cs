using System.Xml.Linq;

namespace CoursePlanner.Tests;

public sealed class ImportExportUiArchitectureTests
{
    [Fact]
    public void PlannerPageDelegatesTheWholeWorkflowToTheCoordinator()
    {
        var page = Read("CoursePlanner", "Pages", "PlannerPage.xaml.cs");

        Assert.Contains("_services!.ImportExport.ImportAsync(this, ShowStatus)", page);
        Assert.Contains("_services!.ImportExport.ExportAsync(this, ShowStatus)", page);
        Assert.DoesNotContain("ImportPlanTextBestEffort", page);
        Assert.DoesNotContain("TimetableExportService", page);
        Assert.DoesNotContain("new FileOpenPicker", page);
        Assert.DoesNotContain("new FileSavePicker", page);
    }

    [Fact]
    public void CoordinatorUsesTheExistingTypographyAndMaterialLayers()
    {
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");
        var requestFactory = Read("CoursePlanner", "Services", "TimetableExportRequestFactory.cs");
        var typography = Read("CoursePlanner", "Services", "AppTypography.cs");
        var plannerXaml = Read("CoursePlanner", "Pages", "PlannerPage.xaml");
        var responsiveToolbar = Read("CoursePlanner", "Controls", "ResponsiveToolbarController.cs");

        Assert.Contains("AppTypography.TextStyle", coordinator);
        Assert.Contains("AppTypography.Apply(dialog)", coordinator);
        Assert.Contains("AppMaterialLayer.ApplySurface", coordinator);
        Assert.Contains("Microsoft.Windows.Storage.Pickers", coordinator);
        Assert.DoesNotContain("using Windows.Storage.Pickers;", coordinator);

        Assert.Contains("AppTypography.SemiBoldFontFilePath", requestFactory);
        Assert.Contains("AppTypography.CourseBlockLineHeight", requestFactory);
        Assert.Contains("AppMaterialLayer.Color(surface, theme", requestFactory);
        Assert.Contains("AppMaterialLayer.Color(role, theme", requestFactory);
        Assert.Contains("CompositeOverOpaque", requestFactory);
        Assert.Contains("AppToolbarIconAlignmentOffset", typography);
        Assert.Contains("AppToolbarIconAlignmentOffset", plannerXaml);
        Assert.Contains("AppTypography.IconAlignmentOffset", responsiveToolbar);

        var introducesMixedIconText =
            coordinator.Contains("new SymbolIcon", StringComparison.Ordinal) ||
            coordinator.Contains("new FontIcon", StringComparison.Ordinal) ||
            coordinator.Contains("new PathIcon", StringComparison.Ordinal);
        if (introducesMixedIconText)
            Assert.Contains("AppTypography.IconAlignmentOffset", coordinator);
    }

    [Fact]
    public void ExportDialogsInheritTheGlobalAccentWithoutLocalOverrides()
    {
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");

        Assert.Contains(
            "AppMaterialLayer.ApplySurface(dialog, AppMaterialSurface.Dialog)",
            coordinator);
        Assert.DoesNotContain("SystemAccentColor", coordinator);
        Assert.DoesNotContain("AccentFillColor", coordinator);
        Assert.DoesNotContain("AccentButtonBackground", coordinator);
        Assert.DoesNotContain("dialog.Resources", coordinator);
        Assert.DoesNotContain("PrimaryButtonStyle", coordinator);
    }

    [Fact]
    public void ExportFlowKeepsContentFormatAndConditionalOptionsSeparate()
    {
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");
        var requestFactory = Read("CoursePlanner", "Services", "TimetableExportRequestFactory.cs");

        Assert.Contains("ShowExportContentStepAsync", coordinator);
        Assert.Contains("ShowExportOptionsStepAsync", coordinator);
        Assert.Contains("optionsDraft = optionsDecision.State", coordinator);
        Assert.Contains("initialSelection", coordinator);
        Assert.Contains("new WheelSafeComboBox", coordinator);
        Assert.Contains("ExportContentComboBox", coordinator);
        Assert.Contains("Text[\"ExportChooseContent\"]", coordinator);
        Assert.Contains("ExportContentSelection.DetailedSemester", coordinator);
        Assert.DoesNotContain("ExportContentSelection.SemesterOverview", coordinator);
        Assert.DoesNotContain("ExportContentList", coordinator);
        Assert.DoesNotContain("ExportStepTitleFormat", coordinator);
        Assert.DoesNotContain("ExportContentCurrentWeekDescription", coordinator);
        Assert.Contains("ExportFormatPng", coordinator);
        Assert.Contains("ExportFormatPdf", coordinator);
        Assert.Contains("UsesSingleStepExport(content.Value)", coordinator);
        Assert.Contains("ExportContentSelection.CurrentPlan or ExportContentSelection.CourseLibrary", coordinator);
        Assert.Contains("CreateSingleStepExportState(content.Value)", coordinator);
        Assert.DoesNotContain("ExportFormatJson", coordinator);
        Assert.DoesNotContain("ExportFormatClipboard", coordinator);
        Assert.DoesNotContain("semester-overview", coordinator);
        Assert.Contains("-whole-semester-", coordinator);
        Assert.Contains("clarityBox.Visibility = pngSelected ? Visibility.Visible : Visibility.Collapsed", coordinator);
        Assert.Contains("ExportClarityExtreme", coordinator);
        Assert.Contains("ExportClarityMaximum", coordinator);
        Assert.Contains("var clarityBox = new WheelSafeComboBox", coordinator);
        Assert.Contains("FieldCheckBox(\"ExportFieldCourseName\"", coordinator);
        Assert.Contains("true, false), CourseBlockFields.CourseName", coordinator);
        Assert.Contains("Text[\"ExportCourseNameRequired\"]", coordinator);
        Assert.Contains("Clipboard.SetContent(package)", coordinator);
        Assert.DoesNotContain("PngScale", coordinator);
        Assert.Contains("options.ContentKind == ExportContentKind.CurrentWeek", requestFactory);
    }

    [Fact]
    public void ExportPickerSuggestionsAreBoundedBeforeCompleteNameValidation()
    {
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");

        Assert.Contains("WindowsFileNameRules.CreateBoundedSuggestion(", coordinator, StringComparison.Ordinal);
        Assert.Contains("BuildVisualExportBaseName(state),\n            extension", coordinator.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("ValidateExportNamesAsync(owner, semester, plan, suggestedName)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"{BuildVisualExportBaseName(state)}{extension}\"", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportFlowIsJsonOnlyAndExplainsRequiredCourseSynchronization()
    {
        var coordinator = Read("CoursePlanner", "Services", "ImportExportCoordinator.cs");
        var exchange = Read("CoursePlanner.Exchange", "ImportExportService.cs");

        Assert.Contains("picker.FileTypeFilter.Add(\".json\")", coordinator);
        Assert.DoesNotContain("picker.FileTypeFilter.Add(\".txt\")", coordinator);
        Assert.Contains("PlannerDataLimits.MaxImportFileBytes", coordinator);
        Assert.Contains("preview.RequiresCourseLibrarySync", coordinator);
        Assert.Contains("SynchronizeMissingPlanCourses = true", coordinator);
        Assert.Contains("var result = _planner.ApplyImportPreview", coordinator);
        Assert.Contains("result.Applied ? \"ImportCompleted\" : \"ImportNotApplied\"", coordinator);
        Assert.Contains("ApplySelectionPlanImport", exchange);
        Assert.Contains("var staged = JsonDefaults.Clone(document)", exchange);
    }

    [Fact]
    public void ChineseAndEnglishResourcesStayInParityForTheNewFlow()
    {
        var chinese = ResourceNames("zh-Hans");
        var english = ResourceNames("en-US");

        Assert.Equal(chinese, english);
        foreach (var key in new[]
                 {
                     "ExportContentDetailedSemester",
                     "ExportContentType",
                     "ExportSettings",
                     "ExportFormatPdf",
                     "ExportTheme",
                     "ExportClarityHigh",
                     "ExportClarityExtreme",
                     "ExportClarityMaximum",
                     "ExportCourseNameRequired",
                     "ImportCourseSyncRequired",
                     "ImportSyncAndApply",
                     "ImportNotApplied",
                     "ShareTextCopied"
                 })
        {
            Assert.Contains(key, chinese);
        }

        Assert.DoesNotContain("ExportContentSemesterOverview", chinese);
        Assert.DoesNotContain("ExportStepTitleFormat", chinese);
        Assert.DoesNotContain("ExportFormatJson", chinese);
        Assert.DoesNotContain("ExportFormatClipboard", chinese);
        Assert.DoesNotContain("ExportSummaryDataFormat", chinese);
        Assert.DoesNotContain(chinese, key => key.EndsWith("Description", StringComparison.Ordinal) &&
                                             key.StartsWith("ExportContent", StringComparison.Ordinal));

        Assert.Equal("整个学期", ResourceValue("zh-Hans", "ExportContentDetailedSemester"));
        Assert.Equal("Entire semester", ResourceValue("en-US", "ExportContentDetailedSemester"));
        Assert.Equal("当前方案数据（JSON）", ResourceValue("zh-Hans", "ExportContentCurrentPlan"));
        Assert.Equal("课程库数据（JSON）", ResourceValue("zh-Hans", "ExportContentCourseLibrary"));
        Assert.Equal("Current plan data (JSON)", ResourceValue("en-US", "ExportContentCurrentPlan"));
        Assert.Equal("Course library data (JSON)", ResourceValue("en-US", "ExportContentCourseLibrary"));
        Assert.Equal("矢量 PDF", ResourceValue("zh-Hans", "ExportFormatPdf"));
        Assert.Equal("Vector PDF", ResourceValue("en-US", "ExportFormatPdf"));
    }

    private static string ResourceValue(string culture, string key)
    {
        var document = XDocument.Load(PathFor(
            "CoursePlanner.Application",
            "Resources",
            culture,
            "Resources.resw"));
        return document.Root!
            .Elements("data")
            .Single(element => string.Equals((string?)element.Attribute("name"), key, StringComparison.Ordinal))
            .Element("value")!
            .Value;
    }

    private static SortedSet<string> ResourceNames(string culture)
    {
        var document = XDocument.Load(PathFor(
            "CoursePlanner.Application",
            "Resources",
            culture,
            "Resources.resw"));
        return new SortedSet<string>(document.Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!), StringComparer.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(PathFor(segments));

    private static string PathFor(params string[] segments) =>
        RepositoryPaths.FromRoot(segments);
}

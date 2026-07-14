namespace CoursePlanner.Tests;

public sealed class UiStateRegressionArchitectureTests
{
    [Fact]
    public void PlansPageSelectsRowsByPlanIdAfterCreateAndCopy()
    {
        var code = Read("CoursePlanner", "Pages", "PlansPage.xaml.cs");

        Assert.Contains("SelectPlanById(plan.PlanId);", code);
        Assert.Contains("SelectPlanById(copy.PlanId);", code);
        Assert.DoesNotContain("PlanList.SelectedItem = plan;", code);
        Assert.DoesNotContain("PlanList.SelectedItem = copy;", code);
    }

    [Fact]
    public void SemesterSaveKeepsModelWeekStartAndStopsAfterValidationFailure()
    {
        var code = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");
        var saveStart = code.IndexOf("private async void SaveSemester_Click", StringComparison.Ordinal);
        var saveEnd = code.IndexOf("private void SemesterDate_DateChanged", saveStart, StringComparison.Ordinal);
        var save = code[saveStart..saveEnd];

        Assert.Contains("ReadWeekStartDay()", save);
        Assert.Contains("if (!result.IsValid)", save);
        Assert.Contains("return;", save);
        Assert.Contains("LoadSettings();", save);
        Assert.DoesNotContain("InitializeControls();", save);
        Assert.DoesNotContain("SelectedIndex == 1 ? WeekStartDay.Sunday : WeekStartDay.Monday", save);
    }

    [Fact]
    public void NewSemesterReloadsTheRowCollectionBeforeSelectingById()
    {
        var code = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");
        var addStart = code.IndexOf("private async void AddSemester_Click", StringComparison.Ordinal);
        var addEnd = code.IndexOf("private async void SaveSemester_Click", addStart, StringComparison.Ordinal);
        var add = code[addStart..addEnd];

        Assert.Contains("ViewModel.Reload();", add);
        Assert.Contains("semester.SemesterId", add);
        Assert.DoesNotContain("SemesterList.SelectedItem = semester;", add);
    }

    [Fact]
    public void SemestersPageDisablesLastSemesterDeletionAndClearsEmptyEditor()
    {
        var code = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");

        Assert.Contains("DeleteSemesterButton.IsEnabled = hasSemester && ViewModel.Semesters.Count > 1;", code);
        Assert.Contains("LoadEmptySemesterFields();", code);
        Assert.Contains("SemesterNameBox.Text = \"\";", code);
        Assert.Contains("ClearCoursesButton.IsEnabled = hasSemester;", code);
        Assert.Contains("DeletePeriodButton.IsEnabled = ViewModel.Periods.Count > 1;", code);
    }

    [Fact]
    public void SemestersPageReloadsTheCompleteEditorAfterDocumentRollback()
    {
        var code = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");

        Assert.Contains("services.Documents.RolledBack += Documents_RolledBack;", code);
        Assert.Contains("_services.Documents.RolledBack -= Documents_RolledBack;", code);
        Assert.Contains(
            "private void Documents_RolledBack(object? sender, EventArgs e) =>\n        RefreshLocalizedControls();",
            code.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void LabelsPageReloadsGroupsSelectionAndEditorAfterDocumentRollback()
    {
        var code = Read("CoursePlanner", "Pages", "LabelsPage.xaml.cs");

        Assert.Contains("services.Documents.RolledBack += Documents_RolledBack;", code);
        Assert.Contains("_services.Documents.RolledBack -= Documents_RolledBack;", code);
        Assert.Contains(
            "private void Documents_RolledBack(object? sender, EventArgs e) =>\n        RefreshLocalizedControls();",
            code.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("RefreshLabelRows();", code);
        Assert.Contains("LoadSettingsCore();", code);
    }

    [Fact]
    public void SettingsPageRestoresLanguageAndThemeSelectionsAfterDocumentRollback()
    {
        var code = Read("CoursePlanner", "Pages", "SettingsPage.xaml.cs");

        Assert.Contains("services.Documents.RolledBack += Documents_RolledBack;", code);
        Assert.Contains("_services.Documents.RolledBack -= Documents_RolledBack;", code);
        Assert.Contains(
            "private void Documents_RolledBack(object? sender, EventArgs e) =>\n        RefreshLocalizedControls();",
            code.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("LanguageBox.SelectedIndex = ViewModel.Language switch", code);
        Assert.Contains("ThemeBox.SelectedIndex = ViewModel.Theme switch", code);
    }

    [Fact]
    public void PeriodCommandsHandleCoreRejectionsWithoutEscapingUiEvents()
    {
        var code = Read("CoursePlanner", "Pages", "SemestersPage.xaml.cs");
        var saveStart = code.IndexOf("private async void SavePeriod_Click", StringComparison.Ordinal);
        var saveEnd = code.IndexOf("private async void AddPeriod_Click", saveStart, StringComparison.Ordinal);
        if (saveEnd < 0)
            saveEnd = code.IndexOf("private void AddPeriod_Click", saveStart, StringComparison.Ordinal);
        var save = code[saveStart..saveEnd];

        var preflight = save.IndexOf("PeriodOverlapsAdjacent", StringComparison.Ordinal);
        var mutation = save.IndexOf("ViewModel.UpdatePeriodTime", StringComparison.Ordinal);
        Assert.True(preflight >= 0 && mutation > preflight);
        Assert.Contains("catch (InvalidOperationException)", save);
        Assert.Contains("catch (InvalidOperationException)", code[saveEnd..]);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(RepositoryPaths.FromRoot(segments));
}

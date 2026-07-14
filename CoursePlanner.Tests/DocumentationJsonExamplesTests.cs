using CoursePlanner.Core;
using CoursePlanner.Exchange;

namespace CoursePlanner.Tests;

public sealed class DocumentationJsonExamplesTests
{
    [Fact]
    public void CourseLibraryExampleIsImportable()
    {
        var target = new PlannerDocument();
        var preview = ImportExportService.PreviewJson(
            target,
            ReadExample("course-library.json"));

        Assert.True(preview.CanApply, PreviewErrors(preview));
        Assert.True(ImportExportService.ApplyImport(
            target,
            preview,
            new ImportApplyOptions()).Applied);
        Assert.Single(target.Semesters);
        Assert.Equal(4, target.Labels.Count);
        Assert.Equal(3, target.CourseLibrary.Count);
        Assert.Empty(target.Plans);
    }

    [Fact]
    public void SelectionPlanExampleIsImportableWithBundledCourseSynchronization()
    {
        var target = new PlannerDocument();
        var preview = ImportExportService.PreviewJson(
            target,
            ReadExample("selection-plan.json"));

        Assert.True(preview.CanApply, PreviewErrors(preview));
        Assert.True(preview.RequiresCourseLibrarySync);

        Assert.True(ImportExportService.ApplyImport(
            target,
            preview,
            new ImportApplyOptions
            {
                SynchronizeMissingPlanCourses = true
            }).Applied);
        Assert.Single(target.Semesters);
        Assert.Equal(3, target.Labels.Count);
        Assert.Equal(2, target.CourseLibrary.Count);
        Assert.Single(target.Plans);
    }

    private static string ReadExample(string fileName) =>
        File.ReadAllText(RepositoryPaths.FromRoot("docs", "examples", fileName));

    private static string PreviewErrors(ImportPreview preview) =>
        string.Join(
            Environment.NewLine,
            preview.Items.SelectMany(item => item.Errors)
                .Select(error => error.Code));
}

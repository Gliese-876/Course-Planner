using System.Diagnostics;
using System.Globalization;
using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class ImportPreviewResourceSafetyTests
{
    [Fact]
    public void MaximumImportPreviewIsFilteredFullyButOnlyAUiBoundedSubsetIsFormatted()
    {
        var preview = new ImportPreview
        {
            Kind = PlannerSchemas.CourseLibraryKind,
            SchemaVersion = PlannerSchemas.Current,
            Items = Enumerable.Range(0, PlannerDataLimits.MaxCourses)
                .Select(index => new ImportPreviewItem
                {
                    Kind = "course",
                    DisplayName = $"Preview item {index:D4}",
                    SemesterName = "Semester",
                    Status = ImportPreviewStatus.Added
                })
                .ToList()
        };
        var formatter = new CourseDisplayFormatter(new AppLocalizer(
            LanguageMode.English,
            CultureInfo.GetCultureInfo("en-US")));
        var stopwatch = Stopwatch.StartNew();

        var projection = ImportPreviewTextProjectionService.Create(
            preview,
            new ImportPreviewFilter(),
            formatter);

        stopwatch.Stop();
        Assert.Equal(PlannerDataLimits.MaxCourses, projection.MatchingItemCount);
        Assert.Equal(ImportPreviewTextProjectionService.MaximumDisplayedItems, projection.DisplayedItemCount);
        Assert.Contains("Preview item 0199", projection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Preview item 0200", projection.Text, StringComparison.Ordinal);
        Assert.Contains("first 200 of 5000 matching items", projection.Text, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Projection took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void LatestRequestTrackerRejectsAStaleResultAfterANewerFilterRequest()
    {
        using var tracker = new LatestRequestTracker();
        using var stale = tracker.Begin();
        using var latest = tracker.Begin();
        var published = new List<string>();

        var stalePublished = tracker.TryExecuteIfCurrent(stale, () => published.Add("stale"));
        var latestPublished = tracker.TryExecuteIfCurrent(latest, () => published.Add("latest"));

        Assert.True(stale.Token.IsCancellationRequested);
        Assert.False(stalePublished);
        Assert.True(latestPublished);
        Assert.Equal(new[] { "latest" }, published);
    }

    [Fact]
    public void ImportPreviewUiDebouncesBackgroundProjectionAndKeepsFullReportExport()
    {
        var coordinator = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Services",
            "ImportExportCoordinator.cs"));

        Assert.Contains("ImportPreviewFilterDebounce", coordinator, StringComparison.Ordinal);
        Assert.Contains("new LatestRequestTracker()", coordinator, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(ImportPreviewFilterDebounce, request.Token)", coordinator, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(", coordinator, StringComparison.Ordinal);
        Assert.Contains("ImportPreviewTextProjectionService.Create(", coordinator, StringComparison.Ordinal);
        Assert.Contains("reportRequests.TryExecuteIfCurrent(", coordinator, StringComparison.Ordinal);
        Assert.Contains("new CourseDisplayFormatter(Text).ImportPreviewReport(preview)", coordinator, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

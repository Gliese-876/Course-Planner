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
    public void MergePreviewPrioritizesErrorsAndConflictsWithinTheUiBound()
    {
        var preview = new ImportPreview
        {
            Kind = PlannerSchemas.CourseLibraryKind,
            SchemaVersion = PlannerSchemas.Current,
            Items = Enumerable.Range(0, 250)
                .Select(index => new ImportPreviewItem
                {
                    Kind = "course",
                    DisplayName = $"Added {index:D3}",
                    Status = ImportPreviewStatus.Added
                })
                .Append(new ImportPreviewItem
                {
                    Kind = "semester",
                    DisplayName = "Conflicting semester",
                    Status = ImportPreviewStatus.Conflict,
                    Warnings =
                    {
                        new ValidationIssue
                        {
                            Code = "Import.SemesterIdentityConflict",
                            Parameters = ["Local semester"]
                        }
                    }
                })
                .Append(new ImportPreviewItem
                {
                    Kind = "file",
                    DisplayName = "Broken input",
                    Status = ImportPreviewStatus.NotImportable,
                    Errors = { new ValidationIssue { Code = "Import.InvalidJson" } }
                })
                .ToList()
        };
        var formatter = new CourseDisplayFormatter(new AppLocalizer(
            LanguageMode.English,
            CultureInfo.GetCultureInfo("en-US")));

        var projection = ImportMergePreviewProjectionService.Create(preview, formatter);

        Assert.Equal(252, projection.TotalItemCount);
        Assert.Equal(ImportMergePreviewProjectionService.MaximumDisplayedItems, projection.DisplayedItemCount);
        Assert.Contains("Parsed 252 items", projection.Text, StringComparison.Ordinal);
        Assert.Contains("x File: Broken input", projection.Text, StringComparison.Ordinal);
        Assert.Contains("! Semester: Conflicting semester", projection.Text, StringComparison.Ordinal);
        Assert.Contains("error: This file is not valid Course Planner JSON.", projection.Text, StringComparison.Ordinal);
        Assert.Contains("displaying 200 / 252", projection.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportPreviewUiUsesOneMergeConfirmationWithoutAdvancedFilters()
    {
        var coordinator = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Services",
            "ImportExportCoordinator.cs"));

        Assert.Contains("await Task.Run(", coordinator, StringComparison.Ordinal);
        Assert.Contains("ImportMergePreviewProjectionService.Create(preview, display)", coordinator, StringComparison.Ordinal);
        Assert.Contains("ImportMergeReportBox", coordinator, StringComparison.Ordinal);
        Assert.Contains("decision.Options.SynchronizeMissingPlanCourses = preview.RequiresCourseLibrarySync", coordinator, StringComparison.Ordinal);
        Assert.Contains("new CourseDisplayFormatter(Text).ImportPreviewReport(preview)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportPreviewFilterDebounce", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("LatestRequestTracker", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportStatusFilterBox", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportSemesterFilterBox", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportSearchBox", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmCourseLibrarySyncAsync", coordinator, StringComparison.Ordinal);
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}

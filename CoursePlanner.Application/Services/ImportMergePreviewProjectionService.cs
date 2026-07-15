using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed record ImportMergePreviewProjection(
    string Text,
    int TotalItemCount,
    int DisplayedItemCount);

public static class ImportMergePreviewProjectionService
{
    public const int MaximumDisplayedItems = 200;

    public static ImportMergePreviewProjection Create(
        ImportPreview preview,
        CourseDisplayFormatter formatter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(formatter);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedItems = preview.Items
            .OrderBy(item => StatusPriority(item.Status))
            .ThenBy(item => item.Kind, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumDisplayedItems)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();

        var text = formatter.ImportMergePreviewReport(
            preview,
            orderedItems,
            preview.Items.Count);
        cancellationToken.ThrowIfCancellationRequested();
        return new ImportMergePreviewProjection(text, preview.Items.Count, orderedItems.Count);
    }

    private static int StatusPriority(ImportPreviewStatus status) => status switch
    {
        ImportPreviewStatus.NotImportable => 0,
        ImportPreviewStatus.Conflict => 1,
        ImportPreviewStatus.Warning => 2,
        ImportPreviewStatus.Added => 3,
        ImportPreviewStatus.Updated => 4,
        ImportPreviewStatus.Skipped => 5,
        _ => 6
    };
}

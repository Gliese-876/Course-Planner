using CoursePlanner.Core;
using CoursePlanner.Exchange;

namespace CoursePlanner.Services;

public sealed record ImportPreviewTextProjection(
    string Text,
    int MatchingItemCount,
    int DisplayedItemCount);

public static class ImportPreviewTextProjectionService
{
    public const int MaximumDisplayedItems = 200;

    public static ImportPreviewTextProjection Create(
        ImportPreview preview,
        ImportPreviewFilter filter,
        CourseDisplayFormatter formatter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(formatter);
        cancellationToken.ThrowIfCancellationRequested();

        var matchingItems = ImportExportService.FilterPreviewItems(preview, filter);
        cancellationToken.ThrowIfCancellationRequested();
        var displayedItems = matchingItems.Count <= MaximumDisplayedItems
            ? matchingItems
            : matchingItems.Take(MaximumDisplayedItems).ToList();
        var text = formatter.ImportPreviewReport(preview, displayedItems, matchingItems.Count);
        cancellationToken.ThrowIfCancellationRequested();
        return new ImportPreviewTextProjection(text, matchingItems.Count, displayedItems.Count);
    }
}

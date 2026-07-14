using System.Globalization;
using CoursePlanner.Core;
using SkiaSharp;

namespace CoursePlanner.Export;

public static class TimetableExportService
{
    private const float Margin = 28;
    private const float PeriodColumnWidth = 88;
    private const float DayColumnWidth = 168;
    private const float MinimumPeriodHeight = 74;
    private const float PanelPadding = 12;
    private const float MatrixGap = 16;
    internal const int EstimatedBytesPerPngPixel = 4;
    internal const int EstimatedPeakBytesPerPngPixel = 8;
    internal const long MaximumPngBitmapBytes = 256L * 1024 * 1024;
    internal const long MaximumPngWorkingSetBytes = 512L * 1024 * 1024;
    internal const int MaximumRenderedCourseBlocks = 30_000;
    internal const long MaximumRenderedTextCharacters = 8_000_000;
    internal const int MaximumConflictLanes = 64;
    internal const int MaximumPdfLogicalDimension = 100_000;
    internal const float MaximumFontSize = 256;
    internal const float MaximumLineHeight = 512;
    private const int MaximumPngDimension = 32_767;

    public static TimetableExportMeasurement Measure(TimetableExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = request.Options ?? throw new ArgumentException("Export options are required.", nameof(request));
        ValidateRequest(request, options, requireRenderResources: false);
        if (options.ContentKind == ExportContentKind.DetailedSemester)
            EnsureFontFiles(request.Fonts);
        using var resources = options.ContentKind == ExportContentKind.DetailedSemester
            ? new RenderResources(request.Fonts)
            : null;
        var layout = CreateLayout(request, options, resources);
        return Measurement(layout, ScaleFor(options));
    }

    public static void ExportPng(TimetableExportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ExportPathAtomically(path, stream => ExportPng(request, stream));
    }

    public static void ExportPng(TimetableExportRequest request, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        var options = request.Options ?? throw new ArgumentException("Export options are required.", nameof(request));
        ExportPngCore(request, options, destination, scaleOverride: null);
    }

    public static void ExportPdf(TimetableExportRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ExportPathAtomically(path, stream => ExportPdf(request, stream));
    }

    public static void ExportPdf(TimetableExportRequest request, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        var options = request.Options ?? throw new ArgumentException("Export options are required.", nameof(request));
        EnsureWritableDestination(destination);
        if (destination.CanSeek)
        {
            ExportPdfCore(request, options, destination);
            return;
        }

        ExportPdfThroughSeekableBuffer(request, options, destination);
    }

    private static void ExportPngCore(
        TimetableExportRequest request,
        TimetableExportOptions options,
        Stream destination,
        int? scaleOverride)
    {
        EnsureWritableDestination(destination);
        ValidateRequest(request, options, requireRenderResources: true);
        if (options.FileFormat != ExportFileFormat.Png)
            throw new ArgumentException("ExportPng requires PNG options.", nameof(request));

        using var resources = new RenderResources(request.Fonts);
        var layout = CreateLayout(request, options, resources);
        var scale = scaleOverride ?? ScaleFor(options);
        var measurement = Measurement(layout, scale);
        EnsureSafeBitmap(measurement);

        var imageInfo = new SKImageInfo(
            measurement.PixelWidth,
            measurement.PixelHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(imageInfo))
        {
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.BitmapMemory,
                checked((long)measurement.PixelWidth * measurement.PixelHeight * EstimatedBytesPerPngPixel),
                MaximumPngBitmapBytes,
                "The PNG bitmap could not be allocated within the export memory budget.");
        }
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);
        DrawDocument(canvas, request, layout, resources);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap)
                          ?? throw new InvalidDataException("The PNG image surface could not be created.");
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new InvalidDataException("The PNG encoder did not produce output.");
        data.SaveTo(destination);
    }

    private static void ExportPdfCore(
        TimetableExportRequest request,
        TimetableExportOptions options,
        Stream destination)
    {
        EnsureWritableDestination(destination);
        ValidateRequest(request, options, requireRenderResources: true);
        if (options.FileFormat != ExportFileFormat.Pdf)
            throw new ArgumentException("ExportPdf requires PDF options.", nameof(request));

        using var resources = new RenderResources(request.Fonts);
        var layout = CreateLayout(request, options, resources);
        EnsureSafeVectorLayout(layout);
        using var document = SKDocument.CreatePdf(destination)
                             ?? throw new InvalidDataException("The PDF document could not be created.");
        var canvas = document.BeginPage(layout.Width, layout.Height)
                     ?? throw new InvalidDataException("The PDF page surface could not be created.");
        DrawDocument(canvas, request, layout, resources);
        document.EndPage();
        document.Close();
    }

    private static void ExportPdfThroughSeekableBuffer(
        TimetableExportRequest request,
        TimetableExportOptions options,
        Stream destination)
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"course-planner-pdf-{Guid.NewGuid():N}.tmp");
        ExportPdfThroughSeekableBuffer(request, options, destination, temporaryPath);
    }

    internal static void ExportPdfThroughSeekableBuffer(
        TimetableExportRequest request,
        TimetableExportOptions options,
        Stream destination,
        string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        try
        {
            using var buffer = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            ExportPdfCore(request, options, buffer);
            buffer.Position = 0;
            buffer.CopyTo(destination, 128 * 1024);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DrawDocument(
        SKCanvas canvas,
        TimetableExportRequest request,
        RenderLayout layout,
        RenderResources resources)
    {
        canvas.Clear(ToSkColor(request.Palette.PageBackground));
        DrawDocumentHeader(canvas, request, layout, resources);

        switch (layout.ContentKind)
        {
            case ExportContentKind.CurrentWeek:
                DrawWeekGrid(
                    canvas,
                    request,
                    request.Week,
                    Margin,
                    Margin + layout.DocumentHeaderHeight,
                    layout.WeekMetrics,
                    layout.Fields,
                    wrapCourseText: false,
                    layout.BlocksByWeek[request.Week],
                    resources);
                break;
            case ExportContentKind.WeekRange:
            case ExportContentKind.DetailedSemester:
                DrawDetailedMatrix(canvas, request, layout, resources);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout.ContentKind), layout.ContentKind, null);
        }
    }

    private static void DrawDocumentHeader(
        SKCanvas canvas,
        TimetableExportRequest request,
        RenderLayout layout,
        RenderResources resources)
    {
        var typography = request.Typography;
        DrawText(
            canvas,
            request.Text.Title,
            Margin,
            Margin + typography.Title.FontSize,
            typography.Title.FontSize,
            bold: true,
            courseFont: false,
            request.Palette.PrimaryText,
            resources);
        var subtitle = layout.ContentKind switch
        {
            ExportContentKind.CurrentWeek => request.Text.WeekSubtitle,
            ExportContentKind.WeekRange => request.Text.WeekRangeSubtitle,
            ExportContentKind.DetailedSemester => request.Text.DetailedSemesterSubtitle,
            _ => throw new ArgumentOutOfRangeException(nameof(layout.ContentKind), layout.ContentKind, null)
        };
        DrawText(
            canvas,
            subtitle,
            Margin,
            Margin + typography.Title.LineHeight + typography.Subtitle.FontSize,
            typography.Subtitle.FontSize,
            bold: true,
            courseFont: false,
            request.Palette.SecondaryText,
            resources);
    }

    private static void DrawDetailedMatrix(
        SKCanvas canvas,
        TimetableExportRequest request,
        RenderLayout layout,
        RenderResources resources)
    {
        var palette = request.Palette;
        var cardPaint = resources.Paint(palette.MatrixCardBackground);

        for (var index = 0; index < layout.Weeks.Count; index++)
        {
            var column = index % layout.Columns;
            var row = index / layout.Columns;
            var x = Margin + (column * (layout.PanelWidth + MatrixGap));
            var y = Margin + layout.DocumentHeaderHeight + (row * (layout.PanelHeight + MatrixGap));
            var card = new SKRect(x, y, x + layout.PanelWidth, y + layout.PanelHeight);
            canvas.DrawRoundRect(card, 6, 6, cardPaint);

            var week = layout.Weeks[index];
            DrawText(
                canvas,
                string.Format(CultureInfo.CurrentCulture, request.Text.WeekHeadingFormat, week),
                x + PanelPadding,
                y + ((layout.PanelHeaderHeight + request.Typography.BodyStrong.FontSize) / 2) - 2,
                request.Typography.BodyStrong.FontSize,
                bold: true,
                courseFont: false,
                palette.PrimaryText,
                resources);
            DrawFittedText(
                canvas,
                SemesterRules.WeekRangeText(request.Semester, week),
                x + 92,
                y + ((layout.PanelHeaderHeight + request.Typography.Caption.FontSize) / 2) - 2,
                layout.PanelWidth - 104,
                request.Typography.Caption.FontSize,
                bold: false,
                courseFont: false,
                palette.SecondaryText,
                resources);

            DrawWeekGrid(
                canvas,
                request,
                week,
                x + PanelPadding,
                y + layout.PanelHeaderHeight,
                layout.WeekMetrics,
                layout.Fields,
                wrapCourseText: layout.ContentKind == ExportContentKind.DetailedSemester,
                layout.BlocksByWeek[week],
                resources);
        }
    }

    private static void DrawWeekGrid(
        SKCanvas canvas,
        TimetableExportRequest request,
        int week,
        float x,
        float y,
        WeekGridMetrics metrics,
        CourseBlockFields fields,
        bool wrapCourseText,
        IReadOnlyList<TimetableCourseBlock> blocks,
        RenderResources resources)
    {
        var semester = request.Semester;
        var palette = request.Palette;
        var typography = request.Typography;
        var periods = semester.PeriodSchedule.OrderBy(period => period.Period).ToList();
        var visualPeriodCount = Math.Max(1, periods.Count);
        var periodIndexes = periods
            .Select((period, index) => (period.Period, index))
            .ToDictionary(item => item.Period, item => item.index);
        var gridWidth = metrics.Width;
        var gridHeight = metrics.HeaderHeight + (metrics.PeriodHeight * visualPeriodCount);
        var headerPaint = resources.Paint(palette.HeaderBackground);
        var outsidePaint = resources.Paint(palette.OutsideSemesterOverlay);
        var linePaint = resources.Paint(palette.Divider, stroke: true, strokeWidth: 1);

        canvas.DrawRect(x, y, gridWidth, gridHeight, resources.Paint(palette.PageBackground));
        canvas.DrawRect(x, y, gridWidth, metrics.HeaderHeight, headerPaint);

        var weekdayOrder = SemesterRules.GetWeekdayOrder(semester.WeekStartDay).ToList();
        var dates = SemesterRules.GetWeekDates(semester, week);
        for (var dayIndex = 0; dayIndex < weekdayOrder.Count; dayIndex++)
        {
            var dayX = x + metrics.LeftWidth + (dayIndex * metrics.DayWidth);
            if (SemesterRules.IsOutsideSemester(semester, dates[dayIndex]))
                canvas.DrawRect(dayX, y, metrics.DayWidth, gridHeight, outsidePaint);

            DrawFittedText(
                canvas,
                WeekdayText(request, weekdayOrder[dayIndex]),
                dayX + 8,
                y + 4 + typography.BodyStrong.FontSize,
                metrics.DayWidth - 16,
                typography.BodyStrong.FontSize,
                bold: true,
                courseFont: false,
                palette.PrimaryText,
                resources);
            DrawFittedText(
                canvas,
                DateDisplay.Date(dates[dayIndex]),
                dayX + 8,
                y + 5 + typography.BodyStrong.LineHeight + typography.Caption.FontSize,
                metrics.DayWidth - 16,
                typography.Caption.FontSize,
                bold: false,
                courseFont: false,
                palette.SecondaryText,
                resources);
        }

        for (var periodIndex = 0; periodIndex < periods.Count; periodIndex++)
        {
            var period = periods[periodIndex];
            var periodY = y + metrics.HeaderHeight + (periodIndex * metrics.PeriodHeight);
            DrawText(
                canvas,
                period.Period.ToString(CultureInfo.CurrentCulture),
                x + 8,
                periodY + 7 + typography.BodyStrong.FontSize,
                typography.BodyStrong.FontSize,
                true,
                false,
                palette.PrimaryText,
                resources);
            DrawFittedText(
                canvas,
                $"{period.Start:HH\\:mm}-{period.End:HH\\:mm}",
                x + 8,
                periodY + 8 + typography.BodyStrong.LineHeight + typography.Caption.FontSize,
                metrics.LeftWidth - 16,
                typography.Caption.FontSize,
                false,
                false,
                palette.SecondaryText,
                resources);
        }

        for (var day = 0; day <= 7; day++)
        {
            var lineX = x + metrics.LeftWidth + (day * metrics.DayWidth);
            canvas.DrawLine(lineX, y, lineX, y + gridHeight, linePaint);
        }

        canvas.DrawLine(x, y + metrics.HeaderHeight, x + gridWidth, y + metrics.HeaderHeight, linePaint);
        for (var row = 0; row <= visualPeriodCount; row++)
        {
            var lineY = y + metrics.HeaderHeight + (row * metrics.PeriodHeight);
            canvas.DrawLine(x, lineY, x + gridWidth, lineY, linePaint);
        }
        canvas.DrawRect(x, y, gridWidth, gridHeight, linePaint);

        foreach (var block in blocks)
        {
            var dayIndex = weekdayOrder.IndexOf(block.Slot.Weekday);
            if (dayIndex < 0 || !periodIndexes.TryGetValue(block.StartPeriod, out var startIndex))
                continue;
            if (!periodIndexes.TryGetValue(block.EndPeriod, out var endIndex))
                endIndex = Math.Min(visualPeriodCount - 1, startIndex + Math.Max(0, block.EndPeriod - block.StartPeriod));

            var conflictCount = Math.Max(1, block.ConflictCount);
            var conflictIndex = Math.Clamp(block.ConflictIndex, 0, conflictCount - 1);
            var availableWidth = metrics.DayWidth - 8;
            var segmentWidth = availableWidth / conflictCount;
            var blockX = x + metrics.LeftWidth + (dayIndex * metrics.DayWidth) + 4 + (segmentWidth * conflictIndex);
            var blockY = y + metrics.HeaderHeight + (startIndex * metrics.PeriodHeight) + 4;
            var blockWidth = Math.Max(8, segmentWidth - 4);
            var blockHeight = Math.Max(8, ((endIndex - startIndex + 1) * metrics.PeriodHeight) - 8);
            DrawCourseBlock(
                canvas,
                request,
                block,
                blockX,
                blockY,
                blockWidth,
                blockHeight,
                fields,
                wrapCourseText,
                resources);
        }
    }

    private static void DrawCourseBlock(
        SKCanvas canvas,
        TimetableExportRequest request,
        TimetableCourseBlock block,
        float x,
        float y,
        float width,
        float height,
        CourseBlockFields fields,
        bool wrapCourseText,
        RenderResources resources)
    {
        var palette = request.Palette;
        var fill = DifferenceFill(block.Difference, palette);
        var rect = new SKRect(x, y, x + width, y + height);
        canvas.DrawRoundRect(rect, 4, 4, resources.Paint(fill));
        canvas.DrawRoundRect(new SKRect(x, y, x + 5, y + height), 4, 4, resources.Paint(ParseCourseColor(block.Course.Color)));

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, 4, 4));
        var lines = LayoutCourseBlockText(
            block.Course,
            fields,
            Math.Max(1, width - 17),
            request,
            resources,
            wrapCourseText);
        var typography = request.Typography;
        var baseline = y + 5 + typography.CourseTitle.FontSize;
        foreach (var line in lines)
        {
            var role = line.IsTitle ? typography.CourseTitle : typography.CourseDetail;
            if (!wrapCourseText && baseline > y + height - 3)
                break;

            var color = line.Field == CourseBlockFields.Capacity
                ? CapacityColor(block.Course, palette)
                : line.IsTitle ? palette.PrimaryText : palette.SecondaryText;
            if (wrapCourseText)
            {
                DrawText(
                    canvas,
                    line.Text,
                    x + 11,
                    baseline,
                    role.FontSize,
                    line.IsTitle,
                    courseFont: true,
                    color,
                    resources);
            }
            else
            {
                DrawFittedText(
                    canvas,
                    line.Text,
                    x + 11,
                    baseline,
                    Math.Max(1, width - 17),
                    role.FontSize,
                    line.IsTitle,
                    courseFont: true,
                    color,
                    resources);
            }
            baseline += role.LineHeight;
        }
        canvas.Restore();
    }

    private static RenderLayout CreateLayout(
        TimetableExportRequest request,
        TimetableExportOptions options,
        RenderResources? resources = null)
    {
        var weeks = WeeksFor(request, options);
        var fields = EffectiveFields(request, options);
        var exportCourses = TimetableRenderModelService.CoursesForExport(
            request.Plan,
            request.CourseLibrary,
            request.Differences);
        var blocksByWeek = TimetableRenderModelService.BuildCourseBlocksByWeek(
            exportCourses,
            request.Semester,
            weeks,
            request.Differences);
        var workload = AnalyzeWorkload(blocksByWeek, fields);
        EnsureSafeWorkload(workload);
        var typography = request.Typography;
        var documentHeaderHeight = Math.Max(88, 12 + typography.Title.LineHeight + typography.Subtitle.LineHeight);
        var panelHeaderHeight = Math.Max(38, Math.Max(typography.BodyStrong.LineHeight, typography.Caption.LineHeight) + 14);
        var weekdayHeaderHeight = Math.Max(46, typography.BodyStrong.LineHeight + typography.Caption.LineHeight + 8);
        var maximumConflicts = MaximumConflictCount(blocksByWeek);
        var dayWidth = options.ContentKind is ExportContentKind.DetailedSemester or ExportContentKind.WeekRange
            ? Math.Max(DayColumnWidth, 150 * maximumConflicts)
            : DayColumnWidth;
        var periodHeight = RequiredPeriodHeight(
            request,
            blocksByWeek,
            fields,
            dayWidth,
            wrapCourseText: options.ContentKind == ExportContentKind.DetailedSemester,
            resources);
        var weekMetrics = new WeekGridMetrics(
            PeriodColumnWidth,
            dayWidth,
            weekdayHeaderHeight,
            periodHeight,
            Math.Max(1, request.Semester.PeriodSchedule.Count));

        if (options.ContentKind == ExportContentKind.CurrentWeek)
        {
            return new RenderLayout(
                options.ContentKind,
                weeks,
                fields,
                ToSafeLogicalDimension((Margin * 2) + weekMetrics.Width),
                ToSafeLogicalDimension((Margin * 2) + documentHeaderHeight + weekMetrics.Height),
                1,
                1,
                documentHeaderHeight,
                panelHeaderHeight,
                weekMetrics.Width,
                weekMetrics.Height,
                weekMetrics,
                blocksByWeek,
                workload);
        }

        var (matrixColumns, matrixRows) = ResolveMatrixDimensions(weeks.Count);
        var matrixPanelWidth = (PanelPadding * 2) + weekMetrics.Width;
        var matrixPanelHeight = panelHeaderHeight + weekMetrics.Height + PanelPadding;
        return new RenderLayout(
            options.ContentKind,
            weeks,
            fields,
            ToSafeLogicalDimension((Margin * 2) + (matrixPanelWidth * matrixColumns) + (MatrixGap * Math.Max(0, matrixColumns - 1))),
            ToSafeLogicalDimension((Margin * 2) + documentHeaderHeight + (matrixPanelHeight * matrixRows) + (MatrixGap * Math.Max(0, matrixRows - 1))),
            matrixColumns,
            matrixRows,
            documentHeaderHeight,
            panelHeaderHeight,
            matrixPanelWidth,
            matrixPanelHeight,
            weekMetrics,
            blocksByWeek,
            workload);
    }

    private static IReadOnlyList<int> WeeksFor(TimetableExportRequest request, TimetableExportOptions options) =>
        options.ContentKind switch
        {
            ExportContentKind.CurrentWeek => new[] { request.Week },
            ExportContentKind.WeekRange => Enumerable.Range(options.StartWeek, options.EndWeek - options.StartWeek + 1).ToArray(),
            ExportContentKind.DetailedSemester => Enumerable.Range(1, request.Semester.WeekCount).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(options.ContentKind), options.ContentKind, null)
        };

    private static float RequiredPeriodHeight(
        TimetableExportRequest request,
        IReadOnlyDictionary<int, IReadOnlyList<TimetableCourseBlock>> blocksByWeek,
        CourseBlockFields fields,
        float dayWidth,
        bool wrapCourseText,
        RenderResources? resources)
    {
        if (wrapCourseText && resources is null)
            throw new InvalidOperationException("Entire-semester layout requires export font resources.");

        var required = MinimumPeriodHeight;
        foreach (var blocks in blocksByWeek.Values)
        {
            foreach (var block in blocks)
            {
                var textWidth = CourseBlockTextWidth(dayWidth, block.ConflictCount);
                var lines = LayoutCourseBlockText(
                    block.Course,
                    fields,
                    textWidth,
                    request,
                    resources,
                    wrapCourseText);
                var span = Math.Max(1, block.EndPeriod - block.StartPeriod + 1);
                var contentHeight = 12 + lines.Sum(line =>
                    line.IsTitle
                        ? request.Typography.CourseTitle.LineHeight
                        : request.Typography.CourseDetail.LineHeight);
                required = Math.Max(required, (float)Math.Ceiling((contentHeight + 8) / (double)span));
            }
        }
        return required;
    }

    private static IReadOnlyList<CourseBlockTextLine> LayoutCourseBlockText(
        CourseOffering course,
        CourseBlockFields fields,
        float availableWidth,
        TimetableExportRequest request,
        RenderResources? resources,
        bool wrapCourseText)
    {
        if (resources is not null &&
            resources.TryGetCourseText(course, fields, availableWidth, wrapCourseText, out var cached))
        {
            return cached;
        }

        var source = TimetableCourseBlockContent.Build(course, fields);
        if (!wrapCourseText)
        {
            resources?.CacheCourseText(course, fields, availableWidth, wrapCourseText, source);
            return source;
        }
        if (resources is null)
            throw new InvalidOperationException("Wrapped course text requires export font resources.");

        var result = new List<CourseBlockTextLine>();
        foreach (var line in source)
        {
            var role = line.IsTitle ? request.Typography.CourseTitle : request.Typography.CourseDetail;
            var font = resources.Font(course: true, bold: line.IsTitle, role.FontSize);
            var paint = resources.Paint(request.Palette.PrimaryText);
            foreach (var visualLine in TimetableTextWrapper.Wrap(
                         line.Text,
                         availableWidth,
                         value => font.MeasureText(value, paint)))
            {
                result.Add(new CourseBlockTextLine(line.Field, visualLine, line.IsTitle));
            }
        }
        resources.CacheCourseText(course, fields, availableWidth, wrapCourseText, result);
        return result;
    }

    private static float CourseBlockTextWidth(float dayWidth, int conflictCount)
    {
        var count = Math.Max(1, conflictCount);
        var segmentWidth = (dayWidth - 8) / count;
        var blockWidth = Math.Max(8, segmentWidth - 4);
        return Math.Max(1, blockWidth - 17);
    }

    private static int MaximumConflictCount(
        IReadOnlyDictionary<int, IReadOnlyList<TimetableCourseBlock>> blocksByWeek)
    {
        var maximum = 1;
        foreach (var blocks in blocksByWeek.Values)
        {
            foreach (var block in blocks)
                maximum = Math.Max(maximum, block.ConflictCount);
        }
        return maximum;
    }

    private static int ToSafeLogicalDimension(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > MaximumPdfLogicalDimension)
        {
            var reported = !double.IsFinite(value) || value > long.MaxValue
                ? long.MaxValue
                : Math.Max(0, (long)Math.Ceiling(value));
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.VectorDimension,
                reported,
                MaximumPdfLogicalDimension,
                $"The requested timetable dimension exceeds the supported {MaximumPdfLogicalDimension} logical-pixel limit.");
        }

        return checked((int)Math.Ceiling(value));
    }

    private static TimetableExportWorkload AnalyzeWorkload(
        IReadOnlyDictionary<int, IReadOnlyList<TimetableCourseBlock>> blocksByWeek,
        CourseBlockFields fields)
    {
        var appearances = new Dictionary<CourseOffering, long>(ReferenceEqualityComparer.Instance);
        long blockCount = 0;
        var maximumConflicts = 1;
        foreach (var blocks in blocksByWeek.Values)
        {
            blockCount = checked(blockCount + blocks.Count);
            foreach (var block in blocks)
            {
                appearances.TryGetValue(block.Course, out var count);
                appearances[block.Course] = checked(count + 1);
                maximumConflicts = Math.Max(maximumConflicts, block.ConflictCount);
            }
        }

        long textCharacters = 0;
        foreach (var (course, count) in appearances)
        {
            var charactersPerAppearance = TimetableCourseBlockContent.Build(course, fields)
                .Aggregate(0L, (total, line) => checked(total + line.Text.Length));
            textCharacters = checked(textCharacters + checked(charactersPerAppearance * count));
        }

        return new TimetableExportWorkload(blockCount, textCharacters, maximumConflicts);
    }

    internal static void EnsureSafeWorkload(TimetableExportWorkload workload)
    {
        if (workload.CourseBlockCount > MaximumRenderedCourseBlocks)
        {
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.CourseBlocks,
                workload.CourseBlockCount,
                MaximumRenderedCourseBlocks,
                $"The export would render {workload.CourseBlockCount} course blocks, exceeding the " +
                $"{MaximumRenderedCourseBlocks} block work limit. Export a smaller week range.");
        }

        if (workload.TextCharacterCount > MaximumRenderedTextCharacters)
        {
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.TextCharacters,
                workload.TextCharacterCount,
                MaximumRenderedTextCharacters,
                $"The export would lay out {workload.TextCharacterCount} text characters, exceeding the " +
                $"{MaximumRenderedTextCharacters} character work limit. Export fewer weeks or course fields.");
        }

        if (workload.MaximumConflictCount > MaximumConflictLanes)
        {
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.ConflictLanes,
                workload.MaximumConflictCount,
                MaximumConflictLanes,
                $"One timetable cell would require {workload.MaximumConflictCount} conflict lanes, exceeding the " +
                $"{MaximumConflictLanes} lane layout limit. Export a less dense plan.");
        }
    }

    internal static (int Columns, int Rows) ResolveMatrixDimensions(int weekCount)
    {
        if (weekCount < 1)
            throw new ArgumentOutOfRangeException(nameof(weekCount), "At least one week is required.");
        if (weekCount == 1)
            return (1, 1);

        var bestColumns = weekCount;
        var bestRows = 1;
        var bestDifference = weekCount - 1;
        var bestEmptyCells = 0L;

        for (var columns = 1; columns < weekCount; columns++)
        {
            var rows = (int)(((long)weekCount + columns - 1) / columns);
            var difference = columns - rows;
            if (difference <= 0)
                continue;

            var emptyCells = ((long)columns * rows) - weekCount;
            if (!IsPreferredMatrixCandidate(
                    difference,
                    emptyCells,
                    columns,
                    bestDifference,
                    bestEmptyCells,
                    bestColumns))
                continue;

            bestColumns = columns;
            bestRows = rows;
            bestDifference = difference;
            bestEmptyCells = emptyCells;
        }

        return (bestColumns, bestRows);
    }

    internal static bool IsPreferredMatrixCandidate(
        int difference,
        long emptyCells,
        int columns,
        int currentDifference,
        long currentEmptyCells,
        int currentColumns) =>
        difference < currentDifference ||
        (difference == currentDifference && emptyCells < currentEmptyCells) ||
        (difference == currentDifference && emptyCells == currentEmptyCells && columns < currentColumns);

    private static TimetableExportMeasurement Measurement(RenderLayout layout, int scale)
    {
        int pixelWidth;
        int pixelHeight;
        checked
        {
            pixelWidth = layout.Width * scale;
            pixelHeight = layout.Height * scale;
        }
        return new TimetableExportMeasurement(
            layout.Width,
            layout.Height,
            pixelWidth,
            pixelHeight,
            layout.Weeks.Count,
            layout.WeekMetrics.PeriodCount,
            layout.Columns,
            layout.Rows);
    }

    internal static void EnsureSafeBitmap(TimetableExportMeasurement measurement)
    {
        if (measurement.PixelWidth <= 0 || measurement.PixelHeight <= 0)
            throw new InvalidOperationException("The requested PNG dimensions must be positive.");
        if (measurement.PixelWidth > MaximumPngDimension ||
            measurement.PixelHeight > MaximumPngDimension)
        {
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.SurfaceDimension,
                Math.Max(measurement.PixelWidth, measurement.PixelHeight),
                MaximumPngDimension,
                $"The requested PNG would be {measurement.PixelWidth} x {measurement.PixelHeight} pixels. " +
                "Choose a lower clarity or export a vector PDF.");
        }

        var bitmapBytes = checked(
            (long)measurement.PixelWidth * measurement.PixelHeight * EstimatedBytesPerPngPixel);
        var peakBytes = checked(
            (long)measurement.PixelWidth * measurement.PixelHeight * EstimatedPeakBytesPerPngPixel);
        if (bitmapBytes > MaximumPngBitmapBytes || peakBytes > MaximumPngWorkingSetBytes)
        {
            var requestedMiB = Math.Ceiling(peakBytes / (1024d * 1024d));
            var maximumMiB = MaximumPngWorkingSetBytes / (1024 * 1024);
            throw new TimetableExportLimitExceededException(
                TimetableExportLimitKind.BitmapMemory,
                peakBytes,
                MaximumPngWorkingSetBytes,
                $"The requested PNG would require approximately {requestedMiB:0} MiB of peak bitmap and encoder memory, " +
                $"which exceeds the {maximumMiB} MiB working-set safety limit. " +
                "Choose a lower clarity, fewer course fields, or export a vector PDF.");
        }
    }

    private static void EnsureWritableDestination(Stream destination)
    {
        if (!destination.CanWrite)
            throw new ArgumentException("The export destination stream must be writable.", nameof(destination));
    }

    private static void EnsureSafeVectorLayout(RenderLayout layout)
    {
        var largestDimension = Math.Max(layout.Width, layout.Height);
        if (largestDimension <= MaximumPdfLogicalDimension)
            return;
        throw new TimetableExportLimitExceededException(
            TimetableExportLimitKind.VectorDimension,
            largestDimension,
            MaximumPdfLogicalDimension,
            $"The requested PDF page dimension ({largestDimension}) exceeds the supported vector page limit " +
            $"of {MaximumPdfLogicalDimension} logical pixels. Export fewer weeks or fewer course fields.");
    }

    private static int ScaleFor(TimetableExportOptions options) =>
        options.FileFormat == ExportFileFormat.Png
            ? (int)(options.ImageClarity ?? throw new ArgumentException("PNG clarity is required.", nameof(options)))
            : 1;

    private static CourseBlockFields EffectiveFields(TimetableExportRequest request, TimetableExportOptions options)
    {
        var fields = options.CourseBlockFields;
        if (request.IncludeNotes)
            fields |= CourseBlockFields.Notes;
        return fields;
    }

    private static void ValidateRequest(
        TimetableExportRequest request,
        TimetableExportOptions options,
        bool requireRenderResources)
    {
        if (request.Semester is null)
            throw new ArgumentException("An export semester is required.", nameof(request));
        if (request.Plan is null)
            throw new ArgumentException("An export plan is required.", nameof(request));
        if (request.CourseLibrary is null)
            throw new ArgumentException("An export course library is required.", nameof(request));
        if (request.Text is null)
            throw new ArgumentException("Localized export text is required.", nameof(request));
        if (request.Fonts is null)
            throw new ArgumentException("Export font paths are required.", nameof(request));
        if (request.Palette is null)
            throw new ArgumentException("An export palette is required.", nameof(request));
        if (request.Typography is null)
            throw new ArgumentException("Export typography is required.", nameof(request));
        if (request.Semester.WeekCount is < 1 or > SemesterRules.MaxWeekCount)
        {
            throw new ArgumentException(
                $"The semester week count must be between 1 and {SemesterRules.MaxWeekCount}.",
                nameof(request));
        }
        if (!Enum.IsDefined(request.Semester.WeekStartDay))
            throw new ArgumentException("The semester week-start day is invalid.", nameof(request));
        if (request.Semester.PeriodSchedule is null ||
            request.Semester.PeriodSchedule.Count is < 1 or > PlannerDataLimits.MaxPeriodsPerSemester ||
            request.Semester.PeriodSchedule.Any(period => period is null) ||
            request.Semester.PeriodSchedule
                .Select(period => period.Period)
                .Distinct()
                .Count() != request.Semester.PeriodSchedule.Count)
        {
            throw new ArgumentException("The semester period schedule is invalid.", nameof(request));
        }
        if (request.Plan.Snapshots is null || request.Plan.Snapshots.Any(snapshot => snapshot is null))
            throw new ArgumentException("The export plan contains an invalid course reference.", nameof(request));
        if (request.CourseLibrary.Any(course => course is null || course.MeetingTimes is null))
            throw new ArgumentException("The export course library contains an invalid course.", nameof(request));
        if (request.Differences?.Any(difference => difference is null || difference.Slot is null) == true)
            throw new ArgumentException("The export comparison contains an invalid difference.", nameof(request));

        ValidateTypography(request.Typography);

        TimetableExportOptionsValidator.ValidateAndThrow(options, request.Semester);
        if (options.ContentKind == ExportContentKind.CurrentWeek &&
            (request.Week < 1 || request.Week > request.Semester.WeekCount))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The current week is outside the semester.");
        }

        if (!requireRenderResources)
            return;

        EnsureLocalizedText(request, options.ContentKind);
        EnsureFontFiles(request.Fonts);
    }

    private static void ValidateTypography(TimetableExportTypography typography)
    {
        foreach (var role in new[]
                 {
                     typography.Title,
                     typography.Subtitle,
                     typography.Body,
                     typography.BodyStrong,
                     typography.Caption,
                     typography.CourseTitle,
                     typography.CourseDetail
                 })
        {
            if (!float.IsFinite(role.FontSize) || role.FontSize <= 0 ||
                role.FontSize > MaximumFontSize ||
                !float.IsFinite(role.LineHeight) || role.LineHeight < role.FontSize ||
                role.LineHeight > MaximumLineHeight)
            {
                throw new ArgumentException("Export typography contains invalid font metrics.", nameof(typography));
            }
        }
    }

    private static void EnsureLocalizedText(TimetableExportRequest request, ExportContentKind content)
    {
        if (request.Text.WeekdayShortNames is null ||
            request.Text.WeekdayShortNames.Count != 7 ||
            string.IsNullOrWhiteSpace(request.Text.WeekHeadingFormat) ||
            string.IsNullOrWhiteSpace(request.Text.BeforeSemesterText) ||
            string.IsNullOrWhiteSpace(request.Text.AfterSemesterText))
        {
            throw new InvalidOperationException("Localized export text is incomplete.");
        }

        var subtitle = content switch
        {
            ExportContentKind.CurrentWeek => request.Text.WeekSubtitle,
            ExportContentKind.WeekRange => request.Text.WeekRangeSubtitle,
            ExportContentKind.DetailedSemester => request.Text.DetailedSemesterSubtitle,
            _ => throw new ArgumentOutOfRangeException(nameof(content), content, null)
        };
        if (string.IsNullOrWhiteSpace(subtitle))
            throw new InvalidOperationException("The localized export subtitle is missing.");

        try
        {
            _ = string.Format(CultureInfo.CurrentCulture, request.Text.WeekHeadingFormat, 1);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The localized week-heading format is invalid.", nameof(request), exception);
        }
    }

    private static void EnsureFontFiles(TimetableExportFonts fonts)
    {
        foreach (var path in new[]
                 {
                     fonts.RegularFilePath,
                     fonts.BoldFilePath,
                     fonts.CourseBlockRegularFilePath,
                     fonts.CourseBlockBoldFilePath
                 })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Export font file was not found.", path);
        }

        if (!string.IsNullOrWhiteSpace(fonts.SemiboldFilePath) && !File.Exists(fonts.SemiboldFilePath))
            throw new FileNotFoundException("Export semibold font file was not found.", fonts.SemiboldFilePath);
    }

    private static TimetableExportColor DifferenceFill(SlotDifference? difference, TimetableExportPalette palette) =>
        difference?.Kind switch
        {
            DifferenceKind.Added => palette.DifferenceAddedBackground,
            DifferenceKind.Removed => palette.DifferenceRemovedBackground,
            DifferenceKind.Replaced => palette.DifferenceModifiedBackground,
            _ => palette.CourseBlockBackground
        };

    private static TimetableExportColor CapacityColor(CourseOffering course, TimetableExportPalette palette)
    {
        if (course.EnrolledCount is not { } enrolled || course.Capacity is not { } capacity || capacity <= 0)
            return palette.SecondaryText;
        if (enrolled >= capacity)
            return palette.StatusCritical;
        return enrolled / (double)capacity >= 0.9 ? palette.StatusCaution : palette.SecondaryText;
    }

    private static TimetableExportColor ParseCourseColor(string value)
    {
        var (r, g, b) = CourseColorService.ParseRgb(value);
        return new TimetableExportColor(255, (byte)r, (byte)g, (byte)b);
    }

    private static string WeekdayText(TimetableExportRequest request, int weekday) =>
        request.Text.WeekdayShortNames[weekday - 1];

    private static void DrawText(
        SKCanvas canvas,
        string value,
        float x,
        float y,
        float size,
        bool bold,
        bool courseFont,
        TimetableExportColor color,
        RenderResources resources)
    {
        value = TextRules.SanitizeUtf16(value);
        if (string.IsNullOrEmpty(value))
            return;
        canvas.DrawText(value, x, y, SKTextAlign.Left, resources.Font(courseFont, bold, size), resources.Paint(color));
    }

    private static void DrawFittedText(
        SKCanvas canvas,
        string value,
        float x,
        float y,
        float width,
        float size,
        bool bold,
        bool courseFont,
        TimetableExportColor color,
        RenderResources resources)
    {
        value = TextRules.SanitizeUtf16(value);
        if (string.IsNullOrEmpty(value) || width <= 0)
            return;
        var font = resources.Font(courseFont, bold, size);
        var paint = resources.Paint(color);
        canvas.DrawText(FitText(value, width, font, paint), x, y, SKTextAlign.Left, font, paint);
    }

    private static string FitText(string value, float width, SKFont font, SKPaint paint)
    {
        value = TextRules.SanitizeUtf16(value);
        if (font.MeasureText(value, paint) <= width)
            return value;

        const string ellipsis = "…";
        var elementStarts = StringInfo.ParseCombiningCharacters(value);
        var low = 0;
        var high = elementStarts.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = middle >= elementStarts.Length ? value.Length : elementStarts[middle];
            var candidate = value[..end] + ellipsis;
            if (font.MeasureText(candidate, paint) <= width)
                low = middle;
            else
                high = middle - 1;
        }

        if (low == 0)
            return font.MeasureText(ellipsis, paint) <= width ? ellipsis : "";
        var length = low >= elementStarts.Length ? value.Length : elementStarts[low];
        return value[..length] + ellipsis;
    }

    private static SKColor ToSkColor(TimetableExportColor color) =>
        new(color.R, color.G, color.B, color.A);

    private static void ExportPathAtomically(string path, Action<Stream> export)
    {
        var destinationPath = Path.GetFullPath(path);
        EnsureParentDirectory(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("The export destination has no parent directory.");
        var temporaryPath = Path.Combine(
            directory,
            $".course-planner-export-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.SequentialScan))
            {
                export(stream);
                stream.Flush(flushToDisk: true);
            }

            CommitTemporaryFile(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void CommitTemporaryFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporaryPath, destinationPath);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private sealed record WeekGridMetrics(
        float LeftWidth,
        float DayWidth,
        float HeaderHeight,
        float PeriodHeight,
        int PeriodCount)
    {
        public float Width => LeftWidth + (DayWidth * 7);
        public float Height => HeaderHeight + (PeriodHeight * PeriodCount);
    }

    private sealed record RenderLayout(
        ExportContentKind ContentKind,
        IReadOnlyList<int> Weeks,
        CourseBlockFields Fields,
        int Width,
        int Height,
        int Columns,
        int Rows,
        float DocumentHeaderHeight,
        float PanelHeaderHeight,
        float PanelWidth,
        float PanelHeight,
        WeekGridMetrics WeekMetrics,
        IReadOnlyDictionary<int, IReadOnlyList<TimetableCourseBlock>> BlocksByWeek,
        TimetableExportWorkload Workload);

    private sealed class RenderResources : IDisposable
    {
        private readonly Dictionary<string, SKTypeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(bool Course, bool Bold, float Size), SKFont> _fonts = new();
        private readonly Dictionary<(TimetableExportColor Color, bool Stroke, float Width), SKPaint> _paints = new();
        private readonly Dictionary<
            (CourseOffering Course, CourseBlockFields Fields, float Width, bool Wrap),
            IReadOnlyList<CourseBlockTextLine>> _courseText = new();
        private readonly TimetableExportFonts _fontPaths;

        public RenderResources(TimetableExportFonts fontPaths)
        {
            _fontPaths = fontPaths;
        }

        public SKFont Font(bool course, bool bold, float size)
        {
            var key = (course, bold, size);
            if (_fonts.TryGetValue(key, out var font))
                return font;

            var path = (course, bold) switch
            {
                (true, true) => _fontPaths.CourseBlockBoldFilePath,
                (true, false) => _fontPaths.CourseBlockRegularFilePath,
                (false, true) when !string.IsNullOrWhiteSpace(_fontPaths.SemiboldFilePath) =>
                    _fontPaths.SemiboldFilePath,
                (false, true) => _fontPaths.BoldFilePath,
                _ => _fontPaths.RegularFilePath
            };
            if (!_typefaces.TryGetValue(path, out var typeface))
            {
                typeface = SKTypeface.FromFile(path)
                           ?? throw new InvalidDataException($"Export font file cannot be loaded: {path}");
                _typefaces.Add(path, typeface);
            }

            font = new SKFont(typeface, size);
            _fonts.Add(key, font);
            return font;
        }

        public SKPaint Paint(TimetableExportColor color, bool stroke = false, float strokeWidth = 0)
        {
            var key = (color, stroke, strokeWidth);
            if (_paints.TryGetValue(key, out var paint))
                return paint;
            paint = new SKPaint
            {
                Color = ToSkColor(color),
                IsAntialias = true,
                Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                StrokeWidth = strokeWidth
            };
            _paints.Add(key, paint);
            return paint;
        }

        public bool TryGetCourseText(
            CourseOffering course,
            CourseBlockFields fields,
            float width,
            bool wrap,
            out IReadOnlyList<CourseBlockTextLine> lines) =>
            _courseText.TryGetValue((course, fields, width, wrap), out lines!);

        public void CacheCourseText(
            CourseOffering course,
            CourseBlockFields fields,
            float width,
            bool wrap,
            IReadOnlyList<CourseBlockTextLine> lines) =>
            _courseText.TryAdd((course, fields, width, wrap), lines);

        public void Dispose()
        {
            foreach (var font in _fonts.Values)
                font.Dispose();
            foreach (var paint in _paints.Values)
                paint.Dispose();
            foreach (var typeface in _typefaces.Values)
                typeface.Dispose();
        }
    }
}

internal readonly record struct TimetableExportWorkload(
    long CourseBlockCount,
    long TextCharacterCount,
    int MaximumConflictCount);

internal static class TimetableTextWrapper
{
    public static IReadOnlyList<string> Wrap(
        string value,
        float maximumWidth,
        Func<string, float> measure)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(measure);
        if (!float.IsFinite(maximumWidth) || maximumWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));

        var normalized = TextRules.SanitizeUtf16(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var result = new List<string>();
        foreach (var paragraph in normalized.Split('\n', StringSplitOptions.None))
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            WrapParagraph(paragraph, maximumWidth, measure, result);
        }

        return result;
    }

    private static void WrapParagraph(
        string paragraph,
        float maximumWidth,
        Func<string, float> measure,
        ICollection<string> result)
    {
        var elementStarts = StringInfo.ParseCombiningCharacters(paragraph);
        var start = 0;
        while (start < elementStarts.Length)
        {
            var low = start + 1;
            var high = elementStarts.Length;
            var best = start;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = Slice(paragraph, elementStarts, start, middle);
                var width = measure(candidate);
                if (float.IsFinite(width) && width <= maximumWidth)
                {
                    best = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (best == start)
                best = start + 1;

            var end = best;
            if (end < elementStarts.Length)
            {
                for (var index = end - 1; index > start; index--)
                {
                    if (!string.IsNullOrWhiteSpace(Slice(paragraph, elementStarts, index, index + 1)))
                        continue;
                    end = index + 1;
                    break;
                }
            }

            result.Add(Slice(paragraph, elementStarts, start, end));
            start = end;
        }
    }

    private static string Slice(string value, int[] elementStarts, int start, int end)
    {
        var charStart = elementStarts[start];
        var charEnd = end >= elementStarts.Length ? value.Length : elementStarts[end];
        return value[charStart..charEnd];
    }
}

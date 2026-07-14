using System.Text;
using CoursePlanner.Core;
using CoursePlanner.Export;
using SkiaSharp;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class TimetableExportRenderingTests
{
    [Fact]
    public void PublicSurfaceDoesNotExposeLegacyWeekOrScaleCompatibilityWrappers()
    {
        var publicMethods = typeof(TimetableExportService)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("MeasureWeek", publicMethods);
        Assert.DoesNotContain("ExportWeekPng", publicMethods);
        Assert.DoesNotContain("ExportWeekPdf", publicMethods);
    }

    [Fact]
    public void OptionsRequireCourseNameAndKeepClarityPngOnly()
    {
        var semester = TestDocumentFactory.CreatePopulated().Semesters[0];
        var missingName = new TimetableExportOptions
        {
            CourseBlockFields = CourseBlockFields.Teacher,
            FileFormat = ExportFileFormat.Png,
            ImageClarity = ImageClarity.High
        };
        var pdfWithClarity = new TimetableExportOptions
        {
            CourseBlockFields = CourseBlockFields.Default,
            FileFormat = ExportFileFormat.Pdf,
            ImageClarity = ImageClarity.Standard
        };

        Assert.Contains(TimetableExportOptionsValidator.Validate(missingName, semester), error => error.Contains("CourseName", StringComparison.Ordinal));
        Assert.Contains(TimetableExportOptionsValidator.Validate(pdfWithClarity, semester), error => error.Contains("vector", StringComparison.OrdinalIgnoreCase));

        pdfWithClarity.ImageClarity = null;
        Assert.Empty(TimetableExportOptionsValidator.Validate(pdfWithClarity, semester));
    }

    [Fact]
    public void CourseBlockFieldsFollowUiOrderAndIncludeEverySelectedValue()
    {
        var course = TestDocumentFactory.CreatePopulated().CourseLibrary[0];

        var lines = TimetableCourseBlockContent.Build(course, CourseBlockFields.All);

        Assert.Equal(
            new[]
            {
                CourseBlockFields.CourseName,
                CourseBlockFields.Teacher,
                CourseBlockFields.Location,
                CourseBlockFields.Capacity,
                CourseBlockFields.Credits,
                CourseBlockFields.CourseGroupType,
                CourseBlockFields.StudyType,
                CourseBlockFields.Labels,
                CourseBlockFields.Notes
            },
            lines.Select(line => line.Field));
        Assert.True(lines[0].IsTitle);
        Assert.All(lines.Skip(1), line => Assert.False(line.IsTitle));
    }

    [Fact]
    public void PngUsesInjectedThemePalette()
    {
        var document = SmallDocument();
        var palette = new TimetableExportPalette
        {
            PageBackground = TimetableExportColor.FromHex("#123456"),
            HeaderBackground = TimetableExportColor.FromHex("#234567"),
            Divider = TimetableExportColor.FromHex("#345678"),
            PrimaryText = TimetableExportColor.FromHex("#FFFFFF"),
            SecondaryText = TimetableExportColor.FromHex("#DDDDDD"),
            CourseBlockBackground = TimetableExportColor.FromHex("#456789"),
            MatrixCardBackground = TimetableExportColor.FromHex("#56789A"),
            DifferenceAddedBackground = TimetableExportColor.FromHex("#226644"),
            DifferenceRemovedBackground = TimetableExportColor.FromHex("#662244"),
            DifferenceModifiedBackground = TimetableExportColor.FromHex("#665522"),
            StatusCritical = TimetableExportColor.FromHex("#FF7777"),
            StatusCaution = TimetableExportColor.FromHex("#FFCC66"),
            StatusCurrent = TimetableExportColor.FromHex("#66CCBB"),
            OutsideSemesterOverlay = TimetableExportColor.FromHex("#70345678")
        };
        var request = Request(document, ExportContentKind.CurrentWeek, ExportFileFormat.Png, ImageClarity.Standard);
        request.Palette = palette;
        using var output = new MemoryStream();

        TimetableExportService.ExportPng(request, output);

        output.Position = 0;
        using var bitmap = SKBitmap.Decode(output);
        var pixel = bitmap.GetPixel(0, 0);
        Assert.Equal((byte)0x12, pixel.Red);
        Assert.Equal((byte)0x34, pixel.Green);
        Assert.Equal((byte)0x56, pixel.Blue);
    }

    [Fact]
    public void DetailedSemesterMeasuresEveryWeekAndEveryPeriodUsingDynamicColumns()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var request = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);
        request.Options.CourseBlockFields = CourseBlockFields.All;

        var measurement = TimetableExportService.Measure(request);

        Assert.Equal(document.Semesters[0].WeekCount, measurement.RenderedWeekCount);
        Assert.Equal(document.Semesters[0].PeriodSchedule.Count, measurement.RenderedPeriodCount);
        Assert.Equal(5, measurement.MatrixColumns);
        Assert.Equal(4, measurement.MatrixRows);
    }

    [Fact]
    public void WeekRangeMeasuresOnlyTheRequestedWeeksUsingTheSameDynamicColumns()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var request = Request(document, ExportContentKind.WeekRange, ExportFileFormat.Pdf, clarity: null);
        request.Options.StartWeek = 3;
        request.Options.EndWeek = 7;

        var measurement = TimetableExportService.Measure(request);

        Assert.Equal(5, measurement.RenderedWeekCount);
        Assert.Equal(3, measurement.MatrixColumns);
        Assert.Equal(2, measurement.MatrixRows);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 2)]
    [InlineData(7, 4, 2)]
    [InlineData(9, 4, 3)]
    [InlineData(16, 5, 4)]
    public void DynamicMatrixColumnsMinimizeThePositiveColumnRowDifference(
        int weeks,
        int expectedColumns,
        int expectedRows)
    {
        var (columns, rows) = TimetableExportService.ResolveMatrixDimensions(weeks);

        Assert.Equal(expectedColumns, columns);
        Assert.Equal(expectedRows, rows);
        if (weeks == 1)
        {
            Assert.Equal(rows, columns);
            return;
        }

        Assert.True(columns > rows);
        var selectedDifference = columns - rows;
        var competingPositiveDifferences = Enumerable.Range(1, weeks)
            .Select(candidateColumns =>
            {
                var candidateRows = (int)Math.Ceiling(weeks / (double)candidateColumns);
                return candidateColumns - candidateRows;
            })
            .Where(difference => difference > 0);
        Assert.Equal(competingPositiveDifferences.Min(), selectedDifference);
    }

    [Fact]
    public void DynamicMatrixTieBreakPrefersFewerEmptyCellsThenFewerColumns()
    {
        Assert.True(TimetableExportService.IsPreferredMatrixCandidate(
            difference: 1,
            emptyCells: 0,
            columns: 5,
            currentDifference: 1,
            currentEmptyCells: 2,
            currentColumns: 4));
        Assert.True(TimetableExportService.IsPreferredMatrixCandidate(
            difference: 1,
            emptyCells: 0,
            columns: 4,
            currentDifference: 1,
            currentEmptyCells: 0,
            currentColumns: 5));
        Assert.False(TimetableExportService.IsPreferredMatrixCandidate(
            difference: 1,
            emptyCells: 2,
            columns: 4,
            currentDifference: 1,
            currentEmptyCells: 0,
            currentColumns: 5));
    }

    [Theory]
    [InlineData(ExportContentKind.WeekRange, 7)]
    [InlineData(ExportContentKind.DetailedSemester, 16)]
    public void PngAndPdfUseTheSameDynamicMatrixLayout(ExportContentKind content, int weeks)
    {
        var document = TestDocumentFactory.CreatePopulated();
        var semester = document.Semesters[0];
        semester.WeekCount = weeks;
        semester.EndDate = SemesterRules.CalculateEndDate(
            semester.StartDate,
            semester.WeekCount,
            semester.WeekStartDay);
        var png = Request(document, content, ExportFileFormat.Png, ImageClarity.Standard);
        png.Options.StartWeek = 1;
        png.Options.EndWeek = weeks;
        var pdf = Request(document, content, ExportFileFormat.Pdf, clarity: null);
        pdf.Options.StartWeek = 1;
        pdf.Options.EndWeek = weeks;

        var pngMeasurement = TimetableExportService.Measure(png);
        var pdfMeasurement = TimetableExportService.Measure(pdf);

        Assert.Equal(pdfMeasurement.LogicalWidth, pngMeasurement.LogicalWidth);
        Assert.Equal(pdfMeasurement.LogicalHeight, pngMeasurement.LogicalHeight);
        Assert.Equal(pdfMeasurement.RenderedWeekCount, pngMeasurement.RenderedWeekCount);
        Assert.Equal(pdfMeasurement.MatrixColumns, pngMeasurement.MatrixColumns);
        Assert.Equal(pdfMeasurement.MatrixRows, pngMeasurement.MatrixRows);
    }

    [Fact]
    public void SemesterOverviewIsNotAnExportContentOrPublicServiceApi()
    {
        Assert.DoesNotContain(
            Enum.GetNames<ExportContentKind>(),
            name => name.Contains("SemesterOverview", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(TimetableExportService).GetMethods(),
            method => method.Name.Contains("SemesterOverview", StringComparison.Ordinal));

        var staleOverviewValue = new TimetableExportOptions
        {
            ContentKind = (ExportContentKind)2,
            FileFormat = ExportFileFormat.Png,
            ImageClarity = ImageClarity.Standard,
            CourseBlockFields = CourseBlockFields.CourseName
        };
        Assert.Contains(
            TimetableExportOptionsValidator.Validate(staleOverviewValue),
            error => error.Contains("content kind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageClarityChangesOnlyPixelDimensions()
    {
        var document = SmallDocument();
        var request = Request(document, ExportContentKind.CurrentWeek, ExportFileFormat.Png, ImageClarity.Standard);
        var standard = TimetableExportService.Measure(request);
        request.Options.ImageClarity = ImageClarity.High;
        var high = TimetableExportService.Measure(request);
        request.Options.ImageClarity = ImageClarity.Ultra;
        var ultra = TimetableExportService.Measure(request);
        request.Options.ImageClarity = ImageClarity.Extreme;
        var extreme = TimetableExportService.Measure(request);
        request.Options.ImageClarity = ImageClarity.Maximum;
        var maximum = TimetableExportService.Measure(request);

        Assert.Equal(standard.LogicalWidth, high.LogicalWidth);
        Assert.Equal(standard.LogicalHeight, high.LogicalHeight);
        Assert.Equal(standard.LogicalWidth, ultra.LogicalWidth);
        Assert.Equal(standard.LogicalHeight, ultra.LogicalHeight);
        Assert.Equal(standard.LogicalWidth, extreme.LogicalWidth);
        Assert.Equal(standard.LogicalHeight, extreme.LogicalHeight);
        Assert.Equal(standard.LogicalWidth, maximum.LogicalWidth);
        Assert.Equal(standard.LogicalHeight, maximum.LogicalHeight);
        Assert.Equal(standard.PixelWidth * 2, high.PixelWidth);
        Assert.Equal(standard.PixelHeight * 2, high.PixelHeight);
        Assert.Equal(standard.PixelWidth * 3, ultra.PixelWidth);
        Assert.Equal(standard.PixelHeight * 3, ultra.PixelHeight);
        Assert.Equal(standard.PixelWidth * 4, extreme.PixelWidth);
        Assert.Equal(standard.PixelHeight * 4, extreme.PixelHeight);
        Assert.Equal(standard.PixelWidth * 5, maximum.PixelWidth);
        Assert.Equal(standard.PixelHeight * 5, maximum.PixelHeight);

        request.Options.ImageClarity = ImageClarity.High;
        using var output = new MemoryStream();
        TimetableExportService.ExportPng(request, output);
        output.Position = 0;
        using var bitmap = SKBitmap.Decode(output);
        Assert.Equal(high.PixelWidth, bitmap.Width);
        Assert.Equal(high.PixelHeight, bitmap.Height);
    }

    [Fact]
    public void RequestTypographyControlsLogicalCourseBlockLayout()
    {
        var document = SmallDocument();
        var request = Request(document, ExportContentKind.CurrentWeek, ExportFileFormat.Png, ImageClarity.Standard);
        var baseline = TimetableExportService.Measure(request);
        request.Typography = new TimetableExportTypography
        {
            Title = new TimetableExportTextMetrics(28, 41),
            Subtitle = new TimetableExportTextMetrics(20, 29),
            Body = new TimetableExportTextMetrics(14, 21),
            BodyStrong = new TimetableExportTextMetrics(14, 21),
            Caption = new TimetableExportTextMetrics(12, 18),
            CourseTitle = new TimetableExportTextMetrics(13, 20),
            CourseDetail = new TimetableExportTextMetrics(12, 30)
        };

        var enlarged = TimetableExportService.Measure(request);

        Assert.True(
            enlarged.LogicalHeight > baseline.LogicalHeight,
            $"Expected typography to increase height, baseline={baseline.LogicalHeight}, enlarged={enlarged.LogicalHeight}.");
        Assert.Equal(baseline.LogicalWidth, enlarged.LogicalWidth);
    }

    [Fact]
    public void DetailedSemesterWrapsExplicitBreaksWithoutDroppingTextAndExpandsRows()
    {
        const string value = "Alpha beta gamma\r\n第二行内容\r\n\r\nTail";

        var wrapped = TimetableTextWrapper.Wrap(value, 6, text => text.Length);

        Assert.Equal(new[] { "Alpha ", "beta ", "gamma", "第二行内容", "", "Tail" }, wrapped);
        Assert.DoesNotContain(wrapped, line => line.Contains('…'));

        var document = SmallDocument();
        var course = document.CourseLibrary[0];
        course.CourseName = "A deliberately long course title that must remain complete";
        course.Teacher = "Professor With A Deliberately Long Display Name";
        course.Location = "A building and room description that needs another visual line";
        course.CourseGroupType = "A long course group classification";
        course.StudyType = "A long study type classification";
        course.Labels = new List<string> { "First detailed label", "Second detailed label" };
        course.Notes = "First explicit note line with enough words to wrap naturally.\n" +
                       "Second explicit note line whose final token is COMPLETE_TAIL.";

        var nameOnly = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);
        nameOnly.Options.CourseBlockFields = CourseBlockFields.CourseName;
        var nameOnlyMeasurement = TimetableExportService.Measure(nameOnly);

        var allFields = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);
        allFields.Options.CourseBlockFields = CourseBlockFields.All;
        var allFieldsMeasurement = TimetableExportService.Measure(allFields);

        Assert.True(allFieldsMeasurement.LogicalHeight > nameOnlyMeasurement.LogicalHeight);
        using var output = new MemoryStream();
        TimetableExportService.ExportPdf(allFields, output);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(output.ToArray(), 0, 4));
    }

    [Fact]
    public void FailedPathExportLeavesExistingFileUntouchedAndRemovesTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "existing.png");
            var original = Encoding.UTF8.GetBytes("existing export must survive");
            File.WriteAllBytes(path, original);
            var request = Request(
                SmallDocument(),
                ExportContentKind.CurrentWeek,
                ExportFileFormat.Png,
                ImageClarity.Standard);
            request.Fonts.RegularFilePath = Path.Combine(directory, "missing-font.ttf");

            Assert.Throws<FileNotFoundException>(() => TimetableExportService.ExportPng(request, path));

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulPathExportAtomicallyReplacesExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "existing.png");
            File.WriteAllText(path, "old content");
            var request = Request(
                SmallDocument(),
                ExportContentKind.CurrentWeek,
                ExportFileFormat.Png,
                ImageClarity.Standard);

            TimetableExportService.ExportPng(request, path);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MaximumLengthDestinationNameDoesNotMakeTheTemporaryNameInvalid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var fileName = new string('a', WindowsFileNameRules.MaxComponentLength - ".png".Length) + ".png";
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "old content");
            var request = Request(
                SmallDocument(),
                ExportContentKind.CurrentWeek,
                ExportFileFormat.Png,
                ImageClarity.Standard);

            TimetableExportService.ExportPng(request, path);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(path).Take(4).ToArray());
            Assert.Equal([path], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LockedDestinationCommitFailureKeepsOriginalAndRemovesRenderedTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "locked.png");
            var original = Encoding.UTF8.GetBytes("locked original");
            File.WriteAllBytes(path, original);
            var request = Request(
                SmallDocument(),
                ExportContentKind.CurrentWeek,
                ExportFileFormat.Png,
                ImageClarity.Standard);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => TimetableExportService.ExportPng(request, path));
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PngSafetyBudgetsBothBitmapAndEncoderPeakMemory()
    {
        var safe = new TimetableExportMeasurement(1, 1, 8_000, 8_000, 1, 1, 1, 1);
        var unsafeMeasurement = new TimetableExportMeasurement(1, 1, 9_000, 9_000, 1, 1, 1, 1);

        TimetableExportService.EnsureSafeBitmap(safe);
        var exception = Assert.Throws<TimetableExportLimitExceededException>(() =>
            TimetableExportService.EnsureSafeBitmap(unsafeMeasurement));

        Assert.Equal(4, TimetableExportService.EstimatedBytesPerPngPixel);
        Assert.Equal(8, TimetableExportService.EstimatedPeakBytesPerPngPixel);
        Assert.Equal(256L * 1024 * 1024, TimetableExportService.MaximumPngBitmapBytes);
        Assert.Equal(512L * 1024 * 1024, TimetableExportService.MaximumPngWorkingSetBytes);
        Assert.Equal(TimetableExportLimitKind.BitmapMemory, exception.Kind);
        Assert.Contains("512 MiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRejectsSemesterWeekCountsAboveTheDomainLimitBeforeLayoutExpansion()
    {
        var document = SmallDocument();
        document.Semesters[0].WeekCount = SemesterRules.MaxWeekCount + 1;
        var request = Request(
            document,
            ExportContentKind.DetailedSemester,
            ExportFileFormat.Pdf,
            clarity: null);

        var exception = Assert.Throws<ArgumentException>(() => TimetableExportService.Measure(request));

        Assert.Contains(SemesterRules.MaxWeekCount.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30_001, 0, 1, TimetableExportLimitKind.CourseBlocks)]
    [InlineData(1, 8_000_001, 1, TimetableExportLimitKind.TextCharacters)]
    [InlineData(1, 1, 65, TimetableExportLimitKind.ConflictLanes)]
    public void ExportWorkloadBudgetsFailWithTypedDiagnostics(
        long blocks,
        long characters,
        int conflicts,
        TimetableExportLimitKind expectedKind)
    {
        var exception = Assert.Throws<TimetableExportLimitExceededException>(() =>
            TimetableExportService.EnsureSafeWorkload(
                new TimetableExportWorkload(blocks, characters, conflicts)));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.True(exception.Actual > exception.Maximum);
    }

    [Fact]
    public void MaximumMeetingRowsAcrossSixtyWeeksAreRejectedBeforeRenderingWorkExplodes()
    {
        var document = SmallDocument();
        var semester = document.Semesters[0];
        semester.WeekCount = SemesterRules.MaxWeekCount;
        semester.EndDate = SemesterRules.CalculateEndDate(
            semester.StartDate,
            semester.WeekCount,
            semester.WeekStartDay);
        document.CourseLibrary.Clear();
        document.Plans[0].Snapshots.Clear();
        for (var index = 0; index < PlannerDataLimits.MaxMeetingRowsPerPlan; index++)
        {
            var course = new CourseOffering
            {
                OfferingId = $"dense-{index:D4}",
                SemesterId = semester.SemesterId,
                CourseName = $"Dense {index}",
                Color = "#336699",
                MeetingTimes =
                {
                    new MeetingTime
                    {
                        Weekday = 1,
                        StartPeriod = 1,
                        EndPeriod = 1,
                        Weeks = "1-60"
                    }
                }
            };
            document.CourseLibrary.Add(course);
            document.Plans[0].Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = course.OfferingId });
        }
        var request = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = Assert.Throws<TimetableExportLimitExceededException>(() =>
            TimetableExportService.Measure(request));

        stopwatch.Stop();
        Assert.Equal(TimetableExportLimitKind.CourseBlocks, exception.Kind);
        Assert.Equal(
            (long)PlannerDataLimits.MaxMeetingRowsPerPlan * SemesterRules.MaxWeekCount,
            exception.Actual);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void PdfRejectsAnUnrepresentablyWideConflictMatrixBeforeCreatingSkiaDocument()
    {
        var document = SmallDocument();
        var semester = document.Semesters[0];
        semester.WeekCount = SemesterRules.MaxWeekCount;
        semester.EndDate = SemesterRules.CalculateEndDate(
            semester.StartDate,
            semester.WeekCount,
            semester.WeekStartDay);
        document.CourseLibrary.Clear();
        document.Plans[0].Snapshots.Clear();
        for (var index = 0; index < 12; index++)
        {
            var course = new CourseOffering
            {
                OfferingId = $"wide-{index:D2}",
                SemesterId = semester.SemesterId,
                CourseName = $"Wide {index}",
                Color = "#336699",
                MeetingTimes =
                {
                    new MeetingTime { Weekday = 1, StartPeriod = 1, EndPeriod = 1, Weeks = "1" }
                }
            };
            document.CourseLibrary.Add(course);
            document.Plans[0].Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = course.OfferingId });
        }
        var request = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);

        var exception = Assert.Throws<TimetableExportLimitExceededException>(() =>
            TimetableExportService.Measure(request));

        Assert.Equal(TimetableExportLimitKind.VectorDimension, exception.Kind);
    }

    [Theory]
    [InlineData(ExportFileFormat.Png)]
    [InlineData(ExportFileFormat.Pdf)]
    public void StreamExportSupportsNonSeekableWritableDestinations(ExportFileFormat format)
    {
        var request = Request(
            SmallDocument(),
            ExportContentKind.CurrentWeek,
            format,
            format == ExportFileFormat.Png ? ImageClarity.Standard : null);
        using var backing = new MemoryStream();
        using var output = new NonSeekableWriteStream(backing);

        if (format == ExportFileFormat.Png)
            TimetableExportService.ExportPng(request, output);
        else
            TimetableExportService.ExportPdf(request, output);

        var bytes = backing.ToArray();
        Assert.True(bytes.Length > 4);
        Assert.Equal(
            format == ExportFileFormat.Png ? new byte[] { 0x89, 0x50, 0x4E, 0x47 } : "%PDF"u8.ToArray(),
            bytes.Take(4).ToArray());
    }

    [Fact]
    public void ThrowingDestinationDoesNotPoisonLaterExportsOrGetDisposed()
    {
        var request = Request(
            SmallDocument(),
            ExportContentKind.CurrentWeek,
            ExportFileFormat.Png,
            ImageClarity.Standard);
        using var failing = new ThrowingWriteStream();

        Assert.Throws<IOException>(() => TimetableExportService.ExportPng(request, failing));
        Assert.False(failing.WasDisposed);

        using var healthy = new MemoryStream();
        TimetableExportService.ExportPng(request, healthy);
        Assert.True(healthy.Length > 4);
    }

    [Fact]
    public void NonSeekablePdfCopyFailurePreservesTheWriteExceptionAndCleansItsBuffer()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-pdf-buffer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var temporaryPath = Path.Combine(directory, "buffer.tmp");
            var request = Request(
                SmallDocument(),
                ExportContentKind.CurrentWeek,
                ExportFileFormat.Pdf,
                clarity: null);
            using var destination = new ThrowingWriteStream(bytesBeforeThrow: 32);

            var exception = Assert.Throws<IOException>(() =>
                TimetableExportService.ExportPdfThroughSeekableBuffer(
                    request,
                    request.Options,
                    destination,
                    temporaryPath));

            Assert.Equal("Injected write failure.", exception.Message);
            Assert.Equal(32, destination.BytesWritten);
            Assert.False(destination.WasDisposed);
            Assert.False(File.Exists(temporaryPath));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnavailablePdfTemporaryDirectoryFailsBeforeTouchingCallerDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-pdf-buffer-{Guid.NewGuid():N}");
        var temporaryPath = Path.Combine(directory, "missing", "buffer.tmp");
        var request = Request(
            SmallDocument(),
            ExportContentKind.CurrentWeek,
            ExportFileFormat.Pdf,
            clarity: null);
        using var destination = new ThrowingWriteStream(bytesBeforeThrow: 32);

        Assert.Throws<DirectoryNotFoundException>(() =>
            TimetableExportService.ExportPdfThroughSeekableBuffer(
                request,
                request.Options,
                destination,
                temporaryPath));

        Assert.Equal(0, destination.BytesWritten);
        Assert.False(destination.WasDisposed);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void NonWritableDestinationIsRejectedBeforeFontOrSkiaWork()
    {
        var request = Request(
            SmallDocument(),
            ExportContentKind.CurrentWeek,
            ExportFileFormat.Png,
            ImageClarity.Standard);
        request.Fonts.RegularFilePath = "missing-font.ttf";
        using var output = new MemoryStream(Array.Empty<byte>(), writable: false);

        Assert.Throws<ArgumentException>(() => TimetableExportService.ExportPng(request, output));
    }

    [Fact]
    public void InvalidUtf16TextIsReplacedInsteadOfCrashingSkiaOrTextElementParsing()
    {
        var document = SmallDocument();
        document.CourseLibrary[0].CourseName = "Broken \uD800 title";
        var request = Request(
            document,
            ExportContentKind.CurrentWeek,
            ExportFileFormat.Png,
            ImageClarity.Standard);
        request.Text.Title = "Header \uDC00 text";
        using var output = new MemoryStream();

        TimetableExportService.ExportPng(request, output);

        Assert.True(output.Length > 4);
    }

    [Fact]
    public void PdfHasPdfHeaderAndDoesNotRasterizeThePage()
    {
        var document = SmallDocument();
        var request = Request(document, ExportContentKind.DetailedSemester, ExportFileFormat.Pdf, clarity: null);
        using var output = new MemoryStream();

        TimetableExportService.ExportPdf(request, output);

        var bytes = output.ToArray();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        var pdf = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("/Subtype /Image", pdf, StringComparison.Ordinal);
        Assert.Contains("/Type /Font", pdf, StringComparison.Ordinal);
    }

    private static PlannerDocument SmallDocument()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var semester = document.Semesters[0];
        semester.WeekCount = 2;
        semester.EndDate = SemesterRules.CalculateEndDate(semester.StartDate, semester.WeekCount, semester.WeekStartDay);
        semester.PeriodSchedule = semester.PeriodSchedule.Take(2).ToList();
        foreach (var course in document.CourseLibrary)
            course.MeetingTimes.Clear();
        var courseWithBlock = document.CourseLibrary[0];
        courseWithBlock.MeetingTimes.Add(new MeetingTime
        {
            Weekday = 1,
            StartPeriod = 1,
            EndPeriod = 1,
            Weeks = "1-2"
        });
        DocumentConsistencyService.Ensure(document);
        document.Plans[0].Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = courseWithBlock.OfferingId });
        return document;
    }

    private static TimetableExportRequest Request(
        PlannerDocument document,
        ExportContentKind content,
        ExportFileFormat format,
        ImageClarity? clarity)
    {
        var semester = document.Semesters[0];
        return new TimetableExportRequest
        {
            Semester = semester,
            Plan = document.Plans[0],
            CourseLibrary = document.CourseLibrary,
            Week = 1,
            Palette = TimetableExportPalette.Light,
            Options = new TimetableExportOptions
            {
                ContentKind = content,
                FileFormat = format,
                ImageClarity = clarity,
                CourseBlockFields = CourseBlockFields.Default,
                StartWeek = 1,
                EndWeek = semester.WeekCount
            },
            Text = new TimetableExportText
            {
                Title = "Export Test",
                WeekSubtitle = "Week subtitle",
                WeekRangeSubtitle = "Week range subtitle",
                DetailedSemesterSubtitle = "Entire semester subtitle",
                WeekHeadingFormat = "Week {0}",
                BeforeSemesterText = "Before semester",
                AfterSemesterText = "After semester",
                WeekdayShortNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
            },
            Fonts = new TimetableExportFonts
            {
                RegularFilePath = FontPath("DreamHanSans-W12.ttc"),
                SemiboldFilePath = FontPath("DreamHanSans-W16.ttc"),
                BoldFilePath = FontPath("DreamHanSans-W22.ttc"),
                CourseBlockRegularFilePath = FontPath("DreamHanSans-W12.ttc"),
                CourseBlockBoldFilePath = FontPath("DreamHanSans-W22.ttc")
            }
        };
    }

    private static string FontPath(string fileName) =>
        RepositoryPaths.FromRoot(
            "CoursePlanner",
            "Assets",
            "Fonts",
            "DreamHanSansSC",
            fileName);

    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    private sealed class ThrowingWriteStream(int bytesBeforeThrow = 0) : Stream
    {
        private int _remaining = bytesBeforeThrow;
        public bool WasDisposed { get; private set; }
        public int BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var accepted = Math.Min(_remaining, buffer.Length);
            BytesWritten += accepted;
            _remaining -= accepted;
            throw new IOException("Injected write failure.");
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}

using CoursePlanner.Core;
using CoursePlanner.Export;
using CoursePlanner.ViewModels;
using Windows.UI;

namespace CoursePlanner.Services;

public static class TimetableExportRequestFactory
{
    public static TimetableExportRequest Create(
        PlannerViewModel viewModel,
        CourseDisplayFormatter display,
        IEnumerable<CourseOffering> courseLibrary,
        TimetableExportOptions options,
        ResolvedThemeMode resolvedTheme,
        int week)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(courseLibrary);
        ArgumentNullException.ThrowIfNull(options);

        var semester = JsonDefaults.Clone(
            viewModel.CurrentSemester ?? throw new InvalidOperationException("No semester is selected."));
        var plan = JsonDefaults.Clone(
            viewModel.CurrentPlan ?? throw new InvalidOperationException("No plan is selected."));
        var library = JsonDefaults.Clone(courseLibrary.ToList());
        var isCurrentWeekComparison =
            options.ContentKind == ExportContentKind.CurrentWeek &&
            viewModel.ViewMode == PlannerViewMode.Comparison;
        var differences = isCurrentWeekComparison
            ? JsonDefaults.Clone(viewModel.GetCurrentDifferences())
            : null;
        var optionsSnapshot = new TimetableExportOptions
        {
            ContentKind = options.ContentKind,
            FileFormat = options.FileFormat,
            ImageClarity = options.ImageClarity,
            CourseBlockFields = options.CourseBlockFields,
            StartWeek = options.StartWeek,
            EndWeek = options.EndWeek
        };
        TimetableExportOptionsValidator.ValidateAndThrow(optionsSnapshot, semester);
        var rangeStartDates = SemesterRules.GetWeekDates(semester, optionsSnapshot.StartWeek);
        var rangeEndDates = SemesterRules.GetWeekDates(semester, optionsSnapshot.EndWeek);

        var weekdays = display.WeekdayShortNames().ToList();
        return new TimetableExportRequest
        {
            Semester = semester,
            Plan = plan,
            CourseLibrary = library,
            Week = week,
            IncludeNotes = (optionsSnapshot.CourseBlockFields & CourseBlockFields.Notes) != 0,
            Differences = differences,
            Options = optionsSnapshot,
            Palette = CreatePalette(resolvedTheme),
            Text = new TimetableExportText
            {
                Title = isCurrentWeekComparison
                    ? $"{viewModel.BaseComparePlan?.PlanName} -> {viewModel.CurrentPlan?.PlanName}"
                    : viewModel.T["AppTitle"],
                WeekSubtitle = string.Format(
                    viewModel.T["ExportWeekSubtitleFormat"],
                    semester.SemesterName,
                    plan.PlanName,
                    week,
                    SemesterRules.WeekRangeText(semester, week)),
                WeekRangeSubtitle = string.Format(
                    viewModel.T["ExportWeekRangeSubtitleFormat"],
                    semester.SemesterName,
                    plan.PlanName,
                    optionsSnapshot.StartWeek,
                    optionsSnapshot.EndWeek,
                    DateDisplay.Date(rangeStartDates[0]),
                    DateDisplay.Date(rangeEndDates[^1])),
                DetailedSemesterSubtitle = string.Format(
                    viewModel.T["ExportDetailedSemesterSubtitleFormat"],
                    semester.SemesterName,
                    plan.PlanName,
                    semester.WeekCount,
                    DateDisplay.Date(semester.StartDate),
                    DateDisplay.Date(semester.EndDate)),
                WeekHeadingFormat = viewModel.T["ExportWeekHeadingFormat"],
                BeforeSemesterText = viewModel.T["BeforeSemester"],
                AfterSemesterText = viewModel.T["AfterSemester"],
                WeekdayShortNames = weekdays
            },
            Fonts = new TimetableExportFonts
            {
                RegularFilePath = AppTypography.RegularFontFilePath,
                SemiboldFilePath = AppTypography.SemiBoldFontFilePath,
                BoldFilePath = AppTypography.BoldFontFilePath,
                CourseBlockRegularFilePath = AppTypography.CourseBlockRegularFontFilePath,
                CourseBlockBoldFilePath = AppTypography.CourseBlockBoldFontFilePath
            },
            Typography = new TimetableExportTypography
            {
                Title = Metrics(AppTextRole.Title),
                Subtitle = Metrics(AppTextRole.Subtitle),
                Body = Metrics(AppTextRole.Body),
                BodyStrong = Metrics(AppTextRole.BodyStrong),
                Caption = Metrics(AppTextRole.Caption),
                CourseTitle = new TimetableExportTextMetrics(
                    13,
                    (float)AppTypography.CourseBlockLineHeight(13, bold: true)),
                CourseDetail = new TimetableExportTextMetrics(
                    12,
                    (float)AppTypography.CourseBlockLineHeight(12, bold: false))
            }
        };
    }

    public static TimetableExportRequest Create(
        PlannerViewModel viewModel,
        CourseDisplayFormatter display,
        IEnumerable<CourseOffering> courseLibrary,
        bool includeNotes,
        int week)
    {
        var options = new TimetableExportOptions
        {
            ContentKind = ExportContentKind.CurrentWeek,
            FileFormat = ExportFileFormat.Png,
            ImageClarity = ImageClarity.High,
            CourseBlockFields = includeNotes
                ? CourseBlockFields.Default | CourseBlockFields.Notes
                : CourseBlockFields.Default,
            StartWeek = week,
            EndWeek = week
        };
        return Create(viewModel, display, courseLibrary, options, ResolvedThemeMode.Light, week);
    }

    private static TimetableExportPalette CreatePalette(ResolvedThemeMode theme)
    {
        var fallback = theme == ResolvedThemeMode.Dark
            ? TimetableExportPalette.Dark
            : TimetableExportPalette.Light;
        var opaqueThemeBase = theme == ResolvedThemeMode.Dark
            ? Color.FromArgb(255, 0x1E, 0x23, 0x21)
            : Color.FromArgb(255, 0xF8, 0xFB, 0xFA);
        var pageMaterial = AppMaterialLayer.Color(
            AppMaterialSurface.Page,
            theme,
            opaqueThemeBase);
        var pageBase = CompositeOverOpaque(pageMaterial, opaqueThemeBase);
        var timetableCanvas = AppMaterialLayer.Color(
            AppMaterialSurface.TimetableCanvas,
            theme,
            UiColor(fallback.PageBackground));
        var opaquePageBackground = CompositeOverOpaque(timetableCanvas, pageBase);
        var divider = SurfaceColor(AppMaterialSurface.Divider, theme, fallback.Divider);
        return new TimetableExportPalette
        {
            PageBackground = ExportColor(opaquePageBackground),
            HeaderBackground = SurfaceColor(AppMaterialSurface.TimetableHeader, theme, fallback.HeaderBackground),
            Divider = divider,
            PrimaryText = RoleColor(AppColorRole.TextPrimary, theme, fallback.PrimaryText),
            SecondaryText = RoleColor(AppColorRole.TextSecondary, theme, fallback.SecondaryText),
            CourseBlockBackground = RoleColor(AppColorRole.CourseBlock, theme, fallback.CourseBlockBackground),
            LockedCourseBlockBackground = RoleColor(
                AppColorRole.CourseBlockLocked,
                theme,
                fallback.LockedCourseBlockBackground),
            MatrixCardBackground = SurfaceColor(
                AppMaterialSurface.SemesterOverviewCard,
                theme,
                fallback.MatrixCardBackground),
            DifferenceAddedBackground = RoleColor(
                AppColorRole.CourseBlockAdded,
                theme,
                fallback.DifferenceAddedBackground),
            DifferenceRemovedBackground = RoleColor(
                AppColorRole.CourseBlockRemoved,
                theme,
                fallback.DifferenceRemovedBackground),
            DifferenceModifiedBackground = RoleColor(
                AppColorRole.CourseBlockModified,
                theme,
                fallback.DifferenceModifiedBackground),
            StatusCritical = RoleColor(AppColorRole.StatusCritical, theme, fallback.StatusCritical),
            StatusCaution = RoleColor(AppColorRole.StatusCaution, theme, fallback.StatusCaution),
            StatusCurrent = RoleColor(AppColorRole.StatusCurrent, theme, fallback.StatusCurrent),
            OutsideSemesterOverlay = new TimetableExportColor(0x70, divider.R, divider.G, divider.B)
        };
    }

    private static TimetableExportColor SurfaceColor(
        AppMaterialSurface surface,
        ResolvedThemeMode theme,
        TimetableExportColor fallback) =>
        ExportColor(AppMaterialLayer.Color(surface, theme, UiColor(fallback)));

    private static TimetableExportColor RoleColor(
        AppColorRole role,
        ResolvedThemeMode theme,
        TimetableExportColor fallback) =>
        ExportColor(AppMaterialLayer.Color(role, theme, UiColor(fallback)));

    private static TimetableExportColor ExportColor(Color color) =>
        new(color.A, color.R, color.G, color.B);

    private static Color UiColor(TimetableExportColor color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    private static Color CompositeOverOpaque(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromArgb(
            255,
            CompositeChannel(foreground.R, background.R, alpha),
            CompositeChannel(foreground.G, background.G, alpha),
            CompositeChannel(foreground.B, background.B, alpha));
    }

    private static byte CompositeChannel(byte foreground, byte background, double alpha) =>
        (byte)Math.Clamp(
            (int)Math.Round((foreground * alpha) + (background * (1 - alpha))),
            byte.MinValue,
            byte.MaxValue);

    private static TimetableExportTextMetrics Metrics(AppTextRole role) =>
        new((float)AppTypography.FontSize(role), (float)AppTypography.LineHeight(role));
}

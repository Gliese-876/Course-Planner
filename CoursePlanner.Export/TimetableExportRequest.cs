using CoursePlanner.Core;

namespace CoursePlanner.Export;

public sealed class TimetableExportRequest
{
    public Semester Semester { get; set; } = new();
    public SelectionPlan Plan { get; set; } = new();
    public IReadOnlyList<CourseOffering> CourseLibrary { get; set; } = Array.Empty<CourseOffering>();
    public int Week { get; set; } = 1;

    // Kept for callers using the original request shape. New callers should select Notes in Options.
    public bool IncludeNotes { get; set; }

    public IReadOnlyList<SlotDifference>? Differences { get; set; }
    public TimetableExportText Text { get; set; } = new();
    public TimetableExportFonts Fonts { get; set; } = new();
    public TimetableExportTypography Typography { get; set; } = TimetableExportTypography.Default;
    public TimetableExportOptions Options { get; set; } = new();
    public TimetableExportPalette Palette { get; set; } = TimetableExportPalette.Light;
}

public sealed class TimetableExportText
{
    public string Title { get; set; } = "";
    public string WeekSubtitle { get; set; } = "";
    public string WeekRangeSubtitle { get; set; } = "";
    public string DetailedSemesterSubtitle { get; set; } = "";
    public string WeekHeadingFormat { get; set; } = "";
    public string BeforeSemesterText { get; set; } = "";
    public string AfterSemesterText { get; set; } = "";
    public IReadOnlyList<string> WeekdayShortNames { get; set; } = Array.Empty<string>();
}

public sealed class TimetableExportFonts
{
    public string RegularFilePath { get; set; } = "";
    public string SemiboldFilePath { get; set; } = "";
    public string BoldFilePath { get; set; } = "";
    public string CourseBlockRegularFilePath { get; set; } = "";
    public string CourseBlockBoldFilePath { get; set; } = "";
}

using System.Globalization;
using CoursePlanner.Core;

namespace CoursePlanner.Export;

public enum ExportContentKind
{
    CurrentWeek = 0,
    WeekRange = 1,
    // Value 2 belonged to the removed semester-overview export and stays invalid.
    DetailedSemester = 3
}

public enum ExportFileFormat
{
    Png,
    Pdf
}

public enum ImageClarity
{
    Standard = 1,
    High = 2,
    Ultra = 3,
    Extreme = 4,
    Maximum = 5
}

[Flags]
public enum CourseBlockFields
{
    None = 0,
    CourseName = 1 << 0,
    Teacher = 1 << 1,
    Location = 1 << 2,
    Credits = 1 << 3,
    Capacity = 1 << 4,
    CourseGroupType = 1 << 5,
    StudyType = 1 << 6,
    Labels = 1 << 7,
    Notes = 1 << 8,
    Default = CourseName | Teacher | Location | Capacity,
    All = CourseName | Teacher | Location | Credits | Capacity | CourseGroupType | StudyType | Labels | Notes
}

public sealed class TimetableExportOptions
{
    public ExportContentKind ContentKind { get; set; } = ExportContentKind.CurrentWeek;
    public ExportFileFormat FileFormat { get; set; } = ExportFileFormat.Png;
    public ImageClarity? ImageClarity { get; set; } = global::CoursePlanner.Export.ImageClarity.High;
    public CourseBlockFields CourseBlockFields { get; set; } = global::CoursePlanner.Export.CourseBlockFields.Default;
    public int StartWeek { get; set; } = 1;
    public int EndWeek { get; set; } = 1;
}

public sealed record TimetableExportTextMetrics(float FontSize, float LineHeight);

public sealed class TimetableExportTypography
{
    public TimetableExportTextMetrics Title { get; init; } = new(28, 41);
    public TimetableExportTextMetrics Subtitle { get; init; } = new(20, 29);
    public TimetableExportTextMetrics Body { get; init; } = new(14, 21);
    public TimetableExportTextMetrics BodyStrong { get; init; } = new(14, 21);
    public TimetableExportTextMetrics Caption { get; init; } = new(12, 18);
    public TimetableExportTextMetrics CourseTitle { get; init; } = new(13, 17);
    public TimetableExportTextMetrics CourseDetail { get; init; } = new(12, 16);

    public static TimetableExportTypography Default { get; } = new();
}

public static class TimetableExportOptionsValidator
{
    public static IReadOnlyList<string> Validate(TimetableExportOptions? options, Semester? semester = null)
    {
        var errors = new List<string>();
        if (options is null)
        {
            errors.Add("Export options are required.");
            return errors;
        }

        if (!Enum.IsDefined(options.ContentKind))
            errors.Add("The export content kind is invalid.");
        if (!Enum.IsDefined(options.FileFormat))
            errors.Add("The export file format is invalid.");

        const CourseBlockFields knownFields = CourseBlockFields.All;
        if ((options.CourseBlockFields & CourseBlockFields.CourseName) == 0)
            errors.Add("CourseName is required in every course block.");
        if ((options.CourseBlockFields & ~knownFields) != 0)
            errors.Add("The course block field selection contains unsupported values.");

        if (options.FileFormat == ExportFileFormat.Png)
        {
            if (options.ImageClarity is null || !Enum.IsDefined(options.ImageClarity.Value))
                errors.Add("PNG export requires a valid image clarity.");
        }
        else if (options.FileFormat == ExportFileFormat.Pdf && options.ImageClarity is not null)
        {
            errors.Add("PDF export does not accept an image clarity because it is vector-based.");
        }

        if (options.ContentKind == ExportContentKind.WeekRange)
        {
            if (options.StartWeek < 1)
                errors.Add("The first week in a week range must be at least 1.");
            if (options.EndWeek < options.StartWeek)
                errors.Add("The last week in a week range must not precede the first week.");
            if (semester is not null && options.EndWeek > semester.WeekCount)
                errors.Add("The week range exceeds the semester.");
        }

        return errors;
    }

    public static void ValidateAndThrow(TimetableExportOptions? options, Semester? semester = null)
    {
        var errors = Validate(options, semester);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
    }
}

public readonly record struct TimetableExportColor(byte A, byte R, byte G, byte B)
{
    public static TimetableExportColor FromHex(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var hex = value.Trim().TrimStart('#');
        return hex.Length switch
        {
            6 => new TimetableExportColor(
                255,
                ParseByte(hex, 0),
                ParseByte(hex, 2),
                ParseByte(hex, 4)),
            8 => new TimetableExportColor(
                ParseByte(hex, 0),
                ParseByte(hex, 2),
                ParseByte(hex, 4),
                ParseByte(hex, 6)),
            _ => throw new FormatException("Colors must use #RRGGBB or #AARRGGBB notation.")
        };
    }

    private static byte ParseByte(string value, int index) =>
        byte.Parse(value.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

public sealed class TimetableExportPalette
{
    public TimetableExportColor PageBackground { get; init; }
    public TimetableExportColor HeaderBackground { get; init; }
    public TimetableExportColor Divider { get; init; }
    public TimetableExportColor PrimaryText { get; init; }
    public TimetableExportColor SecondaryText { get; init; }
    public TimetableExportColor CourseBlockBackground { get; init; }
    public TimetableExportColor MatrixCardBackground { get; init; }
    public TimetableExportColor DifferenceAddedBackground { get; init; }
    public TimetableExportColor DifferenceRemovedBackground { get; init; }
    public TimetableExportColor DifferenceModifiedBackground { get; init; }
    public TimetableExportColor StatusCritical { get; init; }
    public TimetableExportColor StatusCaution { get; init; }
    public TimetableExportColor StatusCurrent { get; init; }
    public TimetableExportColor OutsideSemesterOverlay { get; init; }

    public static TimetableExportPalette Light { get; } = new()
    {
        PageBackground = TimetableExportColor.FromHex("#FBFDFC"),
        HeaderBackground = TimetableExportColor.FromHex("#F0F6F3"),
        Divider = TimetableExportColor.FromHex("#D7E2DE"),
        PrimaryText = TimetableExportColor.FromHex("#1A1A1A"),
        SecondaryText = TimetableExportColor.FromHex("#575F5C"),
        CourseBlockBackground = TimetableExportColor.FromHex("#E7F1ED"),
        MatrixCardBackground = TimetableExportColor.FromHex("#FFFFFF"),
        DifferenceAddedBackground = TimetableExportColor.FromHex("#DFF4E7"),
        DifferenceRemovedBackground = TimetableExportColor.FromHex("#F8E2E2"),
        DifferenceModifiedBackground = TimetableExportColor.FromHex("#F7EDCF"),
        StatusCritical = TimetableExportColor.FromHex("#A4262C"),
        StatusCaution = TimetableExportColor.FromHex("#724E00"),
        StatusCurrent = TimetableExportColor.FromHex("#00857A"),
        OutsideSemesterOverlay = TimetableExportColor.FromHex("#70D7E2DE")
    };

    public static TimetableExportPalette Dark { get; } = new()
    {
        PageBackground = TimetableExportColor.FromHex("#202725"),
        HeaderBackground = TimetableExportColor.FromHex("#29322F"),
        Divider = TimetableExportColor.FromHex("#3D4744"),
        PrimaryText = TimetableExportColor.FromHex("#F5F7F6"),
        SecondaryText = TimetableExportColor.FromHex("#C5CECB"),
        CourseBlockBackground = TimetableExportColor.FromHex("#313D3A"),
        MatrixCardBackground = TimetableExportColor.FromHex("#2D312F"),
        DifferenceAddedBackground = TimetableExportColor.FromHex("#204336"),
        DifferenceRemovedBackground = TimetableExportColor.FromHex("#4D3136"),
        DifferenceModifiedBackground = TimetableExportColor.FromHex("#4A4129"),
        StatusCritical = TimetableExportColor.FromHex("#FFBAB5"),
        StatusCaution = TimetableExportColor.FromHex("#F4C76E"),
        StatusCurrent = TimetableExportColor.FromHex("#72DED0"),
        OutsideSemesterOverlay = TimetableExportColor.FromHex("#70424B48")
    };
}

public sealed record TimetableExportMeasurement(
    int LogicalWidth,
    int LogicalHeight,
    int PixelWidth,
    int PixelHeight,
    int RenderedWeekCount,
    int RenderedPeriodCount,
    int MatrixColumns,
    int MatrixRows);

public enum TimetableExportLimitKind
{
    CourseBlocks,
    TextCharacters,
    ConflictLanes,
    BitmapMemory,
    SurfaceDimension,
    VectorDimension
}

public sealed class TimetableExportLimitExceededException : InvalidOperationException
{
    public TimetableExportLimitExceededException(
        TimetableExportLimitKind kind,
        long actual,
        long maximum,
        string message)
        : base(message)
    {
        Kind = kind;
        Actual = actual;
        Maximum = maximum;
    }

    public TimetableExportLimitKind Kind { get; }
    public long Actual { get; }
    public long Maximum { get; }
}

internal readonly record struct CourseBlockTextLine(CourseBlockFields Field, string Text, bool IsTitle = false);

internal static class TimetableCourseBlockContent
{
    public static IReadOnlyList<CourseBlockTextLine> Build(CourseOffering course, CourseBlockFields fields)
    {
        var lines = new List<CourseBlockTextLine>();
        Add(lines, CourseBlockFields.CourseName, course.CourseName, fields, isTitle: true);
        Add(lines, CourseBlockFields.Teacher, course.Teacher, fields);
        Add(lines, CourseBlockFields.Location, course.Location, fields);
        Add(lines, CourseBlockFields.Capacity, CourseDerivedValues.CapacityText(course), fields);
        Add(lines, CourseBlockFields.Credits, course.Credits.ToString("0.##", CultureInfo.CurrentCulture), fields);
        Add(lines, CourseBlockFields.CourseGroupType, course.CourseGroupType, fields);
        Add(lines, CourseBlockFields.StudyType, course.StudyType, fields);
        Add(lines, CourseBlockFields.Labels, string.Join(" · ", course.Labels), fields);
        Add(lines, CourseBlockFields.Notes, course.Notes, fields);
        return lines;
    }

    private static void Add(
        ICollection<CourseBlockTextLine> lines,
        CourseBlockFields field,
        string? value,
        CourseBlockFields selected,
        bool isTitle = false)
    {
        if ((selected & field) != 0 && !string.IsNullOrWhiteSpace(value))
            lines.Add(new CourseBlockTextLine(field, TextRules.SanitizeUtf16(value).Trim(), isTitle));
    }
}

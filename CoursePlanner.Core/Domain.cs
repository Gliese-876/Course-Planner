namespace CoursePlanner.Core;

public enum WeekStartDay
{
    Monday,
    Sunday
}

public enum LabelKind
{
    Ordinary,
    CourseGroupType,
    StudyType
}

public enum PlannerViewMode
{
    Week,
    SemesterOverview,
    Comparison
}

public enum LanguageMode
{
    FollowSystem,
    SimplifiedChinese,
    English
}

public enum ThemeMode
{
    FollowSystem,
    Light,
    Dark
}

public enum WeekParity
{
    All,
    Odd,
    Even
}

public enum DuplicateResolution
{
    SkipExisting,
    ReplaceExisting
}

public enum ConflictResolution
{
    Cancel,
    KeepConflict,
    RemoveConflictingThenAdd
}

public enum ImportPreviewStatus
{
    Added,
    Updated,
    Skipped,
    Conflict,
    Warning,
    NotImportable
}

public enum DifferenceKind
{
    Unchanged,
    Added,
    Removed,
    Replaced
}

public sealed class PeriodDefinition
{
    public int Period { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }

}

public sealed class Semester
{
    public string SemesterId { get; set; } = "";
    public string SemesterName { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int WeekCount { get; set; }
    public WeekStartDay WeekStartDay { get; set; } = WeekStartDay.Monday;
    public int DisplayOrder { get; set; }
    public List<PeriodDefinition> PeriodSchedule { get; set; } = new();

}

public sealed class CourseLabel
{
    public string Name { get; set; } = "";
    public LabelKind Kind { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class MeetingTime
{
    public int Weekday { get; set; }
    public int StartPeriod { get; set; }
    public int EndPeriod { get; set; }
    public string Weeks { get; set; } = "1-16";
    public WeekParity WeekParity { get; set; } = WeekParity.All;
}

public sealed class CourseOffering
{
    public string OfferingId { get; set; } = "";
    public string SemesterId { get; set; } = "";
    public string CourseName { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Location { get; set; } = "";
    public decimal Credits { get; set; }
    public string? CourseGroupType { get; set; }
    public string? StudyType { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<MeetingTime> MeetingTimes { get; set; } = new();
    public string Notes { get; set; } = "";
    public int? EnrolledCount { get; set; }
    public int? Capacity { get; set; }
    public string Color { get; set; } = "#C3637A";
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

}

public sealed class PlanCourseSnapshot
{
    public string SnapshotId { get; set; } = Guid.NewGuid().ToString("N");
    public string CourseOfferingId { get; set; } = "";
    public int? RegistrationOrder { get; set; }
    public DateTimeOffset SnapshotAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SelectionPlan
{
    public string PlanId { get; set; } = Guid.NewGuid().ToString("N");
    public string SemesterId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public int DisplayOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PlanCourseSnapshot> Snapshots { get; set; } = new();

}

public sealed class AppSettings
{
    public LanguageMode Language { get; set; } = LanguageMode.FollowSystem;
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;
    public string? CurrentSemesterId { get; set; }
    public List<string> OpenPlanIds { get; set; } = new();
    public string? CurrentPlanId { get; set; }
}

public sealed class PlannerDocument
{
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    public List<Semester> Semesters { get; set; } = new();
    public List<CourseLabel> Labels { get; set; } = new();
    public List<CourseOffering> CourseLibrary { get; set; } = new();
    public List<SelectionPlan> Plans { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public sealed class TimetableSlot : IEquatable<TimetableSlot>, IComparable<TimetableSlot>
{
    public int Week { get; init; }
    public int Weekday { get; init; }
    public int Period { get; init; }

    public bool Equals(TimetableSlot? other) =>
        other is not null && Week == other.Week && Weekday == other.Weekday && Period == other.Period;

    public override bool Equals(object? obj) => Equals(obj as TimetableSlot);

    public override int GetHashCode() => HashCode.Combine(Week, Weekday, Period);

    public int CompareTo(TimetableSlot? other)
    {
        if (other is null)
            return 1;
        var week = Week.CompareTo(other.Week);
        if (week != 0)
            return week;
        var weekday = Weekday.CompareTo(other.Weekday);
        return weekday != 0 ? weekday : Period.CompareTo(other.Period);
    }

    public override string ToString() => $"W{Week}-D{Weekday}-P{Period}";
}

public sealed class SlotDifference
{
    private List<CourseOffering> _baseCourses = new();
    private List<CourseOffering> _currentCourses = new();

    public TimetableSlot Slot { get; set; } = new();
    public DifferenceKind Kind { get; set; }

    /// <summary>
    /// All courses occupying this slot in the base plan. The complete collections are the
    /// canonical comparison representation; the singular properties below remain as
    /// compatibility representatives for existing consumers.
    /// </summary>
    public List<CourseOffering> BaseCourses
    {
        get => _baseCourses;
        set => _baseCourses = value ?? new List<CourseOffering>();
    }

    /// <summary>All courses occupying this slot in the current plan.</summary>
    public List<CourseOffering> CurrentCourses
    {
        get => _currentCourses;
        set => _currentCourses = value ?? new List<CourseOffering>();
    }

    public IReadOnlyList<CourseOffering> UnchangedCourses =>
        CoursesByMembership(BaseCourses, CurrentCourses, included: true);

    public IReadOnlyList<CourseOffering> RemovedCourses =>
        CoursesByMembership(BaseCourses, CurrentCourses, included: false);

    public IReadOnlyList<CourseOffering> AddedCourses =>
        CoursesByMembership(CurrentCourses, BaseCourses, included: false);

    public CourseOffering? BaseCourse
    {
        get => RemovedCourses.FirstOrDefault() ?? OrderedDistinct(BaseCourses).FirstOrDefault();
        set => BaseCourses = value is null ? new List<CourseOffering>() : new List<CourseOffering> { value };
    }

    public CourseOffering? CurrentCourse
    {
        get => AddedCourses.FirstOrDefault() ?? OrderedDistinct(CurrentCourses).FirstOrDefault();
        set => CurrentCourses = value is null ? new List<CourseOffering>() : new List<CourseOffering> { value };
    }

    public DifferenceKind? KindForCourse(CourseOffering course)
    {
        var baseCourseIds = CourseIds(BaseCourses);
        var currentCourseIds = CourseIds(CurrentCourses);
        var isInBase = ContainsCourse(BaseCourses, baseCourseIds, course);
        var isInCurrent = ContainsCourse(CurrentCourses, currentCourseIds, course);
        if (isInBase && isInCurrent)
            return DifferenceKind.Unchanged;

        var isReplacement = baseCourseIds.Except(currentCourseIds).Any() &&
                            currentCourseIds.Except(baseCourseIds).Any();
        if (isInBase)
            return isReplacement ? DifferenceKind.Replaced : DifferenceKind.Removed;
        if (isInCurrent)
            return isReplacement ? DifferenceKind.Replaced : DifferenceKind.Added;
        return null;
    }

    public SlotDifference WithKind(DifferenceKind kind) => new()
    {
        Slot = Slot,
        Kind = kind,
        BaseCourses = BaseCourses.ToList(),
        CurrentCourses = CurrentCourses.ToList()
    };

    private static IReadOnlyList<CourseOffering> CoursesByMembership(
        IEnumerable<CourseOffering> source,
        IEnumerable<CourseOffering> other,
        bool included)
    {
        var otherCourses = other.ToList();
        var otherCourseIds = CourseIds(otherCourses);
        return OrderedDistinct(source.Where(course =>
            ContainsCourse(otherCourses, otherCourseIds, course) == included));
    }

    private static HashSet<string> CourseIds(IEnumerable<CourseOffering> courses) =>
        courses
            .Select(course => course.OfferingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

    private static bool ContainsCourse(
        IEnumerable<CourseOffering> courses,
        IReadOnlySet<string> courseIds,
        CourseOffering candidate) =>
        !string.IsNullOrWhiteSpace(candidate.OfferingId)
            ? courseIds.Contains(candidate.OfferingId)
            : courses.Any(course => ReferenceEquals(course, candidate));

    private static IReadOnlyList<CourseOffering> OrderedDistinct(IEnumerable<CourseOffering> courses)
    {
        var result = new List<CourseOffering>();
        var offeringIds = new HashSet<string>(StringComparer.Ordinal);
        var courseReferences = new HashSet<CourseOffering>(ReferenceEqualityComparer.Instance);
        foreach (var course in courses
                     .OrderBy(course => course.OfferingId, StringComparer.Ordinal)
                     .ThenBy(course => course.CourseName, StringComparer.Ordinal))
        {
            var added = string.IsNullOrWhiteSpace(course.OfferingId)
                ? courseReferences.Add(course)
                : offeringIds.Add(course.OfferingId);
            if (!added)
                continue;
            result.Add(course);
        }

        return result;
    }
}

public sealed class TimetableCourseBlock
{
    public CourseOffering Course { get; init; } = new();
    public TimetableSlot Slot { get; init; } = new();
    public int StartPeriod { get; init; }
    public int EndPeriod { get; init; }
    public int ConflictIndex { get; init; }
    public int ConflictCount { get; init; } = 1;
    public SlotDifference? Difference { get; init; }
}

public sealed class CourseFilter
{
    public string SearchText { get; set; } = "";
    public bool AllSemesters { get; set; }
    public HashSet<string> OrdinaryLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CourseGroupTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StudyTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Teachers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Locations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LibraryGroup
{
    public string SemesterName { get; set; } = "";
    public string CourseGroupType { get; set; } = "";
    public string StudyType { get; set; } = "";
    public List<CourseOffering> Courses { get; set; } = new();
}

public sealed class BulkAddCourseResult
{
    public int TargetCount { get; set; }
    public int Added { get; set; }
    public int ReplacedDuplicates { get; set; }
    public int Skipped { get; set; }
    public int Cancelled { get; set; }
    public int ConflictPlans { get; set; }
    public int ConflictingCoursesRemoved { get; set; }
    public List<string> AddedPlanNames { get; set; } = new();
    public List<string> SkippedPlanNames { get; set; } = new();
    public List<string> CancelledPlanNames { get; set; } = new();
    public List<string> ConflictPlanNames { get; set; } = new();
    public ValidationResult Validation { get; } = new();
}

public static class PlannerLabels
{
    public const string Uncategorized = "__uncategorized__";
    public const string General = "General";
    public const string Major = "Major";
    public const string Free = "Free";
    public const string Core = "Core";
    public const string Required = "Required";
    public const string Elective = "Elective";
    public const string Morning = "Morning";
    public const string Project = "Project";

    public static IReadOnlyList<BuiltInLabelDefinition> BuiltIn { get; } =
    [
        new(General, LabelKind.CourseGroupType, 0, "CourseTypeGeneral"),
        new(Major, LabelKind.CourseGroupType, 1, "CourseTypeMajor"),
        new(Free, LabelKind.CourseGroupType, 2, "CourseTypeFree"),
        new(Core, LabelKind.StudyType, 0, "StudyTypeCore"),
        new(Required, LabelKind.StudyType, 1, "Required"),
        new(Elective, LabelKind.StudyType, 2, "Elective"),
        new(Morning, LabelKind.Ordinary, 0, "Morning"),
        new(Project, LabelKind.Ordinary, 1, "Project")
    ];
}

public sealed record BuiltInLabelDefinition(string Name, LabelKind Kind, int DisplayOrder, string LocalizationKey);

public static class PlannerSchemas
{
    public const string Current = "2.0.0";
    public const string CourseLibraryKind = "courseLibrary";
    public const string SelectionPlanKind = "selectionPlan";
    public const string BackupKind = "coursePlannerBackup";
}

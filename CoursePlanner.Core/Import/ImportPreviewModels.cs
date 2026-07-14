using System.Text.Json.Serialization;

namespace CoursePlanner.Core;

public sealed class ImportPreviewItem
{
    public ImportPreviewStatus Status { get; set; }
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SemesterName { get; set; } = "";
    public string? TargetSemesterId { get; set; }
    public Semester? Semester { get; set; }
    public CourseLabel? Label { get; set; }
    public CourseOffering? Course { get; set; }
    public SelectionPlan? Plan { get; set; }
    public List<ValidationIssue> Warnings { get; set; } = new();
    public List<ValidationIssue> Errors { get; set; } = new();
    public bool RequiresSemesterSettingsDecision { get; set; }
    public bool CanApplyWithForcedSemesterMerge { get; set; }
    public bool RequiresForceImport { get; set; }
    public bool RequiresCourseLibrarySync { get; set; }
}

public sealed class ImportPreview
{
    public string Kind { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public List<ImportPreviewItem> Items { get; set; } = new();

    [JsonIgnore]
    public bool CanApply
    {
        get
        {
            var candidates = string.Equals(Kind, PlannerSchemas.SelectionPlanKind, StringComparison.Ordinal)
                ? Items.Where(x => x.Kind == "plan")
                : Items;
            return candidates.Any(x =>
                x.Status is ImportPreviewStatus.Added or ImportPreviewStatus.Updated or ImportPreviewStatus.Warning ||
                x.Status == ImportPreviewStatus.Conflict && x.CanApplyWithForcedSemesterMerge);
        }
    }

    [JsonIgnore]
    public bool RequiresCourseLibrarySync => Items.Any(x => x.RequiresCourseLibrarySync);
}

public sealed class ImportPreviewFilter
{
    public string SearchText { get; set; } = "";
    public string SemesterText { get; set; } = "";
    public HashSet<ImportPreviewStatus> Statuses { get; set; } = new();
    public HashSet<string> OrdinaryLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CourseGroupTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StudyTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ImportApplyOptions
{
    public bool UpdateExistingSemesterSettings { get; set; }
    public bool ForceSemesterMergeConflicts { get; set; }
    public bool ForceOutOfRangeCourses { get; set; }
    public bool SynchronizeMissingPlanCourses { get; set; }
}

public sealed record ImportApplyResult(bool Applied)
{
    public static ImportApplyResult NotApplied { get; } = new(false);
    public static ImportApplyResult Success { get; } = new(true);
}

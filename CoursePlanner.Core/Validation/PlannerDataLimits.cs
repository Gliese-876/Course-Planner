namespace CoursePlanner.Core;

public static class PlannerDataLimits
{
    public const int MaxTextFieldLength = 2_048;
    public const int MaxCourseLabelEditorLength = 32_768;
    public const int MaxMeetingWeeksLength = MeetingWeeksParser.MaxExpressionLength;
    public const int MaxSemesters = 128;
    public const int MaxLabels = 512;
    public const int MaxCourses = 5_000;
    public const int MaxPlans = 5_000;
    public const int MaxPeriodsPerSemester = 128;
    public const int MaxLabelsPerCourse = 128;
    public const int MaxMeetingsPerCourse = 32;
    public const int MaxMeetingRowsPerPlan = 2_000;
    public const int MaxSnapshotsPerPlan = 5_000;
    public const int MaxTotalSnapshots = 100_000;
    public const int MaxTotalLabelReferences = 100_000;
    public const int MaxAggregateTextCharacters = 5_000_000;
    public const int MaxImportTextCharacters = 64 * 1024 * 1024;
    public const long MaxImportFileBytes = 64L * 1024 * 1024;
    public const int MaxReportedPersistenceIssues = 64;
    public const int MaxSchemaVersionLength = 64;
    public const long MaxPersistedStateJsonBytes = 64L * 1024 * 1024;
}

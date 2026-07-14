using System.Text.Json.Serialization;
using CoursePlanner.Core;

namespace CoursePlanner.Persistence;

internal sealed class PlannerDocumentDto
{
    [JsonRequired]
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    [JsonRequired]
    public List<SemesterDto> Semesters { get; set; } = new();
    [JsonRequired]
    public List<CourseLabelDto> Labels { get; set; } = new();
    [JsonRequired]
    public List<CourseOfferingDto> CourseLibrary { get; set; } = new();
    [JsonRequired]
    public List<SelectionPlanDto> Plans { get; set; } = new();
    [JsonRequired]
    public AppSettingsDto Settings { get; set; } = new();
}

internal sealed class SemesterDto
{
    [JsonRequired]
    public string SemesterId { get; set; } = "";
    [JsonRequired]
    public string SemesterName { get; set; } = "";
    [JsonRequired]
    public DateOnly StartDate { get; set; }
    [JsonRequired]
    public DateOnly EndDate { get; set; }
    [JsonRequired]
    public int WeekCount { get; set; }
    [JsonRequired]
    public WeekStartDay WeekStartDay { get; set; } = WeekStartDay.Monday;
    [JsonRequired]
    public int DisplayOrder { get; set; }
    [JsonRequired]
    public List<PeriodDefinitionDto> PeriodSchedule { get; set; } = new();
}

internal sealed class PeriodDefinitionDto
{
    [JsonRequired]
    public int Period { get; set; }
    [JsonRequired]
    public TimeOnly Start { get; set; }
    [JsonRequired]
    public TimeOnly End { get; set; }
}

internal sealed class CourseLabelDto
{
    [JsonRequired]
    public string Name { get; set; } = "";
    [JsonRequired]
    public LabelKind Kind { get; set; }
    [JsonRequired]
    public int DisplayOrder { get; set; }
}

internal sealed class CourseOfferingDto
{
    [JsonRequired]
    public string OfferingId { get; set; } = "";
    [JsonRequired]
    public string SemesterId { get; set; } = "";
    [JsonRequired]
    public string CourseName { get; set; } = "";
    [JsonRequired]
    public string Teacher { get; set; } = "";
    [JsonRequired]
    public string Location { get; set; } = "";
    [JsonRequired]
    public decimal Credits { get; set; }
    public string? CourseGroupType { get; set; }
    public string? StudyType { get; set; }
    [JsonRequired]
    public List<string> Labels { get; set; } = new();
    [JsonRequired]
    public List<MeetingTimeDto> MeetingTimes { get; set; } = new();
    [JsonRequired]
    public string Notes { get; set; } = "";
    public int? EnrolledCount { get; set; }
    public int? Capacity { get; set; }
    [JsonRequired]
    public string Color { get; set; } = "#C3637A";
    [JsonRequired]
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class MeetingTimeDto
{
    [JsonRequired]
    public int Weekday { get; set; }
    [JsonRequired]
    public int StartPeriod { get; set; }
    [JsonRequired]
    public int EndPeriod { get; set; }
    [JsonRequired]
    public string Weeks { get; set; } = "1-16";
    [JsonRequired]
    public WeekParity WeekParity { get; set; } = WeekParity.All;
}

internal sealed class SelectionPlanDto
{
    [JsonRequired]
    public string PlanId { get; set; } = "";
    [JsonRequired]
    public string SemesterId { get; set; } = "";
    [JsonRequired]
    public string PlanName { get; set; } = "";
    [JsonRequired]
    public int DisplayOrder { get; set; }
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public List<PlanCourseSnapshotDto> Snapshots { get; set; } = new();
}

internal sealed class PlanCourseSnapshotDto
{
    [JsonRequired]
    public string SnapshotId { get; set; } = "";
    [JsonRequired]
    public string CourseOfferingId { get; set; } = "";
    [JsonRequired]
    public int? RegistrationOrder { get; set; }
    [JsonRequired]
    public DateTimeOffset SnapshotAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class AppSettingsDto
{
    [JsonRequired]
    public LanguageMode Language { get; set; } = LanguageMode.FollowSystem;
    [JsonRequired]
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;
    [JsonRequired]
    public string? CurrentSemesterId { get; set; }
    [JsonRequired]
    public List<string> OpenPlanIds { get; set; } = new();
    public string? CurrentPlanId { get; set; }
}

internal static class PersistenceDocumentMapper
{
    public static PlannerDocumentDto ToDto(PlannerDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        Semesters = document.Semesters.Select(ToDto).ToList(),
        Labels = document.Labels.Select(ToDto).ToList(),
        CourseLibrary = document.CourseLibrary.Select(ToDto).ToList(),
        Plans = document.Plans.Select(ToDto).ToList(),
        Settings = ToDto(document.Settings)
    };

    public static PlannerDocument ToDomain(PlannerDocumentDto dto) => new()
    {
        SchemaVersion = dto.SchemaVersion,
        Semesters = dto.Semesters.Select(ToDomain).ToList(),
        Labels = dto.Labels.Select(ToDomain).ToList(),
        CourseLibrary = dto.CourseLibrary.Select(ToDomain).ToList(),
        Plans = dto.Plans.Select(ToDomain).ToList(),
        Settings = ToDomain(dto.Settings)
    };

    private static SemesterDto ToDto(Semester semester) => new()
    {
        SemesterId = semester.SemesterId,
        SemesterName = semester.SemesterName,
        StartDate = semester.StartDate,
        EndDate = semester.EndDate,
        WeekCount = semester.WeekCount,
        WeekStartDay = semester.WeekStartDay,
        DisplayOrder = semester.DisplayOrder,
        PeriodSchedule = semester.PeriodSchedule.Select(ToDto).ToList()
    };

    private static Semester ToDomain(SemesterDto dto) => new()
    {
        SemesterId = dto.SemesterId,
        SemesterName = dto.SemesterName,
        StartDate = dto.StartDate,
        EndDate = dto.EndDate,
        WeekCount = dto.WeekCount,
        WeekStartDay = dto.WeekStartDay,
        DisplayOrder = dto.DisplayOrder,
        PeriodSchedule = dto.PeriodSchedule.Select(ToDomain).ToList()
    };

    private static PeriodDefinitionDto ToDto(PeriodDefinition period) => new()
    {
        Period = period.Period,
        Start = period.Start,
        End = period.End
    };

    private static PeriodDefinition ToDomain(PeriodDefinitionDto dto) => new()
    {
        Period = dto.Period,
        Start = dto.Start,
        End = dto.End
    };

    private static CourseLabelDto ToDto(CourseLabel label) => new()
    {
        Name = label.Name,
        Kind = label.Kind,
        DisplayOrder = label.DisplayOrder
    };

    private static CourseLabel ToDomain(CourseLabelDto dto) => new()
    {
        Name = dto.Name,
        Kind = dto.Kind,
        DisplayOrder = dto.DisplayOrder
    };

    private static CourseOfferingDto ToDto(CourseOffering course) => new()
    {
        OfferingId = course.OfferingId,
        SemesterId = course.SemesterId,
        CourseName = course.CourseName,
        Teacher = course.Teacher,
        Location = course.Location,
        Credits = course.Credits,
        CourseGroupType = course.CourseGroupType,
        StudyType = course.StudyType,
        Labels = course.Labels.ToList(),
        MeetingTimes = course.MeetingTimes.Select(ToDto).ToList(),
        Notes = course.Notes,
        EnrolledCount = course.EnrolledCount,
        Capacity = course.Capacity,
        Color = course.Color,
        ModifiedAt = course.ModifiedAt
    };

    private static CourseOffering ToDomain(CourseOfferingDto dto) => new()
    {
        OfferingId = dto.OfferingId,
        SemesterId = dto.SemesterId,
        CourseName = dto.CourseName,
        Teacher = dto.Teacher,
        Location = dto.Location,
        Credits = dto.Credits,
        CourseGroupType = dto.CourseGroupType,
        StudyType = dto.StudyType,
        Labels = dto.Labels.ToList(),
        MeetingTimes = dto.MeetingTimes.Select(ToDomain).ToList(),
        Notes = dto.Notes,
        EnrolledCount = dto.EnrolledCount,
        Capacity = dto.Capacity,
        Color = dto.Color,
        ModifiedAt = dto.ModifiedAt
    };

    private static MeetingTimeDto ToDto(MeetingTime meeting) => new()
    {
        Weekday = meeting.Weekday,
        StartPeriod = meeting.StartPeriod,
        EndPeriod = meeting.EndPeriod,
        Weeks = meeting.Weeks,
        WeekParity = meeting.WeekParity
    };

    private static MeetingTime ToDomain(MeetingTimeDto dto) => new()
    {
        Weekday = dto.Weekday,
        StartPeriod = dto.StartPeriod,
        EndPeriod = dto.EndPeriod,
        Weeks = dto.Weeks,
        WeekParity = dto.WeekParity
    };

    private static SelectionPlanDto ToDto(SelectionPlan plan) => new()
    {
        PlanId = plan.PlanId,
        SemesterId = plan.SemesterId,
        PlanName = plan.PlanName,
        DisplayOrder = plan.DisplayOrder,
        CreatedAt = plan.CreatedAt,
        ModifiedAt = plan.ModifiedAt,
        Snapshots = plan.Snapshots.Select(ToDto).ToList()
    };

    private static SelectionPlan ToDomain(SelectionPlanDto dto) => new()
    {
        PlanId = dto.PlanId,
        SemesterId = dto.SemesterId,
        PlanName = dto.PlanName,
        DisplayOrder = dto.DisplayOrder,
        CreatedAt = dto.CreatedAt,
        ModifiedAt = dto.ModifiedAt,
        Snapshots = dto.Snapshots.Select(ToDomain).ToList()
    };

    private static PlanCourseSnapshotDto ToDto(PlanCourseSnapshot snapshot) => new()
    {
        SnapshotId = snapshot.SnapshotId,
        CourseOfferingId = snapshot.CourseOfferingId,
        RegistrationOrder = snapshot.RegistrationOrder,
        SnapshotAt = snapshot.SnapshotAt
    };

    private static PlanCourseSnapshot ToDomain(PlanCourseSnapshotDto dto) => new()
    {
        SnapshotId = dto.SnapshotId,
        CourseOfferingId = dto.CourseOfferingId,
        RegistrationOrder = dto.RegistrationOrder,
        SnapshotAt = dto.SnapshotAt
    };

    private static AppSettingsDto ToDto(AppSettings settings) => new()
    {
        Language = settings.Language,
        Theme = settings.Theme,
        CurrentSemesterId = settings.CurrentSemesterId,
        OpenPlanIds = settings.OpenPlanIds.ToList(),
        CurrentPlanId = settings.CurrentPlanId
    };

    private static AppSettings ToDomain(AppSettingsDto dto) => new()
    {
        Language = dto.Language,
        Theme = dto.Theme,
        CurrentSemesterId = dto.CurrentSemesterId,
        OpenPlanIds = dto.OpenPlanIds.ToList(),
        CurrentPlanId = dto.CurrentPlanId
    };
}

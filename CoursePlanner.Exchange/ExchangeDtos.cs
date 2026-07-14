using System.Text.Json.Serialization;
using CoursePlanner.Core;

namespace CoursePlanner.Exchange;

internal sealed class CourseLibraryPackageDto
{
    [JsonRequired]
    public string Kind { get; set; } = PlannerSchemas.CourseLibraryKind;
    [JsonRequired]
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    [JsonRequired]
    public List<SemesterDto> Semesters { get; set; } = new();
    [JsonRequired]
    public List<CourseLabelDto> Labels { get; set; } = new();
    [JsonRequired]
    public List<CourseOfferingDto> Courses { get; set; } = new();
}

internal sealed class SelectionPlanPackageDto
{
    [JsonRequired]
    public string Kind { get; set; } = PlannerSchemas.SelectionPlanKind;
    [JsonRequired]
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    [JsonRequired]
    public SemesterDto Semester { get; set; } = new();
    [JsonRequired]
    public List<CourseLabelDto> Labels { get; set; } = new();
    [JsonRequired]
    public List<CourseOfferingDto> Courses { get; set; } = new();
    [JsonRequired]
    public SelectionPlanDto Plan { get; set; } = new();
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
    [JsonRequired]
    public string? CourseGroupType { get; set; }
    [JsonRequired]
    public string? StudyType { get; set; }
    [JsonRequired]
    public List<string> Labels { get; set; } = new();
    [JsonRequired]
    public List<MeetingTimeDto> MeetingTimes { get; set; } = new();
    [JsonRequired]
    public string Notes { get; set; } = "";
    [JsonRequired]
    public int? EnrolledCount { get; set; }
    [JsonRequired]
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
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? RegistrationOrder { get; set; }
    public bool IsLocked { get; set; }
    [JsonRequired]
    public DateTimeOffset SnapshotAt { get; set; } = DateTimeOffset.UtcNow;
}

internal static class ExchangePackageMapper
{
    public static CourseLibraryPackageDto ToDto(CourseLibraryPackage package) => new()
    {
        Kind = package.Kind,
        SchemaVersion = package.SchemaVersion,
        Semesters = package.Semesters.Select(ToDto).ToList(),
        Labels = package.Labels.Select(ToDto).ToList(),
        Courses = package.Courses.Select(ToDto).ToList()
    };

    public static CourseLibraryPackage ToDomain(CourseLibraryPackageDto dto) => new()
    {
        Kind = RequiredText(dto.Kind, nameof(dto.Kind)),
        SchemaVersion = RequiredText(dto.SchemaVersion, nameof(dto.SchemaVersion)),
        Semesters = RequiredItems(dto.Semesters, nameof(dto.Semesters)).Select(ToDomain).ToList(),
        Labels = RequiredItems(dto.Labels, nameof(dto.Labels)).Select(ToDomain).ToList(),
        Courses = RequiredItems(dto.Courses, nameof(dto.Courses)).Select(ToDomain).ToList()
    };

    public static SelectionPlanPackageDto ToDto(SelectionPlanPackage package) => new()
    {
        Kind = package.Kind,
        SchemaVersion = package.SchemaVersion,
        Semester = ToDto(package.Semester),
        Labels = package.Labels.Select(ToDto).ToList(),
        Courses = package.Courses.Select(ToDto).ToList(),
        Plan = ToDto(package.Plan)
    };

    public static SelectionPlanPackage ToDomain(SelectionPlanPackageDto dto) => new()
    {
        Kind = RequiredText(dto.Kind, nameof(dto.Kind)),
        SchemaVersion = RequiredText(dto.SchemaVersion, nameof(dto.SchemaVersion)),
        Semester = ToDomain(RequiredItem(dto.Semester, nameof(dto.Semester))),
        Labels = RequiredItems(dto.Labels, nameof(dto.Labels)).Select(ToDomain).ToList(),
        Courses = RequiredItems(dto.Courses, nameof(dto.Courses)).Select(ToDomain).ToList(),
        Plan = ToDomain(RequiredItem(dto.Plan, nameof(dto.Plan)))
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
        SemesterId = RequiredText(dto.SemesterId, nameof(dto.SemesterId)),
        SemesterName = RequiredText(dto.SemesterName, nameof(dto.SemesterName)),
        StartDate = dto.StartDate,
        EndDate = dto.EndDate,
        WeekCount = dto.WeekCount,
        WeekStartDay = dto.WeekStartDay,
        DisplayOrder = dto.DisplayOrder,
        PeriodSchedule = RequiredItems(dto.PeriodSchedule, nameof(dto.PeriodSchedule)).Select(ToDomain).ToList()
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
        Name = RequiredText(dto.Name, nameof(dto.Name)),
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
        OfferingId = RequiredText(dto.OfferingId, nameof(dto.OfferingId)),
        SemesterId = RequiredText(dto.SemesterId, nameof(dto.SemesterId)),
        CourseName = RequiredText(dto.CourseName, nameof(dto.CourseName)),
        Teacher = RequiredText(dto.Teacher, nameof(dto.Teacher)),
        Location = RequiredText(dto.Location, nameof(dto.Location)),
        Credits = dto.Credits,
        CourseGroupType = dto.CourseGroupType,
        StudyType = dto.StudyType,
        Labels = RequiredItems(dto.Labels, nameof(dto.Labels))
            .Select(label => RequiredText(label, nameof(dto.Labels)))
            .ToList(),
        MeetingTimes = RequiredItems(dto.MeetingTimes, nameof(dto.MeetingTimes)).Select(ToDomain).ToList(),
        Notes = RequiredText(dto.Notes, nameof(dto.Notes)),
        EnrolledCount = dto.EnrolledCount,
        Capacity = dto.Capacity,
        Color = RequiredText(dto.Color, nameof(dto.Color)),
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
        Weeks = RequiredText(dto.Weeks, nameof(dto.Weeks)),
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
        PlanId = RequiredText(dto.PlanId, nameof(dto.PlanId)),
        SemesterId = RequiredText(dto.SemesterId, nameof(dto.SemesterId)),
        PlanName = RequiredText(dto.PlanName, nameof(dto.PlanName)),
        DisplayOrder = dto.DisplayOrder,
        CreatedAt = dto.CreatedAt,
        ModifiedAt = dto.ModifiedAt,
        Snapshots = RequiredItems(dto.Snapshots, nameof(dto.Snapshots)).Select(ToDomain).ToList()
    };

    private static PlanCourseSnapshotDto ToDto(PlanCourseSnapshot snapshot) => new()
    {
        SnapshotId = snapshot.SnapshotId,
        CourseOfferingId = snapshot.CourseOfferingId,
        RegistrationOrder = snapshot.RegistrationOrder,
        IsLocked = snapshot.IsLocked,
        SnapshotAt = snapshot.SnapshotAt
    };

    private static PlanCourseSnapshot ToDomain(PlanCourseSnapshotDto dto) => new()
    {
        SnapshotId = RequiredText(dto.SnapshotId, nameof(dto.SnapshotId)),
        CourseOfferingId = RequiredText(dto.CourseOfferingId, nameof(dto.CourseOfferingId)),
        RegistrationOrder = dto.RegistrationOrder,
        IsLocked = dto.IsLocked,
        SnapshotAt = dto.SnapshotAt
    };

    private static T RequiredItem<T>(T? value, string fieldName)
        where T : class =>
        value ?? throw new InvalidDataException($"The JSON field '{fieldName}' cannot be null.");

    private static IReadOnlyList<T> RequiredItems<T>(IEnumerable<T>? values, string fieldName)
        where T : class
    {
        if (values is null)
            throw new InvalidDataException($"The JSON collection '{fieldName}' cannot be null.");

        var items = values.ToList();
        if (items.Any(item => item is null))
            throw new InvalidDataException($"The JSON collection '{fieldName}' cannot contain null items.");
        return items;
    }

    private static string RequiredText(string? value, string fieldName) =>
        value ?? throw new InvalidDataException($"The JSON field '{fieldName}' cannot be null.");
}

namespace CoursePlanner.Core;

public static class SeedData
{
    public static PlannerDocument Create() => Create("2026 Fall", "Plan 1");

    public static PlannerDocument Create(string semesterName, string planName)
    {
        var startDate = new DateOnly(2026, 9, 7);
        var endDate = new DateOnly(2027, 1, 10);
        var semester = new Semester
        {
            SemesterId = "2026-fall",
            SemesterName = semesterName,
            StartDate = startDate,
            EndDate = endDate,
            WeekStartDay = WeekStartDay.Monday,
            WeekCount = SemesterRules.CalculateWeekCount(startDate, endDate, WeekStartDay.Monday),
            DisplayOrder = 0,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };

        var labels = PlannerLabels.BuiltIn
            .Select(label => new CourseLabel { Name = label.Name, Kind = label.Kind, DisplayOrder = label.DisplayOrder })
            .ToList();

        var plan = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            PlanName = planName,
            DisplayOrder = 0
        };

        var document = new PlannerDocument
        {
            Semesters = { semester },
            Labels = labels,
            Plans = { plan },
            Settings = new AppSettings
            {
                Language = LanguageMode.FollowSystem,
                Theme = ThemeMode.FollowSystem,
                CurrentSemesterId = semester.SemesterId,
                CurrentPlanId = plan.PlanId,
                OpenPlanIds = { plan.PlanId }
            }
        };

        DocumentConsistencyService.Ensure(document);
        return document;
    }
}

namespace CoursePlanner.Core;

public static class DocumentConsistencyService
{
    public static void Ensure(PlannerDocument document)
    {
        foreach (var semester in document.Semesters)
        {
            semester.SemesterName = TextRules.SanitizeUtf16(semester.SemesterName);
            if (semester.PeriodSchedule.Count == 0)
                semester.PeriodSchedule = PeriodScheduleFactory.CreateDefault12();
            if (semester.WeekCount < 1)
                semester.WeekCount = SemesterRules.CalculateWeekCount(semester.StartDate, semester.EndDate, semester.WeekStartDay);
        }

        foreach (var label in document.Labels)
            label.Name = TextRules.SanitizeUtf16(label.Name);

        var canonicalCourseIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < document.CourseLibrary.Count; i++)
        {
            var course = document.CourseLibrary[i];
            course.CourseName = TextRules.SanitizeUtf16(course.CourseName);
            course.Teacher = TextRules.SanitizeUtf16(course.Teacher);
            course.Location = TextRules.SanitizeUtf16(course.Location);
            course.CourseGroupType = SanitizeNullable(course.CourseGroupType);
            course.StudyType = SanitizeNullable(course.StudyType);
            course.Notes = TextRules.SanitizeUtf16(course.Notes);
            for (var labelIndex = 0; labelIndex < course.Labels.Count; labelIndex++)
                course.Labels[labelIndex] = TextRules.SanitizeUtf16(course.Labels[labelIndex]);
            foreach (var meeting in course.MeetingTimes)
                meeting.Weeks = TextRules.SanitizeUtf16(meeting.Weeks);
            course.Color = CourseColorService.EnsureValid(course.Color, i);
            var originalOfferingId = course.OfferingId;
            CourseIdentityService.AssignOfferingId(course);
            if (!string.IsNullOrWhiteSpace(originalOfferingId) &&
                !string.Equals(originalOfferingId, course.OfferingId, StringComparison.Ordinal))
            {
                canonicalCourseIds.TryAdd(originalOfferingId, course.OfferingId);
            }
        }

        foreach (var plan in document.Plans)
            plan.PlanName = TextRules.SanitizeUtf16(plan.PlanName);

        foreach (var snapshot in document.Plans.SelectMany(plan => plan.Snapshots))
        {
            if (canonicalCourseIds.TryGetValue(snapshot.CourseOfferingId, out var canonicalOfferingId))
                snapshot.CourseOfferingId = canonicalOfferingId;
        }

        PlannerDomainService.ResolvePlanCourseReferences(document);
        foreach (var plan in document.Plans)
            RegistrationPriorityService.NormalizeOrders(plan);
        var semestersById = document.Semesters
            .Where(semester => !string.IsNullOrWhiteSpace(semester.SemesterId))
            .GroupBy(semester => semester.SemesterId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var plansById = document.Plans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PlanId))
            .GroupBy(plan => plan.PlanId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        document.Settings.OpenPlanIds = document.Settings.OpenPlanIds
            .Where(id => plansById.TryGetValue(id, out var plan) && semestersById.ContainsKey(plan.SemesterId))
            .Distinct(StringComparer.Ordinal)
            .Take(PlanTabLimits.MaximumOpenPlans)
            .ToList();

        var (currentSemester, currentPlan) = ResolveCurrentContext(
            document,
            semestersById,
            plansById);
        document.Settings.CurrentSemesterId = currentSemester?.SemesterId;
        document.Settings.CurrentPlanId = currentPlan?.PlanId;
    }

    /// <summary>
    /// Resolves an explicitly valid open current plan first. When that id is
    /// absent or invalid, it preserves the selected semester and falls back only
    /// to an open plan in that semester. Callers expressing a semester switch
    /// must clear CurrentPlanId before resolving.
    /// </summary>
    public static (Semester? Semester, SelectionPlan? Plan) ResolveCurrentContext(
        PlannerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var semestersById = document.Semesters
            .Where(semester => !string.IsNullOrWhiteSpace(semester.SemesterId))
            .GroupBy(semester => semester.SemesterId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var plansById = document.Plans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PlanId))
            .GroupBy(plan => plan.PlanId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return ResolveCurrentContext(document, semestersById, plansById);
    }

    private static (Semester? Semester, SelectionPlan? Plan) ResolveCurrentContext(
        PlannerDocument document,
        IReadOnlyDictionary<string, Semester> semestersById,
        IReadOnlyDictionary<string, SelectionPlan> plansById)
    {
        var openPlans = document.Settings.OpenPlanIds
            .Select(planId => plansById.GetValueOrDefault(planId))
            .Where(plan => plan is not null && semestersById.ContainsKey(plan.SemesterId))
            .Select(plan => plan!)
            .ToList();
        var currentPlan = openPlans.FirstOrDefault(plan =>
            string.Equals(
                plan.PlanId,
                document.Settings.CurrentPlanId,
                StringComparison.Ordinal));
        if (currentPlan is not null)
        {
            var planSemester = semestersById[currentPlan.SemesterId];
            return (planSemester, currentPlan);
        }

        var currentSemester = document.Settings.CurrentSemesterId is { } currentSemesterId
            ? semestersById.GetValueOrDefault(currentSemesterId)
            : null;
        currentSemester ??= document.Semesters.FirstOrDefault();
        if (currentSemester is null)
            return (null, null);

        currentPlan = openPlans.FirstOrDefault(plan =>
            string.Equals(
                plan.SemesterId,
                currentSemester.SemesterId,
                StringComparison.Ordinal));
        return (currentSemester, currentPlan);
    }

    private static string? SanitizeNullable(string? value) =>
        value is null ? null : TextRules.SanitizeUtf16(value);
}

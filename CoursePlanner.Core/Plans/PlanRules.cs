namespace CoursePlanner.Core;

public static class PlanRules
{
    public static int CountMeetingRows(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(libraryCourses);
        var referencedCourseIds = plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (referencedCourseIds.Count == 0)
            return 0;

        var total = 0L;
        foreach (var course in libraryCourses)
        {
            if (!referencedCourseIds.Remove(course.OfferingId))
                continue;
            total += course.MeetingTimes.Count;
            if (referencedCourseIds.Count == 0)
                break;
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    public static int CountMeetingRows(
        SelectionPlan plan,
        IReadOnlyDictionary<string, CourseOffering> courseIndex,
        CourseOffering? additionalCourse = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(courseIndex);
        var referencedCourseIds = plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (referencedCourseIds.Count == 0)
            return 0;

        var total = 0L;
        foreach (var courseId in referencedCourseIds)
        {
            if (!courseIndex.TryGetValue(courseId, out var course) &&
                (additionalCourse is null ||
                 !string.Equals(additionalCourse.OfferingId, courseId, StringComparison.Ordinal)))
            {
                continue;
            }

            course ??= additionalCourse!;
            total += course.MeetingTimes.Count;
        }
        return (int)Math.Min(total, int.MaxValue);
    }

    public static ValidationResult ValidateMeetingRows(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses) =>
        ValidateMeetingRowCount(CountMeetingRows(plan, libraryCourses));

    public static ValidationResult ValidateMeetingRows(
        SelectionPlan plan,
        IReadOnlyDictionary<string, CourseOffering> courseIndex,
        CourseOffering? additionalCourse = null) =>
        ValidateMeetingRowCount(CountMeetingRows(plan, courseIndex, additionalCourse));

    public static ValidationResult ValidateMeetingRowCount(int projectedMeetingRowCount)
    {
        if (projectedMeetingRowCount < 0)
            throw new ArgumentOutOfRangeException(nameof(projectedMeetingRowCount));
        var validation = new ValidationResult();
        if (projectedMeetingRowCount > PlannerDataLimits.MaxMeetingRowsPerPlan)
        {
            validation.Error(
                "PlanMeetingRowsMaximum",
                PlannerDataLimits.MaxMeetingRowsPerPlan.ToString());
        }
        return validation;
    }

    public static ValidationResult Validate(
        SelectionPlan candidate,
        IEnumerable<SelectionPlan> existing,
        SelectionPlan? original = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        var result = new ValidationResult();
        var existingSample = existing.Take(PlannerDataLimits.MaxPlans + 1).ToList();
        var rawName = candidate.PlanName ?? "";
        var nameTooLong = rawName.Length > PlannerDataLimits.MaxTextFieldLength;
        var name = nameTooLong ? "" : rawName.Trim();

        if (name.Length == 0)
        {
            if (!nameTooLong)
                result.Error("PlanNameRequired");
        }
        if (nameTooLong)
            result.Error("PlanNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        foreach (var issue in WindowsFileNameRules.ValidateFileComponent(rawName).Errors)
            result.Errors.Add(issue);
        if (original is null && existingSample.Count >= PlannerDataLimits.MaxPlans)
            result.Error("PlanCatalogMaximum", PlannerDataLimits.MaxPlans.ToString());
        if (candidate.Snapshots.Count > PlannerDataLimits.MaxSnapshotsPerPlan)
            result.Error("PlanSnapshotsMaximum", PlannerDataLimits.MaxSnapshotsPerPlan.ToString());
        if (name.Length > 0 && existingSample.Any(plan =>
                !ReferenceEquals(plan, original) &&
                string.Equals(plan.SemesterId, candidate.SemesterId, StringComparison.Ordinal) &&
                TextRules.IsSameIdentityText(plan.PlanName, name)))
        {
            result.Error("PlanNameUnique");
        }

        return result;
    }
}

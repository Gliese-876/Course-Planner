namespace CoursePlanner.Core;

public static class PlannerCapacityRules
{
    public static ValidationResult ValidateCanAddSemester(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxSemesters, "SemesterCatalogMaximum");

    public static ValidationResult ValidateCanAddLabel(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxLabels, "LabelCatalogMaximum");

    public static ValidationResult ValidateCanAddCourse(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxCourses, "CourseCatalogMaximum");

    public static ValidationResult ValidateCanAddPlan(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxPlans, "PlanCatalogMaximum");

    public static ValidationResult ValidateCanAddSnapshot(int existingCount) =>
        ValidateAddition(existingCount, 1, PlannerDataLimits.MaxSnapshotsPerPlan, "PlanSnapshotsMaximum");

    public static ValidationResult ValidateCanAddSnapshot(int existingInPlan, int totalSnapshotCount)
        => ValidateSnapshotAddition(existingInPlan, totalSnapshotCount, 1);

    public static ValidationResult ValidateSnapshotAddition(
        int existingInPlan,
        int totalSnapshotCount,
        int additionalCount)
    {
        if (additionalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalCount));
        return ValidateSnapshotChange(existingInPlan, totalSnapshotCount, additionalCount);
    }

    public static ValidationResult ValidateSnapshotChange(
        int existingInPlan,
        int totalSnapshotCount,
        int snapshotDelta)
    {
        if (existingInPlan < 0)
            throw new ArgumentOutOfRangeException(nameof(existingInPlan));
        if (totalSnapshotCount < existingInPlan)
            throw new ArgumentOutOfRangeException(nameof(totalSnapshotCount));
        if (snapshotDelta < -existingInPlan)
            throw new ArgumentOutOfRangeException(nameof(snapshotDelta));
        var result = ValidateAddition(
            existingInPlan + Math.Min(0, snapshotDelta),
            Math.Max(0, snapshotDelta),
            PlannerDataLimits.MaxSnapshotsPerPlan,
            "PlanSnapshotsMaximum");
        AppendAdditionLimit(
            result,
            totalSnapshotCount + Math.Min(0, snapshotDelta),
            Math.Max(0, snapshotDelta),
            PlannerDataLimits.MaxTotalSnapshots,
            "TotalSnapshotsMaximum");
        return result;
    }

    public static ValidationResult ValidateCanAddPeriod(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxPeriodsPerSemester, "PeriodScheduleMaximum");

    public static ValidationResult ValidateCanAddCourseLabel(int existingCount) =>
        ValidateAddition(existingCount, 1, PlannerDataLimits.MaxLabelsPerCourse, "CourseLabelsMaximum");

    public static ValidationResult ValidateCanAddCourseLabel(int existingInCourse, int totalReferenceCount)
    {
        var result = ValidateCanAddCourseLabel(existingInCourse);
        AppendAtLimit(
            result,
            totalReferenceCount,
            PlannerDataLimits.MaxTotalLabelReferences,
            "TotalLabelReferencesMaximum");
        return result;
    }

    public static ValidationResult ValidateCanAddMeeting(int existingCount) =>
        Validate(existingCount, PlannerDataLimits.MaxMeetingsPerCourse, "MeetingTimesMaximum");

    public static ValidationResult ValidateLabelAddition(int existingCount, int additionalCount) =>
        ValidateAddition(existingCount, additionalCount, PlannerDataLimits.MaxLabels, "LabelCatalogMaximum");

    public static ValidationResult ValidateCourseAddition(int existingCount, int additionalCount) =>
        ValidateAddition(existingCount, additionalCount, PlannerDataLimits.MaxCourses, "CourseCatalogMaximum");

    public static ValidationResult ValidateCourseLabelReferenceChange(
        int totalReferenceCount,
        int originalCourseReferenceCount,
        int candidateCourseReferenceCount)
    {
        if (originalCourseReferenceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(originalCourseReferenceCount));
        if (candidateCourseReferenceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(candidateCourseReferenceCount));
        if (originalCourseReferenceCount > totalReferenceCount)
            throw new ArgumentOutOfRangeException(nameof(originalCourseReferenceCount));

        var result = new ValidationResult();
        var retainedCount = totalReferenceCount - originalCourseReferenceCount;
        AppendAdditionLimit(
            result,
            retainedCount,
            candidateCourseReferenceCount,
            PlannerDataLimits.MaxTotalLabelReferences,
            "TotalLabelReferencesMaximum");
        return result;
    }

    private static ValidationResult Validate(int existingCount, int maximum, string errorCode)
    {
        if (existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(existingCount));
        var result = new ValidationResult();
        if (existingCount >= maximum)
            result.Error(errorCode, maximum.ToString());
        return result;
    }

    private static ValidationResult ValidateAddition(
        int existingCount,
        int additionalCount,
        int maximum,
        string errorCode)
    {
        var result = new ValidationResult();
        AppendAdditionLimit(result, existingCount, additionalCount, maximum, errorCode);
        return result;
    }

    private static void AppendAtLimit(
        ValidationResult result,
        int existingCount,
        int maximum,
        string errorCode)
    {
        if (existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(existingCount));
        if (existingCount >= maximum)
            result.Error(errorCode, maximum.ToString());
    }

    private static void AppendAdditionLimit(
        ValidationResult result,
        int existingCount,
        int additionalCount,
        int maximum,
        string errorCode)
    {
        if (existingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(existingCount));
        if (additionalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalCount));
        if (existingCount > maximum || additionalCount > maximum - existingCount)
            result.Error(errorCode, maximum.ToString());
    }
}

namespace CoursePlanner.Core;

public static class PlannerDocumentTextCapacity
{
    public static long Count(PlannerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var total = (long)Length(document.SchemaVersion);
        foreach (var semester in document.Semesters)
            total += Count(semester);
        foreach (var label in document.Labels)
            total += Count(label);
        foreach (var course in document.CourseLibrary)
            total += Count(course);
        foreach (var plan in document.Plans)
            total += Count(plan);
        foreach (var planId in document.Settings.OpenPlanIds)
            total += Length(planId);
        // CurrentSemesterId and CurrentPlanId are aliases of identifiers already
        // represented by the catalog (and, for a current plan, the open-tab list).
        // Keeping them capacity-neutral makes pure context switches and fallback
        // after destructive operations unable to overflow the semantic text cap.
        return total;
    }

    public static long Count(Semester semester) =>
        Length(semester.SemesterId) + Length(semester.SemesterName);

    public static long Count(CourseLabel label) => Length(label.Name);

    public static long Count(CourseOffering course)
    {
        var total =
            Length(course.OfferingId) +
            Length(course.SemesterId) +
            Length(course.CourseName) +
            Length(course.Teacher) +
            Length(course.Location) +
            Length(course.CourseGroupType) +
            Length(course.StudyType) +
            Length(course.Notes) +
            Length(course.Color);
        foreach (var label in course.Labels)
            total += Length(label);
        foreach (var meeting in course.MeetingTimes)
            total += Length(meeting.Weeks);
        return total;
    }

    public static long Count(SelectionPlan plan)
    {
        var total = Length(plan.PlanId) + Length(plan.SemesterId) + Length(plan.PlanName);
        foreach (var snapshot in plan.Snapshots)
            total += Length(snapshot.SnapshotId) + Length(snapshot.CourseOfferingId);
        return total;
    }

    public static ValidationResult ValidateChange(
        long currentCharacterCount,
        long replacedCharacterCount,
        long replacementCharacterCount)
    {
        if (currentCharacterCount < 0)
            throw new ArgumentOutOfRangeException(nameof(currentCharacterCount));
        if (replacedCharacterCount < 0 || replacedCharacterCount > currentCharacterCount)
            throw new ArgumentOutOfRangeException(nameof(replacedCharacterCount));
        if (replacementCharacterCount < 0)
            throw new ArgumentOutOfRangeException(nameof(replacementCharacterCount));

        var validation = new ValidationResult();
        var retained = currentCharacterCount - replacedCharacterCount;
        if (retained > PlannerDataLimits.MaxAggregateTextCharacters ||
            replacementCharacterCount > PlannerDataLimits.MaxAggregateTextCharacters - retained)
        {
            validation.Error(
                "AggregateTextMaximum",
                PlannerDataLimits.MaxAggregateTextCharacters.ToString());
        }
        return validation;
    }

    public static ValidationResult ValidateDelta(long currentCharacterCount, long characterDelta)
    {
        if (characterDelta < -currentCharacterCount)
            throw new ArgumentOutOfRangeException(nameof(characterDelta));
        return ValidateChange(
            currentCharacterCount,
            characterDelta < 0 ? -characterDelta : 0,
            characterDelta > 0 ? characterDelta : 0);
    }

    private static int Length(string? value) => value?.Length ?? 0;
}

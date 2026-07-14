namespace CoursePlanner.Core;

public static class LabelRules
{
    public static ValidationResult Validate(
        CourseLabel candidate,
        IEnumerable<CourseLabel> existing,
        CourseLabel? original = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        var result = new ValidationResult();
        var existingSample = existing.Take(PlannerDataLimits.MaxLabels + 1).ToList();
        var rawName = candidate.Name ?? "";
        var nameTooLong = rawName.Length > PlannerDataLimits.MaxTextFieldLength;
        var name = nameTooLong ? "" : TextRules.NormalizeIdentityText(rawName);

        if (name.Length == 0)
        {
            if (!nameTooLong)
                result.Error("LabelNameRequired");
        }
        if (nameTooLong)
            result.Error("LabelNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (!Enum.IsDefined(candidate.Kind))
            result.Error("InvalidLabelKind");
        if (original is null && existingSample.Count >= PlannerDataLimits.MaxLabels)
            result.Error("LabelCatalogMaximum", PlannerDataLimits.MaxLabels.ToString());
        if (name.Length > 0 && existingSample.Any(label =>
                !ReferenceEquals(label, original) &&
                TextRules.IsSameLabel(label.Name, name)))
        {
            result.Error("LabelNameDuplicate");
        }

        return result;
    }

    public static ValidationResult ValidateCourseReferences(
        CourseOffering course,
        IEnumerable<CourseLabel> catalog)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(catalog);

        var result = new ValidationResult();
        var kindsByIdentity = new Dictionary<string, LabelKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in catalog.Take(PlannerDataLimits.MaxLabels + 1))
        {
            if (label is null || !Enum.IsDefined(label.Kind))
                continue;
            var identity = TextRules.NormalizeIdentityText(label.Name);
            if (identity.Length > 0)
                kindsByIdentity.TryAdd(identity, label.Kind);
        }

        foreach (var name in course.Labels.Take(PlannerDataLimits.MaxLabelsPerCourse + 1))
            ValidateReference(name, LabelKind.Ordinary, kindsByIdentity, result);
        ValidateReference(course.CourseGroupType, LabelKind.CourseGroupType, kindsByIdentity, result);
        ValidateReference(course.StudyType, LabelKind.StudyType, kindsByIdentity, result);
        return result;
    }

    private static void ValidateReference(
        string? name,
        LabelKind expectedKind,
        IReadOnlyDictionary<string, LabelKind> kindsByIdentity,
        ValidationResult result)
    {
        var identity = TextRules.NormalizeIdentityText(name);
        if (identity.Length == 0)
            return;
        if (!kindsByIdentity.TryGetValue(identity, out var actualKind))
            result.Error("LabelReference.Missing", identity);
        else if (actualKind != expectedKind)
            result.Error("LabelReference.KindMismatch", identity);
    }
}

using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed record CourseEditNumericValues(decimal Credits, int? EnrolledCount, int? Capacity);

public sealed class CourseEditNumericResult
{
    internal CourseEditNumericResult(ValidationResult validation, CourseEditNumericValues? value)
    {
        Validation = validation;
        Value = value;
    }

    public ValidationResult Validation { get; }
    public IReadOnlyList<ValidationIssue> Errors => Validation.Errors;
    public bool IsValid => Validation.IsValid;
    public CourseEditNumericValues? Value { get; }
}

public static class CourseEditNumericInput
{
    public static CourseEditNumericResult Map(double credits, double enrolledCount, double capacity)
    {
        var validation = new ValidationResult();
        var mappedCredits = MapCredits(credits, validation);
        var mappedEnrolled = MapOptionalCount(enrolledCount, "Enrolled", validation);
        var mappedCapacity = MapOptionalCount(capacity, "Capacity", validation);
        var value = validation.IsValid
            ? new CourseEditNumericValues(mappedCredits, mappedEnrolled, mappedCapacity)
            : null;
        return new CourseEditNumericResult(validation, value);
    }

    private static decimal MapCredits(double value, ValidationResult validation)
    {
        if (!double.IsFinite(value))
        {
            validation.Error("CreditsRequired");
            return 0;
        }

        if (value < 0)
        {
            validation.Error("CreditsNonNegative");
            return 0;
        }

        if (value > (double)CourseNumericRules.MaximumCredits)
        {
            validation.Error("CreditsMaximum", CourseNumericRules.MaximumCredits.ToString());
            return 0;
        }

        return (decimal)value;
    }

    private static int? MapOptionalCount(double value, string field, ValidationResult validation)
    {
        if (double.IsNaN(value))
            return null;

        if (double.IsNegativeInfinity(value) || value < 0)
        {
            validation.Error($"{field}NonNegative");
            return null;
        }

        if (!double.IsFinite(value) || value > CourseNumericRules.MaximumPeopleCount)
        {
            validation.Error($"{field}Maximum", CourseNumericRules.MaximumPeopleCount.ToString());
            return null;
        }

        if (Math.Truncate(value) != value)
        {
            validation.Error($"{field}WholeNumber");
            return null;
        }

        return (int)value;
    }
}

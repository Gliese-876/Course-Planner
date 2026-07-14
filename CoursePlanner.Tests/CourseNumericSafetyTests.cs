using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class CourseNumericSafetyTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(1e29)]
    public void NumericInputRejectsUnrepresentableCreditsWithoutThrowing(double rawCredits)
    {
        var result = CourseEditNumericInput.Map(rawCredits, double.NaN, double.NaN);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Code is "CreditsRequired" or "CreditsMaximum");
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(1e29)]
    [InlineData(2147483648d)]
    [InlineData(12.5d)]
    public void NumericInputRejectsInvalidEnrollmentWithoutThrowing(double rawEnrollment)
    {
        var result = CourseEditNumericInput.Map(3, rawEnrollment, double.NaN);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code.StartsWith("Enrolled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(1e29)]
    [InlineData(2147483648d)]
    [InlineData(12.5d)]
    public void NumericInputRejectsInvalidCapacityWithoutThrowing(double rawCapacity)
    {
        var result = CourseEditNumericInput.Map(3, double.NaN, rawCapacity);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code.StartsWith("Capacity", StringComparison.Ordinal));
    }

    [Fact]
    public void NumericInputMapsOptionalBlanksAndInclusiveLimits()
    {
        var blank = CourseEditNumericInput.Map(0, double.NaN, double.NaN);
        var limits = CourseEditNumericInput.Map(
            (double)CourseNumericRules.MaximumCredits,
            CourseNumericRules.MaximumPeopleCount,
            CourseNumericRules.MaximumPeopleCount);

        Assert.True(blank.IsValid);
        Assert.Equal(0, blank.Value!.Credits);
        Assert.Null(blank.Value.EnrolledCount);
        Assert.Null(blank.Value.Capacity);
        Assert.True(limits.IsValid);
        Assert.Equal(CourseNumericRules.MaximumCredits, limits.Value!.Credits);
        Assert.Equal(CourseNumericRules.MaximumPeopleCount, limits.Value.EnrolledCount);
        Assert.Equal(CourseNumericRules.MaximumPeopleCount, limits.Value.Capacity);
    }

    [Fact]
    public void ValidatorEnforcesEveryNumericDomainBoundary()
    {
        var semester = SeedData.Create().Semesters[0];
        var course = new CourseOffering
        {
            CourseName = "Unsafe",
            SemesterId = semester.SemesterId,
            Credits = CourseNumericRules.MaximumCredits + 0.01m,
            EnrolledCount = -1,
            Capacity = CourseNumericRules.MaximumPeopleCount + 1,
            Color = "#112233"
        };

        var validation = CourseValidator.Validate(course, semester, allowUnscheduled: true);

        Assert.Contains(validation.Errors, error => error.Code == "CreditsMaximum");
        Assert.Contains(validation.Errors, error => error.Code == "EnrolledNonNegative");
        Assert.Contains(validation.Errors, error => error.Code == "CapacityMaximum");
    }

    [Fact]
    public void ValidatorAllowsInclusiveLimitsAndWarnsWhenEnrollmentExceedsPositiveCapacity()
    {
        var semester = SeedData.Create().Semesters[0];
        var course = new CourseOffering
        {
            CourseName = "Full",
            SemesterId = semester.SemesterId,
            Credits = CourseNumericRules.MaximumCredits,
            EnrolledCount = CourseNumericRules.MaximumPeopleCount,
            Capacity = CourseNumericRules.MaximumPeopleCount - 1,
            Color = "#112233"
        };

        var validation = CourseValidator.Validate(course, semester, allowUnscheduled: true);

        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, warning => warning.Code == "EnrolledExceedsCapacity");
    }

    [Fact]
    public void TotalCreditsSaturatesInsteadOfOverflowingForCorruptedDocuments()
    {
        var semesterId = "semester";
        var first = new CourseOffering { OfferingId = "first", SemesterId = semesterId, Credits = decimal.MaxValue };
        var second = new CourseOffering { OfferingId = "second", SemesterId = semesterId, Credits = decimal.MaxValue };
        var plan = new SelectionPlan
        {
            SemesterId = semesterId,
            Snapshots =
            [
                new PlanCourseSnapshot { CourseOfferingId = first.OfferingId },
                new PlanCourseSnapshot { CourseOfferingId = second.OfferingId }
            ]
        };

        var total = SelectionPlanMetrics.TotalCredits(plan, [first, second]);

        Assert.Equal(decimal.MaxValue, total);
    }

    [Fact]
    public void BothEditorsTakeMaximumValuesFromSharedCoreRules()
    {
        var root = FindRepositoryRoot();
        var planner = File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var library = File.ReadAllText(Path.Combine(root, "CoursePlanner", "Pages", "CourseLibraryPage.xaml.cs"));

        foreach (var source in new[] { planner, library })
        {
            Assert.Contains("CourseNumericRules.MaximumCredits", source);
            Assert.Contains("CourseNumericRules.MaximumPeopleCount", source);
            Assert.DoesNotContain("(decimal)CreditsBox.Value", source);
            Assert.DoesNotContain("(decimal)LibraryCreditsBox.Value", source);
            Assert.DoesNotContain("(int)Math.Round(EnrolledBox.Value)", source);
            Assert.DoesNotContain("(int)Math.Round(LibraryEnrolledBox.Value)", source);
        }
    }

    [Theory]
    [InlineData(LanguageMode.English)]
    [InlineData(LanguageMode.SimplifiedChinese)]
    public void EveryNumericValidationIssueHasAFormattableLocalizedMessage(LanguageMode language)
    {
        var localizer = new AppLocalizer(language);
        var issues = new[]
        {
            Issue("CreditsRequired"),
            Issue("CreditsMaximum", "100"),
            Issue("EnrolledNonNegative"),
            Issue("EnrolledMaximum", "1000000"),
            Issue("EnrolledWholeNumber"),
            Issue("CapacityNonNegative"),
            Issue("CapacityMaximum", "1000000"),
            Issue("CapacityWholeNumber"),
            Issue("EnrolledExceedsCapacity")
        };

        var summary = localizer.ValidationSummary(issues);

        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    private static ValidationIssue Issue(string code, params string[] parameters) => new()
    {
        Code = code,
        Parameters = parameters.ToList()
    };

    private static string FindRepositoryRoot() => RepositoryPaths.Root;
}

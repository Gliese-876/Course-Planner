using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class CourseLabelSemanticsTests
{
    [Fact]
    public void SeedDataUsesRequestedEditableDefaultTaxonomy()
    {
        var document = SeedData.Create();

        Assert.Equal(
            new[] { PlannerLabels.General, PlannerLabels.Major, PlannerLabels.Free },
            document.Labels
                .Where(label => label.Kind == LabelKind.CourseGroupType)
                .OrderBy(label => label.DisplayOrder)
                .Select(label => label.Name));
        Assert.Equal(
            new[] { PlannerLabels.Core, PlannerLabels.Required, PlannerLabels.Elective },
            document.Labels
                .Where(label => label.Kind == LabelKind.StudyType)
                .OrderBy(label => label.DisplayOrder)
                .Select(label => label.Name));

        var defaultLabel = document.Labels.Single(label => label.Name == PlannerLabels.Free);
        Assert.True(document.Labels.Remove(defaultLabel));
        Assert.DoesNotContain(document.Labels, label => label.Name == PlannerLabels.Free);
    }

    [Fact]
    public void NewDefaultsLocalizeAndCanonicalize()
    {
        var chinese = new AppLocalizer(LanguageMode.SimplifiedChinese);

        Assert.Equal("通识", chinese.LocalizeKnownLabel(PlannerLabels.General));
        Assert.Equal("专业", chinese.LocalizeKnownLabel(PlannerLabels.Major));
        Assert.Equal("自由", chinese.LocalizeKnownLabel(PlannerLabels.Free));
        Assert.Equal("核心", chinese.LocalizeKnownLabel(PlannerLabels.Core));
        Assert.Equal(PlannerLabels.General, chinese.CanonicalizeKnownLabel("通识"));
        Assert.Equal(PlannerLabels.Core, chinese.CanonicalizeKnownLabel("核心"));
    }

    [Theory]
    [InlineData("通识", CourseTypeSemantic.General)]
    [InlineData("通识教育课", CourseTypeSemantic.General)]
    [InlineData("公共基础课程", CourseTypeSemantic.General)]
    [InlineData("General Studies", CourseTypeSemantic.General)]
    [InlineData("Gen-Ed", CourseTypeSemantic.General)]
    [InlineData("专业", CourseTypeSemantic.Major)]
    [InlineData("专业方向课", CourseTypeSemantic.Major)]
    [InlineData("Major Required", CourseTypeSemantic.Major)]
    [InlineData("Professional Foundation", CourseTypeSemantic.Major)]
    [InlineData("专业任选课", CourseTypeSemantic.Major)]
    [InlineData("通识任选课", CourseTypeSemantic.General)]
    [InlineData("自由", CourseTypeSemantic.Free)]
    [InlineData("自由选修课", CourseTypeSemantic.Free)]
    [InlineData("任选课程", CourseTypeSemantic.Free)]
    [InlineData("跨专业选修", CourseTypeSemantic.Free)]
    [InlineData("Free Elective", CourseTypeSemantic.Free)]
    [InlineData("Unrestricted elective", CourseTypeSemantic.Free)]
    public void CourseTypeClassifierAcceptsDefaultAndCustomExpressions(
        string label,
        CourseTypeSemantic expected)
    {
        Assert.Equal(expected, CourseLabelSemantics.ClassifyCourseType(label));
    }

    [Theory]
    [InlineData("非专业课程")]
    [InlineData("非通识课程")]
    [InlineData("非自由课程")]
    [InlineData("非任选课程")]
    [InlineData("非跨专业课程")]
    [InlineData("Non-major Course")]
    [InlineData("Not General")]
    public void NegatedCourseTypesRemainNeutral(string label)
    {
        Assert.Equal(CourseTypeSemantic.Unknown, CourseLabelSemantics.ClassifyCourseType(label));
    }

    [Fact]
    public void ConflictingStrongCourseTypeSignalsRemainNeutral()
    {
        Assert.Equal(
            CourseTypeSemantic.Unknown,
            CourseLabelSemantics.ClassifyCourseType("专业通识融合课"));
    }

    [Theory]
    [InlineData("核心", StudyTypeSemantic.Core)]
    [InlineData("专业核心课", StudyTypeSemantic.Core)]
    [InlineData("Core Course", StudyTypeSemantic.Core)]
    [InlineData("Compulsory Core", StudyTypeSemantic.Core)]
    [InlineData("必修", StudyTypeSemantic.Required)]
    [InlineData("公共必修课", StudyTypeSemantic.Required)]
    [InlineData("Required Course", StudyTypeSemantic.Required)]
    [InlineData("Mandatory", StudyTypeSemantic.Required)]
    [InlineData("选修", StudyTypeSemantic.Elective)]
    [InlineData("自由选修课", StudyTypeSemantic.Elective)]
    [InlineData("Elective", StudyTypeSemantic.Elective)]
    [InlineData("Optional Course", StudyTypeSemantic.Elective)]
    public void StudyTypeClassifierAcceptsDefaultAndCustomExpressions(
        string label,
        StudyTypeSemantic expected)
    {
        Assert.Equal(expected, CourseLabelSemantics.ClassifyStudyType(label));
    }

    [Theory]
    [InlineData("非核心课程")]
    [InlineData("非必修课程")]
    [InlineData("非选修课程")]
    [InlineData("非任选课程")]
    [InlineData("非自选课程")]
    [InlineData("Non-core Course")]
    [InlineData("Not Required")]
    public void NegatedStudyTypesRemainNeutral(string label)
    {
        Assert.Equal(StudyTypeSemantic.Unknown, CourseLabelSemantics.ClassifyStudyType(label));
    }

    [Fact]
    public void NegatedHigherTierCanFallThroughToAnExplicitLowerTier()
    {
        Assert.Equal(
            StudyTypeSemantic.Required,
            CourseLabelSemantics.ClassifyStudyType("非核心必修"));
        Assert.Equal(
            StudyTypeSemantic.Elective,
            CourseLabelSemantics.ClassifyStudyType("Not required, elective"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("创新实践")]
    [InlineData("Studio")]
    public void UnknownCustomLabelsRemainNeutral(string? label)
    {
        Assert.Equal(CourseTypeSemantic.Unknown, CourseLabelSemantics.ClassifyCourseType(label));
        Assert.Equal(StudyTypeSemantic.Unknown, CourseLabelSemantics.ClassifyStudyType(label));
    }

    [Fact]
    public void SmartSortUsesBothSemanticDimensionsAndKeepsStudyRequirementDominant()
    {
        var courses = new[]
        {
            Course("free-elective", "自由选修", "Optional Course"),
            Course("major-elective", "专业方向", "Elective"),
            Course("general-required", "通识教育", "Mandatory"),
            Course("major-required", "Professional", "Required Course"),
            Course("general-core", "Liberal Arts", "Core Course"),
            Course("major-core", "Major", "专业核心")
        };
        var plan = new SelectionPlan { SemesterId = SemesterId };
        for (var index = 0; index < courses.Length; index++)
        {
            plan.Snapshots.Add(new PlanCourseSnapshot
            {
                SnapshotId = $"{courses[index].OfferingId}-snapshot",
                CourseOfferingId = courses[index].OfferingId,
                RegistrationOrder = index
            });
        }

        var recommendation = RegistrationPriorityService.Recommend(plan, courses);

        Assert.Equal(
            new[]
            {
                "major-core",
                "general-core",
                "major-required",
                "general-required",
                "major-elective",
                "free-elective"
            },
            recommendation.Select(item => item.OfferingId));
        Assert.Equal(CourseTypeSemantic.Major, recommendation[0].CourseType);
        Assert.Equal(StudyTypeSemantic.Core, recommendation[0].StudyType);
        Assert.True(recommendation[0].AcademicValue > recommendation[1].AcademicValue);
    }

    [Fact]
    public void SemanticallyEquivalentCustomLabelsStillCountParallelOfferings()
    {
        var target = Course("target", "专业", "必修");
        target.CourseName = "Parallel Course";
        var alternative = Course("alternative", "Major Course", "Required Course");
        alternative.CourseName = target.CourseName;
        alternative.EnrolledCount = 0;
        alternative.MeetingTimes[0].Weekday = 2;
        var plan = new SelectionPlan
        {
            SemesterId = SemesterId,
            Snapshots =
            {
                new PlanCourseSnapshot
                {
                    SnapshotId = "target-snapshot",
                    CourseOfferingId = target.OfferingId,
                    RegistrationOrder = 0
                }
            }
        };

        var analysis = Assert.Single(
            RegistrationPriorityService.Analyze(plan, new[] { target, alternative }));

        Assert.InRange(analysis.EffectiveAlternatives, 0.99d, 1d);
    }

    private const string SemesterId = "semester";

    private static CourseOffering Course(string id, string courseType, string studyType) => new()
    {
        OfferingId = id,
        SemesterId = SemesterId,
        CourseName = id,
        Credits = 3m,
        CourseGroupType = courseType,
        StudyType = studyType,
        EnrolledCount = 5,
        Capacity = 10,
        MeetingTimes =
        {
            new MeetingTime
            {
                Weekday = 1,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = "1-16"
            }
        }
    };
}

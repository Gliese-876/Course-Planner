using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class DataLimitValidationTests
{
    [Fact]
    public void SharedLimitsMatchPersistenceAndImportSafetyEnvelope()
    {
        Assert.Equal(2_048, PlannerDataLimits.MaxTextFieldLength);
        Assert.Equal(32_768, PlannerDataLimits.MaxCourseLabelEditorLength);
        Assert.Equal(MeetingWeeksParser.MaxExpressionLength, PlannerDataLimits.MaxMeetingWeeksLength);
        Assert.Equal(128, PlannerDataLimits.MaxSemesters);
        Assert.Equal(512, PlannerDataLimits.MaxLabels);
        Assert.Equal(5_000, PlannerDataLimits.MaxCourses);
        Assert.Equal(5_000, PlannerDataLimits.MaxPlans);
        Assert.Equal(128, PlannerDataLimits.MaxPeriodsPerSemester);
        Assert.Equal(128, PlannerDataLimits.MaxLabelsPerCourse);
        Assert.Equal(32, PlannerDataLimits.MaxMeetingsPerCourse);
        Assert.Equal(2_000, PlannerDataLimits.MaxMeetingRowsPerPlan);
        Assert.Equal(5_000, PlannerDataLimits.MaxSnapshotsPerPlan);
        Assert.Equal(100_000, PlannerDataLimits.MaxTotalSnapshots);
        Assert.Equal(100_000, PlannerDataLimits.MaxTotalLabelReferences);
        Assert.Equal(5_000_000, PlannerDataLimits.MaxAggregateTextCharacters);
        Assert.Equal(64 * 1024 * 1024, PlannerDataLimits.MaxImportTextCharacters);
        Assert.Equal(64L * 1024 * 1024, PlannerDataLimits.MaxImportFileBytes);
    }

    [Fact]
    public void CourseValidatorRejectsEveryOversizedEditableTextField()
    {
        var semester = SeedData.Create().Semesters[0];
        var oversized = new string('x', PlannerDataLimits.MaxTextFieldLength + 1);
        var cases = new (string Code, Action<CourseOffering> Mutate)[]
        {
            ("CourseNameTooLong", course => course.CourseName = oversized),
            ("TeacherTooLong", course => course.Teacher = oversized),
            ("LocationTooLong", course => course.Location = oversized),
            ("CourseGroupTypeTooLong", course => course.CourseGroupType = oversized),
            ("StudyTypeTooLong", course => course.StudyType = oversized),
            ("CourseNotesTooLong", course => course.Notes = oversized),
            ("CourseColorTooLong", course => course.Color = oversized),
            ("CourseLabelTooLong", course => course.Labels.Add(oversized))
        };

        foreach (var (code, mutate) in cases)
        {
            var course = ValidCourse(semester);
            mutate(course);

            var validation = CourseValidator.Validate(course, semester, allowUnscheduled: true);

            Assert.Contains(validation.Errors, issue => issue.Code == code);
        }
    }

    [Fact]
    public void CourseValidatorBoundsLabelsMeetingsAndWeekExpressionBeforeDetailedWork()
    {
        var semester = SeedData.Create().Semesters[0];
        var course = ValidCourse(semester);
        course.Labels = Enumerable.Range(0, PlannerDataLimits.MaxLabelsPerCourse + 1)
            .Select(index => $"label-{index}")
            .ToList();
        var meeting = course.MeetingTimes[0];
        meeting.Weeks = new string('1', PlannerDataLimits.MaxMeetingWeeksLength + 1);
        course.MeetingTimes = Enumerable.Range(0, PlannerDataLimits.MaxMeetingsPerCourse + 1)
            .Select(_ => JsonDefaults.Clone(meeting))
            .ToList();

        var validation = CourseValidator.Validate(course, semester, allowUnscheduled: true);

        Assert.Contains(validation.Errors, issue => issue.Code == "CourseLabelsMaximum");
        Assert.Contains(validation.Errors, issue => issue.Code == "MeetingTimesMaximum");
        Assert.Contains(validation.Errors, issue => issue.Code == "MeetingWeeksTooLong");
    }

    [Fact]
    public void SemesterRulesRejectOversizedNameAndPeriodCollection()
    {
        var semester = SeedData.Create().Semesters[0];
        semester.SemesterName = new string('s', PlannerDataLimits.MaxTextFieldLength + 1);
        semester.PeriodSchedule = Enumerable.Range(1, PlannerDataLimits.MaxPeriodsPerSemester + 1)
            .Select(period => new PeriodDefinition
            {
                Period = period,
                Start = new TimeOnly(8, 0),
                End = new TimeOnly(8, 45)
            })
            .ToList();

        var validation = SemesterRules.ValidateSemester(semester, []);

        Assert.Contains(validation.Errors, issue => issue.Code == "SemesterNameTooLong");
        Assert.Contains(validation.Errors, issue => issue.Code == "PeriodScheduleMaximum");
    }

    [Fact]
    public void LabelRulesValidateTextKindUniquenessAndCatalogCapacity()
    {
        var oversized = new CourseLabel
        {
            Name = new string('l', PlannerDataLimits.MaxTextFieldLength + 1),
            Kind = (LabelKind)999
        };
        var existing = Enumerable.Range(0, PlannerDataLimits.MaxLabels)
            .Select(index => new CourseLabel { Name = $"label-{index}", Kind = LabelKind.Ordinary })
            .ToList();

        var validation = LabelRules.Validate(oversized, existing);

        Assert.Contains(validation.Errors, issue => issue.Code == "LabelNameTooLong");
        Assert.Contains(validation.Errors, issue => issue.Code == "InvalidLabelKind");
        Assert.Contains(validation.Errors, issue => issue.Code == "LabelCatalogMaximum");

        var duplicate = LabelRules.Validate(
            new CourseLabel { Name = " LABEL-1 ", Kind = LabelKind.Ordinary },
            existing);
        Assert.Contains(duplicate.Errors, issue => issue.Code == "LabelNameDuplicate");
    }

    [Fact]
    public void PlanRulesEnforceBothDomainAndExportSafeNameLimits()
    {
        var ordinaryLongWindowsName = new string('p', WindowsFileNameRules.MaxComponentLength + 1);
        var plan = new SelectionPlan
        {
            PlanId = "new-plan",
            SemesterId = "semester",
            PlanName = ordinaryLongWindowsName
        };

        Assert.Contains(
            PlanRules.Validate(plan, []).Errors,
            issue => issue.Code == "FileNameTooLong");
        Assert.False(WindowsFileNameRules.ValidateFileComponent(ordinaryLongWindowsName).IsValid);

        plan.PlanName = new string('p', PlannerDataLimits.MaxTextFieldLength + 1);
        plan.Snapshots = Enumerable.Range(0, PlannerDataLimits.MaxSnapshotsPerPlan + 1)
            .Select(index => new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-{index}",
                CourseOfferingId = $"course-{index}"
            })
            .ToList();
        var existing = Enumerable.Range(0, PlannerDataLimits.MaxPlans)
            .Select(index => new SelectionPlan
            {
                PlanId = $"plan-{index}",
                SemesterId = "semester",
                PlanName = $"Plan {index}"
            })
            .ToList();

        var validation = PlanRules.Validate(plan, existing);

        Assert.Contains(validation.Errors, issue => issue.Code == "PlanNameTooLong");
        Assert.Contains(validation.Errors, issue => issue.Code == "PlanSnapshotsMaximum");
        Assert.Contains(validation.Errors, issue => issue.Code == "PlanCatalogMaximum");
    }

    [Theory]
    [InlineData("semester")]
    [InlineData("label")]
    [InlineData("course")]
    [InlineData("plan")]
    [InlineData("snapshot")]
    public void CapacityRulesRejectAtLimitButAllowOneSlotBelow(string kind)
    {
        (int Maximum, Func<int, ValidationResult> Validate) selected = kind switch
        {
            "semester" => (PlannerDataLimits.MaxSemesters, PlannerCapacityRules.ValidateCanAddSemester),
            "label" => (PlannerDataLimits.MaxLabels, PlannerCapacityRules.ValidateCanAddLabel),
            "course" => (PlannerDataLimits.MaxCourses, PlannerCapacityRules.ValidateCanAddCourse),
            "plan" => (PlannerDataLimits.MaxPlans, PlannerCapacityRules.ValidateCanAddPlan),
            "snapshot" => (PlannerDataLimits.MaxSnapshotsPerPlan, PlannerCapacityRules.ValidateCanAddSnapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var (maximum, validate) = selected;

        Assert.True(validate(maximum - 1).IsValid);
        Assert.False(validate(maximum).IsValid);
    }

    [Fact]
    public void NestedCapacityRulesExposePeriodLabelMeetingAndTotalSnapshotLimits()
    {
        Assert.False(PlannerCapacityRules.ValidateCanAddPeriod(PlannerDataLimits.MaxPeriodsPerSemester).IsValid);
        Assert.False(PlannerCapacityRules.ValidateCanAddCourseLabel(PlannerDataLimits.MaxLabelsPerCourse).IsValid);
        Assert.False(PlannerCapacityRules.ValidateCanAddMeeting(PlannerDataLimits.MaxMeetingsPerCourse).IsValid);
        var totalSnapshots = PlannerCapacityRules.ValidateCanAddSnapshot(
            existingInPlan: 0,
            totalSnapshotCount: PlannerDataLimits.MaxTotalSnapshots);
        Assert.Contains(totalSnapshots.Errors, issue => issue.Code == "TotalSnapshotsMaximum");
        var totalLabels = PlannerCapacityRules.ValidateCanAddCourseLabel(
            existingInCourse: 0,
            totalReferenceCount: PlannerDataLimits.MaxTotalLabelReferences);
        Assert.Contains(totalLabels.Errors, issue => issue.Code == "TotalLabelReferencesMaximum");
    }

    [Fact]
    public void DeltaCapacityRulesAllowReplacementButRejectActualOverflow()
    {
        Assert.True(PlannerCapacityRules.ValidateSnapshotAddition(
            PlannerDataLimits.MaxSnapshotsPerPlan,
            PlannerDataLimits.MaxTotalSnapshots,
            additionalCount: 0).IsValid);
        Assert.False(PlannerCapacityRules.ValidateSnapshotAddition(
            PlannerDataLimits.MaxSnapshotsPerPlan - 1,
            PlannerDataLimits.MaxTotalSnapshots - 1,
            additionalCount: 2).IsValid);
        Assert.False(PlannerCapacityRules.ValidateCourseLabelReferenceChange(
            PlannerDataLimits.MaxTotalLabelReferences,
            originalCourseReferenceCount: 1,
            candidateCourseReferenceCount: 2).IsValid);
        Assert.True(PlannerCapacityRules.ValidateCourseLabelReferenceChange(
            PlannerDataLimits.MaxTotalLabelReferences,
            originalCourseReferenceCount: 2,
            candidateCourseReferenceCount: 2).IsValid);
    }

    [Fact]
    public void AggregateTextCapacityUsesReplacementDeltaAtExactBoundary()
    {
        Assert.True(PlannerDocumentTextCapacity.ValidateChange(
            PlannerDataLimits.MaxAggregateTextCharacters,
            replacedCharacterCount: 10,
            replacementCharacterCount: 10).IsValid);
        var overflow = PlannerDocumentTextCapacity.ValidateChange(
            PlannerDataLimits.MaxAggregateTextCharacters,
            replacedCharacterCount: 10,
            replacementCharacterCount: 11);

        Assert.Contains(overflow.Errors, issue => issue.Code == "AggregateTextMaximum");
    }

    [Fact]
    public void AggregateTextCapacityExcludesCurrentContextReferenceCopies()
    {
        var document = SeedData.Create();
        var expected = PlannerDocumentTextCapacity.Count(document);

        document.Settings.CurrentSemesterId = new string('s', PlannerDataLimits.MaxTextFieldLength);
        document.Settings.CurrentPlanId = new string('p', PlannerDataLimits.MaxTextFieldLength);

        Assert.Equal(expected, PlannerDocumentTextCapacity.Count(document));
    }

    [Fact]
    public void DomainPlanAddRejectsNetNewSnapshotAtPlanLimitWithoutMutation()
    {
        var semester = SeedData.Create().Semesters[0];
        var snapshots = Enumerable.Range(0, PlannerDataLimits.MaxSnapshotsPerPlan)
            .Select(index => new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-{index}",
                CourseOfferingId = $"course-{index}"
            })
            .ToList();
        var plan = new SelectionPlan { SemesterId = semester.SemesterId, Snapshots = snapshots };
        var course = ValidCourse(semester);
        CourseIdentityService.AssignOfferingId(course);

        var result = PlannerDomainService.AddCourseToPlan(
            plan,
            course,
            semester,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict,
            [course]);

        Assert.False(result.Added);
        Assert.True(result.Cancelled);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "PlanSnapshotsMaximum");
        Assert.Equal(PlannerDataLimits.MaxSnapshotsPerPlan, plan.Snapshots.Count);
    }

    [Theory]
    [InlineData(LanguageMode.English)]
    [InlineData(LanguageMode.SimplifiedChinese)]
    public void NewLimitValidationCodesAreLocalized(LanguageMode language)
    {
        var localizer = new AppLocalizer(language);
        var issues = new[]
        {
            new ValidationIssue { Code = "CourseNameTooLong", Parameters = ["2048"] },
            new ValidationIssue { Code = "CourseLabelsMaximum", Parameters = ["128"] },
            new ValidationIssue { Code = "MeetingTimesMaximum", Parameters = ["32"] },
            new ValidationIssue { Code = "MeetingWeeksTooLong", Parameters = ["1024"] },
            new ValidationIssue { Code = "SemesterNameTooLong", Parameters = ["2048"] },
            new ValidationIssue { Code = "PeriodScheduleMaximum", Parameters = ["128"] },
            new ValidationIssue { Code = "LabelNameTooLong", Parameters = ["2048"] },
            new ValidationIssue { Code = "LabelCatalogMaximum", Parameters = ["512"] },
            new ValidationIssue { Code = "PlanNameTooLong", Parameters = ["2048"] },
            new ValidationIssue { Code = "PlanSnapshotsMaximum", Parameters = ["5000"] },
            new ValidationIssue { Code = "CourseCatalogMaximum", Parameters = ["5000"] },
            new ValidationIssue { Code = "TotalSnapshotsMaximum", Parameters = ["100000"] },
            new ValidationIssue { Code = "TotalLabelReferencesMaximum", Parameters = ["100000"] },
            new ValidationIssue { Code = "AggregateTextMaximum", Parameters = ["5000000"] }
        };

        Assert.All(issues, issue => Assert.False(string.IsNullOrWhiteSpace(localizer.ValidationMessage(issue))));
    }

    private static CourseOffering ValidCourse(Semester semester) => new()
    {
        SemesterId = semester.SemesterId,
        CourseName = "Valid course",
        Teacher = "Teacher",
        Location = "Room",
        Credits = 3m,
        Color = "#123456",
        MeetingTimes =
        [
            new MeetingTime
            {
                Weekday = 1,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = "1-18",
                WeekParity = WeekParity.All
            }
        ]
    };
}

using System.Text.Json;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

public sealed class CoreSafetyInvariantTests
{
    [Fact]
    public void WeeksParserDoesNotSilentlyDropTokensWithinTheExpressionLengthLimit()
    {
        var expression = string.Join(',', Enumerable.Repeat("1", 64).Append("2"));

        var result = MeetingWeeksParser.ParseDetailed(expression, weekCount: 16);

        Assert.True(result.IsValid);
        Assert.False(result.WasBounded);
        Assert.Contains(2, result.Weeks);
    }

    [Fact]
    public void BoundedTextTruncationNeverLeavesAnUnpairedUtf16Surrogate()
    {
        const string value = "ab😀tail";

        Assert.Equal("ab", TextRules.TruncateUtf16(value, 3));
        Assert.Equal("ab😀", TextRules.TruncateUtf16(value, 4));
        Assert.Equal("", TextRules.TruncateUtf16(value, 0));
        Assert.Equal("", TextRules.TruncateUtf16(null, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRules.TruncateUtf16(value, -1));
    }

    [Fact]
    public void TextOperationsReplaceMalformedUtf16WithoutLosingValidSurrogatePairs()
    {
        const string malformed = "A\uD800B\uDC00C😀";
        const string sanitized = "A\uFFFDB\uFFFDC😀";

        Assert.Equal(sanitized, TextRules.SanitizeUtf16(malformed));
        Assert.Equal(sanitized, TextRules.NormalizeIdentityText(malformed));
        Assert.Equal("A\uFFFD", TextRules.TruncateUtf16(malformed, 2));
        Assert.Equal(CourseTypeSemantic.Unknown, CourseLabelSemantics.ClassifyCourseType(malformed));

        var lines = TextRules.WrapTextWithAsciiHyphenation(
            malformed,
            maxLines: 8,
            text => text.Length <= 2);
        Assert.NotEmpty(lines);
        Assert.Equal(sanitized, string.Concat(lines).Replace("-", "", StringComparison.Ordinal));
        Assert.All(string.Concat(lines), character =>
            Assert.False(char.IsSurrogate(character) && character is not '\uD83D' and not '\uDE00'));
    }

    [Fact]
    public void DocumentConsistencySanitizesAllUserEditableTextBeforeIdentityGeneration()
    {
        const string malformed = "bad\uD800text";
        const string sanitized = "bad\uFFFDtext";
        var semester = SemesterWithPeriods((1, "08:00", "08:45"));
        semester.SemesterName = malformed;
        var course = Course(semester, malformed, (1, 1, 1, malformed));
        course.Teacher = malformed;
        course.Location = malformed;
        course.CourseGroupType = malformed;
        course.StudyType = malformed;
        course.Labels = [malformed];
        course.Notes = malformed;
        var plan = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            PlanName = malformed,
            Snapshots = { new PlanCourseSnapshot { CourseOfferingId = course.OfferingId } }
        };
        var document = new PlannerDocument
        {
            Semesters = { semester },
            Labels = { new CourseLabel { Name = malformed } },
            CourseLibrary = { course },
            Plans = { plan }
        };

        DocumentConsistencyService.Ensure(document);

        Assert.Equal(sanitized, semester.SemesterName);
        Assert.Equal(sanitized, document.Labels[0].Name);
        Assert.Equal(sanitized, course.CourseName);
        Assert.Equal(sanitized, course.Teacher);
        Assert.Equal(sanitized, course.Location);
        Assert.Equal(sanitized, course.CourseGroupType);
        Assert.Equal(sanitized, course.StudyType);
        Assert.Equal(sanitized, Assert.Single(course.Labels));
        Assert.Equal(sanitized, course.Notes);
        Assert.Equal(sanitized, Assert.Single(course.MeetingTimes).Weeks);
        Assert.Equal(sanitized, plan.PlanName);
        Assert.Equal(course.OfferingId, Assert.Single(plan.Snapshots).CourseOfferingId);
    }

    [Fact]
    public void DeletingTheOnlyPeriodIsRejectedWithoutMutatingCoursesOrSnapshots()
    {
        var semester = SemesterWithPeriods((1, "08:00", "08:45"));
        var course = Course(semester, "Only", (1, 1, 1, "1"));
        var originalOfferingId = course.OfferingId;
        var originalModifiedAt = course.ModifiedAt;
        var snapshot = new PlanCourseSnapshot { CourseOfferingId = originalOfferingId };

        Assert.Throws<InvalidOperationException>(() =>
            PeriodScheduleService.DeletePeriod(semester, 1, [course], [snapshot]));

        var period = Assert.Single(semester.PeriodSchedule);
        Assert.Equal((1, new TimeOnly(8, 0), new TimeOnly(8, 45)), (period.Period, period.Start, period.End));
        var meeting = Assert.Single(course.MeetingTimes);
        Assert.Equal((1, 1), (meeting.StartPeriod, meeting.EndPeriod));
        Assert.Equal(originalOfferingId, course.OfferingId);
        Assert.Equal(originalModifiedAt, course.ModifiedAt);
        Assert.Equal(originalOfferingId, snapshot.CourseOfferingId);
    }

    [Fact]
    public void DeletingPeriodMapsExtremeMeetingEndpointsWithoutExpandingTheRange()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"));
        var course = Course(semester, "Extreme", (1, int.MinValue, int.MaxValue, "1"));

        PeriodScheduleService.DeletePeriod(semester, 2, [course], []);

        var meeting = Assert.Single(course.MeetingTimes);
        Assert.Equal(int.MinValue, meeting.StartPeriod);
        Assert.Equal(int.MaxValue - 1, meeting.EndPeriod);
    }

    [Fact]
    public void DeletingAPeriodThatWouldCollapseDistinctOfferingsIsRejectedAtomically()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"));
        var unscheduled = Course(semester, "Same course");
        var scheduled = Course(semester, "Same course", (1, 2, 2, "1"));
        var scheduledId = scheduled.OfferingId;
        var scheduledModifiedAt = scheduled.ModifiedAt;
        var snapshot = new PlanCourseSnapshot { CourseOfferingId = scheduledId };

        Assert.Throws<PeriodScheduleCourseIdentityConflictException>(() =>
            PeriodScheduleService.DeletePeriod(
                semester,
                2,
                [unscheduled, scheduled],
                [snapshot]));

        Assert.Equal(2, semester.PeriodSchedule.Count);
        var meeting = Assert.Single(scheduled.MeetingTimes);
        Assert.Equal((2, 2), (meeting.StartPeriod, meeting.EndPeriod));
        Assert.Equal(scheduledId, scheduled.OfferingId);
        Assert.Equal(scheduledModifiedAt, scheduled.ModifiedAt);
        Assert.Equal(scheduledId, snapshot.CourseOfferingId);
    }

    [Fact]
    public void ResettingPeriodsThatWouldCollapseDistinctOfferingsIsRejectedAtomically()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "10:00", "10:45"),
            (4, "10:55", "11:40"),
            (5, "13:30", "14:15"),
            (6, "14:25", "15:10"),
            (7, "15:30", "16:15"),
            (8, "16:25", "17:10"),
            (9, "18:00", "18:45"),
            (10, "18:55", "19:40"),
            (11, "19:50", "20:35"),
            (12, "20:45", "21:30"),
            (13, "21:40", "22:25"));
        var unscheduled = Course(semester, "Same course");
        var scheduled = Course(semester, "Same course", (1, 13, 13, "1"));
        var snapshot = new PlanCourseSnapshot
        {
            CourseOfferingId = scheduled.OfferingId,
            SnapshotAt = DateTimeOffset.UnixEpoch
        };
        var before = JsonSerializer.Serialize(
            new { Semester = semester, Courses = new[] { unscheduled, scheduled }, Snapshot = snapshot },
            JsonDefaults.Options);

        Assert.Throws<PeriodScheduleCourseIdentityConflictException>(() =>
            PeriodScheduleService.ResetToDefault(
                semester,
                [unscheduled, scheduled],
                [snapshot]));

        Assert.Equal(
            before,
            JsonSerializer.Serialize(
                new { Semester = semester, Courses = new[] { unscheduled, scheduled }, Snapshot = snapshot },
                JsonDefaults.Options));
    }

    [Fact]
    public void ResettingPeriodsUpdatesChangedCourseAndSnapshotTimestamps()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "10:00", "10:45"),
            (4, "10:55", "11:40"),
            (5, "13:30", "14:15"),
            (6, "14:25", "15:10"),
            (7, "15:30", "16:15"),
            (8, "16:25", "17:10"),
            (9, "18:00", "18:45"),
            (10, "18:55", "19:40"),
            (11, "19:50", "20:35"),
            (12, "20:45", "21:30"),
            (13, "21:40", "22:25"));
        var course = Course(semester, "Thirteenth period", (1, 13, 13, "1"));
        var oldOfferingId = course.OfferingId;
        var oldModifiedAt = course.ModifiedAt;
        var snapshot = new PlanCourseSnapshot
        {
            CourseOfferingId = oldOfferingId,
            SnapshotAt = DateTimeOffset.UnixEpoch
        };

        PeriodScheduleService.ResetToDefault(semester, [course], [snapshot]);

        Assert.Empty(course.MeetingTimes);
        Assert.NotEqual(oldOfferingId, course.OfferingId);
        Assert.True(course.ModifiedAt > oldModifiedAt);
        Assert.Equal(course.OfferingId, snapshot.CourseOfferingId);
        Assert.True(snapshot.SnapshotAt > DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [InlineData(0x10203040)]
    [InlineData(0x55667788)]
    [InlineData(0x7E57CA5E)]
    public void RandomPeriodScheduleSequencesPreserveAtomicIdentityAndReferenceInvariants(int seed)
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "10:00", "10:45"),
            (4, "10:55", "11:40"));
        var courses = new[]
        {
            Course(semester, "Model A", (1, 1, 2, "1")),
            Course(semester, "Model B", (2, 3, 4, "1"))
        };
        var snapshots = courses.Select((course, index) => new PlanCourseSnapshot
        {
            SnapshotId = $"model-snapshot-{index}",
            CourseOfferingId = course.OfferingId,
            SnapshotAt = DateTimeOffset.UnixEpoch
        }).ToArray();
        var random = new Random(seed);

        for (var step = 0; step < 128; step++)
        {
            var before = JsonSerializer.Serialize(
                new { Semester = semester, Courses = courses, Snapshots = snapshots },
                JsonDefaults.CompactOptions);
            try
            {
                switch (random.Next(4))
                {
                    case 0:
                        var selected = random.Next(5) == 0
                            ? (int?)null
                            : semester.PeriodSchedule[random.Next(semester.PeriodSchedule.Count)].Period;
                        PeriodScheduleService.AddPeriodAfter(semester, selected, courses, snapshots);
                        break;
                    case 1:
                        var deleted = semester.PeriodSchedule[random.Next(semester.PeriodSchedule.Count)].Period;
                        PeriodScheduleService.DeletePeriod(semester, deleted, courses, snapshots);
                        break;
                    case 2:
                        PeriodScheduleService.ResetToDefault(semester, courses, snapshots);
                        break;
                    case 3:
                        var period = semester.PeriodSchedule[random.Next(semester.PeriodSchedule.Count)];
                        PeriodScheduleService.UpdatePeriodTime(
                            semester,
                            period.Period,
                            period.Start,
                            period.Start);
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                Assert.Equal(
                    before,
                    JsonSerializer.Serialize(
                        new { Semester = semester, Courses = courses, Snapshots = snapshots },
                        JsonDefaults.CompactOptions));
            }

            Assert.Equal(
                Enumerable.Range(1, semester.PeriodSchedule.Count),
                semester.PeriodSchedule.Select(period => period.Period));
            Assert.True(SemesterRules.ValidateSemester(semester, []).IsValid, $"Seed {seed:X8}, step {step}");
            Assert.Equal(
                courses.Length,
                courses.Select(course => course.OfferingId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(courses, course =>
            {
                Assert.Equal(CourseIdentityService.GenerateOfferingId(course), course.OfferingId);
                Assert.All(course.MeetingTimes, meeting =>
                {
                    Assert.InRange(meeting.StartPeriod, 1, semester.PeriodSchedule.Count);
                    Assert.InRange(meeting.EndPeriod, meeting.StartPeriod, semester.PeriodSchedule.Count);
                });
            });
            Assert.All(
                snapshots,
                snapshot => Assert.Contains(courses, course => course.OfferingId == snapshot.CourseOfferingId));
        }
    }

    [Fact]
    public void MeetingStartingBeforeFirstPeriodIsAValidationError()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"));
        var course = Course(semester, "Invalid", (1, 0, 2, "1"));

        var validation = CourseValidator.Validate(course, semester);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, issue => issue.Code == "InvalidPeriodRange");
    }

    [Fact]
    public void InsertingPeriodUsesFortyFiveMinutesAndShiftsEntireSuffixWhenNeeded()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "10:00", "10:45"));

        var inserted = PeriodScheduleService.AddPeriodAfter(semester, 1, [], []);

        Assert.Equal((2, new TimeOnly(8, 55), new TimeOnly(9, 40)), (inserted.Period, inserted.Start, inserted.End));
        Assert.Collection(
            semester.PeriodSchedule,
            period => Assert.Equal((1, new TimeOnly(8, 0), new TimeOnly(8, 45)), (period.Period, period.Start, period.End)),
            period => Assert.Equal((2, new TimeOnly(8, 55), new TimeOnly(9, 40)), (period.Period, period.Start, period.End)),
            period => Assert.Equal((3, new TimeOnly(9, 50), new TimeOnly(10, 35)), (period.Period, period.Start, period.End)),
            period => Assert.Equal((4, new TimeOnly(10, 55), new TimeOnly(11, 40)), (period.Period, period.Start, period.End)));
    }

    [Fact]
    public void InsertingPeriodPreservesSuffixWhenExistingGapIsLargeEnough()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "10:00", "10:45"),
            (4, "10:55", "11:40"),
            (5, "13:30", "14:15"));

        PeriodScheduleService.AddPeriodAfter(semester, 4, [], []);

        Assert.Equal((new TimeOnly(11, 50), new TimeOnly(12, 35)),
            (semester.PeriodSchedule[4].Start, semester.PeriodSchedule[4].End));
        Assert.Equal((new TimeOnly(13, 30), new TimeOnly(14, 15)),
            (semester.PeriodSchedule[5].Start, semester.PeriodSchedule[5].End));
    }

    [Fact]
    public void InsertingPeriodAcrossMidnightIsRejectedAtomically()
    {
        var semester = SemesterWithPeriods(
            (1, "22:00", "22:45"),
            (2, "23:20", "23:55"));
        var course = Course(semester, "Late", (1, 2, 2, "1"));
        var originalOfferingId = course.OfferingId;
        var snapshot = new PlanCourseSnapshot { CourseOfferingId = originalOfferingId };

        Assert.Throws<InvalidOperationException>(() =>
            PeriodScheduleService.AddPeriodAfter(semester, 2, [course], [snapshot]));

        Assert.Collection(
            semester.PeriodSchedule,
            period => Assert.Equal((1, new TimeOnly(22, 0), new TimeOnly(22, 45)), (period.Period, period.Start, period.End)),
            period => Assert.Equal((2, new TimeOnly(23, 20), new TimeOnly(23, 55)), (period.Period, period.Start, period.End)));
        Assert.Equal((2, 2), (course.MeetingTimes[0].StartPeriod, course.MeetingTimes[0].EndPeriod));
        Assert.Equal(originalOfferingId, course.OfferingId);
        Assert.Equal(originalOfferingId, snapshot.CourseOfferingId);
    }

    [Theory]
    [InlineData("08:30", "09:15")]
    [InlineData("09:30", "09:15")]
    public void UpdatingPeriodToAnOverlapOrReversedRangeIsRejectedAtomically(string start, string end)
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"));

        Assert.Throws<InvalidOperationException>(() =>
            PeriodScheduleService.UpdatePeriodTime(semester, 2, TimeOnly.Parse(start), TimeOnly.Parse(end)));

        Assert.Equal((new TimeOnly(8, 55), new TimeOnly(9, 40)),
            (semester.PeriodSchedule[1].Start, semester.PeriodSchedule[1].End));
    }

    [Theory]
    [InlineData("08:00", "09:00", "08:30", "09:15")]
    [InlineData("10:00", "10:45", "09:00", "09:45")]
    public void SemesterValidationRejectsOverlappingOrChronologicallyReversedPeriods(
        string firstStart,
        string firstEnd,
        string secondStart,
        string secondEnd)
    {
        var semester = SemesterWithPeriods(
            (1, firstStart, firstEnd),
            (2, secondStart, secondEnd));

        var validation = SemesterRules.ValidateSemester(semester, []);

        Assert.Contains(validation.Errors, issue => issue.Code == "PeriodTimeOverlap");
    }

    [Fact]
    public void SemesterValidationRejectsWeekCountsAboveTheProductLimit()
    {
        var semester = SemesterWithPeriods((1, "08:00", "08:45"));
        semester.WeekCount = SemesterRules.MaxWeekCount + 1;
        semester.EndDate = new DateOnly(2027, 11, 7);

        var validation = SemesterRules.ValidateSemester(semester, []);

        Assert.Contains(validation.Errors, issue => issue.Code == "SemesterWeekCount");
    }

    [Fact]
    public void DateCalculationsDoNotThrowAtDateOnlyBoundaries()
    {
        var minSemester = SemesterWithPeriods((1, "08:00", "08:45"));
        minSemester.StartDate = DateOnly.MinValue;
        minSemester.EndDate = DateOnly.MinValue;
        minSemester.WeekStartDay = WeekStartDay.Sunday;
        minSemester.WeekCount = 1;
        var maxSemester = SemesterWithPeriods((1, "08:00", "08:45"));
        maxSemester.StartDate = DateOnly.MaxValue;
        maxSemester.EndDate = DateOnly.MaxValue;
        maxSemester.WeekStartDay = WeekStartDay.Monday;
        maxSemester.WeekCount = SemesterRules.MaxWeekCount;

        Assert.Equal(1, SemesterRules.CalculateWeekCount(DateOnly.MinValue, DateOnly.MinValue, WeekStartDay.Sunday));
        Assert.Equal(DateOnly.MaxValue, SemesterRules.CalculateEndDate(DateOnly.MaxValue, 1, WeekStartDay.Monday));
        Assert.Null(Record.Exception(() => SemesterRules.ValidateSemester(minSemester, [])));
        var dates = SemesterRules.GetWeekDates(maxSemester, SemesterRules.MaxWeekCount);
        Assert.Equal(7, dates.Count);
        Assert.All(dates, date => Assert.InRange(date, DateOnly.MinValue, DateOnly.MaxValue));
    }

    [Fact]
    public void ReversedDateCalculationReturnsControlledInvalidValue()
    {
        Assert.Equal(0, SemesterRules.CalculateWeekCount(DateOnly.MaxValue, DateOnly.MinValue, WeekStartDay.Monday));
    }

    [Fact]
    public void InvalidWeekStartDayIsReportedWithoutBreakingWeekDateRendering()
    {
        var semester = SemesterWithPeriods((1, "08:00", "08:45"));
        semester.WeekStartDay = (WeekStartDay)999;

        var validation = SemesterRules.ValidateSemester(semester, []);
        var dates = SemesterRules.GetWeekDates(semester, 1);

        Assert.Contains(validation.Errors, issue => issue.Code == "InvalidWeekStartDay");
        Assert.Equal(7, dates.Count);
        Assert.Equal(0, SemesterRules.CalculateWeekCount(semester.StartDate, semester.EndDate, semester.WeekStartDay));
    }

    [Fact]
    public void MaximumSupportedWeekCountRemainsValid()
    {
        var semester = SemesterWithPeriods((1, "08:00", "08:45"));
        semester.WeekCount = SemesterRules.MaxWeekCount;
        semester.EndDate = SemesterRules.CalculateEndDate(
            semester.StartDate,
            SemesterRules.MaxWeekCount,
            semester.WeekStartDay);

        var validation = SemesterRules.ValidateSemester(semester, []);

        Assert.DoesNotContain(validation.Errors, issue => issue.Code == "SemesterWeekCount");
        Assert.DoesNotContain(validation.Errors, issue => issue.Code == "SemesterEndDateWeekCountMismatch");
    }

    [Fact]
    public void RepeatedLatePeriodInsertionStopsBeforeMidnightWithoutPartialMutation()
    {
        var semester = SemesterWithPeriods((1, "20:00", "20:45"));

        PeriodScheduleService.AddPeriodAfter(semester, null, [], []);
        PeriodScheduleService.AddPeriodAfter(semester, null, [], []);
        PeriodScheduleService.AddPeriodAfter(semester, null, [], []);

        Assert.All(semester.PeriodSchedule, period =>
            Assert.Equal(TimeSpan.FromMinutes(45), period.End - period.Start));
        Assert.All(semester.PeriodSchedule.Zip(semester.PeriodSchedule.Skip(1)), pair =>
            Assert.True(pair.Second.Start - pair.First.End >= TimeSpan.FromMinutes(10)));
        var before = semester.PeriodSchedule
            .Select(period => (period.Period, period.Start, period.End))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            PeriodScheduleService.AddPeriodAfter(semester, null, [], []));

        Assert.Equal(before, semester.PeriodSchedule.Select(period => (period.Period, period.Start, period.End)));
    }

    [Fact]
    public void CourseValidationRejectsMeetingsThatOverlapInTheSameActualWeek()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"));
        semester.WeekCount = 3;
        semester.EndDate = new DateOnly(2026, 9, 27);
        var course = Course(
            semester,
            "Overlap",
            (1, 1, 2, "1-2"),
            (1, 2, 3, "2-3"));

        var validation = CourseValidator.Validate(course, semester);

        Assert.Contains(validation.Errors, issue => issue.Code == "MeetingTimesOverlap");
    }

    [Fact]
    public void CourseValidationAllowsSameDayPeriodsWhenTheirActualWeeksAreDisjoint()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"));
        semester.WeekCount = 3;
        semester.EndDate = new DateOnly(2026, 9, 27);
        var course = Course(
            semester,
            "Disjoint",
            (1, 1, 2, "1"),
            (1, 2, 3, "2"));

        var validation = CourseValidator.Validate(course, semester);

        Assert.DoesNotContain(validation.Errors, issue => issue.Code == "MeetingTimesOverlap");
    }

    [Fact]
    public void CourseValidationTreatsOddAndEvenOccurrencesAsDisjoint()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"));
        semester.WeekCount = 6;
        semester.EndDate = new DateOnly(2026, 10, 18);
        var course = Course(
            semester,
            "Alternating",
            (1, 1, 2, "1-6"),
            (1, 2, 3, "1-6"));
        course.MeetingTimes[0].WeekParity = WeekParity.Odd;
        course.MeetingTimes[1].WeekParity = WeekParity.Even;

        var validation = CourseValidator.Validate(course, semester);

        Assert.DoesNotContain(validation.Errors, issue => issue.Code == "MeetingTimesOverlap");
    }

    [Fact]
    public void TimetableSweepLineUsesStableLanesAcrossPartialIntervalOverlaps()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"),
            (4, "10:45", "11:30"));
        var first = Course(semester, "A", (1, 1, 2, "1"));
        var second = Course(semester, "B", (1, 2, 3, "1"));
        var third = Course(semester, "C", (1, 3, 4, "1"));

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks([first, second, third], semester, 1)
            .OrderBy(block => block.Course.CourseName)
            .ToList();

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(2, block.ConflictCount));
        Assert.Equal(blocks[0].ConflictIndex, blocks[2].ConflictIndex);
        Assert.NotEqual(blocks[0].ConflictIndex, blocks[1].ConflictIndex);
    }

    [Fact]
    public void TimetableRendererMergesDuplicateAndOverlappingMeetingsForOneCourse()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"));
        var course = Course(
            semester,
            "Corrupt duplicate",
            (1, 1, 2, "1"),
            (1, 1, 2, "1"),
            (1, 2, 3, "1"));

        var block = Assert.Single(TimetableRenderModelService.BuildWeekCourseBlocks([course], semester, 1));

        Assert.Equal((1, 3), (block.StartPeriod, block.EndPeriod));
        Assert.Equal(1, block.ConflictCount);
        Assert.Equal(0, block.ConflictIndex);
    }

    [Fact]
    public void TimetableSweepLineNeverSharesALaneBetweenOverlappingBlocks()
    {
        var semester = SemesterWithPeriods(
            (1, "08:00", "08:45"),
            (2, "08:55", "09:40"),
            (3, "09:50", "10:35"),
            (4, "10:45", "11:30"),
            (5, "11:40", "12:25"),
            (6, "13:30", "14:15"),
            (7, "14:25", "15:10"),
            (8, "15:20", "16:05"));
        var courses = new[]
        {
            Course(semester, "A", (1, 1, 4, "1")),
            Course(semester, "B", (1, 1, 2, "1")),
            Course(semester, "C", (1, 2, 5, "1")),
            Course(semester, "D", (1, 3, 3, "1")),
            Course(semester, "E", (1, 4, 6, "1")),
            Course(semester, "F", (1, 6, 8, "1")),
            Course(semester, "G", (1, 7, 7, "1")),
            Course(semester, "H", (1, 1, 8, "1"))
        };

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(courses, semester, 1).ToList();
        var maximumConcurrency = Enumerable.Range(1, 8)
            .Max(period => blocks.Count(block => block.StartPeriod <= period && block.EndPeriod >= period));

        Assert.All(blocks, block =>
        {
            Assert.Equal(maximumConcurrency, block.ConflictCount);
            Assert.InRange(block.ConflictIndex, 0, block.ConflictCount - 1);
        });
        for (var leftIndex = 0; leftIndex < blocks.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < blocks.Count; rightIndex++)
            {
                var left = blocks[leftIndex];
                var right = blocks[rightIndex];
                var overlaps = left.StartPeriod <= right.EndPeriod && right.StartPeriod <= left.EndPeriod;
                if (overlaps)
                    Assert.NotEqual(left.ConflictIndex, right.ConflictIndex);
            }
        }
    }

    private static Semester SemesterWithPeriods(params (int Period, string Start, string End)[] periods) =>
        new()
        {
            SemesterId = "semester",
            SemesterName = "Semester",
            StartDate = new DateOnly(2026, 9, 7),
            EndDate = new DateOnly(2026, 9, 13),
            WeekCount = 1,
            WeekStartDay = WeekStartDay.Monday,
            PeriodSchedule = periods.Select(period => new PeriodDefinition
            {
                Period = period.Period,
                Start = TimeOnly.Parse(period.Start),
                End = TimeOnly.Parse(period.End)
            }).ToList()
        };

    private static CourseOffering Course(
        Semester semester,
        string name,
        params (int Weekday, int Start, int End, string Weeks)[] meetings)
    {
        var course = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = name,
            Color = "#C3637A",
            MeetingTimes = meetings.Select(meeting => new MeetingTime
            {
                Weekday = meeting.Weekday,
                StartPeriod = meeting.Start,
                EndPeriod = meeting.End,
                Weeks = meeting.Weeks
            }).ToList(),
            ModifiedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        CourseIdentityService.AssignOfferingId(course);
        return course;
    }
}

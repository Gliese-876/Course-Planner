using System.IO.Compression;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Export;
using CoursePlanner.Persistence;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class CoreBehaviorTests
{
    private static readonly JsonSerializerOptions CurrentExchangeJsonOptions = new(JsonDefaults.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [Fact]
    public void WeekCountUsesAlignedTeachingWeeks()
    {
        var start = new DateOnly(2026, 9, 9);
        var end = new DateOnly(2026, 9, 14);

        Assert.Equal(2, SemesterRules.CalculateWeekCount(start, end, WeekStartDay.Monday));
    }

    [Fact]
    public void WeekdayOrderFollowsSemesterStartDay()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, SemesterRules.GetWeekdayOrder(WeekStartDay.Monday));
        Assert.Equal(new[] { 7, 1, 2, 3, 4, 5, 6 }, SemesterRules.GetWeekdayOrder(WeekStartDay.Sunday));
    }

    [Fact]
    public void ChineseLocalizerUsesSpecificPlatformLanguageForSystemControls()
    {
        var localizer = new AppLocalizer(LanguageMode.SimplifiedChinese);

        Assert.Equal("zh-Hans", localizer.ResourceLanguageTag);
        Assert.Equal("zh-CN", localizer.PlatformLanguageTag);
        Assert.Equal("zh-CN", localizer.Culture.Name);
    }

    [Fact]
    public void SundayStartWeekDatesBeginOnSunday()
    {
        var semester = new Semester
        {
            StartDate = new DateOnly(2026, 9, 9),
            EndDate = new DateOnly(2026, 9, 20),
            WeekCount = 3,
            WeekStartDay = WeekStartDay.Sunday
        };

        var dates = SemesterRules.GetWeekDates(semester, 1);

        Assert.Equal(new DateOnly(2026, 9, 6), dates[0]);
        Assert.Equal(new DateOnly(2026, 9, 12), dates[^1]);
    }

    [Fact]
    public void DisplayDatesUseYearMonthDayOrderIndependentOfCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");

            var date = new DateOnly(2026, 9, 7);

            Assert.Equal("2026-09-07", DateDisplay.Date(date));
            Assert.Equal("09-07", DateDisplay.MonthDay(date));
            Assert.Equal("9-7", DateDisplay.ShortMonthDay(date));
            Assert.Equal("20260907", DateDisplay.CompactDate(date));
            Assert.Equal("2026-09-07 15:30", DateDisplay.DateTime(new DateTime(2026, 9, 7, 15, 30, 0)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void WeekRangeTextUsesYearMonthDayOrder()
    {
        var semester = new Semester
        {
            StartDate = new DateOnly(2026, 9, 7),
            EndDate = new DateOnly(2026, 12, 27),
            WeekCount = 16,
            WeekStartDay = WeekStartDay.Monday
        };

        Assert.Equal("2026-09-07 - 2026-09-13", SemesterRules.WeekRangeText(semester, 1));
    }

    [Fact]
    public void InvalidSemesterValidationReportsDateRangeWithoutThrowing()
    {
        var semester = new Semester
        {
            SemesterId = "bad",
            SemesterName = "Bad",
            StartDate = new DateOnly(2026, 9, 10),
            EndDate = new DateOnly(2026, 9, 1),
            WeekCount = 1,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };

        var result = SemesterRules.ValidateSemester(semester, Enumerable.Empty<Semester>());

        Assert.Contains(result.Errors, x => x.Code == "SemesterDateRange");
    }

    [Fact]
    public void MeetingWeeksParserAppliesExplicitParity()
    {
        var oddWeeks = MeetingWeeksParser.Parse("1-6,8,10-12", 16, WeekParity.Odd);
        var evenWeeks = MeetingWeeksParser.Parse("1-6,8,10-12", 16, WeekParity.Even);

        Assert.Equal(new[] { 1, 3, 5, 11 }, oddWeeks);
        Assert.Equal(new[] { 2, 4, 6, 8, 10, 12 }, evenWeeks);
    }

    [Fact]
    public void MeetingWeeksParserRejectsLegacyParitySuffix()
    {
        var result = MeetingWeeksParser.ParseDetailed("1-6 odd", 16);

        Assert.Contains("1-6 odd", result.InvalidTokens);
    }

    [Fact]
    public void MeetingWeeksParserBoundsHugeRanges()
    {
        var result = MeetingWeeksParser.ParseDetailed("1-2000000000", 16);

        Assert.Equal(Enumerable.Range(1, 16), result.Weeks);
        Assert.True(result.WasBounded);
        Assert.Contains(17, result.OutOfRangeWeeks);
    }

    [Fact]
    public void OfferingIdIgnoresNonIdentityFields()
    {
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        var first = CourseIdentityService.GenerateOfferingId(course);

        course.Capacity = 120;
        course.EnrolledCount = 20;
        course.Notes = "Changed";
        course.CourseGroupType = "Other";
        course.Color = "#BE123C";

        Assert.Equal(first, CourseIdentityService.GenerateOfferingId(course));
    }

    [Fact]
    public void OfferingIdChangesWhenMeetingChanges()
    {
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        var first = CourseIdentityService.GenerateOfferingId(course);

        course.MeetingTimes[0].EndPeriod = 3;

        Assert.NotEqual(first, CourseIdentityService.GenerateOfferingId(course));
    }

    [Fact]
    public void OfferingIdChangesWhenMeetingParityChanges()
    {
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        var first = CourseIdentityService.GenerateOfferingId(course);

        course.MeetingTimes[0].WeekParity = WeekParity.Odd;

        Assert.NotEqual(first, CourseIdentityService.GenerateOfferingId(course));
    }

    [Fact]
    public void CourseEditFingerprintIgnoresNonEditableAndPresentationNormalization()
    {
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        course.CourseGroupType = null;
        course.StudyType = "Required";
        course.Labels.AddRange(new[] { "Project", "Morning" });
        course.Color = "#c3637a";
        var original = CourseEditFingerprint.Capture(course);

        var roundTripped = JsonDefaults.Clone(course);
        roundTripped.ModifiedAt = roundTripped.ModifiedAt.AddHours(1);
        roundTripped.CourseGroupType = "";
        roundTripped.Labels = new List<string> { "Morning", "Project" };
        roundTripped.Color = "#C3637A";

        Assert.Equal(original, CourseEditFingerprint.Capture(roundTripped));
    }

    [Fact]
    public void CourseEditFingerprintDetectsEditableChanges()
    {
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        var original = CourseEditFingerprint.Capture(course);

        course.CourseName = "Advanced Data Structures";

        Assert.NotEqual(original, CourseEditFingerprint.Capture(course));
    }

    [Fact]
    public void CourseFactoryCreatesBlankCourseForSemester()
    {
        var semester = new Semester { SemesterId = "2026-fall", WeekCount = 18 };

        var course = CourseFactory.CreateBlank(semester, 0, weekday: 3, period: 4);

        Assert.Equal("2026-fall", course.SemesterId);
        Assert.Equal(CourseColorService.Generate(0), course.Color);
        var meeting = Assert.Single(course.MeetingTimes);
        Assert.Equal((3, 4, 4, "1-18"), (meeting.Weekday, meeting.StartPeriod, meeting.EndPeriod, meeting.Weeks));
        Assert.Equal(WeekParity.All, meeting.WeekParity);
    }

    [Fact]
    public void CourseDisplayFormatterBuildsStructuredMeetingDetails()
    {
        var localizer = new AppLocalizer(LanguageMode.SimplifiedChinese);
        var formatter = new CourseDisplayFormatter(localizer);
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        course.MeetingTimes.Add(new MeetingTime { Weekday = 3, StartPeriod = 7, EndPeriod = 8, Weeks = "2-16", WeekParity = WeekParity.Even });

        var details = formatter.MeetingDetails(course);

        Assert.Equal(2, details.Count);
        Assert.Equal("上课时间 1", details[0].Title);
        Assert.Equal(
            new[]
            {
                ("星期", "周一"),
                ("节次", "3-4 节"),
                ("周次", "1-16 周"),
                ("单双周", "每周")
            },
            details[0].Fields.Select(field => (field.Label, field.Value)).ToArray());
        Assert.Equal(
            new[]
            {
                ("星期", "周三"),
                ("节次", "7-8 节"),
                ("周次", "2-16 周"),
                ("单双周", "双周")
            },
            details[1].Fields.Select(field => (field.Label, field.Value)).ToArray());
    }

    [Fact]
    public void CourseDisplayFormatterAddsChineseWeekSuffixInMeetingSummary()
    {
        var formatter = new CourseDisplayFormatter(new AppLocalizer(LanguageMode.SimplifiedChinese));
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        course.MeetingTimes.Add(new MeetingTime { Weekday = 3, StartPeriod = 7, EndPeriod = 8, Weeks = "2-16", WeekParity = WeekParity.Even });

        var summary = formatter.MeetingSummary(course);

        Assert.Equal("周一 3-4 节 1-16 周; 周三 7-8 节 2-16 周 双周", summary);
    }

    [Fact]
    public void CourseDisplayFormatterIncludesCapacityInCourseTooltip()
    {
        var formatter = new CourseDisplayFormatter(new AppLocalizer(LanguageMode.SimplifiedChinese));
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "Room A204");
        course.EnrolledCount = 68;
        course.Capacity = 80;

        var tooltip = formatter.CourseTooltipText(course);

        var expected = string.Join(
            Environment.NewLine,
            "Data Structures",
            "",
            "星期: 周一",
            "节次: 3-4 节",
            "周次: 1-16 周",
            "单双周: 每周",
            "",
            "Prof. Lin",
            "Room A204",
            "68/80");
        Assert.Equal(expected, tooltip);
    }

    [Fact]
    public void CourseDisplayFormatterOmitsEmptyCapacityInCourseTooltip()
    {
        var formatter = new CourseDisplayFormatter(new AppLocalizer(LanguageMode.SimplifiedChinese));
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "Room A204");

        var tooltip = formatter.CourseTooltipText(course);

        Assert.EndsWith($"Prof. Lin{Environment.NewLine}Room A204", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain(" / ", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void CourseEditMapperRoundTripsStructuredMeetings()
    {
        var localizer = new AppLocalizer(LanguageMode.English);
        var course = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");
        course.MeetingTimes.Add(new MeetingTime { Weekday = 3, StartPeriod = 7, EndPeriod = 8, Weeks = "2-16", WeekParity = WeekParity.Even });

        var model = CourseEditMapper.FromCourse(course, localizer);
        model.Meetings[0].StartPeriod = 5;
        model.Meetings[0].EndPeriod = 6;
        var edited = Course("2026-fall", "Data Structures", "Prof. Lin", "A204");

        CourseEditMapper.ApplyToCourse(edited, model, localizer);

        Assert.Equal(2, model.Meetings.Count);
        Assert.Equal((1, 3, 4, "1-16"), (course.MeetingTimes[0].Weekday, course.MeetingTimes[0].StartPeriod, course.MeetingTimes[0].EndPeriod, course.MeetingTimes[0].Weeks));
        Assert.Equal((1, 5, 6, "1-16"), (edited.MeetingTimes[0].Weekday, edited.MeetingTimes[0].StartPeriod, edited.MeetingTimes[0].EndPeriod, edited.MeetingTimes[0].Weeks));
        Assert.Equal((3, 7, 8, "2-16", WeekParity.Even), (edited.MeetingTimes[1].Weekday, edited.MeetingTimes[1].StartPeriod, edited.MeetingTimes[1].EndPeriod, edited.MeetingTimes[1].Weeks, edited.MeetingTimes[1].WeekParity));
    }

    [Theory]
    [InlineData(WeekParity.All, "Every week")]
    [InlineData(WeekParity.Odd, "Odd weeks")]
    [InlineData(WeekParity.Even, "Even weeks")]
    public void CourseDisplayFormatterLocalizesExplicitParity(WeekParity parity, string expected)
    {
        var formatter = new CourseDisplayFormatter(new AppLocalizer(LanguageMode.English));

        Assert.Equal(expected, formatter.ParityText(parity));
    }

    [Fact]
    public void CompletelyInvisibleCourseIsRejected()
    {
        var semester = TestDocumentFactory.CreatePopulated().Semesters[0];
        var course = Course(semester.SemesterId, "Invisible", "T", "L");
        course.MeetingTimes[0].Weeks = "20-21";

        var result = CourseValidator.Validate(course, semester);

        Assert.Contains(result.Errors, x => x.Code == "CourseCompletelyInvisible");
    }

    [Fact]
    public void InvalidMeetingParityIsRejected()
    {
        var semester = TestDocumentFactory.CreatePopulated().Semesters[0];
        var course = Course(semester.SemesterId, "Invalid parity", "T", "L");
        course.MeetingTimes[0].WeekParity = (WeekParity)999;

        var result = CourseValidator.Validate(course, semester);

        Assert.Contains(result.Errors, x => x.Code == "InvalidWeekParity");
    }

    [Fact]
    public void PartialOutOfRangeCourseWarnsButCanBeForced()
    {
        var semester = TestDocumentFactory.CreatePopulated().Semesters[0];
        var course = Course(semester.SemesterId, "Partial", "T", "L");
        course.MeetingTimes[0].Weeks = "15-20";

        var result = CourseValidator.Validate(course, semester);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, x => x.Code == "WeeksOutOfRange");
        Assert.True(result.RequiresForce);
    }

    [Fact]
    public void LibraryGroupingPlacesUncategorizedLast()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        doc.CourseLibrary[^1].CourseGroupType = null;
        var groups = LibraryFilterService.Group(doc.CourseLibrary, doc.Semesters, doc.Labels);

        Assert.Equal(PlannerLabels.General, groups[0].CourseGroupType);
        Assert.Equal(PlannerLabels.Uncategorized, groups[^1].CourseGroupType);
    }

    [Fact]
    public void LibraryGroupingUsesSemesterDisplayOrder()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var second = JsonDefaults.Clone(doc.Semesters[0]);
        second.SemesterId = "2025-fall";
        second.SemesterName = "2025 Fall";
        second.DisplayOrder = -1;
        doc.Semesters.Add(second);
        var copied = PlannerDomainService.CopyCourseToSemester(doc.CourseLibrary[0], second.SemesterId, 1);
        doc.CourseLibrary.Add(copied);

        var groups = LibraryFilterService.Group(doc.CourseLibrary, doc.Semesters, doc.Labels);

        Assert.Equal("2025 Fall", groups[0].SemesterName);
    }

    [Fact]
    public void FilterCombinesSearchAndLabels()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var filtered = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter
        {
            SearchText = "Data",
            OrdinaryLabels = { "Project" }
        }, doc.Semesters[0].SemesterId).ToList();

        Assert.Single(filtered);
        Assert.Equal("Data Structures", filtered[0].CourseName);
    }

    [Fact]
    public void FilterSearchIncludesGroupStudyAndCredits()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semesterId = doc.Semesters[0].SemesterId;

        var byGroup = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter { SearchText = PlannerLabels.Major }, semesterId).ToList();
        var byStudy = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter { SearchText = PlannerLabels.Core }, semesterId).ToList();
        var byCredits = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter { SearchText = "3" }, semesterId).ToList();

        Assert.NotEmpty(byGroup);
        Assert.NotEmpty(byStudy);
        Assert.NotEmpty(byCredits);
    }

    [Fact]
    public void FilterTeacherAndLocationUseTextContains()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semesterId = doc.Semesters[0].SemesterId;

        var byTeacher = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter
        {
            Teachers = { "Wang" }
        }, semesterId).Select(x => x.CourseName).ToList();
        var byLocation = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter
        {
            Locations = { "B10" }
        }, semesterId).Select(x => x.CourseName).ToList();

        Assert.Equal(["Linear Algebra"], byTeacher);
        Assert.Equal(["Linear Algebra"], byLocation);
    }

    [Fact]
    public void FilterMultipleValuesWithinOneFieldUseOr()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var filtered = LibraryFilterService.Filter(doc.CourseLibrary, new CourseFilter
        {
            OrdinaryLabels = { "Project", "Morning" }
        }, doc.Semesters[0].SemesterId).Select(x => x.CourseName).ToList();

        Assert.Contains("Data Structures", filtered);
        Assert.Contains("Linear Algebra", filtered);
        Assert.Contains("Human-Computer Interaction", filtered);
    }

    [Fact]
    public void GeneratedCoursePaletteUsesCridColors()
    {
        Assert.Equal("#C3637A", CourseColorService.Generate(0));
        Assert.Equal("#D25E3D", CourseColorService.Generate(1));
        Assert.True(CourseColorService.GeneratedPalette.Count >= 12);
        Assert.Equal(CourseColorService.GeneratedPalette.Count, CourseColorService.GeneratedPalette.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GeneratedCoursePaletteStaysSeparatedFromCourseBlockSurfaces()
    {
        foreach (var color in CourseColorService.GeneratedPalette)
        {
            Assert.True(CourseColorService.IsValidHex(color));
            Assert.False(CourseColorService.ViolatesGeneratedColorGuidance(color), color);
            Assert.True(CourseColorService.ContrastRatio(color, CourseColorService.LightCourseBlockSurface) >= CourseColorService.MinimumGeneratedSurfaceContrast, color);
            Assert.True(CourseColorService.ContrastRatio(color, CourseColorService.DarkCourseBlockSurface) >= CourseColorService.MinimumGeneratedSurfaceContrast, color);
            Assert.True(CourseColorService.ColorVisualDistance(color, CourseColorService.LightCourseBlockSurface) >= CourseColorService.MinimumGeneratedSurfaceVisualDistance, color);
            Assert.True(CourseColorService.ColorVisualDistance(color, CourseColorService.DarkCourseBlockSurface) >= CourseColorService.MinimumGeneratedSurfaceVisualDistance, color);
        }
    }

    [Fact]
    public void UserCourseColorInputIsNormalizedButNotConstrainedToGeneratedRange()
    {
        Assert.Equal("#FFFFFF", CourseColorService.NormalizeUserInput("  #ffffff "));
        Assert.Equal("blue", CourseColorService.NormalizeUserInput(" blue "));
        Assert.True(CourseColorService.ViolatesGeneratedColorGuidance("#FFFFFF"));
        Assert.Equal("#FFFFFF", CourseColorService.EnsureValid("#ffffff", 0));
        Assert.Equal(CourseColorService.Generate(0), CourseColorService.EnsureValid("blue", 0));
    }

    [Fact]
    public void CourseBlockWrappingHyphenatesOnlyAsciiWords()
    {
        static bool FitsFive(string value) => value.Length <= 5;
        static bool FitsTwo(string value) => value.Length <= 2;

        Assert.Equal(
            new[] { "Data", "Stru-", "ctur-", "es" },
            TextRules.WrapTextWithAsciiHyphenation("Data Structures", 4, FitsFive));
        Assert.Equal(
            new[] { "高等", "数学", "分析" },
            TextRules.WrapTextWithAsciiHyphenation("高等数学分析", 3, FitsTwo));
        Assert.Equal(
            new[] { "Data", "结构", "A204" },
            TextRules.WrapTextWithAsciiHyphenation("Data 结构 A204", 3, FitsFive));
    }

    [Fact]
    public void CourseBlockWrappingNeverSplitsUnicodeTextElements()
    {
        static bool FitsOneUtf16CodeUnit(string value) => value.Length <= 1;

        Assert.Equal(
            new[] { "😀", "A" },
            TextRules.WrapTextWithAsciiHyphenation("😀A", 3, FitsOneUtf16CodeUnit));
        Assert.Equal(
            new[] { "e\u0301", "X" },
            TextRules.WrapTextWithAsciiHyphenation("e\u0301X", 3, FitsOneUtf16CodeUnit));
        Assert.Equal(
            new[] { "👩‍💻", "Z" },
            TextRules.WrapTextWithAsciiHyphenation("👩‍💻Z", 3, FitsOneUtf16CodeUnit));
    }

    [Fact]
    public void CourseBlockWrappingTruncatesTheLastLineWithoutOverflowingOrSplittingTextElements()
    {
        static bool FitsFiveUtf16CodeUnits(string value) => value.Length <= 5;
        static bool FitsThreeUtf16CodeUnits(string value) => value.Length <= 3;

        var ascii = TextRules.WrapTextWithAsciiHyphenation(
            "Data Structures That Cannot Fit",
            maxLines: 1,
            FitsFiveUtf16CodeUnits);
        var combining = TextRules.WrapTextWithAsciiHyphenation(
            "e\u0301XYZ",
            maxLines: 1,
            FitsThreeUtf16CodeUnits);

        Assert.Equal(new[] { "Data…" }, ascii);
        Assert.Equal(new[] { "e\u0301…" }, combining);
        Assert.All(ascii, line => Assert.True(FitsFiveUtf16CodeUnits(line)));
        Assert.All(combining, line => Assert.True(FitsThreeUtf16CodeUnits(line)));
        Assert.Equal(new[] { 0, 2 }, StringInfo.ParseCombiningCharacters(combining[0]));
    }

    [Fact]
    public void CourseBlockWrappingHasBoundedAllocationForMaximumLengthText()
    {
        var text = new string('A', 2048);
        static bool FitsTwentyCharacters(string value) => value.Length <= 20;
        TextRules.WrapTextWithAsciiHyphenation(text, 64, FitsTwentyCharacters);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var lines = TextRules.WrapTextWithAsciiHyphenation(text, 64, FitsTwentyCharacters);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(64, lines.Count);
        Assert.True(allocated < 8 * 1024 * 1024, $"Wrapping allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void ComparisonIsPerSlot()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var basePlan = doc.Plans[0];
        var currentPlan = JsonDefaults.Clone(basePlan);
        currentPlan.Snapshots.RemoveAt(0);
        var replacement = Course(semester.SemesterId, "Replacement", "T", "A204");
        replacement.MeetingTimes[0].Weekday = 1;
        replacement.MeetingTimes[0].StartPeriod = 4;
        replacement.MeetingTimes[0].EndPeriod = 5;
        CourseIdentityService.AssignOfferingId(replacement);
        doc.CourseLibrary.Add(replacement);
        currentPlan.Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = replacement.OfferingId });

        var differences = PlannerDomainService.Compare(basePlan, currentPlan, semester, 1, doc.CourseLibrary);

        Assert.Contains(differences, x => x.Slot.Period == 3 && x.Kind == DifferenceKind.Removed);
        Assert.Contains(differences, x => x.Slot.Period == 4 && x.Kind == DifferenceKind.Replaced);
        Assert.Contains(differences, x => x.Slot.Period == 5 && x.Kind == DifferenceKind.Added);
    }

    [Fact]
    public void TimetableRenderModelComputesConflictLayoutAndRemovedExportCourses()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var first = doc.CourseLibrary[0];
        var conflicting = JsonDefaults.Clone(first);
        conflicting.CourseName = "Conflicting Course";
        conflicting.Teacher = "T";
        conflicting.Location = "R";
        CourseIdentityService.AssignOfferingId(conflicting);
        var courses = new[] { first, conflicting };

        var blocks = TimetableRenderModelService.BuildWeekCourseBlocks(courses, semester, 1);

        var conflictBlocks = blocks
            .Where(x => x.Slot.Weekday == first.MeetingTimes[0].Weekday &&
                        x.Slot.Period == first.MeetingTimes[0].StartPeriod)
            .ToList();
        Assert.Equal(2, conflictBlocks.Count);
        Assert.All(conflictBlocks, block => Assert.Equal(2, block.ConflictCount));
        Assert.Equal(new[] { 0, 1 }, conflictBlocks.Select(x => x.ConflictIndex).Order().ToArray());

        var plan = new SelectionPlan { Snapshots = { new PlanCourseSnapshot { CourseOfferingId = conflicting.OfferingId } } };
        var exportCourses = TimetableRenderModelService.CoursesForExport(plan, courses, new[]
        {
            new SlotDifference { Kind = DifferenceKind.Removed, BaseCourse = first, Slot = new TimetableSlot { Week = 1, Weekday = 1, Period = 1 } }
        });
        Assert.Contains(exportCourses, x => x.OfferingId == first.OfferingId);
        Assert.Contains(exportCourses, x => x.OfferingId == conflicting.OfferingId);
    }

    [Fact]
    public void CourseLibraryJsonPreviewDetectsKindAndWarnings()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var json = ImportExportService.ExportCourseLibraryJson(doc);

        var preview = ImportExportService.PreviewJson(doc, json);

        Assert.Equal(PlannerSchemas.CourseLibraryKind, preview.Kind);
        Assert.Contains(preview.Items, x => x.Kind == "course" && x.Status is ImportPreviewStatus.Updated or ImportPreviewStatus.Warning);
    }

    [Fact]
    public void SelectionPlanImportRequiresForcedMergeForSameSemesterIdDifferentName()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var localSemester = doc.Semesters[0];
        var importedSemester = JsonDefaults.Clone(localSemester);
        importedSemester.SemesterName = "Imported Fall";
        var importedPlan = new SelectionPlan
        {
            SemesterId = importedSemester.SemesterId,
            PlanName = "Imported Plan",
            Snapshots =
            {
                new PlanCourseSnapshot { RegistrationOrder = 0 }
            }
        };
        var importedCourse = Course(importedSemester.SemesterId, "Imported Course", "T", "R");
        importedPlan.Snapshots[0].CourseOfferingId = importedCourse.OfferingId;
        var json = JsonSerializer.Serialize(new SelectionPlanPackage
        {
            Semester = importedSemester,
            Courses = { importedCourse },
            Plan = importedPlan
        }, CurrentExchangeJsonOptions);

        var preview = ImportExportService.PreviewSelectionPlan(doc, json);

        Assert.Contains(preview.Items, x => x.Kind == "semester" &&
                                            x.Status == ImportPreviewStatus.Conflict &&
                                            x.CanApplyWithForcedSemesterMerge);

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions());
        Assert.DoesNotContain(doc.Plans, x => x.PlanName == "Imported Plan");

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions
        {
            ForceSemesterMergeConflicts = true,
            SynchronizeMissingPlanCourses = true
        });
        var plan = Assert.Single(doc.Plans, x => x.PlanName == "Imported Plan");
        Assert.Equal(localSemester.SemesterId, plan.SemesterId);
        Assert.DoesNotContain(doc.Semesters, x => x.SemesterName == "Imported Fall");
    }

    [Fact]
    public void ImportSemesterSettingsUpdateIsExplicit()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var originalEnd = semester.EndDate;
        var importedSemester = JsonDefaults.Clone(semester);
        importedSemester.WeekCount = semester.WeekCount + 1;
        importedSemester.EndDate = SemesterRules.CalculateEndDate(importedSemester.StartDate, importedSemester.WeekCount, importedSemester.WeekStartDay);
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = { importedSemester }
        }, JsonDefaults.Options);
        var preview = ImportExportService.PreviewCourseLibrary(doc, json);

        Assert.Contains(preview.Items, x => x.Kind == "semester" &&
                                            x.Status == ImportPreviewStatus.Warning &&
                                            x.RequiresSemesterSettingsDecision);

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions());
        Assert.Equal(originalEnd, semester.EndDate);

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions { UpdateExistingSemesterSettings = true });
        Assert.Equal(importedSemester.EndDate, semester.EndDate);
    }

    [Fact]
    public void OutOfRangeImportWarningsRequireExplicitForce()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = Course(semester.SemesterId, "Late Course", "T", "R");
        course.MeetingTimes[0].Weeks = $"{semester.WeekCount}-{semester.WeekCount + 2}";
        CourseIdentityService.AssignOfferingId(course);
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = { JsonDefaults.Clone(semester) },
            Courses = { course }
        }, CurrentExchangeJsonOptions);

        var preview = ImportExportService.PreviewCourseLibrary(doc, json);

        Assert.Contains(preview.Items, x => x.Kind == "course" &&
                                            x.DisplayName == "Late Course" &&
                                            x.Status == ImportPreviewStatus.Warning &&
                                            x.RequiresForceImport);

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions());
        Assert.DoesNotContain(doc.CourseLibrary, x => x.CourseName == "Late Course");

        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions { ForceOutOfRangeCourses = true });
        Assert.Contains(doc.CourseLibrary, x => x.CourseName == "Late Course");
    }

    [Fact]
    public void SelectionPlanImportIsAtomicWhenMissingCourseRequiresForce()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = Course(semester.SemesterId, "Late Snapshot", "T", "R");
        course.MeetingTimes[0].Weeks = $"{semester.WeekCount}-{semester.WeekCount + 2}";
        CourseIdentityService.AssignOfferingId(course);
        var package = new SelectionPlanPackage
        {
            Semester = JsonDefaults.Clone(semester),
            Plan = new SelectionPlan
            {
                SemesterId = semester.SemesterId,
                PlanName = "Plan With Late Snapshot",
                Snapshots =
                {
                    new PlanCourseSnapshot
                    {
                        CourseOfferingId = course.OfferingId,
                        RegistrationOrder = 0
                    }
                }
            },
            Courses = { course }
        };
        var json = JsonSerializer.Serialize(package, CurrentExchangeJsonOptions);

        var preview = ImportExportService.PreviewSelectionPlan(doc, json);

        Assert.Contains(preview.Items, x => x.Kind == "planCourse" &&
                                            x.DisplayName == "Late Snapshot" &&
                                            x.RequiresForceImport);

        var blocked = ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });
        Assert.False(blocked.Applied);
        Assert.DoesNotContain(doc.Plans, x => x.PlanName == "Plan With Late Snapshot");

        var applied = ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true,
            ForceOutOfRangeCourses = true
        });
        Assert.True(applied.Applied);
        var importedPlan = Assert.Single(doc.Plans, x => x.PlanName == "Plan With Late Snapshot");
        Assert.Single(importedPlan.Snapshots);
        Assert.Contains(doc.CourseLibrary, item => item.OfferingId == course.OfferingId);
    }

    [Fact]
    public void RemovingLibraryCourseRemovesPlanReferences()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var plan = doc.Plans[0];
        var removedId = plan.Snapshots[0].CourseOfferingId;

        doc.CourseLibrary.RemoveAll(x => x.OfferingId == removedId);
        PlannerDomainService.RemoveCourseReferences(doc, [removedId]);

        Assert.DoesNotContain(plan.Snapshots, x => x.CourseOfferingId == removedId);
    }

    [Fact]
    public void PlanSnapshotsResolveLibraryCourseByStableId()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var course = doc.CourseLibrary[0];
        var snapshot = doc.Plans[0].Snapshots.Single(x => x.CourseOfferingId == course.OfferingId);

        course.CourseName = "Renamed Data Structures";

        var resolved = PlanCourseResolver.CourseForSnapshot(snapshot, doc.CourseLibrary);
        Assert.Same(course, resolved);
        Assert.Equal("Renamed Data Structures", resolved!.CourseName);
    }

    [Fact]
    public void UndoRedoRestoresResolvedPlanCourseReferences()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var undoRedo = new PlannerUndoRedo();
        var originalCourseName = PlanCourseResolver.CourseForSnapshot(doc.Plans[0].Snapshots[0], doc.CourseLibrary)!.CourseName;

        undoRedo.Capture(doc);
        doc.CourseLibrary[0].CourseName = "Changed Before Undo";

        var restored = undoRedo.Undo(doc);

        Assert.NotNull(restored);
        var restoredSnapshot = restored.Plans[0].Snapshots[0];
        var restoredCourse = PlanCourseResolver.CourseForSnapshot(restoredSnapshot, restored.CourseLibrary);
        Assert.NotNull(restoredCourse);
        Assert.Equal(restoredSnapshot.CourseOfferingId, restoredCourse!.OfferingId);
        Assert.Equal(originalCourseName, restoredCourse.CourseName);
    }

    [Fact]
    public void UndoHistoryIsBoundedByEntryCountAndTotalSerializedBytes()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var undoRedo = new PlannerUndoRedo();

        for (var index = 0; index < 75; index++)
        {
            document.Plans[0].PlanName = $"History state {index}";
            undoRedo.Capture(document);
        }

        Assert.InRange(undoRedo.UndoCount, 1, PlannerUndoRedo.MaxHistoryEntries);
        Assert.Equal(0, undoRedo.RedoCount);
        Assert.InRange(undoRedo.HistoryBytes, 1, PlannerUndoRedo.MaxHistoryBytes);
    }

    [Fact]
    public void ConsecutiveDuplicateUndoCapturesCollapseToOneState()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var undoRedo = new PlannerUndoRedo();
        Assert.True(undoRedo.Capture(document));
        Assert.False(undoRedo.Capture(document));
        document.Plans[0].PlanName = "Changed after duplicate capture";

        var restored = undoRedo.Undo(document);

        Assert.NotNull(restored);
        Assert.Null(undoRedo.Undo(restored));
    }

    [Fact]
    public void OversizedUndoSnapshotIsRejectedWithoutRetainingItsBytes()
    {
        var document = TestDocumentFactory.CreatePopulated();
        document.CourseLibrary[0].Notes = new string('X', PlannerUndoRedo.MaxHistoryBytes + 1);
        var undoRedo = new PlannerUndoRedo();

        var captured = undoRedo.Capture(document);

        Assert.False(captured);
        Assert.False(undoRedo.CanUndo);
        Assert.Equal(0, undoRedo.HistoryBytes);
    }

    [Fact]
    public void CourseIdentityChangeUpdatesPlanReferences()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var course = doc.CourseLibrary[0];
        var oldId = course.OfferingId;
        course.CourseName = "Renamed Data Structures";
        CourseIdentityService.AssignOfferingId(course);

        PlannerDomainService.UpdateCourseReferenceId(doc, oldId, course.OfferingId);

        var snapshot = Assert.Single(doc.Plans[0].Snapshots, x => x.CourseOfferingId == course.OfferingId);
        Assert.Same(course, PlanCourseResolver.CourseForSnapshot(snapshot, doc.CourseLibrary));
        Assert.DoesNotContain(doc.Plans.SelectMany(x => x.Snapshots), x => x.CourseOfferingId == oldId);
    }

    [Fact]
    public void ImportPreviewFilteringCombinesStatusLabelsAndVisibleFields()
    {
        var preview = new ImportPreview
        {
            Kind = PlannerSchemas.CourseLibraryKind,
            SchemaVersion = PlannerSchemas.Current,
            Items =
            {
                new ImportPreviewItem
                {
                    Kind = "course",
                    DisplayName = "Data Structures",
                    SemesterName = "2026 Fall",
                    Status = ImportPreviewStatus.Warning,
                    Course = new CourseOffering
                    {
                        CourseName = "Data Structures",
                        Teacher = "Prof. Lin",
                        CourseGroupType = "Major Core",
                        StudyType = "Required",
                        Labels = { "Project" }
                    }
                },
                new ImportPreviewItem
                {
                    Kind = "course",
                    DisplayName = "Art History",
                    SemesterName = "2026 Fall",
                    Status = ImportPreviewStatus.Added,
                    Course = new CourseOffering
                    {
                        CourseName = "Art History",
                        Teacher = "Prof. Wu",
                        CourseGroupType = "General Education",
                        StudyType = "Elective",
                        Labels = { "Evening" }
                    }
                }
            }
        };
        var filter = new ImportPreviewFilter
        {
            SearchText = "Lin",
            SemesterText = "Fall"
        };
        filter.Statuses.Add(ImportPreviewStatus.Warning);
        filter.OrdinaryLabels.Add("Project");
        filter.CourseGroupTypes.Add("Major Core");
        filter.StudyTypes.Add("Required");

        var visible = ImportExportService.FilterPreviewItems(preview, filter);

        Assert.Single(visible);
        Assert.Equal("Data Structures", visible[0].DisplayName);
    }

    [Fact]
    public void WindowsFileNameRulesRejectReservedTrailingAndIllegalComponents()
    {
        Assert.False(WindowsFileNameRules.ValidateFileComponent("CON").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("CON.txt.more").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("com1.backup.json").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("Plan.").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("Plan:One").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("Plan\0One").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("Plan\u0001One").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent("Plan\nOne").IsValid);
        Assert.False(WindowsFileNameRules.ValidateFileComponent(new string('A', 256)).IsValid);
        Assert.True(WindowsFileNameRules.ValidateFileComponent(new string('A', 255)).IsValid);
        Assert.True(WindowsFileNameRules.ValidateFileComponent("2026 Fall Plan").IsValid);
    }

    [Fact]
    public void SuggestedExportFileNamesAreBoundedWithoutSplittingUnicodeTextElementsOrLosingExtensions()
    {
        var stem = string.Concat(Enumerable.Repeat("👩‍💻e\u0301", 80)) + "-whole-semester-20260101-20261231";

        var suggested = WindowsFileNameRules.CreateBoundedSuggestion(stem, ".pdf");

        Assert.EndsWith(".pdf", suggested, StringComparison.Ordinal);
        Assert.True(suggested.Length <= WindowsFileNameRules.MaxComponentLength);
        Assert.True(WindowsFileNameRules.ValidateFileComponent(suggested).IsValid);
        var fittedStem = suggested[..^4];
        var marker = fittedStem.IndexOf('…');
        Assert.True(marker >= 0);
        var prefix = fittedStem[..marker];
        var suffix = fittedStem[(marker + 1)..];
        Assert.StartsWith(prefix, stem, StringComparison.Ordinal);
        Assert.EndsWith(suffix, stem, StringComparison.Ordinal);
        var boundaries = StringInfo.ParseCombiningCharacters(stem).Append(stem.Length).ToHashSet();
        Assert.Contains(prefix.Length, boundaries);
        Assert.Contains(stem.Length - suffix.Length, boundaries);
    }

    [Fact]
    public void SuggestedExportFileNameKeepsAnExactBoundaryNameUnchanged()
    {
        const string extension = ".json";
        var stem = new string('A', WindowsFileNameRules.MaxComponentLength - extension.Length);

        var suggested = WindowsFileNameRules.CreateBoundedSuggestion(stem, extension);

        Assert.Equal(stem + extension, suggested);
    }

    [Fact]
    public void DuplicateAndConflictResolutionAreExplicit()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var plan = new SelectionPlan { SemesterId = semester.SemesterId };
        var original = Course(semester.SemesterId, "Course A", "T", "R1");
        var duplicate = JsonDefaults.Clone(original);
        duplicate.Notes = "updated snapshot";
        var conflict = Course(semester.SemesterId, "Course B", "T", "R2");
        conflict.MeetingTimes[0].Weekday = original.MeetingTimes[0].Weekday;
        conflict.MeetingTimes[0].StartPeriod = original.MeetingTimes[0].StartPeriod;
        conflict.MeetingTimes[0].EndPeriod = original.MeetingTimes[0].EndPeriod;
        CourseIdentityService.AssignOfferingId(conflict);
        var library = new List<CourseOffering> { original, duplicate, conflict };

        PlannerDomainService.AddCourseToPlan(plan, original, semester, DuplicateResolution.SkipExisting, ConflictResolution.KeepConflict, library);
        var skipped = PlannerDomainService.AddCourseToPlan(plan, duplicate, semester, DuplicateResolution.SkipExisting, ConflictResolution.KeepConflict, library);
        var replaced = PlannerDomainService.AddCourseToPlan(plan, duplicate, semester, DuplicateResolution.ReplaceExisting, ConflictResolution.KeepConflict, library);
        var conflicted = PlannerDomainService.AddCourseToPlan(plan, conflict, semester, DuplicateResolution.SkipExisting, ConflictResolution.RemoveConflictingThenAdd, library);

        Assert.False(skipped.Added);
        Assert.True(replaced.ReplacedDuplicate);
        Assert.True(conflicted.Added);
        Assert.DoesNotContain(plan.Snapshots, x => x.CourseOfferingId == original.OfferingId);
        Assert.Contains(plan.Snapshots, x => x.CourseOfferingId == conflict.OfferingId);
    }

    [Fact]
    public void CancelledConflictDoesNotRemoveDuplicateSnapshot()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var plan = new SelectionPlan { SemesterId = semester.SemesterId };
        var original = Course(semester.SemesterId, "Course A", "T", "R1");
        var conflict = Course(semester.SemesterId, "Course B", "T", "R2");
        conflict.MeetingTimes[0].Weekday = original.MeetingTimes[0].Weekday;
        conflict.MeetingTimes[0].StartPeriod = original.MeetingTimes[0].StartPeriod;
        conflict.MeetingTimes[0].EndPeriod = original.MeetingTimes[0].EndPeriod;
        CourseIdentityService.AssignOfferingId(conflict);
        var library = new List<CourseOffering> { original, conflict };
        PlannerDomainService.AddCourseToPlan(plan, original, semester, DuplicateResolution.SkipExisting, ConflictResolution.KeepConflict, library);
        PlannerDomainService.AddCourseToPlan(plan, conflict, semester, DuplicateResolution.SkipExisting, ConflictResolution.KeepConflict, library);

        var duplicateReplacement = JsonDefaults.Clone(original);
        duplicateReplacement.Notes = "replacement";
        library.Add(duplicateReplacement);
        var result = PlannerDomainService.AddCourseToPlan(plan, duplicateReplacement, semester, DuplicateResolution.ReplaceExisting, ConflictResolution.Cancel, library);

        Assert.True(result.Cancelled);
        Assert.Equal(2, plan.Snapshots.Count);
        Assert.Contains(plan.Snapshots, x => x.CourseOfferingId == original.OfferingId);
        Assert.Contains(plan.Snapshots, x => x.CourseOfferingId == conflict.OfferingId);
    }

    [Fact]
    public void CancellingCourseConflictDoesNotNormalizeOrOtherwiseMutatePlan()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var semester = document.Semesters[0];
        var existingCourses = document.CourseLibrary.Take(2).ToArray();
        var plan = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            ModifiedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Snapshots =
            {
                new PlanCourseSnapshot { CourseOfferingId = existingCourses[0].OfferingId, RegistrationOrder = 99 },
                new PlanCourseSnapshot { CourseOfferingId = existingCourses[1].OfferingId, RegistrationOrder = -5 }
            }
        };
        var conflicting = JsonDefaults.Clone(existingCourses[0]);
        conflicting.CourseName = "Different identity, same slot";
        CourseIdentityService.AssignOfferingId(conflicting);
        var library = existingCourses.Append(conflicting).ToArray();
        var before = JsonSerializer.Serialize(plan, JsonDefaults.Options);

        var result = PlannerDomainService.AddCourseToPlan(
            plan,
            conflicting,
            semester,
            DuplicateResolution.SkipExisting,
            ConflictResolution.Cancel,
            library);

        Assert.True(result.Cancelled);
        Assert.False(result.Added);
        Assert.Equal(before, JsonSerializer.Serialize(plan, JsonDefaults.Options));
    }

    [Fact]
    public void SkippingExistingCourseDoesNotNormalizeOrOtherwiseMutatePlan()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var semester = document.Semesters[0];
        var existingCourses = document.CourseLibrary.Take(2).ToArray();
        var plan = new SelectionPlan
        {
            SemesterId = semester.SemesterId,
            ModifiedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Snapshots =
            {
                new PlanCourseSnapshot { CourseOfferingId = existingCourses[0].OfferingId, RegistrationOrder = 99 },
                new PlanCourseSnapshot { CourseOfferingId = existingCourses[1].OfferingId, RegistrationOrder = -5 }
            }
        };
        var before = JsonSerializer.Serialize(plan, JsonDefaults.Options);

        var result = PlannerDomainService.AddCourseToPlan(
            plan,
            existingCourses[0],
            semester,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict,
            existingCourses);

        Assert.False(result.Cancelled);
        Assert.False(result.Added);
        Assert.Equal(before, JsonSerializer.Serialize(plan, JsonDefaults.Options));
    }

    [Fact]
    public void ApplyingPlanDisplayOrderReordersKnownPlansAndKeepsRemainderStable()
    {
        var plans = new List<SelectionPlan>
        {
            new() { PlanId = "a", PlanName = "A", DisplayOrder = 0 },
            new() { PlanId = "b", PlanName = "B", DisplayOrder = 1 },
            new() { PlanId = "c", PlanName = "C", DisplayOrder = 2 },
            new() { PlanId = "d", PlanName = "D", DisplayOrder = 3 }
        };

        var changed = PlannerDomainService.ApplyPlanDisplayOrder(plans, new[] { "c", "a" });

        Assert.Equal(3, changed);
        Assert.Equal(0, plans.Single(x => x.PlanId == "c").DisplayOrder);
        Assert.Equal(1, plans.Single(x => x.PlanId == "a").DisplayOrder);
        Assert.Equal(2, plans.Single(x => x.PlanId == "b").DisplayOrder);
        Assert.Equal(3, plans.Single(x => x.PlanId == "d").DisplayOrder);
    }

    [Fact]
    public void AddingPeriodAfterSelectedPeriodReindexesLaterMeetings()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = doc.CourseLibrary.Single(x => x.CourseName == "Data Structures");
        var originalCount = semester.PeriodSchedule.Count;

        var inserted = PeriodScheduleService.AddPeriodAfter(
            semester,
            2,
            doc.CourseLibrary.Where(x => x.SemesterId == semester.SemesterId),
            doc.Plans.Where(x => x.SemesterId == semester.SemesterId).SelectMany(x => x.Snapshots));

        Assert.Equal(3, inserted.Period);
        Assert.Equal(originalCount + 1, semester.PeriodSchedule.Count);
        Assert.Equal(Enumerable.Range(1, semester.PeriodSchedule.Count), semester.PeriodSchedule.Select(x => x.Period));
        Assert.All(course.MeetingTimes, meeting => Assert.Equal((4, 5), (meeting.StartPeriod, meeting.EndPeriod)));
    }

    [Fact]
    public void PeriodReindexPreservesPlanSnapshotReferences()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var plan = doc.Plans[0];
        var snapshot = plan.Snapshots[0];
        var course = PlanCourseResolver.CourseForSnapshot(snapshot, doc.CourseLibrary)!;

        PeriodScheduleService.AddPeriodAfter(
            semester,
            2,
            doc.CourseLibrary.Where(x => x.SemesterId == semester.SemesterId),
            doc.Plans.Where(x => x.SemesterId == semester.SemesterId).SelectMany(x => x.Snapshots));
        DocumentConsistencyService.Ensure(doc);

        Assert.Contains(plan.Snapshots, x => x.CourseOfferingId == course.OfferingId);
        Assert.Same(course, PlanCourseResolver.CourseForSnapshot(snapshot, doc.CourseLibrary));
    }

    [Fact]
    public void AddingPeriodWithoutSelectionAppendsToEnd()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = doc.CourseLibrary.Single(x => x.CourseName == "Data Structures");
        var originalCount = semester.PeriodSchedule.Count;

        var inserted = PeriodScheduleService.AddPeriodAfter(
            semester,
            null,
            doc.CourseLibrary.Where(x => x.SemesterId == semester.SemesterId),
            doc.Plans.Where(x => x.SemesterId == semester.SemesterId).SelectMany(x => x.Snapshots));

        Assert.Equal(originalCount + 1, inserted.Period);
        Assert.Equal(Enumerable.Range(1, originalCount + 1), semester.PeriodSchedule.Select(x => x.Period));
        Assert.All(course.MeetingTimes, meeting => Assert.Equal((3, 4), (meeting.StartPeriod, meeting.EndPeriod)));
    }

    [Fact]
    public void PeriodScheduleRejectsMissingSelectedPeriod()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var libraryCourses = doc.CourseLibrary.Where(x => x.SemesterId == semester.SemesterId);
        var planSnapshots = doc.Plans.Where(x => x.SemesterId == semester.SemesterId).SelectMany(x => x.Snapshots);

        Assert.Throws<InvalidOperationException>(() => PeriodScheduleService.AddPeriodAfter(semester, 99, libraryCourses, planSnapshots));
        Assert.Throws<InvalidOperationException>(() => PeriodScheduleService.DeletePeriod(semester, 99, libraryCourses, planSnapshots));
        Assert.Throws<InvalidOperationException>(() => PeriodScheduleService.UpdatePeriodTime(semester, 99, new TimeOnly(8, 0), new TimeOnly(8, 45)));
    }

    [Fact]
    public void DeletingPeriodReindexesPeriodsAndCourseMeetings()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = doc.CourseLibrary.Single(x => x.CourseName == "Data Structures");

        PeriodScheduleService.DeletePeriod(
            semester,
            2,
            doc.CourseLibrary.Where(x => x.SemesterId == semester.SemesterId),
            doc.Plans.Where(x => x.SemesterId == semester.SemesterId).SelectMany(x => x.Snapshots));

        Assert.Equal(11, semester.PeriodSchedule.Count);
        Assert.Equal(Enumerable.Range(1, 11), semester.PeriodSchedule.Select(x => x.Period));
        Assert.All(course.MeetingTimes, meeting => Assert.Equal((2, 3), (meeting.StartPeriod, meeting.EndPeriod)));
    }

    [Fact]
    public void DocumentConsistencyPreservesEmptyOpenPlanTabs()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        doc.Settings.OpenPlanIds.Clear();
        doc.Settings.CurrentPlanId = null;

        DocumentConsistencyService.Ensure(doc);

        Assert.Empty(doc.Settings.OpenPlanIds);
        Assert.Null(doc.Settings.CurrentPlanId);
    }

    [Fact]
    public void CurrentContextResolutionIndexesNearCapacityPlansOnce()
    {
        var semester = new Semester
        {
            SemesterId = "resolver-semester",
            SemesterName = "Resolver semester"
        };
        var plans = Enumerable.Range(0, PlannerDataLimits.MaxPlans)
            .Select(index => new SelectionPlan
            {
                PlanId = $"resolver-plan-{index}",
                SemesterId = semester.SemesterId,
                PlanName = $"Resolver {index}"
            })
            .ToList();
        var document = new PlannerDocument
        {
            Semesters = [semester],
            Plans = plans,
            Settings = new AppSettings
            {
                CurrentSemesterId = semester.SemesterId,
                CurrentPlanId = plans[^1].PlanId,
                OpenPlanIds = plans.Select(plan => plan.PlanId).ToList()
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var context = DocumentConsistencyService.ResolveCurrentContext(document);
        stopwatch.Stop();

        Assert.Same(semester, context.Semester);
        Assert.Same(plans[^1], context.Plan);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Resolving {plans.Count} plans took {stopwatch.Elapsed.TotalMilliseconds:N1} ms.");
    }

    [Fact]
    public void WholeSemesterExportCreatesPngAndVectorPdfFiles()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        doc.Semesters[0].WeekCount = 2;
        doc.Semesters[0].EndDate = SemesterRules.CalculateEndDate(
            doc.Semesters[0].StartDate,
            doc.Semesters[0].WeekCount,
            doc.Semesters[0].WeekStartDay);
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var request = new TimetableExportRequest
        {
            Semester = doc.Semesters[0],
            Plan = doc.Plans[0],
            Text = ExportText(),
            Fonts = ExportFonts(),
            Options = new TimetableExportOptions
            {
                ContentKind = ExportContentKind.DetailedSemester,
                FileFormat = ExportFileFormat.Png,
                ImageClarity = ImageClarity.Standard,
                CourseBlockFields = CourseBlockFields.Default,
                StartWeek = 1,
                EndWeek = 2
            }
        };
        var png = Path.Combine(temp, "whole-semester.png");
        var pdf = Path.Combine(temp, "whole-semester.pdf");

        try
        {
            TimetableExportService.ExportPng(request, png);
            request.Options.FileFormat = ExportFileFormat.Pdf;
            request.Options.ImageClarity = null;
            TimetableExportService.ExportPdf(request, pdf);

            Assert.True(new FileInfo(png).Length > 0);
            Assert.True(new FileInfo(pdf).Length > 0);
            Assert.Equal("%PDF"u8.ToArray(), File.ReadAllBytes(pdf).Take(4).ToArray());
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void PngExportAcceptsFileNameWithoutDirectory()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var fileName = $"week-{Guid.NewGuid():N}.png";
        var request = ExportRequest(doc);

        try
        {
            TimetableExportService.ExportPng(request, fileName);
            Assert.True(new FileInfo(fileName).Length > 0);
        }
        finally
        {
            if (File.Exists(fileName))
                File.Delete(fileName);
        }
    }

    [Fact]
    public void BackupZipContainsDatabaseAndManifest()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var repo = new SqliteAppRepository(temp);
        var doc = TestDocumentFactory.CreatePopulated();
        repo.Save(doc, "test");
        var zip = Path.Combine(temp, "backup.zip");

        BackupService.CreateBackup(repo.DatabasePath, zip);

        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("course-planner.sqlite"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
    }

    [Fact]
    public void BackupServiceAcceptsZipPathWithoutDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var repo = new SqliteAppRepository(temp);
        repo.Save(TestDocumentFactory.CreatePopulated(), "test");
        var zipName = $"backup-{Guid.NewGuid():N}.zip";
        var zipPath = Path.GetFullPath(zipName);

        try
        {
            BackupService.CreateBackup(repo.DatabasePath, zipName);
            Assert.True(File.Exists(zipPath));
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    [Fact]
    public void RestoreRejectsNonSqliteDatabase()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(JsonSerializer.Serialize(new BackupManifest(), JsonDefaults.Options));
            var db = archive.CreateEntry("course-planner.sqlite");
            using var dbWriter = new StreamWriter(db.Open());
            dbWriter.Write("not sqlite");
        }

        Assert.Throws<InvalidDataException>(() =>
            BackupService.RestoreWithPreBackup(Path.Combine(temp, "target.sqlite"), zip, Path.Combine(temp, "auto")));
    }

    [Fact]
    public void CourseLibraryImportPreservesLabels()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var importedLabel = new CourseLabel { Kind = LabelKind.Ordinary, Name = "Studio", DisplayOrder = 99 };
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = { JsonDefaults.Clone(doc.Semesters[0]) },
            Labels = { importedLabel }
        }, JsonDefaults.Options);

        var preview = ImportExportService.PreviewCourseLibrary(doc, json);

        Assert.Contains(preview.Items, x => x.Kind == "label" && x.DisplayName == "Studio" && x.Status == ImportPreviewStatus.Added);
        ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions());
        Assert.Contains(doc.Labels, x => x.Kind == LabelKind.Ordinary && x.Name == "Studio" && x.DisplayOrder == 99);
    }

    [Fact]
    public void InvalidImportedCourseColorIsRejectedWithoutLossyRegeneration()
    {
        var doc = TestDocumentFactory.CreatePopulated();
        var semester = doc.Semesters[0];
        var course = Course(semester.SemesterId, "Color Warning", "T", "R");
        course.Color = "blue";
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = { JsonDefaults.Clone(semester) },
            Courses = { course }
        }, JsonDefaults.Options);

        var preview = ImportExportService.PreviewCourseLibrary(doc, json);

        var result = ImportExportService.ApplyImport(doc, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Items.SelectMany(item => item.Errors), issue => issue.Code == "Import.InvalidJson");
        Assert.False(result.Applied);
        Assert.DoesNotContain(doc.CourseLibrary, item => item.CourseName == "Color Warning");
    }

    [Fact]
    public void MalformedJsonPreviewIsNotImportable()
    {
        var preview = ImportExportService.PreviewJson(TestDocumentFactory.CreatePopulated(), "{ definitely not json");

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewStatus.NotImportable, item.Status);
        Assert.Contains(item.Errors, x => x.Code == "Import.InvalidJson");
    }

    [Fact]
    public void UnsupportedSchemaPreviewIsNotImportable()
    {
        var json = JsonSerializer.Serialize(new CourseLibraryPackage { SchemaVersion = "1.0.0" }, JsonDefaults.Options);

        var preview = ImportExportService.PreviewJson(TestDocumentFactory.CreatePopulated(), json);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewStatus.NotImportable, item.Status);
        Assert.Contains(item.Errors, x => x.Code == "Import.UnsupportedSchemaVersion");
    }

    [Fact]
    public void LocalizationCatalogsDeclareSameKeys()
    {
        static string[] Keys(string file) =>
            XDocument.Load(file).Root!
                .Elements("data")
                .Select(x => (string?)x.Attribute("name") ?? "")
                .Order(StringComparer.Ordinal)
                .ToArray();

        var en = ProjectFilePath("CoursePlanner.Application", "Resources", "en-US", "Resources.resw");
        var zh = ProjectFilePath("CoursePlanner.Application", "Resources", "zh-Hans", "Resources.resw");

        Assert.Equal(Keys(en), Keys(zh));
    }

    [Fact]
    public void LocalizationCatalogsDoNotDeclareDuplicateKeys()
    {
        var localizationDirectory = ProjectFilePath("CoursePlanner.Application", "Resources");

        foreach (var file in Directory.EnumerateFiles(localizationDirectory, "*.resw", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(file);
            var duplicates = document.Root!
                .Elements("data")
                .Select(x => (string?)x.Attribute("name") ?? "")
                .GroupBy(x => x, StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToArray();

            Assert.True(
                duplicates.Length == 0,
                $"{Path.GetFileName(file)} has duplicate localization keys: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void ChineseLocalizationCatalogsSeparateCjkFromLatinNumbersAndAsciiSymbols()
    {
        var files = new[]
        {
            ProjectFilePath("CoursePlanner.Application", "Resources", "zh-Hans", "Resources.resw"),
            ProjectFilePath("CoursePlanner", "Strings", "zh-Hans", "Resources.resw")
        };
        var joinedValues = files
            .SelectMany(file => XDocument.Load(file).Root!
                .Elements("data")
                .Select(element => new
                {
                    File = Path.GetRelativePath(ProjectFilePath(), file),
                    Key = (string?)element.Attribute("name") ?? "",
                    Value = (string?)element.Element("value") ?? ""
                }))
            .Where(entry => HasInvalidChineseMixedTokenBoundary(entry.Value))
            .Select(entry => $"{entry.File}:{entry.Key}={entry.Value}")
            .ToArray();

        Assert.True(
            joinedValues.Length == 0,
            $"zh-Hans resources join CJK text with Latin letters, numbers, placeholders, or ASCII symbols: {string.Join("; ", joinedValues)}");
    }

    [Theory]
    [InlineData("中文English")]
    [InlineData("English中文")]
    [InlineData("中文123")]
    [InlineData("123中文")]
    [InlineData("中文&")]
    [InlineData("&中文")]
    [InlineData("{0}中文")]
    [InlineData("中文{0}")]
    public void ChineseLocalizationSpacingRuleRecognizesInvalidMixedTokenBoundaries(string value) =>
        Assert.True(HasInvalidChineseMixedTokenBoundary(value));

    [Theory]
    [InlineData("中文 English")]
    [InlineData("English 中文")]
    [InlineData("中文 123")]
    [InlineData("123 中文")]
    [InlineData("中文 & English")]
    [InlineData("{0} 中文")]
    [InlineData("中文，English")]
    [InlineData("其他方案……")]
    [InlineData("输入/输出")]
    [InlineData("课程-方案")]
    public void ChineseLocalizationSpacingRuleAcceptsSeparatedTokensAndChinesePunctuation(string value) =>
        Assert.False(HasInvalidChineseMixedTokenBoundary(value));

    [Fact]
    public void DropdownSelectorsUseWheelSafeComboBox()
    {
        var projectRoot = ProjectFilePath("CoursePlanner");
        var directXaml = Directory
            .EnumerateFiles(projectRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(IsSourcePath)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"<\s*ComboBox\b"))
            .Select(file => Path.GetRelativePath(projectRoot, file))
            .ToArray();
        var directCode = Directory
            .EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsSourcePath)
            .Where(file => !string.Equals(Path.GetFileName(file), "WheelSafeComboBox.cs", StringComparison.Ordinal))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bnew\s+ComboBox\s*[\(\{]"))
            .Select(file => Path.GetRelativePath(projectRoot, file))
            .ToArray();

        Assert.True(
            directXaml.Length == 0 && directCode.Length == 0,
            $"Direct ComboBox usage must use WheelSafeComboBox. XAML: {string.Join(", ", directXaml)}; C#: {string.Join(", ", directCode)}");
    }

    [Fact]
    public void ThemeResourcesAreSplitIntoMaterialStateAndDomainDictionaries()
    {
        var appXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "App.xaml"));
        Assert.Contains("Styles/MaterialResources.xaml", appXaml);
        Assert.Contains("Styles/ControlStateResources.xaml", appXaml);
        Assert.Contains("Styles/DomainColorResources.xaml", appXaml);
        Assert.DoesNotContain("Styles/ThemeResources.xaml", appXaml);
        Assert.False(File.Exists(ProjectFilePath("CoursePlanner", "Styles", "ThemeResources.xaml")));

        var materialResources = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "MaterialResources.xaml"));
        var stateResources = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "ControlStateResources.xaml"));
        var domainResources = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "DomainColorResources.xaml"));
        var materialLayer = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs"));
        var adaptiveMaterialResources = materialResources[..materialResources.IndexOf("<ResourceDictionary x:Key=\"HighContrast\"", StringComparison.Ordinal)];
        Assert.Contains("AppMaterialNavigationRailBrush", materialResources);
        Assert.DoesNotContain("AppMaterialOverlayPaneStrokeBrush", materialResources);
        Assert.Contains("<AcrylicBrush", adaptiveMaterialResources);
        Assert.Equal(2, materialResources.Split("<AcrylicBrush", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, materialResources.Split("x:Key=\"AppMaterialOverlayPaneBrush\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("AppMaterialFlyoutBrush", materialResources);
        Assert.Contains("AppMaterialSurface.OverlayPane => new(\"AppMaterialOverlayPaneBrush\"", materialLayer);
        Assert.DoesNotContain("AppMaterialSurface.TransientFlyout", materialLayer);
        Assert.Contains("AppColorRole.TextPrimary => \"AppTextPrimaryBrush\"", materialLayer);
        Assert.Contains("AppColorRole.TextSecondary => \"AppTextSecondaryBrush\"", materialLayer);
        Assert.DoesNotContain("AppCourseBlockBrush", materialResources);
        Assert.Contains("AppShellTabSelectedBrush", stateResources);
        Assert.Contains("AppColorRole.ShellTabRest => \"AppShellTabRestBrush\"", materialLayer);
        Assert.Contains("AppColorRole.ShellTabSelected => \"AppShellTabSelectedBrush\"", materialLayer);
        Assert.DoesNotContain("AppPickerFlyoutBrush", stateResources);
        Assert.DoesNotContain("AppCourseBlockBrush", stateResources);
        Assert.Contains("AppCourseBlockBrush", domainResources);
        Assert.Contains("AppSemesterOverviewCardBrush", domainResources);
        Assert.Contains("AppSemesterOverviewCardHoverBrush", domainResources);
        Assert.Contains("AppSemesterOverviewCardPressedBrush", domainResources);
        Assert.DoesNotContain("AppSemesterOverviewCardStrokeBrush", domainResources);
        Assert.DoesNotContain("AppShellTabSelectedBrush", domainResources);

        var retiredKeys = new[]
        {
            RetiredAppBrush("Shell", "Backdrop"),
            RetiredAppBrush("Navigation", "Pane"),
            RetiredAppBrush("Tab", "Layer"),
            RetiredAppBrush("Command", "Layer"),
            RetiredAppBrush("Content", "Layer"),
            RetiredAppBrush("Sidebar", "Layer"),
            RetiredAppBrush("Overlay", "Pane"),
            RetiredAppBrush("Card", "Layer"),
            RetiredAppBrush("Subtle", "Stroke"),
            RetiredAppBrush("Control", "Stroke"),
            "ShellPlanTab" + "HoverBackgroundBrush"
        };
        var sourceText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(ProjectFilePath("CoursePlanner"), "*.*", SearchOption.AllDirectories)
                .Where(IsSourcePath)
                .Where(file => Path.GetExtension(file) is ".xaml" or ".cs")
                .Select(File.ReadAllText));
        foreach (var key in retiredKeys)
            Assert.DoesNotContain(key, sourceText);
    }

    [Fact]
    public void AppBrandAccentFlowsThroughNativeControlsAndDomainRoles()
    {
        var appXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "App.xaml"));
        var materialLayer = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs"));
        var stateResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Styles", "ControlStateResources.xaml"));
        var domainResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Styles", "DomainColorResources.xaml"));
        var exportContracts = File.ReadAllText(ProjectFilePath("CoursePlanner.Export", "TimetableExportContracts.cs"));

        var xamlControlsIndex = appXaml.IndexOf("<XamlControlsResources", StringComparison.Ordinal);
        var stateResourcesIndex = appXaml.IndexOf("Styles/ControlStateResources.xaml", StringComparison.Ordinal);
        Assert.True(xamlControlsIndex >= 0 && stateResourcesIndex > xamlControlsIndex);

        Assert.Contains("<Color x:Key=\"SystemAccentColor\">#00857A</Color>", appXaml);
        Assert.Contains("<Color x:Key=\"SystemAccentColor\">#72DED0</Color>", appXaml);
        Assert.Contains("AccentFillColorDefaultBrush\" Color=\"#00857A", appXaml);
        Assert.Contains("AccentFillColorDefaultBrush\" Color=\"#72DED0", appXaml);
        Assert.Contains("AppColorRole.PickerSelected => \"AppPickerSelectedBrush\"", materialLayer);
        Assert.Contains("AppColorRole.PickerSelectedText => \"AppPickerSelectedTextBrush\"", materialLayer);
        Assert.Equal("#00857A", ThemeBrushColor(stateResources, "Light", "AppPickerSelectedBrush"));
        Assert.Equal("#72DED0", ThemeBrushColor(stateResources, "Dark", "AppPickerSelectedBrush"));
        Assert.Equal("#00857A", ThemeBrushColor(domainResources, "Light", "AppStatusCurrentBrush"));
        Assert.Equal("#72DED0", ThemeBrushColor(domainResources, "Dark", "AppStatusCurrentBrush"));

        var lightContrast = ContrastRatio(
            ParseResourceColor("#00857A"),
            ParseResourceColor("#FFFFFF"));
        var darkContrast = ContrastRatio(
            ParseResourceColor("#72DED0"),
            ParseResourceColor("#0B2622"));
        Assert.True(lightContrast >= 4.5, $"Light accent contrast is {lightContrast:0.00}:1; expected at least 4.5:1.");
        Assert.True(darkContrast >= 4.5, $"Dark accent contrast is {darkContrast:0.00}:1; expected at least 4.5:1.");

        Assert.Contains("StatusCurrent = TimetableExportColor.FromHex(\"#00857A\")", exportContracts);
        Assert.Contains("StatusCurrent = TimetableExportColor.FromHex(\"#72DED0\")", exportContracts);
        Assert.Contains("ResourceKey=\"SystemColorHighlightColorBrush\"", HighContrastResource(domainResources, "AppStatusCurrentBrush"));
        Assert.DoesNotContain("SystemAccentColor", ThemeDictionaryXml(stateResources, "HighContrast"));
        Assert.DoesNotContain("SystemAccentColor", ThemeDictionaryXml(domainResources, "HighContrast"));
    }

    [Fact]
    public void HoverStatesUseNativeFluentResourcesAndCourseChangesRemainVisible()
    {
        var stateText = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "ControlStateResources.xaml"));
        var materialLayer = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs"));
        var domainResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Styles", "DomainColorResources.xaml"));

        Assert.Contains("AppColorRole.ShellTabHover => \"AppShellTabHoverBrush\"", materialLayer);
        Assert.Contains("AppColorRole.PickerHover => \"AppPickerHoverBrush\"", materialLayer);
        Assert.Contains("AppColorRole.CalendarHeaderHover => \"AppCalendarHeaderHoverBrush\"", materialLayer);
        Assert.Contains("AppColorRole.CalendarDateHover => \"AppCalendarDateHoverBrush\"", materialLayer);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"ButtonBackgroundPointerOver\"", stateText);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"ListViewItemBackgroundPointerOver\"", stateText);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"TreeViewItemBackgroundPointerOver\"", stateText);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"NavigationViewItemBackgroundPointerOver\"", stateText);
        Assert.DoesNotContain("<SolidColorBrush x:Key=\"MenuFlyoutItemBackgroundPointerOver\"", stateText);

        var stateResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Styles", "ControlStateResources.xaml"));
        foreach (var key in new[] { "AppShellTabHoverBrush", "AppPickerHoverBrush", "AppCalendarHeaderHoverBrush", "AppCalendarDateHoverBrush" })
            AssertLightHoverDelta(stateResources, key, minimumDelta: 18);

        foreach (var key in new[]
                 {
                     "AppCourseBlockHoverBrush",
                     "AppCourseBlockAddedHoverBrush",
                     "AppCourseBlockRemovedHoverBrush",
                     "AppCourseBlockModifiedHoverBrush"
                 })
        {
            AssertLightHoverDelta(domainResources, key, minimumDelta: 24);
        }
    }

    [Fact]
    public void ManagementDetailCardsUseSharedSurfaceStyle()
    {
        var courseLibraryXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "CourseLibraryPage.xaml"));
        var plansXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlansPage.xaml"));
        var settingsXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "SettingsPage.xaml"));

        var appStyles = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "AppStyles.xaml"));
        Assert.Contains("<Style x:Key=\"AppCardSurfaceStyle\" TargetType=\"Border\">", appStyles);
        Assert.Contains("Property=\"services:AppMaterialLayer.Surface\" Value=\"Card\"", appStyles);
        Assert.Contains("Property=\"Background\" Value=\"{ThemeResource AppMaterialCardBrush}\"", appStyles);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0\"", appStyles);
        Assert.Contains("Property=\"CornerRadius\" Value=\"6\"", appStyles);

        var appMaterialLayer = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs"));
        Assert.Contains("AppMaterialSurface.Card => new(\"AppMaterialCardBrush\", null, new Thickness(0), new CornerRadius(6), AppMaterialElevation.None)", appMaterialLayer);

        Assert.Contains("Style=\"{StaticResource AppCardSurfaceStyle}\"", courseLibraryXaml);
        Assert.Contains("Style=\"{StaticResource AppCardSurfaceStyle}\"", plansXaml);
        Assert.Contains("BasedOn=\"{StaticResource AppCardSurfaceStyle}\"", settingsXaml);

        Assert.DoesNotContain("Background=\"Transparent\" Style=\"{StaticResource AppCardSurfaceStyle}\"", courseLibraryXaml);
        Assert.DoesNotContain("Background=\"Transparent\" Style=\"{StaticResource AppCardSurfaceStyle}\"", plansXaml);
        Assert.DoesNotContain("Background=\"Transparent\" Style=\"{StaticResource SettingsRowCardStyle}\"", settingsXaml);
    }

    [Fact]
    public void MaterialLayerOwnsTransientAndOverlayMaterials()
    {
        var appMaterialLayer = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs"));
        Assert.False(File.Exists(ProjectFilePath("CoursePlanner", "Services", "AppMaterialStyles.cs")));
        Assert.DoesNotContain("DesktopAcrylicBackdrop", appMaterialLayer);
        Assert.Contains("AppAnimationLayer.ConfigureFlyout", appMaterialLayer);
        Assert.Contains("ThemeShadow", appMaterialLayer);
        Assert.Contains("AppMaterialSurface.OverlayPane", appMaterialLayer);
        Assert.Contains("AppMaterialSurface.OverlayPane => new(\"AppMaterialOverlayPaneBrush\", null, new Thickness(0), new CornerRadius(0), AppMaterialElevation.Layer)", appMaterialLayer);
        Assert.Contains("AppMaterialSurface.ShellTabRail", appMaterialLayer);
        Assert.Contains("AppMaterialSurface.ShellTabRail => new(\"AppShellTabRestBrush\"", appMaterialLayer);
        Assert.Contains("AppMaterialSurface.SemesterOverviewCard => new(\"AppSemesterOverviewCardBrush\", null, new Thickness(0), new CornerRadius(6), AppMaterialElevation.None)", appMaterialLayer);
        Assert.Contains("AppColorRole.SemesterOverviewCardHover => \"AppSemesterOverviewCardHoverBrush\"", appMaterialLayer);
        Assert.Contains("AppColorRole.SemesterOverviewCardPressed => \"AppSemesterOverviewCardPressedBrush\"", appMaterialLayer);
        Assert.DoesNotContain("TransientFlyout,", appMaterialLayer);
        Assert.Contains("AppTransientFlyoutPresenterStyle", appMaterialLayer);
        Assert.Contains("public static Color Color(AppColorRole role", appMaterialLayer);
        Assert.Contains("element.Loaded += OnSurfaceLoaded;", appMaterialLayer);
        Assert.Contains("ApplySurface(element, GetSurface(element));", appMaterialLayer);

        var appBrushes = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppBrushes.cs"));
        Assert.Contains("UseResolvedTheme", appBrushes);
        Assert.Contains("public static bool IsHighContrast", appBrushes);
        Assert.DoesNotContain("element?.ActualTheme", appBrushes);
        Assert.Contains("return _resolvedTheme == ResolvedThemeMode.Dark ? \"Dark\" : \"Light\";", appBrushes);

        var themeService = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "ThemeService.cs"));
        Assert.Contains("UISettings", themeService);
        Assert.Contains("UIColorType.Background", themeService);
        Assert.Contains("ResolveSystemTheme()", themeService);
        Assert.Contains("AppBrushes.UseResolvedTheme(resolved);", themeService);
        Assert.Contains("AppBrushes.UseResolvedTheme(ResolvedTheme);", themeService);
        Assert.Contains("AppMaterialLayer.RefreshTree(_themeRoot);", themeService);
        Assert.True(
            themeService.IndexOf("AppBrushes.UseResolvedTheme(resolved);", StringComparison.Ordinal) <
            themeService.IndexOf("ThemeChanged?.Invoke", StringComparison.Ordinal),
            "Resolved theme must update before dynamic controls rebuild from ThemeChanged.");

        var directAcrylicFiles = Directory
            .EnumerateFiles(ProjectFilePath("CoursePlanner"), "*.cs", SearchOption.AllDirectories)
            .Where(IsSourcePath)
            .Where(file => !string.Equals(Path.GetFileName(file), "AppMaterialLayer.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("DesktopAcrylicBackdrop", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(ProjectFilePath("CoursePlanner"), file))
            .ToArray();
        Assert.True(directAcrylicFiles.Length == 0, $"Only AppMaterialLayer may construct DesktopAcrylicBackdrop directly: {string.Join(", ", directAcrylicFiles)}");

        var datePicker = File.ReadAllText(ProjectFilePath("CoursePlanner", "Controls", "LocalizedCalendarDatePicker.cs"));
        var timePicker = File.ReadAllText(ProjectFilePath("CoursePlanner", "Controls", "CompactTimePicker.cs"));
        var filterBar = File.ReadAllText(ProjectFilePath("CoursePlanner", "Controls", "CourseLibraryFilterBar.cs"));
        foreach (var text in new[] { datePicker, timePicker })
        {
            Assert.Contains("AppMaterialLayer.ApplyTransientFlyout(_flyout)", text);
            Assert.DoesNotContain("CreateBorderlessFlyoutPresenterStyle", text);
            Assert.DoesNotContain("AppPickerFlyoutBrush\", Colors.White", text);
        }
        Assert.Contains("AppMaterialLayer.ApplyTransientFlyout(filterFlyout)", filterBar);

        var directMaterialBrushAccess = Directory
            .EnumerateFiles(ProjectFilePath("CoursePlanner"), "*.cs", SearchOption.AllDirectories)
            .Where(IsSourcePath)
            .Where(file => !string.Equals(Path.GetFileName(file), "AppMaterialLayer.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("AppBrushes.Resource(\"AppMaterial", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(ProjectFilePath("CoursePlanner"), file))
            .ToArray();
        Assert.True(directMaterialBrushAccess.Length == 0, $"Components must go through AppMaterialLayer for material brushes: {string.Join(", ", directMaterialBrushAccess)}");
    }

    [Fact]
    public void PlannerSidePanesUseBorderMaterialSurface()
    {
        var plannerXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlannerPage.xaml"));
        var plannerCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlannerPage.xaml.cs"));

        Assert.Contains("<Style x:Key=\"SidePaneStyle\" TargetType=\"Border\" BasedOn=\"{StaticResource PlannerSidePaneSurfaceStyle}\">", plannerXaml);
        Assert.Contains("x:Name=\"LibraryPane\"", plannerXaml);
        Assert.Contains("x:Name=\"DetailPane\"", plannerXaml);
        Assert.DoesNotContain("<Grid x:Name=\"LibraryPane\"", plannerXaml);
        Assert.DoesNotContain("<Grid x:Name=\"DetailPane\"", plannerXaml);
        Assert.Contains("ApplyPaneMaterial(pane, presentation == PanePresentation.Overlay, overlaySurface)", plannerCode);
        Assert.Contains("overlay ? overlaySurface : AppMaterialSurface.DockedPane", plannerCode);
        Assert.Equal(2, plannerCode.Split("overlaySurface: AppMaterialSurface.OverlayPane", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("AppMaterialSurface.DetailOverlayPane", plannerCode);
        Assert.DoesNotContain("AppMaterialOverlayPaneBrush", plannerCode);
        Assert.DoesNotContain("AppMaterialDockedPaneBrush", plannerCode);
    }

    [Fact]
    public void SemesterOverviewCardsUseColoredSurfaceInsteadOfCardShadow()
    {
        var plannerCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var materialResources = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "MaterialResources.xaml"));
        var domainResources = XDocument.Load(ProjectFilePath("CoursePlanner", "Styles", "DomainColorResources.xaml"));
        var renderStart = plannerCode.IndexOf("private void RenderSemesterOverview()", StringComparison.Ordinal);
        Assert.True(renderStart >= 0, "RenderSemesterOverview is missing.");
        var renderEnd = plannerCode.IndexOf("private void RenderComparisonView()", renderStart, StringComparison.Ordinal);
        Assert.True(renderEnd > renderStart, "RenderSemesterOverview boundary is missing.");
        var renderCode = plannerCode[renderStart..renderEnd];

        Assert.Contains("AppMaterialSurface.SemesterOverviewCard", renderCode);
        Assert.Contains("AppColorRole.SemesterOverviewCardHover", renderCode);
        Assert.Contains("AppColorRole.SemesterOverviewCardPressed", renderCode);
        Assert.DoesNotContain("AppMaterialSurface.Card", renderCode);
        Assert.DoesNotContain("AppSemesterOverviewCardStrokeBrush", renderCode);

        Assert.Contains("AppMaterialPageBrush\" Color=\"#EAF8FBFA", materialResources);
        Assert.Contains("AppMaterialPageBrush\" Color=\"#ED1E2321", materialResources);
        Assert.Contains("AppMaterialSurface.Page => new(\"AppMaterialPageBrush\"", File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppMaterialLayer.cs")));
        Assert.Equal("#FFFFFFFF", LightBrushColor(domainResources, "AppSemesterOverviewCardBrush"));
        Assert.Equal("#E4F1EC", LightBrushColor(domainResources, "AppSemesterOverviewCardHoverBrush"));
    }

    [Fact]
    public void PlannerWeekHeaderSeparatesDateTitleFromWeekSelector()
    {
        var plannerXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlannerPage.xaml"));
        var plannerCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var zhResources = File.ReadAllText(ProjectFilePath("CoursePlanner.Application", "Resources", "zh-Hans", "Resources.resw"));
        var enResources = File.ReadAllText(ProjectFilePath("CoursePlanner.Application", "Resources", "en-US", "Resources.resw"));

        Assert.Contains("x:Name=\"WeekSelectorHost\"", plannerXaml);
        Assert.Contains("x:Name=\"WeekSelectorPrefixText\"", plannerXaml);
        Assert.Contains("x:Name=\"WeekNumberBox\"", plannerXaml);
        Assert.Contains("x:Name=\"WeekSelectorSuffixText\"", plannerXaml);
        var headerStart = plannerXaml.IndexOf("<Grid Margin=\"10,8,10,0\"", StringComparison.Ordinal);
        Assert.True(headerStart >= 0, "Planner week header grid is missing.");
        var headerEnd = plannerXaml.IndexOf("<Grid Grid.Row=\"1\"", headerStart, StringComparison.Ordinal);
        Assert.True(headerEnd > headerStart, "Planner week header grid boundary is missing.");
        var headerXaml = plannerXaml[headerStart..headerEnd];
        Assert.Contains("ColumnDefinitions=\"*,Auto,Auto,Auto,Auto\"", headerXaml);
        Assert.Contains("x:Name=\"PreviousWeekButton\" AutomationProperties.AutomationId=\"PreviousWeekButton\" AutomationProperties.Name=\"\" Grid.Column=\"1\"", headerXaml);
        Assert.Contains("x:Name=\"WeekSelectorHost\"", headerXaml);
        Assert.Contains("Grid.Column=\"2\"", headerXaml);
        Assert.Contains("appControls:NumberBoxAssist.TextAlignment=\"Center\"", headerXaml);
        Assert.Contains("x:Name=\"NextWeekButton\" AutomationProperties.AutomationId=\"NextWeekButton\" AutomationProperties.Name=\"\" Grid.Column=\"3\"", headerXaml);
        Assert.True(
            headerXaml.IndexOf("x:Name=\"WeekTitleText\"", StringComparison.Ordinal) <
            headerXaml.IndexOf("x:Name=\"PreviousWeekButton\"", StringComparison.Ordinal) &&
            headerXaml.IndexOf("x:Name=\"PreviousWeekButton\"", StringComparison.Ordinal) <
            headerXaml.IndexOf("x:Name=\"WeekSelectorHost\"", StringComparison.Ordinal) &&
            headerXaml.IndexOf("x:Name=\"WeekSelectorHost\"", StringComparison.Ordinal) <
            headerXaml.IndexOf("x:Name=\"NextWeekButton\"", StringComparison.Ordinal),
            "Week header order must be date title, previous-week button, week selector, next-week button.");
        Assert.DoesNotContain("WeekHeaderPeriodColumn", plannerXaml);
        Assert.DoesNotContain("WeekHeaderPeriodColumn", plannerCode);

        var numberBoxAssist = File.ReadAllText(ProjectFilePath("CoursePlanner", "Controls", "NumberBoxAssist.cs"));
        Assert.Contains("DependencyProperty.RegisterAttached", numberBoxAssist);
        Assert.Contains("inputBox.TextAlignment = GetTextAlignment(numberBox);", numberBoxAssist);
        Assert.Contains("numberBox.ApplyTemplate();", numberBoxAssist);
        Assert.Contains("WeekSelectorPrefixText.Text = t[\"WeekSelectorPrefix\"];", plannerCode);
        Assert.Contains("WeekSelectorSuffixText.Text = t[\"WeekSelectorSuffix\"];", plannerCode);
        Assert.Contains("WeekSelectorHost.Visibility = isWeekGridView ? Visibility.Visible : Visibility.Collapsed;", plannerCode);
        Assert.Contains("string.Format(ViewModel.T[\"WeekTitleFormat\"], SemesterRules.WeekRangeText", plannerCode);
        Assert.DoesNotContain("string.Format(ViewModel.T[\"WeekTitleFormat\"], ViewModel.CurrentWeek", plannerCode);
        Assert.Contains("<data name=\"WeekTitleFormat\" xml:space=\"preserve\">", zhResources);
        Assert.Contains("<data name=\"WeekSelectorPrefix\" xml:space=\"preserve\">", zhResources);
        Assert.Contains("<data name=\"WeekSelectorSuffix\" xml:space=\"preserve\">", zhResources);
        Assert.Contains("<value>{0}</value>", zhResources);
        Assert.Contains("<value>第</value>", zhResources);
        Assert.Contains("<value>周</value>", zhResources);
        Assert.Contains("<data name=\"WeekTitleFormat\" xml:space=\"preserve\">", enResources);
        Assert.Contains("<data name=\"WeekSelectorPrefix\" xml:space=\"preserve\">", enResources);
        Assert.Contains("<data name=\"WeekSelectorSuffix\" xml:space=\"preserve\">", enResources);
        Assert.Contains("<value>{0}</value>", enResources);
        Assert.Contains("<value>Week</value>", enResources);
    }

    [Fact]
    public void ShellPlanTabsUseStateBrushesInsteadOfMaterialBrushes()
    {
        var mainWindowXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml.cs"));

        Assert.Contains("services:AppMaterialLayer.Surface=\"ShellTabRail\"", mainWindowXaml);
        Assert.Contains("AppColorRole.ShellTabSelected", mainWindowCode);
        Assert.Contains("AppColorRole.ShellTabRest", mainWindowCode);
        Assert.Contains("AppColorRole.ShellTabHover", mainWindowCode);
        Assert.Contains("ConfigureNativeTitleBar(e.ResolvedTheme);", mainWindowCode);
        Assert.Contains("var background = AppMaterialLayer.Color(AppMaterialSurface.Chrome", mainWindowCode);
        Assert.Contains("titleBar.ButtonBackgroundColor = background;", mainWindowCode);
        Assert.Contains("titleBar.ButtonInactiveBackgroundColor = background;", mainWindowCode);
        Assert.Contains("titleBar.ButtonHoverBackgroundColor = AppMaterialLayer.Color(AppColorRole.TitleBarHover", mainWindowCode);
        Assert.Contains("titleBar.ButtonHoverForegroundColor = AppMaterialLayer.Color(AppColorRole.InteractiveText", mainWindowCode);
        Assert.Contains("var showHighContrastSelection = isSelected && AppBrushes.IsHighContrast;", mainWindowCode);
        Assert.Contains("? new Thickness(2)", mainWindowCode);
        Assert.Contains(": new Thickness(0);", mainWindowCode);
        Assert.DoesNotContain("new Thickness(0, 0, 0, 2)", mainWindowCode);
        Assert.True(
            mainWindowCode.IndexOf("ExtendsContentIntoTitleBar = true;", StringComparison.Ordinal) <
            mainWindowCode.IndexOf("_services.Theme.AttachWindow(this);", StringComparison.Ordinal),
            "Native title bar colors must be configurable before the first forced theme notification.");
        Assert.True(
            mainWindowCode.IndexOf("_services.Theme.ThemeChanged += Theme_ThemeChanged;", StringComparison.Ordinal) <
            mainWindowCode.IndexOf("_services.Theme.AttachWindow(this);", StringComparison.Ordinal),
            "Shell must subscribe before initial theme attach so first-install dynamic tab brushes are refreshed.");
        Assert.DoesNotContain("AppBrushes.Resource(\"AppShellTab", mainWindowCode);
        Assert.DoesNotContain("AppBrushes.Resource(\"AppMaterialCommandBarBrush\")", mainWindowCode);
        Assert.DoesNotContain("AppBrushes.Resource(\"AppMaterialChromeBrush\")", mainWindowCode);
    }

    [Fact]
    public void LabelsNavigationUsesOriginalTagIcon()
    {
        var mainWindowXaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml"));
        var mainWindowCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "MainWindow.xaml.cs"));
        var appCommandIcons = File.ReadAllText(ProjectFilePath("CoursePlanner", "Services", "AppCommandIcons.cs"));
        var labelsItemStart = mainWindowXaml.IndexOf("x:Name=\"LabelsItem\"", StringComparison.Ordinal);
        Assert.True(labelsItemStart >= 0, "Labels navigation item is missing.");

        var labelsItemEnd = mainWindowXaml.IndexOf("</NavigationViewItem>", labelsItemStart, StringComparison.Ordinal);
        Assert.True(labelsItemEnd > labelsItemStart, "Labels navigation item is malformed.");

        var labelsItemXaml = mainWindowXaml[labelsItemStart..labelsItemEnd];
        Assert.DoesNotContain("FontFamily=", labelsItemXaml);
        Assert.Contains("<FontIcon Glyph=\"&#xE8EC;\" />", labelsItemXaml);
        Assert.DoesNotContain("LabelsItem.Icon =", mainWindowCode);
        Assert.DoesNotContain("LabelHash", appCommandIcons);
    }

    [Fact]
    public void ManagementPagesDoNotUseFixedBottomInsets()
    {
        var styles = File.ReadAllText(ProjectFilePath("CoursePlanner", "Styles", "AppStyles.xaml"));
        Assert.Contains("<Thickness x:Key=\"ManagementPagePadding\">0,16,0,0</Thickness>", styles);
        Assert.DoesNotContain("0,16,0,20", styles);
        Assert.DoesNotContain("ManagementScrollableContentMargin", styles);
        Assert.Contains("<Style x:Key=\"ManagementScrollViewerStyle\" TargetType=\"ScrollViewer\">", styles);

        var managementPages = new[]
        {
            "CourseLibraryPage.xaml",
            "LabelsPage.xaml",
            "PlansPage.xaml",
            "SemestersPage.xaml",
            "SettingsPage.xaml"
        };
        foreach (var page in managementPages)
        {
            var xaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", page));
            Assert.Contains("Style=\"{StaticResource ManagementRootGridStyle}\"", xaml);
            Assert.DoesNotContain("0,16,0,20", xaml);
        }

        var managementScrollPages = new[]
        {
            "CourseLibraryPage.xaml",
            "LabelsPage.xaml",
            "PlansPage.xaml",
            "SemestersPage.xaml",
            "SettingsPage.xaml"
        };

        foreach (var page in managementScrollPages)
        {
            var xaml = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", page));
            Assert.Contains("Style=\"{StaticResource ManagementScrollViewerStyle}\"", xaml);
            Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Disabled\"", xaml);
            Assert.DoesNotContain("HorizontalScrollMode=\"Disabled\"", xaml);
            Assert.DoesNotContain("VerticalScrollBarVisibility=\"Auto\"", xaml);
            Assert.DoesNotContain("VerticalScrollMode=\"Enabled\"", xaml);
        }

        var plansCode = File.ReadAllText(ProjectFilePath("CoursePlanner", "Pages", "PlansPage.xaml.cs"));
        Assert.Contains("TwoPaneLayoutService.SizeScrollableContent(PlanContentScrollViewer, PlanContentHost, PlanContentStack, responsiveWidth)", plansCode);
    }

    private static CourseOffering Course(string semesterId, string name, string teacher, string location)
    {
        var course = new CourseOffering
        {
            SemesterId = semesterId,
            CourseName = name,
            Teacher = teacher,
            Location = location,
            Credits = 3,
            Color = "#C3637A",
            MeetingTimes = { new MeetingTime { Weekday = 1, StartPeriod = 3, EndPeriod = 4, Weeks = "1-16" } }
        };
        CourseIdentityService.AssignOfferingId(course);
        return course;
    }

    private static string DreamHanSansScFontPath(string fileName) =>
        ProjectFilePath("CoursePlanner", "Assets", "Fonts", "DreamHanSansSC", fileName);

    private static string RetiredAppBrush(string area, string role) =>
        $"App{area}{role}Brush";

    private static void AssertLightHoverDelta(XDocument resources, string brushKey, double minimumDelta)
    {
        var color = ParseResourceColor(LightBrushColor(resources, brushKey));
        var luminance = CompositedWhiteLuminance(color);
        var delta = 255 - luminance;
        Assert.True(delta >= minimumDelta, $"{brushKey} is too close to white after alpha compositing. Delta={delta:0.0}, required={minimumDelta:0.0}.");
    }

    private static string LightBrushColor(XDocument resources, string brushKey)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var lightDictionary = resources
            .Descendants(presentation + "ResourceDictionary")
            .First(element => string.Equals((string?)element.Attribute(xaml + "Key"), "Light", StringComparison.Ordinal));
        var brush = lightDictionary
            .Descendants(presentation + "SolidColorBrush")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute(xaml + "Key"), brushKey, StringComparison.Ordinal));
        return (string?)brush?.Attribute("Color") ?? throw new InvalidOperationException($"Missing Light SolidColorBrush: {brushKey}");
    }

    private static string ThemeBrushColor(XDocument resources, string theme, string brushKey)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var dictionary = resources
            .Descendants(presentation + "ResourceDictionary")
            .First(element => string.Equals((string?)element.Attribute(xaml + "Key"), theme, StringComparison.Ordinal));
        var brush = dictionary
            .Descendants(presentation + "SolidColorBrush")
            .SingleOrDefault(element => string.Equals((string?)element.Attribute(xaml + "Key"), brushKey, StringComparison.Ordinal));
        return (string?)brush?.Attribute("Color") ?? throw new InvalidOperationException($"Missing {theme} SolidColorBrush: {brushKey}");
    }

    private static string HighContrastResource(XDocument resources, string resourceKey)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var dictionary = resources
            .Descendants(presentation + "ResourceDictionary")
            .First(element => string.Equals((string?)element.Attribute(xaml + "Key"), "HighContrast", StringComparison.Ordinal));
        var resource = dictionary
            .Elements()
            .SingleOrDefault(element => string.Equals((string?)element.Attribute(xaml + "Key"), resourceKey, StringComparison.Ordinal));
        return resource?.ToString(SaveOptions.DisableFormatting) ?? throw new InvalidOperationException($"Missing HighContrast resource: {resourceKey}");
    }

    private static string ThemeDictionaryXml(XDocument resources, string theme)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var dictionary = resources
            .Descendants(presentation + "ResourceDictionary")
            .First(element => string.Equals((string?)element.Attribute(xaml + "Key"), theme, StringComparison.Ordinal));
        return dictionary.ToString(SaveOptions.DisableFormatting);
    }

    private static RgbaColor ParseResourceColor(string color)
    {
        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 6)
            return new RgbaColor(255, HexByte(hex, 0), HexByte(hex, 2), HexByte(hex, 4));
        if (hex.Length == 8)
            return new RgbaColor(HexByte(hex, 0), HexByte(hex, 2), HexByte(hex, 4), HexByte(hex, 6));

        throw new InvalidOperationException($"Unsupported color literal: {color}");
    }

    private static byte HexByte(string hex, int start) =>
        byte.Parse(hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static double CompositedWhiteLuminance(RgbaColor color)
    {
        var alpha = color.A / 255d;
        var r = 255 * (1 - alpha) + color.R * alpha;
        var g = 255 * (1 - alpha) + color.G * alpha;
        var b = 255 * (1 - alpha) + color.B * alpha;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private static double ContrastRatio(RgbaColor first, RgbaColor second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(RgbaColor color) =>
        0.2126 * LinearizedChannel(color.R) +
        0.7152 * LinearizedChannel(color.G) +
        0.0722 * LinearizedChannel(color.B);

    private static double LinearizedChannel(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private readonly record struct RgbaColor(byte A, byte R, byte G, byte B);

    private static TimetableExportRequest ExportRequest(PlannerDocument doc) =>
        new()
        {
            Semester = doc.Semesters[0],
            Plan = doc.Plans[0],
            Text = ExportText(),
            Fonts = ExportFonts()
        };

    private static TimetableExportText ExportText() =>
        new()
        {
            Title = "Export Test",
            WeekSubtitle = "2026 Fall / Balanced Plan / Week 1 / 2026-09-07 - 2026-09-13",
            WeekRangeSubtitle = "2026 Fall / Balanced Plan / Weeks 1-16 / 2026-09-07 - 2026-12-27",
            DetailedSemesterSubtitle = "2026 Fall / Balanced Plan / entire semester / Weeks 1-16 / 2026-09-07 - 2026-12-27",
            WeekHeadingFormat = "Week {0}",
            BeforeSemesterText = "Before semester",
            AfterSemesterText = "After semester",
            WeekdayShortNames = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
        };

    private static TimetableExportFonts ExportFonts() =>
        new()
        {
            RegularFilePath = DreamHanSansScFontPath("DreamHanSans-W12.ttc"),
            BoldFilePath = DreamHanSansScFontPath("DreamHanSans-W22.ttc"),
            CourseBlockRegularFilePath = DreamHanSansScFontPath("DreamHanSans-W12.ttc"),
            CourseBlockBoldFilePath = DreamHanSansScFontPath("DreamHanSans-W22.ttc")
        };

    private static string ProjectFilePath(params string[] segments) =>
        RepositoryPaths.FromRoot(segments);

    private static bool HasInvalidChineseMixedTokenBoundary(string value)
    {
        const string cjk = @"\u3400-\u4dbf\u4e00-\u9fff";
        var withoutCjkConnectors = Regex.Replace(
            value,
            $@"(?<=[{cjk}])[-/](?=[{cjk}])",
            "",
            RegexOptions.CultureInvariant);
        return Regex.IsMatch(
            withoutCjkConnectors,
            $@"[{cjk}][!-~]|[!-~][{cjk}]",
            RegexOptions.CultureInvariant);
    }

    private static bool IsSourcePath(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}

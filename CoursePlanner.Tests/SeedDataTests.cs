using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class SeedDataTests
{
    [Fact]
    public void FreshInstallStartsWithOneEmptySemesterAndOneEmptyPlan()
    {
        var document = SeedData.Create();

        var semester = Assert.Single(document.Semesters);
        Assert.Equal("2026-fall", semester.SemesterId);
        Assert.Equal("2026 Fall", semester.SemesterName);
        Assert.Equal(new DateOnly(2026, 9, 7), semester.StartDate);
        Assert.Equal(new DateOnly(2027, 1, 10), semester.EndDate);
        Assert.Equal(18, semester.WeekCount);
        Assert.Equal(WeekStartDay.Monday, semester.WeekStartDay);

        Assert.Empty(document.CourseLibrary);
        var plan = Assert.Single(document.Plans);
        Assert.Equal(semester.SemesterId, plan.SemesterId);
        Assert.Equal("Plan 1", plan.PlanName);
        Assert.Empty(plan.Snapshots);

        Assert.Equal(semester.SemesterId, document.Settings.CurrentSemesterId);
        Assert.Equal(plan.PlanId, document.Settings.CurrentPlanId);
        Assert.Equal([plan.PlanId], document.Settings.OpenPlanIds);
        Assert.Equal(LanguageMode.FollowSystem, document.Settings.Language);
        Assert.Equal(ThemeMode.FollowSystem, document.Settings.Theme);
    }

    [Fact]
    public void DefaultPeriodScheduleMatchesBnuTeachingSchedule()
    {
        var expected = new[]
        {
            ("08:00", "08:45"),
            ("08:55", "09:40"),
            ("10:00", "10:45"),
            ("10:55", "11:40"),
            ("13:30", "14:15"),
            ("14:25", "15:10"),
            ("15:30", "16:15"),
            ("16:25", "17:10"),
            ("18:00", "18:45"),
            ("18:55", "19:40"),
            ("19:50", "20:35"),
            ("20:45", "21:30")
        };

        var actual = PeriodScheduleFactory.CreateDefault12();

        Assert.Equal(12, actual.Count);
        Assert.Equal(
            expected,
            actual.Select(period => ($"{period.Start:HH\\:mm}", $"{period.End:HH\\:mm}")));
        Assert.Equal(Enumerable.Range(1, 12), actual.Select(period => period.Period));
    }

    [Theory]
    [InlineData(LanguageMode.SimplifiedChinese, "2026-2027 学年第一学期", "方案 1")]
    [InlineData(LanguageMode.English, "2026 Fall", "Plan 1")]
    public void FreshInstallNamesFollowResolvedApplicationLanguage(
        LanguageMode language,
        string expectedSemesterName,
        string expectedPlanName)
    {
        var text = new AppLocalizer(language);
        var document = SeedData.Create(
            text["DefaultSemesterName"],
            text["DefaultPlanName"]);

        Assert.Equal(expectedSemesterName, document.Semesters[0].SemesterName);
        Assert.Equal(expectedPlanName, document.Plans[0].PlanName);
    }

    [Fact]
    public void RepositoryUsesInjectedLocalizedSeedOnlyWhenCreatingState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-seed-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteAppRepository(
                directory,
                () => SeedData.Create("本地化学期", "本地化方案"));

            var created = repository.LoadOrCreate();

            Assert.Equal("本地化学期", created.Semesters[0].SemesterName);
            Assert.Equal("本地化方案", created.Plans[0].PlanName);

            created.Semesters[0].SemesterName = "用户学期";
            created.Plans[0].PlanName = "用户方案";
            repository.Save(created);
            var reloaded = repository.LoadOrCreate();

            Assert.Equal("用户学期", reloaded.Semesters[0].SemesterName);
            Assert.Equal("用户方案", reloaded.Plans[0].PlanName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmptyCourseDetailsDoNotDisplaySyntheticZeroCredits()
    {
        var code = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "Pages", "PlannerPage.xaml.cs"));
        var start = code.IndexOf("private void LoadActiveCourseEditToFieldsCore()", StringComparison.Ordinal);
        var end = code.IndexOf("private async Task ReleaseDetailLoadingAfterPendingControlEventsAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = code[start..end];
        Assert.Contains("CreditsBox.Value = double.NaN;", method);
        Assert.DoesNotContain("CreditsBox.Value = 0;", method);
    }

    [Fact]
    public void CreditsAllowZeroAndCommonFractionsButRejectNegativeValues()
    {
        var semester = SeedData.Create().Semesters[0];
        foreach (var credits in new[] { 0m, 0.1m, 0.25m, 3.75m, 9999.999m })
        {
            var course = new CourseOffering
            {
                CourseName = "Credits",
                SemesterId = semester.SemesterId,
                Credits = credits,
                Color = CourseColorService.Generate(0)
            };

            var validation = CourseValidator.Validate(course, semester, allowUnscheduled: true);

            Assert.DoesNotContain(validation.Errors, issue => issue.Code == "CreditsNonNegative");
        }

        var negative = new CourseOffering
        {
            CourseName = "Negative credits",
            SemesterId = semester.SemesterId,
            Credits = -0.01m,
            Color = CourseColorService.Generate(0)
        };

        var rejected = CourseValidator.Validate(negative, semester, allowUnscheduled: true);

        Assert.Contains(rejected.Errors, issue => issue.Code == "CreditsNonNegative");
    }
}

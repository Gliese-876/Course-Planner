using System.Diagnostics;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class BulkAddCoursePerformanceTests
{
    [Fact]
    public void AddingOneCourseToTheMaximumEmptyPlanCatalogStaysInteractive()
    {
        var semester = new Semester
        {
            SemesterId = "semester",
            SemesterName = "Semester",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 6, 30)
        };
        var document = new PlannerDocument
        {
            Semesters = [semester],
            Settings = new AppSettings { CurrentSemesterId = semester.SemesterId }
        };
        for (var index = 0; index < PlannerDataLimits.MaxCourses; index++)
        {
            var course = new CourseOffering
            {
                SemesterId = semester.SemesterId,
                CourseName = $"Course {index:D4}",
                Teacher = "Teacher",
                Credits = 1m
            };
            CourseIdentityService.AssignOfferingId(course);
            document.CourseLibrary.Add(course);
        }

        for (var index = 0; index < PlannerDataLimits.MaxPlans; index++)
        {
            document.Plans.Add(new SelectionPlan
            {
                PlanId = $"plan-{index:D4}",
                SemesterId = semester.SemesterId,
                PlanName = $"Plan {index:D4}",
                DisplayOrder = index
            });
        }
        document.Settings.CurrentPlanId = document.Plans[0].PlanId;
        document.Settings.OpenPlanIds.Add(document.Plans[0].PlanId);

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(repository, () => document, (_, _) => { });
            var viewModel = new PlannerViewModel(session, new LocalizationService(session));
            var source = document.CourseLibrary[^1];

            var stopwatch = Stopwatch.StartNew();
            var result = viewModel.AddCourseToPlans(
                document.Plans,
                source,
                DuplicateResolution.SkipExisting,
                ConflictResolution.KeepConflict);
            stopwatch.Stop();

            Assert.Equal(PlannerDataLimits.MaxPlans, result.Added);
            Assert.All(document.Plans, plan => Assert.Single(plan.Snapshots));
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Adding one course to 5,000 empty plans took {stopwatch.Elapsed.TotalSeconds:F3}s.");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

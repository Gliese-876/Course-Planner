using System.Diagnostics;
using CoursePlanner.Core;
using CoursePlanner.Persistence;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class PersistencePerformanceTests
{
    [Fact]
    public void SavingTheLargestTextBoundCatalogStaysWithinAReasonableBudget()
    {
        var document = SeedData.Create("S", "P");
        var semester = document.Semesters[0];
        semester.SemesterId = "s";
        document.Labels.Clear();
        document.CourseLibrary.Clear();
        document.Plans.Clear();
        for (var index = 0; index < PlannerDataLimits.MaxLabels; index++)
        {
            document.Labels.Add(new CourseLabel
            {
                Name = $"l{index:x}",
                Kind = LabelKind.Ordinary,
                DisplayOrder = index
            });
        }

        for (var index = 0; index < PlannerDataLimits.MaxCourses; index++)
        {
            var course = new CourseOffering
            {
                SemesterId = semester.SemesterId,
                CourseName = $"c{index:x}",
                Credits = 1m
            };
            CourseIdentityService.AssignOfferingId(course);
            document.CourseLibrary.Add(course);
        }

        var snapshotSequence = 0;
        const int snapshotsPerPlan = 13;
        for (var planIndex = 0; planIndex < PlannerDataLimits.MaxPlans; planIndex++)
        {
            var plan = new SelectionPlan
            {
                PlanId = $"p{planIndex:x}",
                SemesterId = semester.SemesterId,
                PlanName = $"P{planIndex:x}",
                DisplayOrder = planIndex
            };
            for (var snapshotIndex = 0; snapshotIndex < snapshotsPerPlan; snapshotIndex++)
            {
                plan.Snapshots.Add(new PlanCourseSnapshot
                {
                    SnapshotId = $"x{snapshotSequence++:x}",
                    CourseOfferingId = document.CourseLibrary[snapshotIndex].OfferingId,
                    RegistrationOrder = snapshotIndex
                });
            }
            document.Plans.Add(plan);
        }
        document.Settings.CurrentSemesterId = semester.SemesterId;
        document.Settings.CurrentPlanId = document.Plans[0].PlanId;
        document.Settings.OpenPlanIds = [document.Plans[0].PlanId];

        Assert.Equal(65_000, snapshotSequence);
        Assert.True(
            PlannerDocumentTextCapacity.Count(document) <= PlannerDataLimits.MaxAggregateTextCharacters);

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteAppRepository(directory);
            repository.Initialize();

            var stopwatch = Stopwatch.StartNew();
            repository.Save(document, "performance.maximum-shape");
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Saving 5,000 courses, 5,000 plans, 512 labels and 65,000 snapshots took " +
                $"{stopwatch.Elapsed.TotalSeconds:F3}s.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

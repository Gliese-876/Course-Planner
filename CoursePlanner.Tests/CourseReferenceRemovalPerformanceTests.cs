using System.Diagnostics;
using CoursePlanner.Core;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class CourseReferenceRemovalPerformanceTests
{
    [Fact]
    public void BulkRemovalScansEachPlanOnlyOnceAtMaximumDocumentCapacity()
    {
        var removedIds = Enumerable.Range(0, PlannerDataLimits.MaxCourses)
            .Select(index => $"removed-{index}")
            .ToArray();
        var document = new PlannerDocument();
        var planCount = PlannerDataLimits.MaxTotalSnapshots / PlannerDataLimits.MaxSnapshotsPerPlan;
        for (var planIndex = 0; planIndex < planCount; planIndex++)
        {
            var plan = new SelectionPlan { PlanId = $"plan-{planIndex}" };
            for (var snapshotIndex = 0; snapshotIndex < PlannerDataLimits.MaxSnapshotsPerPlan; snapshotIndex++)
            {
                var globalIndex = planIndex * PlannerDataLimits.MaxSnapshotsPerPlan + snapshotIndex;
                plan.Snapshots.Add(new PlanCourseSnapshot
                {
                    CourseOfferingId = globalIndex % 2 == 0
                        ? removedIds[globalIndex % removedIds.Length]
                        : $"retained-{globalIndex}"
                });
            }

            document.Plans.Add(plan);
        }

        var stopwatch = Stopwatch.StartNew();
        var removed = PlannerDomainService.RemoveCourseReferences(document, removedIds);
        stopwatch.Stop();

        Assert.Equal(PlannerDataLimits.MaxTotalSnapshots / 2, removed);
        Assert.All(document.Plans, plan =>
        {
            Assert.Equal(PlannerDataLimits.MaxSnapshotsPerPlan / 2, plan.Snapshots.Count);
            Assert.DoesNotContain(plan.Snapshots, snapshot => snapshot.CourseOfferingId.StartsWith("removed-", StringComparison.Ordinal));
            Assert.Equal(
                Enumerable.Range(0, plan.Snapshots.Count).Select(value => (int?)value),
                plan.Snapshots.Select(snapshot => snapshot.RegistrationOrder));
        });
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Bulk reference removal took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void BulkRemovalIgnoresBlankAndDuplicateIdsAndLeavesUnaffectedPlansUntouched()
    {
        var removedPlan = new SelectionPlan
        {
            PlanId = "removed-plan",
            ModifiedAt = DateTimeOffset.UnixEpoch,
            Snapshots =
            [
                new PlanCourseSnapshot { CourseOfferingId = "remove", RegistrationOrder = 9 },
                new PlanCourseSnapshot { CourseOfferingId = "keep", RegistrationOrder = 8 }
            ]
        };
        var untouchedTimestamp = DateTimeOffset.UnixEpoch.AddDays(1);
        var untouchedPlan = new SelectionPlan
        {
            PlanId = "untouched-plan",
            ModifiedAt = untouchedTimestamp,
            Snapshots = [new PlanCourseSnapshot { CourseOfferingId = "keep", RegistrationOrder = 7 }]
        };
        var document = new PlannerDocument { Plans = [removedPlan, untouchedPlan] };

        var removed = PlannerDomainService.RemoveCourseReferences(
            document,
            ["", " ", "remove", "remove"]);

        Assert.Equal(1, removed);
        Assert.Equal("keep", Assert.Single(removedPlan.Snapshots).CourseOfferingId);
        Assert.Equal(0, removedPlan.Snapshots[0].RegistrationOrder);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, removedPlan.ModifiedAt);
        Assert.Equal(untouchedTimestamp, untouchedPlan.ModifiedAt);
        Assert.Equal(7, Assert.Single(untouchedPlan.Snapshots).RegistrationOrder);
    }
}

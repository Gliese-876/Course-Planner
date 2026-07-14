using CoursePlanner.Core;

namespace CoursePlanner.Tests;

public sealed class RegistrationPriorityServiceTests
{
    [Fact]
    public void RecommendationDistinguishesFillRatioAndRemainingSeatCounterexamples()
    {
        var courses = new[]
        {
            Course("half-large", "Half Large", 100, 200, weekday: 1),
            Course("empty-small", "Empty Small", 0, 5, weekday: 2),
            Course("half-small", "Half Small", 1, 2, weekday: 3),
            Course("near-full", "Near Full", 95, 100, weekday: 4)
        };
        var plan = Plan(courses);

        var analysis = RegistrationPriorityService.Analyze(plan, courses)
            .ToDictionary(item => item.OfferingId, StringComparer.Ordinal);
        var recommendation = RegistrationPriorityService.Recommend(plan, courses);

        Assert.True(analysis["half-small"].LogRisk > analysis["half-large"].LogRisk);
        Assert.True(analysis["near-full"].LogRisk > analysis["empty-small"].LogRisk);
        Assert.InRange(analysis["half-small"].PressureUpperBound!.Value, 0.8357, 0.8358);
        Assert.InRange(Math.Exp(analysis["half-large"].LogRisk!.Value), 4.46e-27, 4.47e-27);
        Assert.InRange(Math.Exp(analysis["empty-small"].LogRisk!.Value), 0.00092, 0.00093);
        Assert.InRange(Math.Exp(analysis["near-full"].LogRisk!.Value), 0.8648, 0.8649);
        Assert.Equal(
            new[] { "near-full", "half-small", "empty-small", "half-large" },
            recommendation.Select(item => item.OfferingId));
    }

    [Fact]
    public void ClassificationKeepsUnknownFullAndMissingItemsInStableBuckets()
    {
        var unknown = Course("unknown", "Unknown", null, null, weekday: 1);
        var full = Course("full", "Full", 5, 5, weekday: 2);
        var overbooked = Course("overbooked", "Overbooked", 6, 5, weekday: 3);
        var plan = new SelectionPlan
        {
            SemesterId = SemesterId,
            Snapshots =
            {
                Snapshot("unknown-snapshot", unknown.OfferingId, 0),
                Snapshot("full-snapshot", full.OfferingId, 1),
                Snapshot("overbooked-snapshot", overbooked.OfferingId, 2),
                Snapshot("missing-snapshot", "missing", 3)
            }
        };

        var recommendation = RegistrationPriorityService.Recommend(plan, new[] { unknown, full, overbooked });

        Assert.Equal(
            new[]
            {
                RegistrationPriorityState.Unknown,
                RegistrationPriorityState.Unavailable,
                RegistrationPriorityState.Unavailable,
                RegistrationPriorityState.MissingReference
            },
            recommendation.Select(item => item.State));
        Assert.False(recommendation[0].IsDataInconsistent);
        Assert.False(recommendation[1].IsDataInconsistent);
        Assert.True(recommendation[2].IsDataInconsistent);
        Assert.True(recommendation[3].IsDataInconsistent);
        Assert.Null(recommendation[0].LogScore);
        Assert.Null(recommendation[1].LogScore);
    }

    [Fact]
    public void EqualScoresUseTransientManualOrderWithoutMutatingThePlan()
    {
        var first = Course("first", "First", 5, 10, weekday: 1);
        var second = Course("second", "Second", 5, 10, weekday: 2);
        var plan = Plan(first, second);
        var physicalOrder = plan.Snapshots.ToArray();
        var persistedOrders = plan.Snapshots.Select(snapshot => snapshot.RegistrationOrder).ToArray();

        var recommendation = RegistrationPriorityService.Recommend(
            plan,
            new[] { first, second },
            new[] { "second-snapshot", "first-snapshot" });

        Assert.Equal(new[] { "second-snapshot", "first-snapshot" }, recommendation.Select(item => item.SnapshotId));
        Assert.Equal(physicalOrder, plan.Snapshots);
        Assert.Equal(persistedOrders, plan.Snapshots.Select(snapshot => snapshot.RegistrationOrder));
    }

    [Fact]
    public void CompatibleOpenParallelOfferingReducesPriorityButConflictingOneDoesNot()
    {
        var noAlternative = Course("no-alt", "No Alternative", 5, 10, weekday: 1);
        var target = Course("target", "Parallel Course", 5, 10, weekday: 2);
        var compatible = Course("compatible", "Parallel Course", 0, 10, weekday: 3);
        var conflicting = Course("conflicting", "Parallel Course", 0, 10, weekday: 1);
        var plan = Plan(noAlternative, target);

        var analysis = RegistrationPriorityService.Analyze(
                plan,
                new[] { noAlternative, target, compatible, conflicting })
            .ToDictionary(item => item.OfferingId, StringComparer.Ordinal);
        var recommendation = RegistrationPriorityService.Recommend(
            plan,
            new[] { noAlternative, target, compatible, conflicting });

        Assert.Equal(0d, analysis["no-alt"].EffectiveAlternatives);
        Assert.InRange(analysis["target"].EffectiveAlternatives, 0.99, 1.0);
        Assert.True(analysis["no-alt"].LogScore > analysis["target"].LogScore);
        Assert.Equal("no-alt", recommendation[0].OfferingId);
    }

    [Fact]
    public void RequiredCourseHasHigherAcademicValueWhenCapacityRiskIsEqual()
    {
        var elective = Course("elective", "Elective", 5, 10, weekday: 1);
        var required = Course("required", "Required", 5, 10, weekday: 2, studyType: PlannerLabels.Required);
        var plan = Plan(elective, required);

        var recommendation = RegistrationPriorityService.Recommend(plan, new[] { elective, required });

        Assert.True(recommendation[0].IsRequired);
        Assert.Equal("required", recommendation[0].OfferingId);
        Assert.Equal(recommendation[1].AcademicValue * 2d, recommendation[0].AcademicValue, 12);
    }

    [Fact]
    public void NormalizeAndApplyOrderAreDenseAndNeverReorderSnapshots()
    {
        var a = Snapshot("a", "a-course", 2);
        var b = Snapshot("b", "b-course", 0);
        var c = Snapshot("c", "c-course", null);
        var plan = new SelectionPlan { SemesterId = SemesterId, Snapshots = { a, b, c } };
        var physicalOrder = plan.Snapshots.ToArray();

        RegistrationPriorityService.NormalizeOrders(plan);

        Assert.Equal(1, a.RegistrationOrder);
        Assert.Equal(0, b.RegistrationOrder);
        Assert.Equal(2, c.RegistrationOrder);
        Assert.Equal(physicalOrder, plan.Snapshots);

        Assert.True(RegistrationPriorityService.ApplyOrder(plan, new[] { "c", "a", "b" }));
        Assert.Equal(1, a.RegistrationOrder);
        Assert.Equal(2, b.RegistrationOrder);
        Assert.Equal(0, c.RegistrationOrder);
        Assert.Equal(physicalOrder, plan.Snapshots);
        Assert.False(RegistrationPriorityService.ApplyOrder(plan, new[] { "c", "a", "b" }));
    }

    [Fact]
    public void LockedCoursesAreExcludedAndUnlockedOrdersRemainDense()
    {
        var first = Course("first", "First", 1, 10, weekday: 1);
        var locked = Course("locked", "Locked", 9, 10, weekday: 2);
        var last = Course("last", "Last", 2, 10, weekday: 3);
        var firstSnapshot = Snapshot("first-snapshot", first.OfferingId, 2);
        var lockedSnapshot = Snapshot("locked-snapshot", locked.OfferingId, 0);
        lockedSnapshot.IsLocked = true;
        var lastSnapshot = Snapshot("last-snapshot", last.OfferingId, null);
        var plan = new SelectionPlan
        {
            SemesterId = SemesterId,
            Snapshots = { firstSnapshot, lockedSnapshot, lastSnapshot }
        };

        RegistrationPriorityService.NormalizeOrders(plan);
        var analysis = RegistrationPriorityService.Analyze(plan, new[] { first, locked, last });

        Assert.Null(lockedSnapshot.RegistrationOrder);
        Assert.Equal(0, firstSnapshot.RegistrationOrder);
        Assert.Equal(1, lastSnapshot.RegistrationOrder);
        Assert.Equal(new[] { "first", "last" }, analysis.Select(item => item.OfferingId));
        Assert.True(RegistrationPriorityService.ApplyOrder(
            plan,
            new[] { "last-snapshot", "first-snapshot" }));
        Assert.Equal(1, firstSnapshot.RegistrationOrder);
        Assert.Null(lockedSnapshot.RegistrationOrder);
        Assert.Equal(0, lastSnapshot.RegistrationOrder);
    }

    private const string SemesterId = "semester";

    private static SelectionPlan Plan(params CourseOffering[] courses)
    {
        var plan = new SelectionPlan { SemesterId = SemesterId };
        for (var index = 0; index < courses.Length; index++)
            plan.Snapshots.Add(Snapshot($"{courses[index].OfferingId}-snapshot", courses[index].OfferingId, index));
        return plan;
    }

    private static PlanCourseSnapshot Snapshot(string snapshotId, string offeringId, int? order) => new()
    {
        SnapshotId = snapshotId,
        CourseOfferingId = offeringId,
        RegistrationOrder = order
    };

    private static CourseOffering Course(
        string offeringId,
        string name,
        int? enrolled,
        int? capacity,
        int weekday,
        string? studyType = PlannerLabels.Elective) => new()
        {
            OfferingId = offeringId,
            SemesterId = SemesterId,
            CourseName = name,
            Credits = 3m,
            StudyType = studyType,
            EnrolledCount = enrolled,
            Capacity = capacity,
            MeetingTimes =
        {
            new MeetingTime
            {
                Weekday = weekday,
                StartPeriod = 1,
                EndPeriod = 2,
                Weeks = "1-16"
            }
        }
        };
}

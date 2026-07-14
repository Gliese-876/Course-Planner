using CoursePlanner.Core;

namespace CoursePlanner.Tests;

public sealed class RegistrationPriorityEquivalenceTests
{
    private const double OneSidedNinetyPercentZ = 1.2815515655446004;

    [Fact]
    public void ApplyOrderPreservesPersistedTieOrderForUnknownAndDuplicateIds()
    {
        var remaining = Snapshot("remaining", registrationOrder: 0);
        var firstDuplicate = Snapshot("duplicate", registrationOrder: 1);
        var secondDuplicate = Snapshot("duplicate", registrationOrder: 2);
        var nullId = Snapshot(null!, registrationOrder: 3);
        var plan = new SelectionPlan
        {
            Snapshots = [secondDuplicate, nullId, remaining, firstDuplicate]
        };

        var changed = RegistrationPriorityService.ApplyOrder(
            plan,
            ["unknown", "duplicate", "duplicate", "duplicate", null!, null!]);

        Assert.True(changed);
        Assert.Equal(0, firstDuplicate.RegistrationOrder);
        Assert.Equal(1, secondDuplicate.RegistrationOrder);
        Assert.Equal(2, nullId.RegistrationOrder);
        Assert.Equal(3, remaining.RegistrationOrder);
    }

    [Fact]
    public void AnalyzeMatchesTheNaiveReplacementRulesAcrossRandomizedSmallDocuments()
    {
        var random = new Random(0x51A7E);
        var nonZeroAlternativeResults = 0;

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var (plan, library) = RandomDocument(random, iteration);
            var expected = NaiveEffectiveAlternatives(plan, library);

            var actual = RegistrationPriorityService.Analyze(plan, library);

            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index], actual[index].EffectiveAlternatives, 12);
                if (expected[index] > 0d)
                    nonZeroAlternativeResults++;
            }
        }

        Assert.True(
            nonZeroAlternativeResults >= 250,
            $"Only {nonZeroAlternativeResults} randomized results exercised a non-zero alternative contribution.");
    }

    private static (SelectionPlan Plan, List<CourseOffering> Library) RandomDocument(
        Random random,
        int iteration)
    {
        var library = new List<CourseOffering>();
        for (var index = 0; index < 10; index++)
        {
            var group = index % 3;
            var course = new CourseOffering
            {
                OfferingId = $"course-{iteration}-{index}",
                SemesterId = group == 2 && random.Next(3) == 0 ? "other-semester" : "semester",
                CourseName = group switch
                {
                    0 => random.Next(2) == 0 ? "Parallel Algorithms" : "  parallel   algorithms ",
                    1 => random.Next(2) == 0 ? "Database Systems" : "DATABASE SYSTEMS",
                    _ => random.Next(2) == 0 ? "Independent Study" : "Independent  Study"
                },
                Credits = group == 1 ? 2m : 3m,
                CourseGroupType = RandomCourseTypeLabel(random, group),
                StudyType = RandomStudyTypeLabel(random, group),
                EnrolledCount = random.Next(5) switch
                {
                    0 => null,
                    1 => 10,
                    2 => -1,
                    _ => random.Next(0, 10)
                },
                Capacity = 10
            };
            AddRandomMeetings(random, course);
            library.Add(course);
        }

        // DistinctBy must keep the first entry for a duplicate offering id.
        if (random.Next(3) == 0)
        {
            library.Add(new CourseOffering
            {
                OfferingId = library[0].OfferingId,
                SemesterId = "different",
                CourseName = "Duplicate that must be ignored",
                Credits = 99m,
                EnrolledCount = 0,
                Capacity = 1,
                MeetingTimes = { ValidMeeting(7, 20, 21) }
            });
        }

        if (iteration % 4 == 0)
        {
            ConfigureGuaranteedCompatiblePair(library[0], library[3]);
        }

        var plan = new SelectionPlan { SemesterId = "semester" };
        var snapshotCount = iteration % 4 == 0 ? 1 : random.Next(1, 7);
        for (var index = 0; index < snapshotCount; index++)
        {
            if (plan.Snapshots.Count > 0 && random.Next(16) == 0)
            {
                // Preserve the original method's unusual ReferenceEquals exclusion rule.
                plan.Snapshots.Add(plan.Snapshots[random.Next(plan.Snapshots.Count)]);
                continue;
            }

            var offeringId = iteration % 4 == 0
                ? library[0].OfferingId
                : random.Next(8) == 0
                ? $"missing-{iteration}-{index}"
                : library[random.Next(8)].OfferingId;
            plan.Snapshots.Add(new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-{iteration}-{index}",
                CourseOfferingId = offeringId,
                RegistrationOrder = index
            });
        }

        return (plan, library);
    }

    private static void ConfigureGuaranteedCompatiblePair(
        CourseOffering target,
        CourseOffering candidate)
    {
        foreach (var course in new[] { target, candidate })
        {
            course.SemesterId = "semester";
            course.CourseName = "Guaranteed Parallel";
            course.Credits = 3m;
            course.CourseGroupType = PlannerLabels.General;
            course.StudyType = PlannerLabels.Elective;
            course.EnrolledCount = 1;
            course.Capacity = 10;
            course.MeetingTimes.Clear();
        }

        target.MeetingTimes.Add(ValidMeeting(1, 1, 2));
        candidate.MeetingTimes.Add(ValidMeeting(2, 1, 2));
    }

    private static string? RandomCourseTypeLabel(Random random, int group) => group switch
    {
        0 => random.Next(3) switch { 0 => PlannerLabels.General, 1 => "general education", _ => "通识课程" },
        1 => random.Next(3) switch { 0 => PlannerLabels.Major, 1 => "major course", _ => "专业课" },
        _ => random.Next(3) switch { 0 => "custom", 1 => " CUSTOM ", _ => "other-custom" }
    };

    private static string? RandomStudyTypeLabel(Random random, int group) => group switch
    {
        0 => random.Next(3) switch { 0 => PlannerLabels.Elective, 1 => "optional course", _ => "选修课" },
        1 => random.Next(3) switch { 0 => PlannerLabels.Required, 1 => "mandatory course", _ => "必修课" },
        _ => random.Next(3) switch { 0 => null, 1 => "custom", _ => " CUSTOM " }
    };

    private static void AddRandomMeetings(Random random, CourseOffering course)
    {
        switch (random.Next(6))
        {
            case 0:
                return;
            case 1:
                course.MeetingTimes.Add(new MeetingTime
                {
                    Weekday = 0,
                    StartPeriod = 1,
                    EndPeriod = 2,
                    Weeks = "1-16"
                });
                return;
            case 2:
                course.MeetingTimes.Add(new MeetingTime
                {
                    Weekday = 1,
                    StartPeriod = 0,
                    EndPeriod = 2,
                    Weeks = "1-16"
                });
                return;
            default:
                var weekday = random.Next(1, 5);
                var start = random.Next(1, 8);
                course.MeetingTimes.Add(ValidMeeting(weekday, start, start + random.Next(0, 3)));
                if (random.Next(2) == 0)
                {
                    var secondStart = random.Next(1, 8);
                    course.MeetingTimes.Add(new MeetingTime
                    {
                        Weekday = random.Next(1, 5),
                        StartPeriod = secondStart,
                        EndPeriod = secondStart + random.Next(0, 3),
                        // The production rule deliberately ignores weeks.
                        Weeks = random.Next(2) == 0 ? "1-8" : "9-16"
                    });
                }
                return;
        }
    }

    private static MeetingTime ValidMeeting(int weekday, int start, int end) => new()
    {
        Weekday = weekday,
        StartPeriod = start,
        EndPeriod = end,
        Weeks = "1-16"
    };

    private static PlanCourseSnapshot Snapshot(string snapshotId, int registrationOrder) => new()
    {
        SnapshotId = snapshotId,
        CourseOfferingId = $"course-{registrationOrder}",
        RegistrationOrder = registrationOrder
    };

    private static List<double> NaiveEffectiveAlternatives(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses)
    {
        var courses = libraryCourses
            .Where(course => !string.IsNullOrWhiteSpace(course.OfferingId))
            .DistinctBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToList();
        var courseIndex = courses.ToDictionary(course => course.OfferingId, StringComparer.Ordinal);
        var planOfferingIds = plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .ToHashSet(StringComparer.Ordinal);
        var orderedSnapshots = plan.Snapshots
            .Select((snapshot, originalIndex) => (Snapshot: snapshot, OriginalIndex: originalIndex))
            .OrderBy(entry => entry.Snapshot.RegistrationOrder is >= 0 ? 0 : 1)
            .ThenBy(entry => entry.Snapshot.RegistrationOrder ?? int.MaxValue)
            .ThenBy(entry => entry.OriginalIndex)
            .Select(entry => entry.Snapshot)
            .ToList();
        var result = new List<double>(orderedSnapshots.Count);

        foreach (var targetSnapshot in orderedSnapshots)
        {
            if (!courseIndex.TryGetValue(targetSnapshot.CourseOfferingId, out var target) ||
                !TryGetActionableLogRisk(target, out _))
            {
                result.Add(0d);
                continue;
            }

            var alternatives = 0d;
            foreach (var candidate in courses)
            {
                if (!IsEquivalentParallelOffering(target, candidate) ||
                    planOfferingIds.Contains(candidate.OfferingId) ||
                    !TryGetActionableLogRisk(candidate, out var candidateLogRisk) ||
                    !CanSafelyReplace(plan, targetSnapshot, candidate, courseIndex))
                {
                    continue;
                }

                alternatives += Math.Clamp(1d - Math.Exp(candidateLogRisk), 0d, 1d);
            }

            result.Add(alternatives);
        }

        return result;
    }

    private static bool TryGetActionableLogRisk(CourseOffering course, out double logRisk)
    {
        logRisk = 0d;
        if (!course.EnrolledCount.HasValue || !course.Capacity.HasValue)
            return false;

        var enrolled = course.EnrolledCount.Value;
        var capacity = course.Capacity.Value;
        if (capacity <= 0 || enrolled < 0 || enrolled >= capacity)
            return false;

        var remaining = capacity - enrolled;
        var proportion = enrolled / (double)capacity;
        var zSquared = OneSidedNinetyPercentZ * OneSidedNinetyPercentZ;
        var pressureUpperBound =
            (proportion +
             (zSquared / (2d * capacity)) +
             (OneSidedNinetyPercentZ * Math.Sqrt(
                 (proportion * (1d - proportion) / capacity) +
                 (zSquared / (4d * capacity * capacity))))) /
            (1d + (zSquared / capacity));
        pressureUpperBound = Math.Clamp(pressureUpperBound, 1e-300, 1d);
        logRisk = remaining * Math.Log(pressureUpperBound);
        return true;
    }

    private static bool IsEquivalentParallelOffering(CourseOffering target, CourseOffering candidate)
    {
        var targetCourseType = CourseLabelSemantics.ClassifyCourseType(target.CourseGroupType);
        var candidateCourseType = CourseLabelSemantics.ClassifyCourseType(candidate.CourseGroupType);
        var targetStudyType = CourseLabelSemantics.ClassifyStudyType(target.StudyType);
        var candidateStudyType = CourseLabelSemantics.ClassifyStudyType(candidate.StudyType);
        return !string.Equals(target.OfferingId, candidate.OfferingId, StringComparison.Ordinal) &&
               string.Equals(target.SemesterId, candidate.SemesterId, StringComparison.Ordinal) &&
               string.Equals(
                   TextRules.NormalizeIdentityText(target.CourseName),
                   TextRules.NormalizeIdentityText(candidate.CourseName),
                   StringComparison.OrdinalIgnoreCase) &&
               target.Credits == candidate.Credits &&
               AreEquivalentLabels(
                   target.CourseGroupType,
                   candidate.CourseGroupType,
                   targetCourseType,
                   candidateCourseType,
                   CourseTypeSemantic.Unknown) &&
               AreEquivalentLabels(
                   target.StudyType,
                   candidate.StudyType,
                   targetStudyType,
                   candidateStudyType,
                   StudyTypeSemantic.Unknown);
    }

    private static bool AreEquivalentLabels<TSemantic>(
        string? leftLabel,
        string? rightLabel,
        TSemantic leftSemantic,
        TSemantic rightSemantic,
        TSemantic unknown)
        where TSemantic : struct, Enum =>
        TextRules.IsSameLabel(leftLabel, rightLabel) ||
        (!EqualityComparer<TSemantic>.Default.Equals(leftSemantic, unknown) &&
         EqualityComparer<TSemantic>.Default.Equals(leftSemantic, rightSemantic));

    private static bool CanSafelyReplace(
        SelectionPlan plan,
        PlanCourseSnapshot targetSnapshot,
        CourseOffering candidate,
        IReadOnlyDictionary<string, CourseOffering> courseIndex)
    {
        if (!HasUsableMeetingTimes(candidate))
            return false;

        foreach (var otherSnapshot in plan.Snapshots)
        {
            if (ReferenceEquals(otherSnapshot, targetSnapshot))
                continue;
            if (!courseIndex.TryGetValue(otherSnapshot.CourseOfferingId, out var otherCourse) ||
                !HasUsableMeetingTimes(otherCourse) ||
                MayConflict(candidate, otherCourse))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUsableMeetingTimes(CourseOffering course) =>
        course.MeetingTimes.Count > 0 &&
        course.MeetingTimes.All(meeting =>
            meeting.Weekday is >= 1 and <= 7 &&
            meeting.StartPeriod >= 1 &&
            meeting.EndPeriod >= meeting.StartPeriod);

    private static bool MayConflict(CourseOffering left, CourseOffering right) =>
        left.MeetingTimes.Any(leftMeeting =>
            right.MeetingTimes.Any(rightMeeting =>
                leftMeeting.Weekday == rightMeeting.Weekday &&
                leftMeeting.StartPeriod <= rightMeeting.EndPeriod &&
                rightMeeting.StartPeriod <= leftMeeting.EndPeriod));
}

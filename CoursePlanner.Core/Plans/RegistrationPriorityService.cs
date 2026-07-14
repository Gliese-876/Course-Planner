namespace CoursePlanner.Core;

public enum RegistrationPriorityState
{
    Actionable,
    Unknown,
    Unavailable,
    MissingReference
}

public sealed record RegistrationPriorityAnalysis
{
    public string SnapshotId { get; init; } = "";
    public string OfferingId { get; init; } = "";
    public int CurrentOrder { get; init; }
    public CourseOffering? Course { get; init; }
    public RegistrationPriorityState State { get; init; }
    public int? RemainingSeats { get; init; }
    public double? PressureUpperBound { get; init; }
    public double? LogRisk { get; init; }
    public double AcademicValue { get; init; }
    public double EffectiveAlternatives { get; init; }
    public double? LogScore { get; init; }
    public CourseTypeSemantic CourseType { get; init; }
    public StudyTypeSemantic StudyType { get; init; }
    public double CourseTypeWeight { get; init; } = 1d;
    public double StudyTypeWeight { get; init; } = 1d;
    public bool IsRequired { get; init; }
    public bool IsDataInconsistent { get; init; }
}

public static class RegistrationPriorityService
{
    private const double OneSidedNinetyPercentZ = 1.2815515655446004;
    private const double MinimumPositivePressure = 1e-300;

    public static void NormalizeOrders(SelectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var ordered = ResolveOrder(plan, currentSnapshotOrder: null);
        for (var index = 0; index < ordered.Count; index++)
            ordered[index].Snapshot.RegistrationOrder = index;
    }

    public static bool ApplyOrder(SelectionPlan plan, IReadOnlyList<string> snapshotIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshotIds);

        var ordered = ResolveOrder(plan, snapshotIds);
        var changed = false;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Snapshot.RegistrationOrder == index)
                continue;

            ordered[index].Snapshot.RegistrationOrder = index;
            changed = true;
        }

        return changed;
    }

    public static IReadOnlyList<RegistrationPriorityAnalysis> Analyze(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses,
        IReadOnlyList<string>? currentSnapshotOrder = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(libraryCourses);

        var courses = libraryCourses
            .Where(course => !string.IsNullOrWhiteSpace(course.OfferingId))
            .DistinctBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToList();
        var courseIndex = courses.ToDictionary(course => course.OfferingId, StringComparer.Ordinal);
        var semanticsIndex = courses.ToDictionary(
            course => course.OfferingId,
            CourseSemantics.For,
            StringComparer.Ordinal);
        var countIndex = courses.ToDictionary(
            course => course.OfferingId,
            AnalyzeCounts,
            StringComparer.Ordinal);
        var parallelKeyIndex = courses.ToDictionary(
            course => course.OfferingId,
            course => ParallelOfferingKey.For(course, semanticsIndex[course.OfferingId]),
            StringComparer.Ordinal);
        var planOfferingIds = plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .ToHashSet(StringComparer.Ordinal);
        var alternativeIndex = BuildAlternativeIndex(
            plan,
            courses,
            courseIndex,
            countIndex,
            parallelKeyIndex,
            planOfferingIds);
        var orderedSnapshots = ResolveOrder(plan, currentSnapshotOrder);
        var results = new List<RegistrationPriorityAnalysis>(orderedSnapshots.Count);

        for (var currentOrder = 0; currentOrder < orderedSnapshots.Count; currentOrder++)
        {
            var snapshot = orderedSnapshots[currentOrder].Snapshot;
            if (!courseIndex.TryGetValue(snapshot.CourseOfferingId, out var course))
            {
                results.Add(new RegistrationPriorityAnalysis
                {
                    SnapshotId = snapshot.SnapshotId,
                    OfferingId = snapshot.CourseOfferingId,
                    CurrentOrder = currentOrder + 1,
                    State = RegistrationPriorityState.MissingReference,
                    IsDataInconsistent = true
                });
                continue;
            }

            var counts = countIndex[course.OfferingId];
            var semantics = semanticsIndex[course.OfferingId];
            var isRequired = semantics.StudyType is StudyTypeSemantic.Core or StudyTypeSemantic.Required;
            var academicValue = AcademicValue(course, semantics.CourseTypeWeight, semantics.StudyTypeWeight);
            var effectiveAlternatives = counts.State == RegistrationPriorityState.Actionable
                ? CalculateEffectiveAlternatives(
                    snapshot,
                    parallelKeyIndex[course.OfferingId],
                    alternativeIndex)
                : 0d;
            double? logScore = counts.LogRisk.HasValue
                ? counts.LogRisk.Value + Math.Log(academicValue) - Math.Log(1d + effectiveAlternatives)
                : null;

            results.Add(new RegistrationPriorityAnalysis
            {
                SnapshotId = snapshot.SnapshotId,
                OfferingId = snapshot.CourseOfferingId,
                CurrentOrder = currentOrder + 1,
                Course = course,
                State = counts.State,
                RemainingSeats = counts.RemainingSeats,
                PressureUpperBound = counts.PressureUpperBound,
                LogRisk = counts.LogRisk,
                AcademicValue = academicValue,
                EffectiveAlternatives = effectiveAlternatives,
                LogScore = counts.State == RegistrationPriorityState.Actionable ? logScore : null,
                CourseType = semantics.CourseType,
                StudyType = semantics.StudyType,
                CourseTypeWeight = semantics.CourseTypeWeight,
                StudyTypeWeight = semantics.StudyTypeWeight,
                IsRequired = isRequired,
                IsDataInconsistent = counts.IsDataInconsistent
            });
        }

        return results;
    }

    public static IReadOnlyList<RegistrationPriorityAnalysis> Recommend(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses,
        IReadOnlyList<string>? currentSnapshotOrder = null)
    {
        return Analyze(plan, libraryCourses, currentSnapshotOrder)
            .OrderBy(item => StateRank(item.State))
            .ThenByDescending(item => item.State == RegistrationPriorityState.Actionable ? item.LogScore : null)
            .ThenBy(item => item.State == RegistrationPriorityState.Actionable ? item.RemainingSeats : null)
            .ThenByDescending(item => item.State == RegistrationPriorityState.Actionable ? item.PressureUpperBound : null)
            .ThenByDescending(item => item.State == RegistrationPriorityState.Actionable ? item.StudyTypeWeight : 0d)
            .ThenByDescending(item => item.State == RegistrationPriorityState.Actionable ? item.CourseTypeWeight : 0d)
            .ThenBy(item => item.State == RegistrationPriorityState.Actionable ? item.EffectiveAlternatives : 0d)
            .ThenBy(item => item.CurrentOrder)
            .ThenBy(item => item.SnapshotId, StringComparer.Ordinal)
            .Select((item, index) => item with { CurrentOrder = index + 1 })
            .ToList();
    }

    private static CountAnalysis AnalyzeCounts(CourseOffering course)
    {
        if (!course.EnrolledCount.HasValue || !course.Capacity.HasValue)
            return new CountAnalysis(RegistrationPriorityState.Unknown, null, null, null, false);

        var enrolled = course.EnrolledCount.Value;
        var capacity = course.Capacity.Value;
        if (capacity <= 0 || enrolled < 0)
            return new CountAnalysis(RegistrationPriorityState.Unknown, null, null, null, true);

        if (enrolled >= capacity)
        {
            return new CountAnalysis(
                RegistrationPriorityState.Unavailable,
                0,
                1d,
                0d,
                enrolled > capacity);
        }

        var remaining = capacity - enrolled;
        var proportion = enrolled / (double)capacity;
        var zSquared = OneSidedNinetyPercentZ * OneSidedNinetyPercentZ;
        var denominator = 1d + (zSquared / capacity);
        var pressureUpperBound =
            (proportion +
             (zSquared / (2d * capacity)) +
             (OneSidedNinetyPercentZ * Math.Sqrt(
                 (proportion * (1d - proportion) / capacity) +
                 (zSquared / (4d * capacity * capacity))))) /
            denominator;
        pressureUpperBound = Math.Clamp(pressureUpperBound, MinimumPositivePressure, 1d);
        var logRisk = remaining * Math.Log(pressureUpperBound);

        return new CountAnalysis(
            RegistrationPriorityState.Actionable,
            remaining,
            pressureUpperBound,
            logRisk,
            false);
    }

    private static double AcademicValue(
        CourseOffering course,
        double courseTypeWeight,
        double studyTypeWeight)
    {
        var nonNegativeCredits = Math.Max(0d, (double)course.Credits);
        var creditValue = 1d + Math.Log(1d + nonNegativeCredits);
        return creditValue * courseTypeWeight * studyTypeWeight;
    }

    private static double CalculateEffectiveAlternatives(
        PlanCourseSnapshot targetSnapshot,
        ParallelOfferingKey targetKey,
        IReadOnlyDictionary<ParallelOfferingKey, List<AlternativeCandidate>> alternativeIndex)
    {
        if (!alternativeIndex.TryGetValue(targetKey, out var candidates))
            return 0d;

        var effectiveAlternatives = 0d;
        foreach (var candidate in candidates)
        {
            if (candidate.Safety.CanReplace(targetSnapshot))
                effectiveAlternatives += candidate.FillContribution;
        }

        return effectiveAlternatives;
    }

    private static Dictionary<ParallelOfferingKey, List<AlternativeCandidate>> BuildAlternativeIndex(
        SelectionPlan plan,
        IReadOnlyList<CourseOffering> courses,
        IReadOnlyDictionary<string, CourseOffering> courseIndex,
        IReadOnlyDictionary<string, CountAnalysis> countIndex,
        IReadOnlyDictionary<string, ParallelOfferingKey> parallelKeyIndex,
        IReadOnlySet<string> planOfferingIds)
    {
        var meetingIndex = courses.ToDictionary(
            course => course.OfferingId,
            MeetingProfile.For,
            StringComparer.Ordinal);
        var planContexts = BuildPlanReplacementContexts(plan, courseIndex, meetingIndex);
        var targetKeys = plan.Snapshots
            .Where(snapshot =>
                countIndex.TryGetValue(snapshot.CourseOfferingId, out var counts) &&
                counts.State == RegistrationPriorityState.Actionable)
            .Select(snapshot => parallelKeyIndex[snapshot.CourseOfferingId])
            .ToHashSet(ParallelOfferingKeyComparer.Instance);
        var result = new Dictionary<ParallelOfferingKey, List<AlternativeCandidate>>(
            ParallelOfferingKeyComparer.Instance);

        foreach (var candidate in courses)
        {
            var key = parallelKeyIndex[candidate.OfferingId];
            var counts = countIndex[candidate.OfferingId];
            var meetings = meetingIndex[candidate.OfferingId];
            if (planOfferingIds.Contains(candidate.OfferingId) ||
                counts.State != RegistrationPriorityState.Actionable ||
                !counts.LogRisk.HasValue ||
                !meetings.IsUsable ||
                !targetKeys.Contains(key))
            {
                continue;
            }

            var safety = AnalyzeReplacementSafety(meetings, planContexts);
            var fillContribution = Math.Clamp(1d - Math.Exp(counts.LogRisk.Value), 0d, 1d);
            if (!result.TryGetValue(key, out var group))
            {
                group = new List<AlternativeCandidate>();
                result.Add(key, group);
            }

            group.Add(new AlternativeCandidate(fillContribution, safety));
        }

        return result;
    }

    private static List<PlanReplacementContext> BuildPlanReplacementContexts(
        SelectionPlan plan,
        IReadOnlyDictionary<string, CourseOffering> courseIndex,
        IReadOnlyDictionary<string, MeetingProfile> meetingIndex)
    {
        var result = new List<PlanReplacementContext>(plan.Snapshots.Count);
        var seenSnapshots = new HashSet<PlanCourseSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var snapshot in plan.Snapshots)
        {
            // The legacy rule excludes snapshots by ReferenceEquals. If a malformed
            // in-memory list repeats the same object, every occurrence is excluded
            // together, so it is safe and cheaper to collapse only exact references.
            if (!seenSnapshots.Add(snapshot))
                continue;

            MeetingProfile? meetings = null;
            if (courseIndex.TryGetValue(snapshot.CourseOfferingId, out var course))
                meetings = meetingIndex[course.OfferingId];
            result.Add(new PlanReplacementContext(snapshot, meetings));
        }

        return result;
    }

    private static ReplacementSafety AnalyzeReplacementSafety(
        MeetingProfile candidate,
        IReadOnlyList<PlanReplacementContext> planContexts)
    {
        PlanCourseSnapshot? onlyUnsafeSnapshot = null;
        foreach (var context in planContexts)
        {
            if (context.Meetings is { IsUsable: true } meetings &&
                !MayConflict(candidate, meetings))
            {
                continue;
            }

            if (onlyUnsafeSnapshot is null)
            {
                onlyUnsafeSnapshot = context.Snapshot;
                continue;
            }

            if (!ReferenceEquals(onlyUnsafeSnapshot, context.Snapshot))
                return ReplacementSafety.MultipleUnsafeSnapshots;
        }

        return new ReplacementSafety(onlyUnsafeSnapshot, false);
    }

    // Analyze has no Semester argument, so overlapping weekday/period ranges are
    // conservatively treated as conflicts even when their week expressions may differ.
    private static bool MayConflict(MeetingProfile left, MeetingProfile right)
    {
        for (var weekday = 0; weekday < 7; weekday++)
        {
            var leftIntervals = left.IntervalsByWeekday[weekday];
            var rightIntervals = right.IntervalsByWeekday[weekday];
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < leftIntervals.Length && rightIndex < rightIntervals.Length)
            {
                var leftInterval = leftIntervals[leftIndex];
                var rightInterval = rightIntervals[rightIndex];
                if (leftInterval.StartPeriod <= rightInterval.EndPeriod &&
                    rightInterval.StartPeriod <= leftInterval.EndPeriod)
                {
                    return true;
                }

                if (leftInterval.EndPeriod < rightInterval.StartPeriod)
                    leftIndex++;
                else
                    rightIndex++;
            }
        }

        return false;
    }

    private static int StateRank(RegistrationPriorityState state) => state switch
    {
        RegistrationPriorityState.Actionable => 0,
        RegistrationPriorityState.Unknown => 1,
        RegistrationPriorityState.Unavailable => 2,
        RegistrationPriorityState.MissingReference => 3,
        _ => int.MaxValue
    };

    private static List<SnapshotEntry> ResolveOrder(
        SelectionPlan plan,
        IReadOnlyList<string>? currentSnapshotOrder)
    {
        var persistedOrder = plan.Snapshots
            .Select((snapshot, originalIndex) => new SnapshotEntry(snapshot, originalIndex))
            .OrderBy(entry => entry.Snapshot.RegistrationOrder is >= 0 ? 0 : 1)
            .ThenBy(entry => entry.Snapshot.RegistrationOrder ?? int.MaxValue)
            .ThenBy(entry => entry.OriginalIndex)
            .ToList();

        if (currentSnapshotOrder is null)
            return persistedOrder;

        var positionsBySnapshotId = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        Queue<int>? nullIdPositions = null;
        for (var index = 0; index < persistedOrder.Count; index++)
        {
            var snapshotId = persistedOrder[index].Snapshot.SnapshotId;
            if (snapshotId is null)
            {
                nullIdPositions ??= new Queue<int>();
                nullIdPositions.Enqueue(index);
                continue;
            }

            if (!positionsBySnapshotId.TryGetValue(snapshotId, out var positions))
            {
                positions = new Queue<int>();
                positionsBySnapshotId.Add(snapshotId, positions);
            }
            positions.Enqueue(index);
        }

        var selected = new bool[persistedOrder.Count];
        var resolved = new List<SnapshotEntry>(persistedOrder.Count);
        foreach (var snapshotId in currentSnapshotOrder)
        {
            var positions = snapshotId is null
                ? nullIdPositions
                : positionsBySnapshotId.GetValueOrDefault(snapshotId);
            if (positions is null || positions.Count == 0)
                continue;

            var index = positions.Dequeue();
            selected[index] = true;
            resolved.Add(persistedOrder[index]);
        }

        for (var index = 0; index < persistedOrder.Count; index++)
        {
            if (!selected[index])
                resolved.Add(persistedOrder[index]);
        }
        return resolved;
    }

    private readonly record struct SnapshotEntry(PlanCourseSnapshot Snapshot, int OriginalIndex);

    private readonly record struct CountAnalysis(
        RegistrationPriorityState State,
        int? RemainingSeats,
        double? PressureUpperBound,
        double? LogRisk,
        bool IsDataInconsistent);

    private readonly record struct AlternativeCandidate(
        double FillContribution,
        ReplacementSafety Safety);

    private readonly record struct PlanReplacementContext(
        PlanCourseSnapshot Snapshot,
        MeetingProfile? Meetings);

    private readonly record struct ReplacementSafety(
        PlanCourseSnapshot? OnlyUnsafeSnapshot,
        bool HasMultipleUnsafeSnapshots)
    {
        public static ReplacementSafety MultipleUnsafeSnapshots { get; } = new(null, true);

        public bool CanReplace(PlanCourseSnapshot targetSnapshot) =>
            !HasMultipleUnsafeSnapshots &&
            (OnlyUnsafeSnapshot is null || ReferenceEquals(OnlyUnsafeSnapshot, targetSnapshot));
    }

    private sealed class MeetingProfile
    {
        private MeetingProfile(bool isUsable, MeetingInterval[][] intervalsByWeekday)
        {
            IsUsable = isUsable;
            IntervalsByWeekday = intervalsByWeekday;
        }

        public bool IsUsable { get; }
        public MeetingInterval[][] IntervalsByWeekday { get; }

        public static MeetingProfile For(CourseOffering course)
        {
            if (course.MeetingTimes.Count == 0 ||
                course.MeetingTimes.Any(meeting =>
                    meeting.Weekday is < 1 or > 7 ||
                    meeting.StartPeriod < 1 ||
                    meeting.EndPeriod < meeting.StartPeriod))
            {
                return new MeetingProfile(false, EmptyIntervals());
            }

            var intervals = Enumerable.Range(0, 7)
                .Select(_ => new List<MeetingInterval>())
                .ToArray();
            foreach (var meeting in course.MeetingTimes)
            {
                intervals[meeting.Weekday - 1].Add(new MeetingInterval(
                    meeting.StartPeriod,
                    meeting.EndPeriod));
            }

            return new MeetingProfile(
                true,
                intervals
                    .Select(day => day
                        .OrderBy(interval => interval.StartPeriod)
                        .ThenBy(interval => interval.EndPeriod)
                        .ToArray())
                    .ToArray());
        }

        private static MeetingInterval[][] EmptyIntervals() =>
            Enumerable.Range(0, 7)
                .Select(_ => Array.Empty<MeetingInterval>())
                .ToArray();
    }

    private readonly record struct MeetingInterval(int StartPeriod, int EndPeriod);

    private readonly record struct ParallelOfferingKey(
        string? SemesterId,
        string CourseName,
        decimal Credits,
        CourseTypeSemantic CourseType,
        string CourseTypeLabel,
        StudyTypeSemantic StudyType,
        string StudyTypeLabel)
    {
        public static ParallelOfferingKey For(CourseOffering course, CourseSemantics semantics) => new(
            course.SemesterId,
            TextRules.NormalizeIdentityText(course.CourseName),
            course.Credits,
            semantics.CourseType,
            semantics.CourseType == CourseTypeSemantic.Unknown
                ? TextRules.NormalizeIdentityText(course.CourseGroupType)
                : "",
            semantics.StudyType,
            semantics.StudyType == StudyTypeSemantic.Unknown
                ? TextRules.NormalizeIdentityText(course.StudyType)
                : "");
    }

    private sealed class ParallelOfferingKeyComparer : IEqualityComparer<ParallelOfferingKey>
    {
        public static ParallelOfferingKeyComparer Instance { get; } = new();

        public bool Equals(ParallelOfferingKey left, ParallelOfferingKey right) =>
            string.Equals(left.SemesterId, right.SemesterId, StringComparison.Ordinal) &&
            string.Equals(left.CourseName, right.CourseName, StringComparison.OrdinalIgnoreCase) &&
            left.Credits == right.Credits &&
            left.CourseType == right.CourseType &&
            string.Equals(left.CourseTypeLabel, right.CourseTypeLabel, StringComparison.OrdinalIgnoreCase) &&
            left.StudyType == right.StudyType &&
            string.Equals(left.StudyTypeLabel, right.StudyTypeLabel, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ParallelOfferingKey value)
        {
            var hash = new HashCode();
            hash.Add(value.SemesterId, StringComparer.Ordinal);
            hash.Add(value.CourseName, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.Credits);
            hash.Add(value.CourseType);
            hash.Add(value.CourseTypeLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.StudyType);
            hash.Add(value.StudyTypeLabel, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    private readonly record struct CourseSemantics(
        CourseTypeSemantic CourseType,
        StudyTypeSemantic StudyType,
        double CourseTypeWeight,
        double StudyTypeWeight)
    {
        public static CourseSemantics For(CourseOffering course)
        {
            var courseType = CourseLabelSemantics.ClassifyCourseType(course.CourseGroupType);
            var studyType = CourseLabelSemantics.ClassifyStudyType(course.StudyType);
            return new CourseSemantics(
                courseType,
                studyType,
                CourseLabelSemantics.Weight(courseType),
                CourseLabelSemantics.Weight(studyType));
        }
    }
}

using System.Runtime.CompilerServices;

namespace CoursePlanner.Core;

public static class TimetableRenderModelService
{
    public static IReadOnlyDictionary<int, IReadOnlyList<TimetableCourseBlock>> BuildCourseBlocksByWeek(
        IEnumerable<CourseOffering> courses,
        Semester semester,
        IEnumerable<int> weeks,
        IReadOnlyList<SlotDifference>? differences = null,
        bool includeConflictLayout = true)
    {
        ArgumentNullException.ThrowIfNull(courses);
        ArgumentNullException.ThrowIfNull(semester);
        ArgumentNullException.ThrowIfNull(weeks);

        var requestedWeeks = weeks.Distinct().ToList();
        var requestedWeekSet = requestedWeeks.ToHashSet();
        var pendingByWeek = requestedWeeks.ToDictionary(week => week, _ => new List<PendingBlock>());
        if (requestedWeeks.Count == 0)
            return new Dictionary<int, IReadOnlyList<TimetableCourseBlock>>();

        var courseList = DistinctCourses(courses);
        var maximumPeriod = semester.PeriodSchedule.Count;
        for (var courseIndex = 0; courseIndex < courseList.Count; courseIndex++)
        {
            var course = courseList[courseIndex];
            var intervalsByOccurrence = new Dictionary<(int Week, int Weekday), List<PeriodInterval>>();
            foreach (var meeting in course.MeetingTimes)
            {
                if (meeting.Weekday is < 1 or > 7)
                    continue;

                var start = Math.Max(1, meeting.StartPeriod);
                var end = Math.Min(maximumPeriod, meeting.EndPeriod);
                if (start > end)
                    continue;

                foreach (var week in MeetingWeeksParser.Parse(
                             meeting.Weeks,
                             semester.WeekCount,
                             meeting.WeekParity))
                {
                    if (!requestedWeekSet.Contains(week))
                        continue;

                    var key = (week, meeting.Weekday);
                    if (!intervalsByOccurrence.TryGetValue(key, out var intervals))
                    {
                        intervals = new List<PeriodInterval>();
                        intervalsByOccurrence[key] = intervals;
                    }

                    intervals.Add(new PeriodInterval(start, end));
                }
            }

            foreach (var (occurrence, intervals) in intervalsByOccurrence)
            {
                PendingBlock? current = null;
                foreach (var interval in intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End))
                {
                    if (current is not null && interval.Start <= current.EndPeriod)
                    {
                        current.EndPeriod = Math.Max(current.EndPeriod, interval.End);
                        continue;
                    }

                    current = new PendingBlock(
                        course,
                        courseIndex,
                        occurrence.Week,
                        occurrence.Weekday,
                        interval.Start,
                        interval.End);
                    pendingByWeek[occurrence.Week].Add(current);
                }
            }
        }

        var differenceBySlot = DifferenceSlotIndex.Build(differences);
        var result = new Dictionary<int, IReadOnlyList<TimetableCourseBlock>>(requestedWeeks.Count);
        foreach (var week in requestedWeeks)
        {
            var pending = pendingByWeek[week];
            if (includeConflictLayout)
                AssignConflictLanes(pending);
            if (differenceBySlot.Count > 0)
                pending = SplitBlocksAtDifferenceBoundaries(pending, differenceBySlot);

            result[week] = pending
                .OrderBy(block => block.CourseIndex)
                .ThenBy(block => block.Weekday)
                .ThenBy(block => block.StartPeriod)
                .Select(ToCourseBlock)
                .ToList();
        }

        return result;
    }

    public static IReadOnlyDictionary<TimetableSlot, CourseOffering> BuildOverviewCourseBySlot(
        IEnumerable<CourseOffering> courses,
        Semester semester,
        int maximumPeriod)
    {
        ArgumentNullException.ThrowIfNull(courses);
        ArgumentNullException.ThrowIfNull(semester);
        if (maximumPeriod < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPeriod));

        var visibleMaximumPeriod = Math.Min(maximumPeriod, semester.PeriodSchedule.Count);
        var result = new Dictionary<TimetableSlot, CourseOffering>();
        if (visibleMaximumPeriod < 1)
            return result;

        foreach (var course in DistinctCourses(courses))
        {
            foreach (var meeting in course.MeetingTimes)
            {
                if (meeting.Weekday is < 1 or > 7)
                    continue;

                var start = Math.Max(1, meeting.StartPeriod);
                var end = Math.Min(visibleMaximumPeriod, meeting.EndPeriod);
                if (start > end)
                    continue;

                foreach (var week in MeetingWeeksParser.Parse(
                             meeting.Weeks,
                             semester.WeekCount,
                             meeting.WeekParity))
                {
                    for (var period = start; period <= end; period++)
                    {
                        result.TryAdd(
                            new TimetableSlot
                            {
                                Week = week,
                                Weekday = meeting.Weekday,
                                Period = period
                            },
                            course);
                    }
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<TimetableCourseBlock> BuildWeekCourseBlocks(
        IEnumerable<CourseOffering> courses,
        Semester semester,
        int week,
        IReadOnlyList<SlotDifference>? differences = null,
        bool includeConflictLayout = true)
    {
        var blocks = BuildCourseBlocksByWeek(
            courses,
            semester,
            new[] { week },
            differences,
            includeConflictLayout);
        return blocks[week];
    }

    public static IReadOnlyList<CourseOffering> CoursesForExport(
        SelectionPlan plan,
        IEnumerable<CourseOffering> libraryCourses,
        IReadOnlyList<SlotDifference>? differences)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(libraryCourses);
        var comparisonCourses = differences?
            .SelectMany(difference => difference.BaseCourses.Concat(difference.CurrentCourses))
            ?? Enumerable.Empty<CourseOffering>();
        return DistinctCourses(PlanCourseResolver.Courses(plan, libraryCourses).Concat(comparisonCourses));
    }

    private static TimetableCourseBlock ToCourseBlock(PendingBlock block) => new()
    {
        Course = block.Course,
        Slot = new TimetableSlot
        {
            Week = block.Week,
            Weekday = block.Weekday,
            Period = block.StartPeriod
        },
        StartPeriod = block.StartPeriod,
        EndPeriod = block.EndPeriod,
        ConflictCount = block.ConflictCount,
        ConflictIndex = block.ConflictIndex,
        Difference = block.Difference
    };

    private static List<PendingBlock> SplitBlocksAtDifferenceBoundaries(
        IEnumerable<PendingBlock> blocks,
        IReadOnlyDictionary<TimetableSlot, DifferenceSlotInfo> differenceBySlot)
    {
        var result = new List<PendingBlock>();
        foreach (var block in blocks)
        {
            PendingBlock? segment = null;
            var previous = ResolvedDifference.None;
            for (var period = block.StartPeriod; period <= block.EndPeriod; period++)
            {
                var slot = new TimetableSlot
                {
                    Week = block.Week,
                    Weekday = block.Weekday,
                    Period = period
                };
                var resolved = differenceBySlot.TryGetValue(slot, out var slotDifference)
                    ? slotDifference.Resolve(block.Course)
                    : ResolvedDifference.None;
                if (segment is not null && previous.IsEquivalentTo(resolved))
                {
                    segment.EndPeriod = period;
                    continue;
                }

                segment = new PendingBlock(
                    block.Course,
                    block.CourseIndex,
                    block.Week,
                    block.Weekday,
                    period,
                    period)
                {
                    Difference = resolved.Value,
                    ConflictIndex = block.ConflictIndex,
                    ConflictCount = block.ConflictCount
                };
                result.Add(segment);
                previous = resolved;
            }
        }

        return result;
    }

    private static List<CourseOffering> DistinctCourses(IEnumerable<CourseOffering> courses)
    {
        var result = new List<CourseOffering>();
        var offeringIds = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<CourseOffering>(ReferenceEqualityComparer.Instance);
        foreach (var course in courses)
        {
            var added = string.IsNullOrWhiteSpace(course.OfferingId)
                ? references.Add(course)
                : offeringIds.Add(course.OfferingId);
            if (added)
                result.Add(course);
        }

        return result;
    }

    private static void AssignConflictLanes(IEnumerable<PendingBlock> blocks)
    {
        foreach (var occurrenceBlocks in blocks.GroupBy(block => (block.Week, block.Weekday)))
        {
            var ordered = occurrenceBlocks
                .OrderBy(block => block.StartPeriod)
                .ThenBy(block => block.EndPeriod)
                .ThenBy(block => block.CourseIndex)
                .ToList();
            var component = new List<PendingBlock>();
            var componentEnd = 0;
            foreach (var block in ordered)
            {
                if (component.Count > 0 && block.StartPeriod > componentEnd)
                {
                    AssignComponentLanes(component);
                    component.Clear();
                }

                component.Add(block);
                componentEnd = component.Count == 1
                    ? block.EndPeriod
                    : Math.Max(componentEnd, block.EndPeriod);
            }

            AssignComponentLanes(component);
        }
    }

    private static void AssignComponentLanes(IReadOnlyList<PendingBlock> component)
    {
        if (component.Count == 0)
            return;

        var active = new PriorityQueue<int, int>();
        var available = new PriorityQueue<int, int>();
        var laneCount = 0;
        foreach (var block in component)
        {
            while (active.TryPeek(out var completedLane, out var endPeriod) && endPeriod < block.StartPeriod)
            {
                active.Dequeue();
                available.Enqueue(completedLane, completedLane);
            }

            int lane;
            if (available.Count > 0)
            {
                lane = available.Dequeue();
            }
            else
            {
                lane = laneCount;
                laneCount++;
            }

            block.ConflictIndex = lane;
            active.Enqueue(lane, block.EndPeriod);
        }

        foreach (var block in component)
            block.ConflictCount = laneCount;
    }

    private readonly record struct PeriodInterval(int Start, int End);

    private readonly record struct ResolvedDifference(
        SlotDifference? Value,
        int MembershipId,
        DifferenceKind? Kind)
    {
        public static ResolvedDifference None { get; } = new(null, 0, null);

        public bool IsEquivalentTo(ResolvedDifference other) =>
            MembershipId == other.MembershipId && Kind == other.Kind;
    }

    private sealed class DifferenceSlotInfo
    {
        private readonly SlotDifference _source;
        private readonly HashSet<string> _baseIds;
        private readonly HashSet<string> _currentIds;
        private readonly HashSet<CourseOffering> _baseReferences;
        private readonly HashSet<CourseOffering> _currentReferences;
        private readonly bool _isReplacement;
        private readonly Dictionary<DifferenceKind, SlotDifference> _variants = new();

        public DifferenceSlotInfo(SlotDifference source)
        {
            _source = source;
            _baseIds = Ids(source.BaseCourses);
            _currentIds = Ids(source.CurrentCourses);
            _baseReferences = References(source.BaseCourses);
            _currentReferences = References(source.CurrentCourses);
            _isReplacement = _baseIds.Except(_currentIds).Any() &&
                             _currentIds.Except(_baseIds).Any();
        }

        public int MembershipId { get; set; }
        public IReadOnlySet<string> BaseIds => _baseIds;
        public IReadOnlySet<string> CurrentIds => _currentIds;
        public IReadOnlySet<CourseOffering> BaseReferences => _baseReferences;
        public IReadOnlySet<CourseOffering> CurrentReferences => _currentReferences;

        public ResolvedDifference Resolve(CourseOffering course)
        {
            if (_baseIds.Count == 0 && _currentIds.Count == 0 &&
                _baseReferences.Count == 0 && _currentReferences.Count == 0)
            {
                return new ResolvedDifference(_source, MembershipId, _source.Kind);
            }

            var isInBase = Contains(course, _baseIds, _baseReferences);
            var isInCurrent = Contains(course, _currentIds, _currentReferences);
            DifferenceKind? kind = (isInBase, isInCurrent) switch
            {
                (true, true) => DifferenceKind.Unchanged,
                (true, false) => _isReplacement ? DifferenceKind.Replaced : DifferenceKind.Removed,
                (false, true) => _isReplacement ? DifferenceKind.Replaced : DifferenceKind.Added,
                _ => null
            };
            if (kind is null)
                return ResolvedDifference.None;

            return new ResolvedDifference(ForKind(kind.Value), MembershipId, kind);
        }

        private SlotDifference ForKind(DifferenceKind kind)
        {
            if (kind == _source.Kind)
                return _source;
            if (_variants.TryGetValue(kind, out var existing))
                return existing;

            var variant = _source.WithKind(kind);
            _variants[kind] = variant;
            return variant;
        }

        private static bool Contains(
            CourseOffering course,
            IReadOnlySet<string> ids,
            IReadOnlySet<CourseOffering> references) =>
            string.IsNullOrWhiteSpace(course.OfferingId)
                ? references.Contains(course)
                : ids.Contains(course.OfferingId);

        private static HashSet<string> Ids(IEnumerable<CourseOffering> courses) =>
            courses
                .Select(course => course.OfferingId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

        private static HashSet<CourseOffering> References(IEnumerable<CourseOffering> courses) =>
            courses
                .Where(course => string.IsNullOrWhiteSpace(course.OfferingId))
                .ToHashSet<CourseOffering>(ReferenceEqualityComparer.Instance);
    }

    private static class DifferenceSlotIndex
    {
        public static IReadOnlyDictionary<TimetableSlot, DifferenceSlotInfo> Build(
            IReadOnlyList<SlotDifference>? differences)
        {
            if (differences is null || differences.Count == 0)
                return new Dictionary<TimetableSlot, DifferenceSlotInfo>();

            var result = new Dictionary<TimetableSlot, DifferenceSlotInfo>();
            var interner = new DifferenceMembershipInterner();
            foreach (var difference in differences)
            {
                if (difference?.Slot is null || result.ContainsKey(difference.Slot))
                    continue;

                var info = new DifferenceSlotInfo(difference);
                info.MembershipId = interner.Intern(info);
                result.Add(difference.Slot, info);
            }

            return result;
        }
    }

    private sealed class DifferenceMembershipInterner
    {
        private readonly Dictionary<MembershipFingerprint, List<DifferenceSlotInfo>> _buckets = new();
        private int _nextId = 1;

        public int Intern(DifferenceSlotInfo info)
        {
            var fingerprint = MembershipFingerprint.Create(info);
            if (_buckets.TryGetValue(fingerprint, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.BaseIds.SetEquals(info.BaseIds) &&
                        candidate.CurrentIds.SetEquals(info.CurrentIds) &&
                        candidate.BaseReferences.SetEquals(info.BaseReferences) &&
                        candidate.CurrentReferences.SetEquals(info.CurrentReferences))
                    {
                        return candidate.MembershipId;
                    }
                }
            }
            else
            {
                candidates = new List<DifferenceSlotInfo>();
                _buckets[fingerprint] = candidates;
            }

            var id = _nextId++;
            info.MembershipId = id;
            candidates.Add(info);
            return id;
        }
    }

    private readonly record struct MembershipFingerprint(
        int BaseIdCount,
        int CurrentIdCount,
        int BaseReferenceCount,
        int CurrentReferenceCount,
        ulong BaseSum,
        ulong BaseXor,
        ulong CurrentSum,
        ulong CurrentXor)
    {
        public static MembershipFingerprint Create(DifferenceSlotInfo info)
        {
            var (baseSum, baseXor) = Hash(info.BaseIds, info.BaseReferences);
            var (currentSum, currentXor) = Hash(info.CurrentIds, info.CurrentReferences);
            return new MembershipFingerprint(
                info.BaseIds.Count,
                info.CurrentIds.Count,
                info.BaseReferences.Count,
                info.CurrentReferences.Count,
                baseSum,
                baseXor,
                currentSum,
                currentXor);
        }

        private static (ulong Sum, ulong Xor) Hash(
            IEnumerable<string> ids,
            IEnumerable<CourseOffering> references)
        {
            ulong sum = 0;
            ulong xor = 0;
            foreach (var id in ids)
            {
                var hash = StableStringHash(id);
                sum = unchecked(sum + hash);
                xor ^= RotateLeft(hash, (int)(hash & 63));
            }

            foreach (var reference in references)
            {
                var hash = unchecked((ulong)(uint)RuntimeHelpers.GetHashCode(reference));
                sum = unchecked(sum + hash);
                xor ^= RotateLeft(hash, (int)(hash & 63));
            }

            return (sum, xor);
        }

        private static ulong StableStringHash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var character in value)
            {
                hash ^= character;
                hash = unchecked(hash * prime);
            }

            return hash;
        }

        private static ulong RotateLeft(ulong value, int offset) =>
            offset == 0 ? value : (value << offset) | (value >> (64 - offset));
    }

    private sealed class PendingBlock(
        CourseOffering course,
        int courseIndex,
        int week,
        int weekday,
        int startPeriod,
        int endPeriod)
    {
        public CourseOffering Course { get; } = course;
        public int CourseIndex { get; } = courseIndex;
        public int Week { get; } = week;
        public int Weekday { get; } = weekday;
        public int StartPeriod { get; } = startPeriod;
        public int EndPeriod { get; set; } = endPeriod;
        public int ConflictIndex { get; set; }
        public int ConflictCount { get; set; } = 1;
        public SlotDifference? Difference { get; init; }
    }
}

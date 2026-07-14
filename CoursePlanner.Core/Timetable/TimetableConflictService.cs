namespace CoursePlanner.Core;

public static class TimetableConflictService
{
    public static int CountConflictSlots(IEnumerable<CourseOffering> courses, Semester semester)
    {
        ArgumentNullException.ThrowIfNull(courses);
        ArgumentNullException.ThrowIfNull(semester);
        var periodCount = semester.PeriodSchedule.Count;
        var weekCount = Math.Clamp(semester.WeekCount, 0, SemesterRules.MaxWeekCount);
        if (periodCount <= 0 || weekCount <= 0)
            return 0;

        var stride = periodCount + 1;
        var occupancyDeltas = new int[checked(weekCount * 7 * stride)];
        foreach (var course in DistinctCourses(courses))
        {
            var occurrences = MeetingOccurrenceIndex.Build(course, semester, periodCount);
            foreach (var (occurrence, intervals) in occurrences)
            {
                var offset = checked((((occurrence.Week - 1) * 7) + (occurrence.Weekday - 1)) * stride);
                foreach (var interval in intervals)
                {
                    occupancyDeltas[offset + interval.Start - 1]++;
                    occupancyDeltas[offset + interval.End]--;
                }
            }
        }

        var conflicts = 0;
        for (var week = 0; week < weekCount; week++)
        {
            for (var weekday = 0; weekday < 7; weekday++)
            {
                var offset = ((week * 7) + weekday) * stride;
                var occupancy = 0;
                for (var period = 0; period < periodCount; period++)
                {
                    occupancy += occupancyDeltas[offset + period];
                    if (occupancy > 1)
                        conflicts++;
                }
            }
        }

        return conflicts;
    }

    public static IReadOnlyList<CourseOffering> FindConflictingCourses(
        CourseOffering source,
        IEnumerable<CourseOffering> candidates,
        Semester semester)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(semester);
        var sourceOccurrences = MeetingOccurrenceIndex.Build(
            source,
            semester,
            semester.PeriodSchedule.Count);
        if (sourceOccurrences.Count == 0)
            return Array.Empty<CourseOffering>();

        var result = new List<CourseOffering>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.OfferingId, source.OfferingId, StringComparison.Ordinal) ||
                !seenIds.Add(candidate.OfferingId))
            {
                continue;
            }

            if (Overlaps(sourceOccurrences, candidate, semester))
                result.Add(candidate);
        }

        return result;
    }

    private static bool Overlaps(
        IReadOnlyDictionary<MeetingOccurrence, IReadOnlyList<PeriodInterval>> source,
        CourseOffering candidate,
        Semester semester)
    {
        foreach (var meeting in candidate.MeetingTimes)
        {
            if (meeting.Weekday is < 1 or > 7)
                continue;
            var start = Math.Max(1, meeting.StartPeriod);
            var end = Math.Min(semester.PeriodSchedule.Count, meeting.EndPeriod);
            if (start > end)
                continue;

            foreach (var week in MeetingWeeksParser.Parse(
                         meeting.Weeks,
                         semester.WeekCount,
                         meeting.WeekParity))
            {
                if (!source.TryGetValue(new MeetingOccurrence(week, meeting.Weekday), out var intervals))
                    continue;
                foreach (var interval in intervals)
                {
                    if (interval.Start > end)
                        break;
                    if (interval.End >= start)
                        return true;
                }
            }
        }

        return false;
    }

    private static List<CourseOffering> DistinctCourses(IEnumerable<CourseOffering> courses)
    {
        var result = new List<CourseOffering>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<CourseOffering>(ReferenceEqualityComparer.Instance);
        foreach (var course in courses)
        {
            var added = string.IsNullOrWhiteSpace(course.OfferingId)
                ? references.Add(course)
                : ids.Add(course.OfferingId);
            if (added)
                result.Add(course);
        }

        return result;
    }

    private readonly record struct MeetingOccurrence(int Week, int Weekday);
    private readonly record struct PeriodInterval(int Start, int End);

    private static class MeetingOccurrenceIndex
    {
        public static IReadOnlyDictionary<MeetingOccurrence, IReadOnlyList<PeriodInterval>> Build(
            CourseOffering course,
            Semester semester,
            int maximumPeriod)
        {
            var pending = new Dictionary<MeetingOccurrence, List<PeriodInterval>>();
            if (maximumPeriod <= 0)
                return new Dictionary<MeetingOccurrence, IReadOnlyList<PeriodInterval>>();

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
                    var occurrence = new MeetingOccurrence(week, meeting.Weekday);
                    if (!pending.TryGetValue(occurrence, out var intervals))
                    {
                        intervals = new List<PeriodInterval>();
                        pending[occurrence] = intervals;
                    }
                    intervals.Add(new PeriodInterval(start, end));
                }
            }

            var result = new Dictionary<MeetingOccurrence, IReadOnlyList<PeriodInterval>>(pending.Count);
            foreach (var (occurrence, intervals) in pending)
            {
                var merged = new List<PeriodInterval>();
                foreach (var interval in intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End))
                {
                    if (merged.Count == 0 || interval.Start > merged[^1].End)
                    {
                        merged.Add(interval);
                        continue;
                    }

                    merged[^1] = new PeriodInterval(
                        merged[^1].Start,
                        Math.Max(merged[^1].End, interval.End));
                }
                result[occurrence] = merged;
            }

            return result;
        }
    }
}

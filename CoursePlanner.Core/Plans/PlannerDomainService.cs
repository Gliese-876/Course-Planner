using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public sealed class AddCourseResult
{
    public bool Added { get; set; }
    public bool ReplacedDuplicate { get; set; }
    public bool Cancelled { get; set; }
    public List<CourseOffering> ConflictingCourses { get; set; } = new();
    public ValidationResult Validation { get; } = new();
}

public static class PlannerDomainService
{
    public static Dictionary<TimetableSlot, List<CourseOffering>> ExpandSlots(
        IEnumerable<CourseOffering> courses,
        Semester semester,
        int? week = null)
    {
        var map = new Dictionary<TimetableSlot, List<CourseOffering>>();
        foreach (var course in courses)
        {
            foreach (var slot in ExpandSlots(course, semester, week))
            {
                if (!map.TryGetValue(slot, out var list))
                {
                    list = new List<CourseOffering>();
                    map[slot] = list;
                }
                list.Add(course);
            }
        }

        return map;
    }

    public static IEnumerable<TimetableSlot> ExpandSlots(CourseOffering course, Semester semester, int? week = null)
    {
        var maxPeriod = semester.PeriodSchedule.Count;
        foreach (var meeting in course.MeetingTimes)
        {
            if (meeting.Weekday is < 1 or > 7)
                continue;

            var weeks = MeetingWeeksParser.Parse(meeting.Weeks, semester.WeekCount, meeting.WeekParity);
            foreach (var currentWeek in weeks)
            {
                if (week.HasValue && currentWeek != week.Value)
                    continue;

                for (var period = Math.Max(1, meeting.StartPeriod); period <= Math.Min(maxPeriod, meeting.EndPeriod); period++)
                    yield return new TimetableSlot { Week = currentWeek, Weekday = meeting.Weekday, Period = period };
            }
        }
    }

    public static AddCourseResult AddCourseToPlan(
        SelectionPlan plan,
        CourseOffering source,
        Semester semester,
        DuplicateResolution duplicateResolution,
        ConflictResolution conflictResolution,
        IEnumerable<CourseOffering> libraryCourses)
    {
        ArgumentNullException.ThrowIfNull(libraryCourses);
        return AddCourseToPlan(
            plan,
            source,
            semester,
            duplicateResolution,
            conflictResolution,
            PlanCourseResolver.BuildCourseIndex(libraryCourses));
    }

    public static AddCourseResult AddCourseToPlan(
        SelectionPlan plan,
        CourseOffering source,
        Semester semester,
        DuplicateResolution duplicateResolution,
        ConflictResolution conflictResolution,
        IReadOnlyDictionary<string, CourseOffering> courseIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(semester);
        ArgumentNullException.ThrowIfNull(courseIndex);
        var result = new AddCourseResult();
        var existing = plan.Snapshots.FirstOrDefault(x => string.Equals(x.CourseOfferingId, source.OfferingId, StringComparison.Ordinal));
        var preservedRegistrationOrder = existing?.RegistrationOrder;
        if (existing is not null)
        {
            if (duplicateResolution == DuplicateResolution.SkipExisting)
                return result;
        }

        var conflicts = FindConflicts(plan, source, semester, courseIndex).ToList();
        result.ConflictingCourses = conflicts;
        if (conflicts.Count > 0)
        {
            if (conflictResolution == ConflictResolution.Cancel)
            {
                result.Cancelled = true;
                return result;
            }

        }

        var removedConflictIds = conflictResolution == ConflictResolution.RemoveConflictingThenAdd
            ? conflicts.Select(course => course.OfferingId).ToHashSet(StringComparer.Ordinal)
            : [];
        var removedConflictSnapshots = plan.Snapshots.Count(snapshot =>
            removedConflictIds.Contains(snapshot.CourseOfferingId));
        var snapshotDelta = 1 - removedConflictSnapshots - (existing is null ? 0 : 1);
        var capacity = PlannerCapacityRules.ValidateSnapshotChange(
            plan.Snapshots.Count,
            plan.Snapshots.Count,
            snapshotDelta);
        CopyValidation(capacity, result.Validation);
        if (!capacity.IsValid)
        {
            result.Cancelled = true;
            return result;
        }

        var projectedPlan = new SelectionPlan
        {
            Snapshots = plan.Snapshots.ToList()
        };
        if (removedConflictIds.Count > 0)
            projectedPlan.Snapshots.RemoveAll(snapshot => removedConflictIds.Contains(snapshot.CourseOfferingId));
        if (existing is not null)
            projectedPlan.Snapshots.Remove(existing);
        projectedPlan.Snapshots.Add(new PlanCourseSnapshot { CourseOfferingId = source.OfferingId });
        var meetingRows = PlanRules.ValidateMeetingRows(projectedPlan, courseIndex, source);
        CopyValidation(meetingRows, result.Validation);
        if (!meetingRows.IsValid)
        {
            result.Cancelled = true;
            return result;
        }

        if (removedConflictIds.Count > 0)
            plan.Snapshots.RemoveAll(snapshot => removedConflictIds.Contains(snapshot.CourseOfferingId));

        if (existing is not null)
        {
            plan.Snapshots.Remove(existing);
            result.ReplacedDuplicate = true;
        }

        plan.Snapshots.Add(new PlanCourseSnapshot
        {
            CourseOfferingId = source.OfferingId,
            RegistrationOrder = preservedRegistrationOrder,
            SnapshotAt = DateTimeOffset.UtcNow
        });
        RegistrationPriorityService.NormalizeOrders(plan);
        plan.ModifiedAt = DateTimeOffset.UtcNow;
        result.Added = true;
        return result;
    }

    private static void CopyValidation(ValidationResult source, ValidationResult target)
    {
        foreach (var issue in source.Errors)
            target.Error(issue.Code, issue.Parameters.ToArray());
        foreach (var issue in source.Warnings)
            target.Warning(issue.Code, issue.Parameters.ToArray());
    }

    public static IEnumerable<CourseOffering> FindConflicts(
        SelectionPlan plan,
        CourseOffering source,
        Semester semester,
        IEnumerable<CourseOffering> libraryCourses)
    {
        return TimetableConflictService.FindConflictingCourses(
            source,
            PlanCourseResolver.Courses(plan, libraryCourses),
            semester);
    }

    public static IEnumerable<CourseOffering> FindConflicts(
        SelectionPlan plan,
        CourseOffering source,
        Semester semester,
        IReadOnlyDictionary<string, CourseOffering> courseIndex)
    {
        return TimetableConflictService.FindConflictingCourses(
            source,
            PlanCourseResolver.Courses(plan, courseIndex),
            semester);
    }

    public static List<SlotDifference> Compare(
        SelectionPlan basePlan,
        SelectionPlan currentPlan,
        Semester semester,
        int week,
        IEnumerable<CourseOffering> libraryCourses)
    {
        var baseSlots = ExpandSlots(PlanCourseResolver.Courses(basePlan, libraryCourses), semester, week);
        var currentSlots = ExpandSlots(PlanCourseResolver.Courses(currentPlan, libraryCourses), semester, week);
        var slots = baseSlots.Keys.Concat(currentSlots.Keys).Distinct().Order().ToList();
        var differences = new List<SlotDifference>();

        foreach (var slot in slots)
        {
            baseSlots.TryGetValue(slot, out var baseCourses);
            currentSlots.TryGetValue(slot, out var currentCourses);
            var orderedBaseCourses = OrderedDistinctCourses(baseCourses);
            var orderedCurrentCourses = OrderedDistinctCourses(currentCourses);
            var baseIds = orderedBaseCourses.Select(x => x.OfferingId).ToHashSet(StringComparer.Ordinal);
            var currentIds = orderedCurrentCourses.Select(x => x.OfferingId).ToHashSet(StringComparer.Ordinal);
            var removedCount = baseIds.Count(id => !currentIds.Contains(id));
            var addedCount = currentIds.Count(id => !baseIds.Contains(id));

            var kind = DifferenceKind.Unchanged;
            if (addedCount > 0 && removedCount == 0)
                kind = DifferenceKind.Added;
            else if (removedCount > 0 && addedCount == 0)
                kind = DifferenceKind.Removed;
            else if (removedCount > 0 && addedCount > 0)
                kind = DifferenceKind.Replaced;

            differences.Add(new SlotDifference
            {
                Slot = slot,
                Kind = kind,
                BaseCourses = orderedBaseCourses,
                CurrentCourses = orderedCurrentCourses
            });
        }

        return differences;
    }

    private static List<CourseOffering> OrderedDistinctCourses(IEnumerable<CourseOffering>? courses) =>
        courses?
            .Where(course => !string.IsNullOrWhiteSpace(course.OfferingId))
            .DistinctBy(course => course.OfferingId, StringComparer.Ordinal)
            .OrderBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToList()
        ?? new List<CourseOffering>();

    public static int ResolvePlanCourseReferences(PlannerDocument document)
    {
        return PlanCourseResolver.RemoveMissingReferences(document);
    }

    public static int UpdateCourseReferenceId(PlannerDocument document, string originalOfferingId, string currentOfferingId)
    {
        if (string.IsNullOrWhiteSpace(originalOfferingId) ||
            string.IsNullOrWhiteSpace(currentOfferingId) ||
            string.Equals(originalOfferingId, currentOfferingId, StringComparison.Ordinal))
        {
            return ResolvePlanCourseReferences(document);
        }

        var changed = 0;
        foreach (var snapshot in document.Plans.SelectMany(x => x.Snapshots))
        {
            if (!string.Equals(snapshot.CourseOfferingId, originalOfferingId, StringComparison.Ordinal))
                continue;
            snapshot.CourseOfferingId = currentOfferingId;
            snapshot.SnapshotAt = DateTimeOffset.UtcNow;
            changed++;
        }

        changed += ResolvePlanCourseReferences(document);
        return changed;
    }

    public static int RemoveCourseReferences(
        PlannerDocument document,
        IEnumerable<string> offeringIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(offeringIds);
        var removedIds = offeringIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (removedIds.Count == 0)
            return 0;

        var changed = 0;
        foreach (var plan in document.Plans)
        {
            var removed = plan.Snapshots.RemoveAll(snapshot => removedIds.Contains(snapshot.CourseOfferingId));
            if (removed == 0)
                continue;
            changed += removed;
            RegistrationPriorityService.NormalizeOrders(plan);
            plan.ModifiedAt = DateTimeOffset.UtcNow;
        }

        return changed;
    }

    public static CourseOffering CopyCourseToSemester(CourseOffering source, string semesterId, int colorIndex)
    {
        var copy = JsonDefaults.Clone(source);
        copy.SemesterId = semesterId;
        copy.Color = CourseColorService.EnsureValid(copy.Color, colorIndex);
        CourseIdentityService.AssignOfferingId(copy);
        copy.ModifiedAt = DateTimeOffset.UtcNow;
        return copy;
    }

    public static int ApplyPlanDisplayOrder(IList<SelectionPlan> plans, IReadOnlyList<string> orderedPlanIds)
    {
        var changed = 0;
        var order = orderedPlanIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);
        var nextOrder = orderedPlanIds.Count;

        foreach (var plan in plans
                     .OrderBy(x => order.TryGetValue(x.PlanId, out var rank) ? rank : int.MaxValue)
                     .ThenBy(x => x.DisplayOrder)
                     .ThenBy(x => x.PlanName, StringComparer.CurrentCultureIgnoreCase))
        {
            var targetOrder = order.TryGetValue(plan.PlanId, out var orderedRank) ? orderedRank : nextOrder++;
            if (plan.DisplayOrder == targetOrder)
                continue;

            plan.DisplayOrder = targetOrder;
            plan.ModifiedAt = DateTimeOffset.UtcNow;
            changed++;
        }

        return changed;
    }
}

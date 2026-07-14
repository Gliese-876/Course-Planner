using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CoursePlanner.Core;

public static class CourseValidator
{
    public static ValidationResult Validate(
        CourseOffering course,
        Semester semester,
        bool importMode = false,
        bool allowUnscheduled = false)
    {
        var result = new ValidationResult();
        var courseNameTooLong = course.CourseName?.Length > PlannerDataLimits.MaxTextFieldLength;
        var colorTooLong = course.Color?.Length > PlannerDataLimits.MaxTextFieldLength;
        if (!courseNameTooLong && string.IsNullOrWhiteSpace(course.CourseName))
            result.Error("CourseNameRequired");
        if (courseNameTooLong)
            result.Error("CourseNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.Teacher?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("TeacherTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.Location?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("LocationTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.CourseGroupType?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("CourseGroupTypeTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.StudyType?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("StudyTypeTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.Notes?.Length > PlannerDataLimits.MaxTextFieldLength)
            result.Error("CourseNotesTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (colorTooLong)
            result.Error("CourseColorTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        if (course.Labels.Count > PlannerDataLimits.MaxLabelsPerCourse)
            result.Error("CourseLabelsMaximum", PlannerDataLimits.MaxLabelsPerCourse.ToString());
        if (course.Labels.Take(PlannerDataLimits.MaxLabelsPerCourse).Any(label =>
                label?.Length > PlannerDataLimits.MaxTextFieldLength))
        {
            result.Error("CourseLabelTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
        }
        var normalizedLabels = course.Labels
            .Take(PlannerDataLimits.MaxLabelsPerCourse)
            .Select(TextRules.NormalizeIdentityText)
            .ToList();
        if (normalizedLabels.Any(string.IsNullOrWhiteSpace))
            result.Error("LabelNameRequired");
        if (normalizedLabels.Where(label => label.Length > 0)
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Skip(1).Any()))
        {
            result.Error("LabelNameDuplicate");
        }
        if (course.MeetingTimes.Count > PlannerDataLimits.MaxMeetingsPerCourse)
            result.Error("MeetingTimesMaximum", PlannerDataLimits.MaxMeetingsPerCourse.ToString());
        if (course.Credits < 0)
            result.Error("CreditsNonNegative");
        if (course.Credits > CourseNumericRules.MaximumCredits)
            result.Error("CreditsMaximum", CourseNumericRules.MaximumCredits.ToString(CultureInfo.CurrentCulture));
        if (course.EnrolledCount is < 0)
            result.Error("EnrolledNonNegative");
        if (course.EnrolledCount > CourseNumericRules.MaximumPeopleCount)
            result.Error("EnrolledMaximum", CourseNumericRules.MaximumPeopleCount.ToString(CultureInfo.CurrentCulture));
        if (course.Capacity is < 0)
            result.Error("CapacityNonNegative");
        if (course.Capacity > CourseNumericRules.MaximumPeopleCount)
            result.Error("CapacityMaximum", CourseNumericRules.MaximumPeopleCount.ToString(CultureInfo.CurrentCulture));
        if (course.Capacity is > 0 && course.EnrolledCount > course.Capacity)
            result.Warning("EnrolledExceedsCapacity");
        if (course.MeetingTimes.Count == 0 && !allowUnscheduled)
            result.Error("MeetingTimeRequired");
        if (!colorTooLong && !CourseColorService.IsValidHex(course.Color))
        {
            if (importMode)
                result.Warning("InvalidCourseColorRegenerated");
            else
                result.Error("InvalidCourseColor");
        }

        var anyVisibleSlot = false;
        var maxPeriod = semester.PeriodSchedule.Count;
        var intervalsByOccurrence = new Dictionary<(int Weekday, int Week), List<(int Start, int End)>>();
        foreach (var meeting in course.MeetingTimes.Take(PlannerDataLimits.MaxMeetingsPerCourse))
        {
            if (meeting.Weekday is < 1 or > 7)
                result.Error("InvalidWeekday");
            if (!Enum.IsDefined(meeting.WeekParity))
                result.Error("InvalidWeekParity");
            if (meeting.StartPeriod < 1 || meeting.StartPeriod > meeting.EndPeriod)
                result.Error("InvalidPeriodRange");
            if (meeting.StartPeriod < 1 || meeting.EndPeriod > maxPeriod)
                result.Warning("PeriodOutOfRange");

            if (meeting.Weeks?.Length > PlannerDataLimits.MaxMeetingWeeksLength)
            {
                result.Error("MeetingWeeksTooLong", PlannerDataLimits.MaxMeetingWeeksLength.ToString());
                continue;
            }

            var weeks = MeetingWeeksParser.ParseDetailed(meeting.Weeks, semester.WeekCount, meeting.WeekParity);
            foreach (var token in weeks.InvalidTokens)
                result.Error("InvalidWeeks", token);
            if (weeks.OutOfRangeWeeks.Count > 0)
                result.Warning("WeeksOutOfRange");
            if (weeks.WasBounded)
                result.Warning("WeeksParsingBounded");

            var visiblePeriods = meeting.StartPeriod <= maxPeriod && meeting.EndPeriod >= 1;
            if (weeks.Weeks.Count > 0 && visiblePeriods && meeting.Weekday is >= 1 and <= 7 && meeting.StartPeriod <= meeting.EndPeriod)
                anyVisibleSlot = true;

            if (meeting.Weekday is < 1 or > 7 ||
                meeting.StartPeriod < 1 ||
                meeting.StartPeriod > meeting.EndPeriod ||
                !Enum.IsDefined(meeting.WeekParity))
            {
                continue;
            }

            foreach (var week in weeks.Weeks)
            {
                var key = (meeting.Weekday, week);
                if (!intervalsByOccurrence.TryGetValue(key, out var intervals))
                {
                    intervals = new List<(int Start, int End)>();
                    intervalsByOccurrence[key] = intervals;
                }

                intervals.Add((meeting.StartPeriod, meeting.EndPeriod));
            }
        }

        if (course.MeetingTimes.Count > 0 && !anyVisibleSlot)
            result.Error("CourseCompletelyInvisible");
        if (intervalsByOccurrence.Values.Any(HasOverlap))
            result.Error("MeetingTimesOverlap");

        return result;
    }

    private static bool HasOverlap(IEnumerable<(int Start, int End)> intervals)
    {
        var hasPrevious = false;
        var furthestEnd = 0;
        foreach (var interval in intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End))
        {
            if (hasPrevious && interval.Start <= furthestEnd)
                return true;

            hasPrevious = true;
            furthestEnd = Math.Max(furthestEnd, interval.End);
        }

        return false;
    }
}

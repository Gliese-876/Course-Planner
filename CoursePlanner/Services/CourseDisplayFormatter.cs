using System.Text;
using CoursePlanner.Core;

namespace CoursePlanner.Services;

public sealed class CourseDisplayFormatter
{
    private readonly AppLocalizer _text;

    public CourseDisplayFormatter(AppLocalizer text)
    {
        _text = text;
    }

    public string Meeting(MeetingTime meeting)
    {
        var weekText = DisplayWeeks(meeting.Weeks);
        var weeks = meeting.WeekParity == WeekParity.All
            ? weekText
            : $"{weekText} {ParityText(meeting.WeekParity)}";
        return string.Format(
            _text["MeetingTimeFormat"],
            WeekdayShort(meeting.Weekday),
            meeting.StartPeriod,
            meeting.EndPeriod,
            weeks);
    }

    public IReadOnlyList<MeetingDisplayPart> MeetingDetails(CourseOffering course) =>
        MeetingDetails(course.MeetingTimes);

    public IReadOnlyList<MeetingDisplayPart> MeetingDetails(IEnumerable<MeetingTime> meetings) =>
        meetings.Select((meeting, index) =>
        {
            return new MeetingDisplayPart
            {
                Title = string.Format(_text["MeetingItemTitleFormat"], index + 1),
                AutomationText = MeetingDetailText(meeting),
                Fields =
                [
                    new MeetingDisplayField(_text["MeetingWeekday"], WeekdayShort(meeting.Weekday)),
                    new MeetingDisplayField(_text["MeetingPeriods"], string.Format(_text["MeetingPeriodsFormat"], meeting.StartPeriod, meeting.EndPeriod)),
                    new MeetingDisplayField(_text["MeetingWeeks"], DisplayWeeks(meeting.Weeks)),
                    new MeetingDisplayField(_text["MeetingParity"], ParityText(meeting.WeekParity))
                ]
            };
        }).ToList();

    public IReadOnlyList<string> MeetingLines(IEnumerable<MeetingTime> meetings) =>
        meetings.Select(Meeting).ToList();

    public string MeetingListText(IEnumerable<MeetingTime> meetings) =>
        string.Join(Environment.NewLine, MeetingLines(meetings));

    public string MeetingListText(CourseOffering course) =>
        MeetingListText(course.MeetingTimes);

    public string MeetingDetailText(CourseOffering course) =>
        string.Join(Environment.NewLine + Environment.NewLine, course.MeetingTimes.Select(MeetingDetailText));

    public string CourseMetadataLine(CourseOffering course) =>
        string.Join(
            " / ",
            CourseMetadataParts(course));

    public string CourseTooltipText(CourseOffering course)
    {
        var metadata = string.Join(Environment.NewLine, CourseMetadataParts(course));
        var sections = new[] { course.CourseName, MeetingDetailText(course), metadata }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static IEnumerable<string> CourseMetadataParts(CourseOffering course) =>
        new[] { course.Teacher, course.Location, CourseDerivedValues.CapacityText(course) }
            .Where(value => !string.IsNullOrWhiteSpace(value));

    public string MeetingDetailText(MeetingTime meeting)
    {
        return string.Join(
            Environment.NewLine,
            $"{_text["MeetingWeekday"]}: {WeekdayShort(meeting.Weekday)}",
            $"{_text["MeetingPeriods"]}: {string.Format(_text["MeetingPeriodsFormat"], meeting.StartPeriod, meeting.EndPeriod)}",
            $"{_text["MeetingWeeks"]}: {DisplayWeeks(meeting.Weeks)}",
            $"{_text["MeetingParity"]}: {ParityText(meeting.WeekParity)}");
    }

    public string MeetingSummary(CourseOffering course) =>
        course.MeetingTimes.Count == 0
            ? ""
            : string.Join("; ", course.MeetingTimes.Select(Meeting));

    public string CourseListSummary(CourseOffering course)
    {
        var parts = new[] { course.Teacher, MeetingSummary(course), course.Location, CourseDerivedValues.CapacityText(course) }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join("  ", parts);
    }

    public string CourseLabels(CourseOffering course)
    {
        var labels = new[] { course.CourseGroupType, course.StudyType }
            .Concat(course.Labels)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => _text.LocalizeKnownLabel(value));
        return string.Join(" · ", labels);
    }

    public IReadOnlyList<string> WeekdayShortNames() =>
        new[]
        {
            _text["MondayShort"],
            _text["TuesdayShort"],
            _text["WednesdayShort"],
            _text["ThursdayShort"],
            _text["FridayShort"],
            _text["SaturdayShort"],
            _text["SundayShort"]
        };

    public string WeekdayShort(int weekday) => weekday switch
    {
        1 => _text["MondayShort"],
        2 => _text["TuesdayShort"],
        3 => _text["WednesdayShort"],
        4 => _text["ThursdayShort"],
        5 => _text["FridayShort"],
        6 => _text["SaturdayShort"],
        7 => _text["SundayShort"],
        _ => throw new ArgumentOutOfRangeException(nameof(weekday), weekday, null)
    };

    public string ParityText(WeekParity parity) => parity switch
    {
        WeekParity.Odd => _text["MeetingParityOdd"],
        WeekParity.Even => _text["MeetingParityEven"],
        _ => _text["MeetingParityAll"]
    };

    public string DisplayWeeks(string weeks) =>
        string.IsNullOrWhiteSpace(weeks)
            ? ""
            : string.Format(_text["MeetingWeeksDisplayFormat"], weeks.Trim());

    public string PlanText(PlannerDocument document, SelectionPlan plan, int week)
    {
        var semester = document.Semesters.First(x => x.SemesterId == plan.SemesterId);
        var builder = new StringBuilder();
        builder.AppendLine($"{semester.SemesterName} / {plan.PlanName}");
        builder.AppendLine(string.Format(_text["PlanTextWeekLine"], week, SemesterRules.WeekRangeText(semester, week)));
        builder.AppendLine(string.Format(_text["PlanTextSummaryLine"], SelectionPlanMetrics.TotalCredits(plan, document.CourseLibrary), SelectionPlanMetrics.CourseCount(plan)));
        builder.AppendLine();
        foreach (var course in PlanCourseResolver.Courses(plan, document.CourseLibrary)
                     .OrderBy(x => x.CourseName, StringComparer.CurrentCultureIgnoreCase))
        {
            builder.AppendLine(string.Format(_text["PlanTextCourseHeader"], course.CourseName, course.Credits));
            builder.AppendLine(string.Format(_text["PlanTextTeacherLine"], course.Teacher));
            builder.AppendLine(string.Format(_text["PlanTextLocationLine"], course.Location));
            builder.AppendLine(string.Format(_text["PlanTextTimeLine"], MeetingSummary(course)));
            if (!string.IsNullOrWhiteSpace(CourseDerivedValues.CapacityText(course)))
                builder.AppendLine(string.Format(_text["PlanTextCapacityLine"], CourseDerivedValues.CapacityText(course)));
            if (!string.IsNullOrWhiteSpace(course.Notes))
                builder.AppendLine(string.Format(_text["PlanTextNotesLine"], course.Notes));
        }

        return builder.ToString();
    }

    public string ImportPreviewReport(ImportPreview preview) =>
        ImportPreviewReport(preview, preview.Items);

    public string ImportPreviewReport(ImportPreview preview, IEnumerable<ImportPreviewItem> items)
    {
        var visibleItems = items.ToList();
        return ImportPreviewReport(preview, visibleItems, visibleItems.Count);
    }

    public string ImportPreviewReport(
        ImportPreview preview,
        IEnumerable<ImportPreviewItem> displayedItems,
        int matchingItemCount)
    {
        var visibleItems = displayedItems.ToList();
        if (matchingItemCount < visibleItems.Count)
            throw new ArgumentOutOfRangeException(nameof(matchingItemCount));
        var builder = new StringBuilder();
        builder.AppendLine(string.Format(_text["ImportPreviewReportTitle"], ImportKind(preview.Kind), preview.SchemaVersion));
        builder.AppendLine(string.Format(_text["ImportPreviewVisibleItems"], matchingItemCount, preview.Items.Count));
        foreach (var group in visibleItems.GroupBy(x => x.Status))
        {
            builder.AppendLine();
            builder.AppendLine(ImportStatus(group.Key));
            foreach (var item in group)
            {
                builder.AppendLine(string.Format(
                    _text["ImportPreviewReportItem"],
                    ImportKind(item.Kind),
                    string.IsNullOrWhiteSpace(item.DisplayName) ? _text["Unknown"] : item.DisplayName,
                    item.SemesterName));
                foreach (var warning in item.Warnings)
                    builder.AppendLine(string.Format(_text["ImportPreviewReportWarning"], _text.ValidationMessage(warning)));
                foreach (var error in item.Errors)
                    builder.AppendLine(string.Format(_text["ImportPreviewReportError"], _text.ValidationMessage(error)));
            }
        }

        if (visibleItems.Count < matchingItemCount)
        {
            builder.AppendLine();
            builder.AppendLine(string.Format(
                _text["ImportPreviewDisplayLimit"],
                visibleItems.Count,
                matchingItemCount));
        }

        return builder.ToString();
    }

    public string ImportMergePreviewReport(
        ImportPreview preview,
        IEnumerable<ImportPreviewItem> displayedItems,
        int totalItemCount)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(displayedItems);
        if (totalItemCount < 0 || totalItemCount < preview.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(totalItemCount));

        var visibleItems = displayedItems.ToList();
        if (visibleItems.Count > totalItemCount)
            throw new ArgumentOutOfRangeException(nameof(displayedItems));

        var counts = Enum.GetValues<ImportPreviewStatus>()
            .ToDictionary(
                status => status,
                status => preview.Items.Count(item => item.Status == status));
        var schema = string.IsNullOrWhiteSpace(preview.SchemaVersion)
            ? _text["Unknown"]
            : preview.SchemaVersion;
        var builder = new StringBuilder();
        builder.AppendLine(string.Format(
            _text["ImportMergeReportTitle"],
            ImportKind(preview.Kind),
            schema));
        builder.AppendLine(string.Format(
            _text["ImportMergeSummaryFormat"],
            totalItemCount,
            counts[ImportPreviewStatus.Added],
            counts[ImportPreviewStatus.Updated],
            counts[ImportPreviewStatus.Skipped],
            counts[ImportPreviewStatus.Conflict],
            counts[ImportPreviewStatus.Warning],
            counts[ImportPreviewStatus.NotImportable]));

        foreach (var group in visibleItems.GroupBy(item => item.Status))
        {
            builder.AppendLine();
            builder.AppendLine(string.Format(
                _text["ImportMergeSectionFormat"],
                ImportStatus(group.Key),
                counts[group.Key]));
            foreach (var item in group)
            {
                var semesterSuffix = string.IsNullOrWhiteSpace(item.SemesterName)
                    ? ""
                    : string.Format(_text["ImportMergeSemesterSuffixFormat"], item.SemesterName);
                builder.AppendLine(string.Format(
                    _text["ImportMergeReportItem"],
                    ImportMergeStatusMarker(item.Status),
                    ImportKind(item.Kind),
                    string.IsNullOrWhiteSpace(item.DisplayName) ? _text["Unknown"] : item.DisplayName,
                    semesterSuffix));
                foreach (var warning in item.Warnings)
                    builder.AppendLine(string.Format(_text["ImportPreviewReportWarning"], _text.ValidationMessage(warning)));
                foreach (var error in item.Errors)
                    builder.AppendLine(string.Format(_text["ImportPreviewReportError"], _text.ValidationMessage(error)));
            }
        }

        if (visibleItems.Count < totalItemCount)
        {
            builder.AppendLine();
            builder.AppendLine(string.Format(
                _text["ImportMergeDisplayLimit"],
                visibleItems.Count,
                totalItemCount));
        }

        return builder.ToString();
    }

    public string ImportStatus(ImportPreviewStatus status) => _text[$"ImportStatus.{status}"];

    public string ImportKind(string kind) => _text[$"ImportKind.{kind}"];

    private static string ImportMergeStatusMarker(ImportPreviewStatus status) => status switch
    {
        ImportPreviewStatus.Added => "+",
        ImportPreviewStatus.Updated => "~",
        ImportPreviewStatus.Skipped => "=",
        ImportPreviewStatus.Conflict => "!",
        ImportPreviewStatus.Warning => "?",
        ImportPreviewStatus.NotImportable => "x",
        _ => "-"
    };
}

public sealed class MeetingDisplayPart
{
    public string Title { get; init; } = "";
    public string AutomationText { get; init; } = "";
    public IReadOnlyList<MeetingDisplayField> Fields { get; init; } = Array.Empty<MeetingDisplayField>();
}

public sealed record MeetingDisplayField(string Label, string Value);

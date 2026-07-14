using CoursePlanner.Core;

namespace CoursePlanner.Persistence;

internal static class PlannerDocumentPersistenceValidator
{
    internal const int MaxSemesters = PlannerDataLimits.MaxSemesters;
    internal const int MaxLabels = PlannerDataLimits.MaxLabels;
    internal const int MaxCourses = PlannerDataLimits.MaxCourses;
    internal const int MaxPlans = PlannerDataLimits.MaxPlans;
    internal const int MaxPeriodsPerSemester = PlannerDataLimits.MaxPeriodsPerSemester;
    internal const int MaxLabelsPerCourse = PlannerDataLimits.MaxLabelsPerCourse;
    internal const int MaxMeetingsPerCourse = PlannerDataLimits.MaxMeetingsPerCourse;
    internal const int MaxMeetingRowsPerPlan = PlannerDataLimits.MaxMeetingRowsPerPlan;
    internal const int MaxSnapshotsPerPlan = PlannerDataLimits.MaxSnapshotsPerPlan;
    internal const int MaxTotalSnapshots = PlannerDataLimits.MaxTotalSnapshots;
    internal const int MaxTextFieldLength = PlannerDataLimits.MaxTextFieldLength;
    internal const int MaxReportedIssues = PlannerDataLimits.MaxReportedPersistenceIssues;

    public static PlannerDocument ToValidatedDomain(PlannerDocumentDto dto)
    {
        ValidateDtoShape(dto);
        var document = PersistenceDocumentMapper.ToDomain(dto);
        ValidateForPersistence(document);
        return document;
    }

    public static void ValidateForPersistence(PlannerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new IssueCollector();
        if (!ValidateDomainShape(document, issues))
        {
            issues.ThrowIfAny();
            return;
        }

        if (document.Semesters.Count == 0)
            issues.Add("Document.Semesters.Required");
        if (!string.Equals(document.SchemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
            issues.Add("Document.SchemaVersion.Unsupported");

        var semestersById = new Dictionary<string, Semester>(StringComparer.Ordinal);
        foreach (var semester in document.Semesters)
        {
            if (string.IsNullOrWhiteSpace(semester.SemesterId))
                issues.Add("Semester.Id.Required");
            else if (!semestersById.TryAdd(semester.SemesterId, semester))
                issues.Add("Semester.Id.Duplicate");

            foreach (var issue in SemesterRules.ValidateSemester(semester, document.Semesters).Errors)
                issues.Add($"Semester.{issue.Code}");
        }

        var labelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in document.Labels)
        {
            if (!Enum.IsDefined(label.Kind))
                issues.Add("Label.Kind.Invalid");
            var normalizedName = TextRules.NormalizeIdentityText(label.Name);
            if (normalizedName.Length == 0)
                issues.Add("Label.Name.Required");
            else if (!labelNames.Add(normalizedName))
                issues.Add("Label.Name.Duplicate");
        }

        if (!Enum.IsDefined(document.Settings.Language))
            issues.Add("Settings.Language.Invalid");
        if (!Enum.IsDefined(document.Settings.Theme))
            issues.Add("Settings.Theme.Invalid");

        var storedCourseIds = new Dictionary<string, CourseOffering>(StringComparer.Ordinal);
        var canonicalCourseIds = new Dictionary<string, CourseOffering>(StringComparer.Ordinal);
        var courseAliases = new Dictionary<string, CourseOffering>(StringComparer.Ordinal);
        foreach (var course in document.CourseLibrary)
        {
            if (string.IsNullOrWhiteSpace(course.OfferingId))
            {
                issues.Add("Course.Id.Required");
            }
            else
            {
                if (!storedCourseIds.TryAdd(course.OfferingId, course))
                    issues.Add("Course.Id.Duplicate");
                AddAlias(courseAliases, course.OfferingId, course, issues);
            }

            var canonicalId = CourseIdentityService.GenerateOfferingId(course);
            if (!string.Equals(course.OfferingId, canonicalId, StringComparison.Ordinal))
                issues.Add("Course.Id.NonCanonical");
            if (!canonicalCourseIds.TryAdd(canonicalId, course))
                issues.Add("Course.Identity.Duplicate");
            AddAlias(courseAliases, canonicalId, course, issues);

            if (string.IsNullOrWhiteSpace(course.SemesterId) ||
                !semestersById.TryGetValue(course.SemesterId, out var semester))
            {
                issues.Add("Course.Semester.Missing");
                continue;
            }

            var validation = CourseValidator.Validate(
                course,
                semester,
                importMode: false,
                allowUnscheduled: true);
            foreach (var issue in validation.Errors)
                issues.Add($"Course.{issue.Code}");
            foreach (var issue in LabelRules.ValidateCourseReferences(course, document.Labels).Errors)
                issues.Add($"Course.{issue.Code}");
        }

        var plansById = new Dictionary<string, SelectionPlan>(StringComparer.Ordinal);
        var planNameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in document.Plans)
        {
            if (string.IsNullOrWhiteSpace(plan.PlanId))
                issues.Add("Plan.Id.Required");
            else if (!plansById.TryAdd(plan.PlanId, plan))
                issues.Add("Plan.Id.Duplicate");

            if (string.IsNullOrWhiteSpace(plan.SemesterId) || !semestersById.ContainsKey(plan.SemesterId))
                issues.Add("Plan.Semester.Missing");
            if (string.IsNullOrWhiteSpace(plan.PlanName))
                issues.Add("Plan.Name.Required");
            else if (!planNameKeys.Add($"{plan.SemesterId}\0{TextRules.NormalizeIdentityText(plan.PlanName)}"))
                issues.Add("Plan.Name.Duplicate");
            foreach (var issue in WindowsFileNameRules.ValidateFileComponent(plan.PlanName ?? "").Errors)
                issues.Add($"Plan.{issue.Code}");

            var planCourseIds = new HashSet<string>(StringComparer.Ordinal);
            var planMeetingRows = 0L;
            foreach (var snapshot in plan.Snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.SnapshotId))
                    issues.Add("Snapshot.Id.Required");
                else if (!snapshotIds.Add(snapshot.SnapshotId))
                    issues.Add("Snapshot.Id.Duplicate");

                if (string.IsNullOrWhiteSpace(snapshot.CourseOfferingId) ||
                    !courseAliases.TryGetValue(snapshot.CourseOfferingId, out var course))
                {
                    issues.Add("Snapshot.Course.Missing");
                    continue;
                }

                var canonicalId = CourseIdentityService.GenerateOfferingId(course);
                if (!planCourseIds.Add(canonicalId))
                    issues.Add("Snapshot.Course.Duplicate");
                else
                    planMeetingRows += course.MeetingTimes.Count;
                if (!string.Equals(plan.SemesterId, course.SemesterId, StringComparison.Ordinal))
                    issues.Add("Snapshot.Course.CrossSemester");
            }
            var unlockedSnapshots = plan.Snapshots
                .Where(snapshot => !snapshot.IsLocked)
                .ToList();
            var registrationOrders = unlockedSnapshots
                .Select(snapshot => snapshot.RegistrationOrder)
                .OrderBy(order => order)
                .ToArray();
            if (!registrationOrders.SequenceEqual(
                    Enumerable.Range(0, unlockedSnapshots.Count).Select(index => (int?)index)) ||
                plan.Snapshots.Any(snapshot => snapshot.IsLocked && snapshot.RegistrationOrder is not null))
            {
                issues.Add("Snapshot.RegistrationOrder.Invalid");
            }
            if (planMeetingRows > MaxMeetingRowsPerPlan)
                issues.Add("Plan.MeetingRows.TooMany");
        }

        var openPlanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var openPlanId in document.Settings.OpenPlanIds)
        {
            if (!openPlanIds.Add(openPlanId))
                issues.Add("Settings.OpenPlanIds.Duplicate");
            if (!plansById.ContainsKey(openPlanId))
                issues.Add("Settings.OpenPlanIds.MissingPlan");
        }

        if (document.Settings.CurrentSemesterId is null)
        {
            issues.Add("Settings.CurrentSemesterId.Required");
        }
        else if (!semestersById.ContainsKey(document.Settings.CurrentSemesterId))
        {
            issues.Add("Settings.CurrentSemesterId.MissingSemester");
        }

        if (document.Settings.CurrentPlanId is not null)
        {
            if (!plansById.TryGetValue(document.Settings.CurrentPlanId, out var currentPlan))
                issues.Add("Settings.CurrentPlanId.MissingPlan");
            else if (!openPlanIds.Contains(document.Settings.CurrentPlanId))
                issues.Add("Settings.CurrentPlanId.NotOpen");
            else if (!string.Equals(
                         document.Settings.CurrentSemesterId,
                         currentPlan.SemesterId,
                         StringComparison.Ordinal))
            {
                issues.Add("Settings.CurrentPlanId.SemesterMismatch");
            }
        }
        else if (document.Settings.CurrentSemesterId is not null &&
                 document.Settings.OpenPlanIds.Any(openPlanId =>
                     plansById.TryGetValue(openPlanId, out var openPlan) &&
                     string.Equals(
                         openPlan.SemesterId,
                         document.Settings.CurrentSemesterId,
                         StringComparison.Ordinal)))
        {
            issues.Add("Settings.CurrentPlanId.Required");
        }

        issues.ThrowIfAny();
    }

    private static void ValidateDtoShape(PlannerDocumentDto dto)
    {
        var issues = new IssueCollector();
        if (!ValidateRootCollections(
                dto.Semesters,
                dto.Labels,
                dto.CourseLibrary,
                dto.Plans,
                dto.Settings,
                issues))
        {
            issues.ThrowIfAny();
            return;
        }

        if (!ValidateTopLevelCounts(
                dto.Semesters.Count,
                dto.Labels.Count,
                dto.CourseLibrary.Count,
                dto.Plans.Count,
                issues))
        {
            issues.ThrowIfAny();
            return;
        }

        foreach (var semester in dto.Semesters)
        {
            if (semester is null)
            {
                issues.Add("Document.Semesters.NullElement");
                continue;
            }
            if (semester.SemesterId is null)
                issues.Add("Semester.Id.Null");
            if (semester.SemesterName is null)
                issues.Add("Semester.Name.Null");
            CheckTextLength(semester.SemesterId, MaxTextFieldLength, "Semester.Text.TooLong", issues);
            CheckTextLength(semester.SemesterName, MaxTextFieldLength, "Semester.Text.TooLong", issues);
            if (semester.PeriodSchedule is null)
                issues.Add("Semester.PeriodSchedule.Null");
            else if (semester.PeriodSchedule.Count > MaxPeriodsPerSemester)
                issues.Add("Semester.PeriodSchedule.TooMany");
            else if (semester.PeriodSchedule.Any(period => period is null))
                issues.Add("Semester.PeriodSchedule.NullElement");
        }

        foreach (var label in dto.Labels)
        {
            if (label is null)
                issues.Add("Document.Labels.NullElement");
            else
            {
                if (label.Name is null)
                    issues.Add("Label.Name.Null");
                CheckTextLength(label.Name, MaxTextFieldLength, "Label.Text.TooLong", issues);
            }
        }

        var totalLabelReferences = 0L;
        foreach (var course in dto.CourseLibrary)
        {
            if (course is null)
            {
                issues.Add("Document.CourseLibrary.NullElement");
                continue;
            }

            if (course.OfferingId is null)
                issues.Add("Course.Id.Null");
            if (course.SemesterId is null)
                issues.Add("Course.SemesterId.Null");
            if (course.CourseName is null || course.Teacher is null || course.Location is null ||
                course.Notes is null || course.Color is null)
            {
                issues.Add("Course.Text.Null");
            }
            foreach (var text in new[]
                     {
                         course.OfferingId,
                         course.SemesterId,
                         course.CourseName,
                         course.Teacher,
                         course.Location,
                         course.CourseGroupType,
                         course.StudyType,
                         course.Notes,
                         course.Color
                     })
            {
                CheckTextLength(text, MaxTextFieldLength, "Course.Text.TooLong", issues);
            }
            if (course.Labels is null)
                issues.Add("Course.Labels.Null");
            else if (course.Labels.Count > MaxLabelsPerCourse)
                issues.Add("Course.Labels.TooMany");
            else if (course.Labels.Any(label => label is null))
                issues.Add("Course.Labels.NullElement");
            else
            {
                totalLabelReferences += course.Labels.Count;
                foreach (var label in course.Labels)
                    CheckTextLength(label, MaxTextFieldLength, "Course.Text.TooLong", issues);
            }
            if (!string.IsNullOrWhiteSpace(course.CourseGroupType))
                totalLabelReferences++;
            if (!string.IsNullOrWhiteSpace(course.StudyType))
                totalLabelReferences++;
            if (course.MeetingTimes is null)
                issues.Add("Course.MeetingTimes.Null");
            else if (course.MeetingTimes.Count > MaxMeetingsPerCourse)
                issues.Add("Course.MeetingTimes.TooMany");
            else
            {
                foreach (var meeting in course.MeetingTimes)
                {
                    if (meeting is null)
                        issues.Add("Course.MeetingTimes.NullElement");
                    else if (meeting.Weeks is null)
                        issues.Add("Meeting.Weeks.Null");
                    else
                        CheckTextLength(
                            meeting.Weeks,
                            MeetingWeeksParser.MaxExpressionLength,
                            "Meeting.Weeks.TooLong",
                            issues);
                }
            }
        }
        if (totalLabelReferences > PlannerDataLimits.MaxTotalLabelReferences)
            issues.Add("Document.LabelReferences.TooMany");

        var totalSnapshots = 0L;
        foreach (var plan in dto.Plans)
        {
            if (plan is null)
            {
                issues.Add("Document.Plans.NullElement");
                continue;
            }
            if (plan.PlanId is null)
                issues.Add("Plan.Id.Null");
            if (plan.SemesterId is null)
                issues.Add("Plan.SemesterId.Null");
            if (plan.PlanName is null)
                issues.Add("Plan.Name.Null");
            CheckTextLength(plan.PlanId, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            CheckTextLength(plan.SemesterId, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            CheckTextLength(plan.PlanName, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            if (plan.Snapshots is null)
            {
                issues.Add("Plan.Snapshots.Null");
                continue;
            }
            totalSnapshots += plan.Snapshots.Count;
            if (plan.Snapshots.Count > MaxSnapshotsPerPlan)
                issues.Add("Plan.Snapshots.TooMany");
            else
            {
                foreach (var snapshot in plan.Snapshots)
                {
                    if (snapshot is null)
                        issues.Add("Plan.Snapshots.NullElement");
                    else if (snapshot.SnapshotId is null || snapshot.CourseOfferingId is null)
                        issues.Add("Snapshot.IdOrCourse.Null");
                    else
                    {
                        CheckTextLength(snapshot.SnapshotId, MaxTextFieldLength, "Snapshot.Id.TooLong", issues);
                        CheckTextLength(snapshot.CourseOfferingId, MaxTextFieldLength, "Snapshot.Id.TooLong", issues);
                    }
                }
            }
        }
        if (totalSnapshots > MaxTotalSnapshots)
            issues.Add("Document.Snapshots.TooMany");

        if (dto.Settings.OpenPlanIds is null)
            issues.Add("Settings.OpenPlanIds.Null");
        else if (dto.Settings.OpenPlanIds.Count > PlanTabLimits.MaximumOpenPlans)
            issues.Add("Settings.OpenPlanIds.TooMany");
        else if (dto.Settings.OpenPlanIds.Any(id => id is null))
            issues.Add("Settings.OpenPlanIds.NullElement");
        else
        {
            foreach (var id in dto.Settings.OpenPlanIds)
                CheckTextLength(id, MaxTextFieldLength, "Settings.Id.TooLong", issues);
        }
        CheckReferenceText(
            dto.Settings.CurrentSemesterId,
            MaxTextFieldLength,
            "Settings.Id.TooLong",
            issues);
        CheckReferenceText(
            dto.Settings.CurrentPlanId,
            MaxTextFieldLength,
            "Settings.Id.TooLong",
            issues);

        issues.ThrowIfAny();
    }

    private static bool ValidateDomainShape(PlannerDocument document, IssueCollector issues)
    {
        if (!ValidateRootCollections(
                document.Semesters,
                document.Labels,
                document.CourseLibrary,
                document.Plans,
                document.Settings,
                issues))
        {
            return false;
        }

        if (!ValidateTopLevelCounts(
                document.Semesters.Count,
                document.Labels.Count,
                document.CourseLibrary.Count,
                document.Plans.Count,
                issues))
        {
            return false;
        }

        CheckTextLength(
            document.SchemaVersion,
            PlannerDataLimits.MaxSchemaVersionLength,
            "Document.SchemaVersion.TooLong",
            issues);
        if (issues.HasIssues)
            return false;

        var totalSnapshots = 0L;
        foreach (var semester in document.Semesters)
        {
            if (semester is null)
                issues.Add("Document.Semesters.NullElement");
            else
            {
                CheckTextLength(semester.SemesterId, MaxTextFieldLength, "Semester.Text.TooLong", issues);
                CheckTextLength(semester.SemesterName, MaxTextFieldLength, "Semester.Text.TooLong", issues);
                if (semester.PeriodSchedule is null)
                    issues.Add("Semester.PeriodSchedule.Null");
                else if (semester.PeriodSchedule.Count > MaxPeriodsPerSemester)
                    issues.Add("Semester.PeriodSchedule.TooMany");
                else if (semester.PeriodSchedule.Any(period => period is null))
                    issues.Add("Semester.PeriodSchedule.NullElement");
            }
        }

        foreach (var label in document.Labels)
        {
            if (label is null)
                issues.Add("Document.Labels.NullElement");
            else
                CheckTextLength(label.Name, MaxTextFieldLength, "Label.Text.TooLong", issues);
        }

        var totalLabelReferences = 0L;
        foreach (var course in document.CourseLibrary)
        {
            if (course is null)
            {
                issues.Add("Document.CourseLibrary.NullElement");
                continue;
            }
            foreach (var text in new[]
                     {
                         course.OfferingId,
                         course.SemesterId,
                         course.CourseName,
                         course.Teacher,
                         course.Location,
                         course.CourseGroupType,
                         course.StudyType,
                         course.Notes,
                         course.Color
                     })
            {
                CheckTextLength(text, MaxTextFieldLength, "Course.Text.TooLong", issues);
            }
            if (course.Labels is null)
                issues.Add("Course.Labels.Null");
            else if (course.Labels.Count > MaxLabelsPerCourse)
                issues.Add("Course.Labels.TooMany");
            else if (course.Labels.Any(label => label is null))
                issues.Add("Course.Labels.NullElement");
            else
            {
                totalLabelReferences += course.Labels.Count;
                foreach (var label in course.Labels)
                    CheckTextLength(label, MaxTextFieldLength, "Course.Text.TooLong", issues);
            }
            if (!string.IsNullOrWhiteSpace(course.CourseGroupType))
                totalLabelReferences++;
            if (!string.IsNullOrWhiteSpace(course.StudyType))
                totalLabelReferences++;
            if (course.MeetingTimes is null)
                issues.Add("Course.MeetingTimes.Null");
            else if (course.MeetingTimes.Count > MaxMeetingsPerCourse)
                issues.Add("Course.MeetingTimes.TooMany");
            else
            {
                foreach (var meeting in course.MeetingTimes)
                {
                    if (meeting is null)
                        issues.Add("Course.MeetingTimes.NullElement");
                    else
                        CheckTextLength(
                            meeting.Weeks,
                            MeetingWeeksParser.MaxExpressionLength,
                            "Meeting.Weeks.TooLong",
                            issues);
                }
            }
        }
        if (totalLabelReferences > PlannerDataLimits.MaxTotalLabelReferences)
            issues.Add("Document.LabelReferences.TooMany");

        foreach (var plan in document.Plans)
        {
            if (plan is null)
            {
                issues.Add("Document.Plans.NullElement");
                continue;
            }
            CheckTextLength(plan.PlanId, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            CheckTextLength(plan.SemesterId, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            CheckTextLength(plan.PlanName, MaxTextFieldLength, "Plan.Text.TooLong", issues);
            if (plan.Snapshots is null)
            {
                issues.Add("Plan.Snapshots.Null");
                continue;
            }
            totalSnapshots += plan.Snapshots.Count;
            if (plan.Snapshots.Count > MaxSnapshotsPerPlan)
                issues.Add("Plan.Snapshots.TooMany");
            else
            {
                foreach (var snapshot in plan.Snapshots)
                {
                    if (snapshot is null)
                        issues.Add("Plan.Snapshots.NullElement");
                    else
                    {
                        CheckTextLength(snapshot.SnapshotId, MaxTextFieldLength, "Snapshot.Id.TooLong", issues);
                        CheckTextLength(snapshot.CourseOfferingId, MaxTextFieldLength, "Snapshot.Id.TooLong", issues);
                    }
                }
            }
        }
        if (totalSnapshots > MaxTotalSnapshots)
            issues.Add("Document.Snapshots.TooMany");

        if (document.Settings.OpenPlanIds is null)
            issues.Add("Settings.OpenPlanIds.Null");
        else if (document.Settings.OpenPlanIds.Count > PlanTabLimits.MaximumOpenPlans)
            issues.Add("Settings.OpenPlanIds.TooMany");
        else if (document.Settings.OpenPlanIds.Any(id => id is null))
            issues.Add("Settings.OpenPlanIds.NullElement");
        else
        {
            foreach (var id in document.Settings.OpenPlanIds)
                CheckTextLength(id, MaxTextFieldLength, "Settings.Id.TooLong", issues);
        }
        CheckReferenceText(
            document.Settings.CurrentSemesterId,
            MaxTextFieldLength,
            "Settings.Id.TooLong",
            issues);
        CheckReferenceText(
            document.Settings.CurrentPlanId,
            MaxTextFieldLength,
            "Settings.Id.TooLong",
            issues);

        return !issues.HasIssues;
    }

    private static bool ValidateRootCollections<TSemester, TLabel, TCourse, TPlan, TSettings>(
        IReadOnlyCollection<TSemester>? semesters,
        IReadOnlyCollection<TLabel>? labels,
        IReadOnlyCollection<TCourse>? courses,
        IReadOnlyCollection<TPlan>? plans,
        TSettings? settings,
        IssueCollector issues)
        where TSettings : class
    {
        if (semesters is null)
            issues.Add("Document.Semesters.Null");
        if (labels is null)
            issues.Add("Document.Labels.Null");
        if (courses is null)
            issues.Add("Document.CourseLibrary.Null");
        if (plans is null)
            issues.Add("Document.Plans.Null");
        if (settings is null)
            issues.Add("Document.Settings.Null");
        return !issues.HasIssues;
    }

    private static bool ValidateTopLevelCounts(
        int semesterCount,
        int labelCount,
        int courseCount,
        int planCount,
        IssueCollector issues)
    {
        if (semesterCount > MaxSemesters)
            issues.Add("Document.Semesters.TooMany");
        if (labelCount > MaxLabels)
            issues.Add("Document.Labels.TooMany");
        if (courseCount > MaxCourses)
            issues.Add("Document.CourseLibrary.TooMany");
        if (planCount > MaxPlans)
            issues.Add("Document.Plans.TooMany");
        return !issues.HasIssues;
    }

    private static void AddAlias(
        Dictionary<string, CourseOffering> aliases,
        string alias,
        CourseOffering course,
        IssueCollector issues)
    {
        if (aliases.TryGetValue(alias, out var existing) && !ReferenceEquals(existing, course))
            issues.Add("Course.Id.AmbiguousAlias");
        else
            aliases[alias] = course;
    }

    private static void CheckTextLength(
        string? value,
        int maximumLength,
        string issueCode,
        IssueCollector issues)
    {
        issues.ObserveText(value, maximumLength, issueCode);
    }

    private static void CheckReferenceText(
        string? value,
        int maximumLength,
        string issueCode,
        IssueCollector issues)
    {
        issues.ObserveReferenceText(value, maximumLength, issueCode);
    }

    private sealed class IssueCollector
    {
        private readonly List<string> _issues = new();
        private readonly HashSet<string> _uniqueIssues = new(StringComparer.Ordinal);

        public bool HasIssues => _issues.Count > 0;
        public bool WasTruncated { get; private set; }
        public long TextCharacterCount { get; private set; }

        public void Add(string issueCode)
        {
            if (!_uniqueIssues.Add(issueCode))
                return;
            if (_issues.Count >= MaxReportedIssues)
            {
                WasTruncated = true;
                return;
            }
            _issues.Add(issueCode);
        }

        public void ObserveText(string? value, int maximumLength, string issueCode)
        {
            if (value is null)
                return;
            ValidateText(value, maximumLength, issueCode);
            if (TextCharacterCount > PlannerDataLimits.MaxAggregateTextCharacters)
                return;
            TextCharacterCount += value.Length;
            if (TextCharacterCount > PlannerDataLimits.MaxAggregateTextCharacters)
                Add("Document.Text.TooLarge");
        }

        public void ObserveReferenceText(string? value, int maximumLength, string issueCode)
        {
            if (value is not null)
                ValidateText(value, maximumLength, issueCode);
        }

        private void ValidateText(string value, int maximumLength, string issueCode)
        {
            if (!string.Equals(value, TextRules.SanitizeUtf16(value), StringComparison.Ordinal))
                Add("Document.Text.InvalidUtf16");
            if (value.Length > maximumLength)
                Add(issueCode);
        }

        public void ThrowIfAny()
        {
            if (HasIssues)
                throw new RepositoryStateValidationException(_issues.AsReadOnly(), WasTruncated);
        }
    }
}

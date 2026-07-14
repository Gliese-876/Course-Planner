using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoursePlanner.Core;

namespace CoursePlanner.Exchange;

public sealed class CourseLibraryPackage
{
    public string Kind { get; set; } = PlannerSchemas.CourseLibraryKind;
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    public List<Semester> Semesters { get; set; } = new();
    public List<CourseLabel> Labels { get; set; } = new();
    public List<CourseOffering> Courses { get; set; } = new();
}

public sealed class SelectionPlanPackage
{
    public string Kind { get; set; } = PlannerSchemas.SelectionPlanKind;
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    public Semester Semester { get; set; } = new();
    public List<CourseLabel> Labels { get; set; } = new();
    public List<CourseOffering> Courses { get; set; } = new();
    public SelectionPlan Plan { get; set; } = new();
}

public static class ImportExportService
{
    private static readonly JsonSerializerOptions ExchangeJsonOptions = new(JsonDefaults.CompactOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private const int MaxImportJsonCharacters = PlannerDataLimits.MaxImportTextCharacters;
    private const int MaxSemestersPerPackage = PlannerDataLimits.MaxSemesters;
    private const int MaxLabelsPerPackage = PlannerDataLimits.MaxLabels;
    private const int MaxCoursesPerPackage = PlannerDataLimits.MaxCourses;
    private const int MaxLabelsPerCourse = PlannerDataLimits.MaxLabelsPerCourse;
    private const int MaxLabelReferencesPerPackage = PlannerDataLimits.MaxTotalLabelReferences;
    private const int MaxMeetingsPerCourse = PlannerDataLimits.MaxMeetingsPerCourse;
    private const int MaxTextFieldLength = PlannerDataLimits.MaxTextFieldLength;

    public static string ExportCourseLibraryJson(PlannerDocument document, string? semesterId = null)
    {
        var semesters = string.IsNullOrWhiteSpace(semesterId)
            ? document.Semesters
            : document.Semesters.Where(x => x.SemesterId == semesterId).ToList();
        var semesterIds = semesters.Select(x => x.SemesterId).ToHashSet(StringComparer.Ordinal);
        var package = new CourseLibraryPackage
        {
            Semesters = JsonDefaults.Clone(semesters),
            Labels = JsonDefaults.Clone(document.Labels),
            Courses = JsonDefaults.Clone(document.CourseLibrary.Where(x => semesterIds.Contains(x.SemesterId)).ToList())
        };
        return SerializeImportablePackage(ExchangePackageMapper.ToDto(package));
    }

    public static string ExportSelectionPlanJson(PlannerDocument document, SelectionPlan plan)
    {
        var semester = document.Semesters.FirstOrDefault(x => x.SemesterId == plan.SemesterId)
                       ?? throw new InvalidDataException("The selection plan references a missing semester.");
        var courseIds = plan.Snapshots.Select(x => x.CourseOfferingId).ToHashSet(StringComparer.Ordinal);
        var missingCourseIds = courseIds
            .Where(id => string.IsNullOrWhiteSpace(id) || document.CourseLibrary.All(course => course.OfferingId != id))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missingCourseIds.Count > 0)
        {
            throw new InvalidDataException(
                $"The selection plan references courses that are missing from the course library: {string.Join(", ", missingCourseIds)}");
        }

        var courses = document.CourseLibrary.Where(x => courseIds.Contains(x.OfferingId)).ToList();
        if (courses.Count > MaxCoursesPerPackage ||
            plan.Snapshots.Count > MaxCoursesPerPackage ||
            !PlanRules.ValidateMeetingRows(plan, courses).IsValid ||
            courses.Any(CourseHasOversizedFields) ||
            HasOversizedReferencedLabelSet(courses))
        {
            throw new InvalidDataException("The selection plan is too large to export safely.");
        }

        var package = new SelectionPlanPackage
        {
            Semester = JsonDefaults.Clone(semester),
            Labels = ReferencedCourseLabels(document.Labels, courses),
            Courses = JsonDefaults.Clone(courses),
            Plan = JsonDefaults.Clone(plan)
        };
        return SerializeImportablePackage(ExchangePackageMapper.ToDto(package));
    }

    public static ImportPreview PreviewJson(PlannerDocument document, string json)
    {
        if (ExceedsImportLimits(json))
            return NotImportablePreview("Import.FileTooLarge");

        string kind;
        try
        {
            kind = JsonInputGuard.ReadRootStringProperty(json, "kind") ?? "";
            if (kind.Length == 0)
                return NotImportablePreview("Import.InvalidJson");
        }
        catch (JsonException)
        {
            return NotImportablePreview("Import.InvalidJson");
        }
        catch (InvalidDataException)
        {
            return NotImportablePreview("Import.InvalidJson");
        }

        return kind switch
        {
            PlannerSchemas.CourseLibraryKind => PreviewCourseLibraryCore(document, json, validateJson: false),
            PlannerSchemas.SelectionPlanKind => PreviewSelectionPlanCore(document, json, validateJson: false),
            _ => NotImportablePreview("Import.UnknownJsonKind")
        };
    }

    public static ImportPreview PreviewCourseLibrary(PlannerDocument document, string json)
        => PreviewCourseLibraryCore(document, json, validateJson: true);

    private static ImportPreview PreviewCourseLibraryCore(
        PlannerDocument document,
        string json,
        bool validateJson)
    {
        if (ExceedsImportLimits(json))
            return NotImportablePreview("Import.FileTooLarge", PlannerSchemas.CourseLibraryKind);

        CourseLibraryPackage package;
        if (validateJson)
        {
            try
            {
                JsonInputGuard.Validate(json);
            }
            catch (JsonException)
            {
                return NotImportablePreview("Import.InvalidJson", PlannerSchemas.CourseLibraryKind);
            }
            catch (InvalidDataException)
            {
                return NotImportablePreview("Import.InvalidJson", PlannerSchemas.CourseLibraryKind);
            }
        }
        try
        {
            var dto = JsonSerializer.Deserialize<CourseLibraryPackageDto>(json, ExchangeJsonOptions)
                      ?? throw new InvalidDataException("Invalid course library JSON.");
            package = ExchangePackageMapper.ToDomain(dto);
        }
        catch (JsonException)
        {
            return NotImportablePreview("Import.InvalidJson", PlannerSchemas.CourseLibraryKind);
        }
        catch (InvalidDataException)
        {
            return NotImportablePreview("Import.InvalidJson", PlannerSchemas.CourseLibraryKind);
        }

        return PreviewCourseLibraryPackage(document, package);
    }

    private static ImportPreview PreviewCourseLibraryPackage(
        PlannerDocument document,
        CourseLibraryPackage package)
    {
        if (!IsSupportedEnvelope(package.Kind, package.SchemaVersion, PlannerSchemas.CourseLibraryKind))
            return NotImportablePreview("Import.UnsupportedSchemaVersion", package.Kind);
        var packageIssue = ValidateCourseLibraryPackage(package);
        if (packageIssue is not null)
            return NotImportablePreview(packageIssue, package.Kind);

        var preview = new ImportPreview { Kind = package.Kind, SchemaVersion = package.SchemaVersion };

        foreach (var semester in package.Semesters)
        {
            var existingByName = document.Semesters.FirstOrDefault(x =>
                TextRules.IsSameIdentityText(x.SemesterName, semester.SemesterName));
            var existingById = document.Semesters.FirstOrDefault(x => x.SemesterId == semester.SemesterId);
            var targetSemester = existingByName ?? existingById;
            var idNameConflict = existingById is not null &&
                                 !TextRules.IsSameIdentityText(existingById.SemesterName, semester.SemesterName);
            var item = new ImportPreviewItem
            {
                Kind = "semester",
                DisplayName = semester.SemesterName,
                SemesterName = targetSemester?.SemesterName ?? semester.SemesterName,
                TargetSemesterId = targetSemester?.SemesterId ?? semester.SemesterId,
                Semester = semester
            };

            var validation = SemesterRules.ValidateSemester(semester, package.Semesters.Where(x => x != semester));
            item.Errors.AddRange(validation.Errors);
            item.Warnings.AddRange(validation.Warnings);

            if (validation.Errors.Count > 0)
            {
                item.Status = ImportPreviewStatus.NotImportable;
            }
            else if (idNameConflict)
            {
                item.Status = ImportPreviewStatus.Conflict;
                item.CanApplyWithForcedSemesterMerge = true;
                item.Warnings.Add(Issue("Import.SemesterIdentityConflict", existingById!.SemesterName));
                if (!SemesterSettingsEqual(existingById, semester))
                {
                    item.RequiresSemesterSettingsDecision = true;
                    item.Warnings.Add(Issue("Import.SemesterSettingsDiffer"));
                }
            }
            else if (existingByName is null)
            {
                item.Status = ImportPreviewStatus.Added;
            }
            else
            {
                item.Status = SemesterSettingsEqual(existingByName, semester) ? ImportPreviewStatus.Skipped : ImportPreviewStatus.Warning;
                item.RequiresSemesterSettingsDecision = item.Status == ImportPreviewStatus.Warning;
                if (item.RequiresSemesterSettingsDecision)
                    item.Warnings.Add(Issue("Import.SemesterSettingsDiffer"));
            }
            preview.Items.Add(item);
        }

        foreach (var label in package.Labels.OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            label.Name = TextRules.NormalizeIdentityText(label.Name);
            var item = new ImportPreviewItem
            {
                Kind = "label",
                DisplayName = label.Name,
                Label = label
            };

            if (string.IsNullOrWhiteSpace(label.Name))
            {
                item.Status = ImportPreviewStatus.NotImportable;
                item.Errors.Add(Issue("LabelNameRequired"));
            }
            else if (HasLocalLabelKindConflict(document, label.Name, label.Kind))
            {
                item.Status = ImportPreviewStatus.NotImportable;
                item.Errors.Add(Issue("LabelNameDuplicate", label.Name));
            }
            else if (document.Labels.Any(x => x.Kind == label.Kind && TextRules.IsSameLabel(x.Name, label.Name)))
            {
                item.Status = ImportPreviewStatus.Updated;
            }
            else
            {
                item.Status = ImportPreviewStatus.Added;
            }

            preview.Items.Add(item);
        }

        foreach (var course in package.Courses)
        {
            var targetSemester = ResolveImportedSemester(document, package.Semesters, course.SemesterId);
            var semesterConflict = HasForcedSemesterMergeConflict(document, package.Semesters, course.SemesterId);
            var item = new ImportPreviewItem
            {
                Kind = "course",
                DisplayName = course.CourseName,
                SemesterName = targetSemester?.SemesterName ?? course.SemesterId,
                TargetSemesterId = targetSemester?.SemesterId ?? course.SemesterId,
                Course = course
            };

            if (targetSemester is null)
            {
                item.Status = ImportPreviewStatus.NotImportable;
                item.Errors.Add(Issue("Import.CourseMissingSemester"));
                preview.Items.Add(item);
                continue;
            }

            course.SemesterId = targetSemester.SemesterId;
            CourseIdentityService.AssignOfferingId(course);
            var validation = CourseValidator.Validate(
                course,
                targetSemester,
                importMode: true,
                allowUnscheduled: true);
            AddLocalLabelKindConflicts(document, course, validation);
            item.Errors.AddRange(validation.Errors);
            item.Warnings.AddRange(validation.Warnings);
            item.RequiresForceImport = validation.RequiresForce;
            if (validation.Errors.Count > 0)
                item.Status = ImportPreviewStatus.NotImportable;
            else if (semesterConflict)
            {
                item.Status = ImportPreviewStatus.Conflict;
                item.CanApplyWithForcedSemesterMerge = true;
                item.Warnings.Add(Issue("Import.CourseSemesterNameConflict"));
            }
            else if (document.CourseLibrary.Any(x => x.OfferingId == course.OfferingId))
                item.Status = item.Warnings.Count > 0 ? ImportPreviewStatus.Warning : ImportPreviewStatus.Updated;
            else
                item.Status = item.Warnings.Count > 0 ? ImportPreviewStatus.Warning : ImportPreviewStatus.Added;
            preview.Items.Add(item);
        }

        ApplyTargetCatalogLimits(document, preview);
        return preview;
    }

    public static ImportPreview PreviewSelectionPlan(PlannerDocument document, string json)
        => PreviewSelectionPlanCore(document, json, validateJson: true);

    private static ImportPreview PreviewSelectionPlanCore(
        PlannerDocument document,
        string json,
        bool validateJson)
    {
        if (ExceedsImportLimits(json))
            return NotImportablePreview("Import.FileTooLarge", PlannerSchemas.SelectionPlanKind);

        SelectionPlanPackage package;
        if (validateJson)
        {
            try
            {
                JsonInputGuard.Validate(json);
            }
            catch (JsonException)
            {
                return NotImportablePreview("Import.InvalidJson", PlannerSchemas.SelectionPlanKind);
            }
            catch (InvalidDataException)
            {
                return NotImportablePreview("Import.InvalidJson", PlannerSchemas.SelectionPlanKind);
            }
        }
        try
        {
            var dto = JsonSerializer.Deserialize<SelectionPlanPackageDto>(json, ExchangeJsonOptions)
                      ?? throw new InvalidDataException("Invalid selection plan JSON.");
            package = ExchangePackageMapper.ToDomain(dto);
        }
        catch (JsonException)
        {
            return NotImportablePreview("Import.InvalidJson", PlannerSchemas.SelectionPlanKind);
        }
        catch (InvalidDataException)
        {
            return NotImportablePreview("Import.InvalidJson", PlannerSchemas.SelectionPlanKind);
        }

        return PreviewSelectionPlanPackage(document, package);
    }

    private static ImportPreview PreviewSelectionPlanPackage(
        PlannerDocument document,
        SelectionPlanPackage package)
    {
        if (!IsSupportedEnvelope(package.Kind, package.SchemaVersion, PlannerSchemas.SelectionPlanKind))
            return NotImportablePreview("Import.UnsupportedSchemaVersion", package.Kind);
        var packageIssue = ValidateSelectionPlanPackage(package);
        if (packageIssue is not null)
            return NotImportablePreview(packageIssue, package.Kind);

        var preview = new ImportPreview { Kind = package.Kind, SchemaVersion = package.SchemaVersion };

        var existingSemester = document.Semesters.FirstOrDefault(x =>
            TextRules.IsSameIdentityText(x.SemesterName, package.Semester.SemesterName));
        var existingById = document.Semesters.FirstOrDefault(x => x.SemesterId == package.Semester.SemesterId);
        var targetSemester = existingSemester ?? existingById ?? package.Semester;
        var idNameConflict = existingById is not null &&
                             !TextRules.IsSameIdentityText(existingById.SemesterName, package.Semester.SemesterName);
        var validation = SemesterRules.ValidateSemester(package.Semester, Enumerable.Empty<Semester>());
        package.Plan.SemesterId = targetSemester.SemesterId;

        var semesterItem = new ImportPreviewItem
        {
            Kind = "semester",
            DisplayName = targetSemester.SemesterName,
            SemesterName = targetSemester.SemesterName,
            TargetSemesterId = targetSemester.SemesterId,
            Semester = package.Semester,
            Status = existingSemester is null && existingById is null ? ImportPreviewStatus.Added : ImportPreviewStatus.Skipped
        };
        semesterItem.Errors.AddRange(validation.Errors);
        if (validation.Errors.Count > 0)
        {
            semesterItem.Status = ImportPreviewStatus.NotImportable;
        }
        else if (idNameConflict)
        {
            semesterItem.Status = ImportPreviewStatus.Conflict;
            semesterItem.CanApplyWithForcedSemesterMerge = true;
            semesterItem.Warnings.Add(Issue("Import.SemesterIdentityConflict", existingById!.SemesterName));
            if (!SemesterSettingsEqual(existingById, package.Semester))
            {
                semesterItem.RequiresSemesterSettingsDecision = true;
                semesterItem.Warnings.Add(Issue("Import.SemesterSettingsDiffer"));
            }
        }
        else if ((existingSemester ?? existingById) is { } existingTarget && !SemesterSettingsEqual(existingTarget, package.Semester))
        {
            semesterItem.Status = ImportPreviewStatus.Warning;
            semesterItem.RequiresSemesterSettingsDecision = true;
            semesterItem.Warnings.Add(Issue("Import.SemesterSettingsDiffer"));
        }
        preview.Items.Add(semesterItem);

        foreach (var label in package.Labels
                     .OrderBy(x => x.Kind)
                     .ThenBy(x => x.DisplayOrder)
                     .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            label.Name = TextRules.NormalizeIdentityText(label.Name);
            var labelItem = new ImportPreviewItem
            {
                Kind = "label",
                DisplayName = label.Name,
                Label = label
            };

            if (string.IsNullOrWhiteSpace(label.Name))
            {
                labelItem.Status = ImportPreviewStatus.NotImportable;
                labelItem.Errors.Add(Issue("LabelNameRequired"));
            }
            else if (HasLocalLabelKindConflict(document, label.Name, label.Kind))
            {
                labelItem.Status = ImportPreviewStatus.NotImportable;
                labelItem.Errors.Add(Issue("LabelNameDuplicate", label.Name));
            }
            else if (document.Labels.Any(x => x.Kind == label.Kind && TextRules.IsSameLabel(x.Name, label.Name)))
            {
                // A plan import links to the local course library. Existing local label
                // metadata remains authoritative just like an existing local course.
                labelItem.Status = ImportPreviewStatus.Skipped;
            }
            else
            {
                labelItem.Status = ImportPreviewStatus.Added;
            }

            preview.Items.Add(labelItem);
        }

        var planNameExists = document.Plans.Any(x => x.SemesterId == targetSemester.SemesterId &&
                                                     TextRules.IsSameIdentityText(x.PlanName, package.Plan.PlanName));
        var planIdentityExists = document.Plans.Any(x =>
            string.Equals(x.PlanId, package.Plan.PlanId, StringComparison.Ordinal));
        var planStatus = validation.Errors.Count > 0
            ? ImportPreviewStatus.NotImportable
            : planNameExists || planIdentityExists || idNameConflict
                ? ImportPreviewStatus.Conflict
                : ImportPreviewStatus.Added;
        var planWarnings = new List<ValidationIssue>();
        if (planNameExists || planIdentityExists)
            planWarnings.Add(Issue("Import.PlanNameExists"));
        if (idNameConflict)
            planWarnings.Add(Issue("Import.PlanSemesterNameConflict"));
        preview.Items.Add(new ImportPreviewItem
        {
            Kind = "plan",
            DisplayName = package.Plan.PlanName,
            SemesterName = targetSemester.SemesterName,
            TargetSemesterId = targetSemester.SemesterId,
            Plan = package.Plan,
            Status = planStatus,
            Warnings = planWarnings,
            CanApplyWithForcedSemesterMerge = idNameConflict && !planNameExists && !planIdentityExists && validation.Errors.Count == 0
        });

        var originalToFinalCourseIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedCourses = new List<CourseOffering>();
        foreach (var course in package.Courses)
        {
            var originalCourseId = course.OfferingId;
            course.SemesterId = targetSemester.SemesterId;
            CourseIdentityService.AssignOfferingId(course);
            originalToFinalCourseIds.Add(originalCourseId, course.OfferingId);
            normalizedCourses.Add(course);
        }

        foreach (var snapshot in package.Plan.Snapshots)
        {
            if (originalToFinalCourseIds.TryGetValue(snapshot.CourseOfferingId, out var finalCourseId))
                snapshot.CourseOfferingId = finalCourseId;
        }

        var effectivePlanCourses = document.CourseLibrary
            .Concat(normalizedCourses);
        var planMeetingValidation = PlanRules.ValidateMeetingRows(
            package.Plan,
            effectivePlanCourses);
        if (!planMeetingValidation.IsValid)
        {
            var planItem = preview.Items.First(item => item.Kind == "plan");
            planItem.Status = ImportPreviewStatus.NotImportable;
            planItem.CanApplyWithForcedSemesterMerge = false;
            planItem.Errors.AddRange(planMeetingValidation.Errors);
        }

        foreach (var course in normalizedCourses)
        {
            var existingCourse = document.CourseLibrary.FirstOrDefault(x => x.OfferingId == course.OfferingId);
            var courseValidation = validation.Errors.Count == 0 && existingCourse is null
                ? CourseValidator.Validate(
                    course,
                    targetSemester,
                    importMode: true,
                    allowUnscheduled: true)
                : new ValidationResult();
            if (existingCourse is null)
                AddLocalLabelKindConflicts(document, course, courseValidation);
            var courseItem = new ImportPreviewItem
            {
                Kind = "planCourse",
                DisplayName = course.CourseName,
                SemesterName = targetSemester.SemesterName,
                TargetSemesterId = targetSemester.SemesterId,
                Course = course,
                Status = existingCourse is not null
                    ? ImportPreviewStatus.Skipped
                    : ImportPreviewStatus.Added,
                RequiresForceImport = courseValidation.RequiresForce,
                RequiresCourseLibrarySync = existingCourse is null && package.Plan.Snapshots.Any(snapshot =>
                    string.Equals(snapshot.CourseOfferingId, course.OfferingId, StringComparison.Ordinal))
            };
            courseItem.Errors.AddRange(courseValidation.Errors);
            courseItem.Warnings.AddRange(courseValidation.Warnings);
            if (validation.Errors.Count > 0)
            {
                courseItem.Status = ImportPreviewStatus.NotImportable;
                courseItem.Errors.Add(Issue("Import.PlanSemesterInvalid"));
            }
            else if (courseValidation.Errors.Count > 0)
                courseItem.Status = ImportPreviewStatus.NotImportable;
            else if (existingCourse is null && courseValidation.Warnings.Count > 0)
                courseItem.Status = ImportPreviewStatus.Warning;
            preview.Items.Add(courseItem);
        }

        var packageCourseIds = package.Courses.Select(x => x.OfferingId).ToHashSet(StringComparer.Ordinal);
        var missingCourseReferences = package.Plan.Snapshots
            .Where(x => string.IsNullOrWhiteSpace(x.CourseOfferingId) || !packageCourseIds.Contains(x.CourseOfferingId))
            .Select(x => string.IsNullOrWhiteSpace(x.CourseOfferingId) ? "(empty)" : x.CourseOfferingId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missingCourseReferences.Count > 0)
        {
            var planItem = preview.Items.First(x => x.Kind == "plan");
            planItem.Status = ImportPreviewStatus.NotImportable;
            planItem.Errors.Add(Issue("Import.PlanCoursePackageIncomplete", string.Join(", ", missingCourseReferences)));
        }

        var blockedRequiredCourses = preview.Items
            .Where(item => item.Kind == "planCourse" &&
                           item.Course is not null &&
                           item.Status == ImportPreviewStatus.NotImportable &&
                           package.Plan.Snapshots.Any(snapshot =>
                               string.Equals(snapshot.CourseOfferingId, item.Course.OfferingId, StringComparison.Ordinal)) &&
                           document.CourseLibrary.All(local =>
                               !string.Equals(local.OfferingId, item.Course.OfferingId, StringComparison.Ordinal)))
            .Select(item => item.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (blockedRequiredCourses.Count > 0)
        {
            var planItem = preview.Items.First(item => item.Kind == "plan");
            planItem.Status = ImportPreviewStatus.NotImportable;
            planItem.CanApplyWithForcedSemesterMerge = false;
            planItem.Errors.Add(Issue("Import.PlanCourseNotImportable", string.Join(", ", blockedRequiredCourses)));
        }

        ApplyTargetCatalogLimits(document, preview);
        return preview;
    }

    public static ImportApplyResult ApplyImport(
        PlannerDocument document,
        ImportPreview preview,
        bool updateSemesterSettings = false)
    {
        return ApplyImport(document, preview, new ImportApplyOptions
        {
            UpdateExistingSemesterSettings = updateSemesterSettings
        });
    }

    public static ImportApplyResult ApplyImport(
        PlannerDocument document,
        ImportPreview preview,
        ImportApplyOptions? options)
    {
        options ??= new ImportApplyOptions();
        var refreshedPreview = RefreshPreview(document, preview);
        if (refreshedPreview is null)
            return ImportApplyResult.NotApplied;
        preview = refreshedPreview;
        if (string.Equals(preview.Kind, PlannerSchemas.SelectionPlanKind, StringComparison.Ordinal))
            return ApplySelectionPlanImport(document, preview, options);

        var before = JsonSerializer.Serialize(document, JsonDefaults.Options);
        var staged = JsonDefaults.Clone(document);
        foreach (var item in preview.Items)
        {
            if (!CanApplyItem(item, options))
                continue;

            if (item.Kind == "semester")
            {
                ApplySemesterItem(staged, item, options);
                continue;
            }

            if (item.Kind == "label" && item.Label is not null)
            {
                UpsertLabel(staged.Labels, item.Label);
            }
            else if (item.Kind == "course" && item.Course is not null)
            {
                UpsertCourse(staged.CourseLibrary, item.Course);
            }
        }

        if (!HasConsistentDependencies(staged) || !HasImportSafeDocumentLimits(staged))
            return ImportApplyResult.NotApplied;

        var after = JsonSerializer.Serialize(staged, JsonDefaults.Options);
        if (string.Equals(before, after, StringComparison.Ordinal))
            return ImportApplyResult.NotApplied;

        CommitCourseLibraryImport(document, staged);
        return ImportApplyResult.Success;
    }

    private static ImportPreview? RefreshPreview(PlannerDocument document, ImportPreview preview)
    {
        if (!string.Equals(preview.SchemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
            return null;
        try
        {
            if (string.Equals(preview.Kind, PlannerSchemas.CourseLibraryKind, StringComparison.Ordinal))
            {
                if (preview.Items.Any(item => item.Kind is not ("semester" or "label" or "course")) ||
                    preview.Items.Any(item =>
                        (item.Kind == "semester" && item.Semester is null) ||
                        (item.Kind == "label" && item.Label is null) ||
                        (item.Kind == "course" && item.Course is null)))
                {
                    return null;
                }

                var package = new CourseLibraryPackage
                {
                    Semesters = preview.Items
                        .Where(item => item.Kind == "semester")
                        .Select(item =>
                        {
                            var semester = JsonDefaults.Clone(item.Semester!);
                            semester.SemesterId = item.TargetSemesterId ?? semester.SemesterId;
                            return semester;
                        })
                        .ToList(),
                    Labels = preview.Items
                        .Where(item => item.Kind == "label")
                        .Select(item => JsonDefaults.Clone(item.Label!))
                        .ToList(),
                    Courses = preview.Items
                        .Where(item => item.Kind == "course")
                        .Select(item => JsonDefaults.Clone(item.Course!))
                        .ToList()
                };
                return PreviewCourseLibraryPackage(document, package);
            }

            if (!string.Equals(preview.Kind, PlannerSchemas.SelectionPlanKind, StringComparison.Ordinal) ||
                preview.Items.Any(item => item.Kind is not ("semester" or "label" or "plan" or "planCourse")))
            {
                return null;
            }

            var semesterItems = preview.Items.Where(item => item.Kind == "semester").Take(2).ToList();
            var planItems = preview.Items.Where(item => item.Kind == "plan").Take(2).ToList();
            if (semesterItems.Count != 1 || planItems.Count != 1)
                return null;
            var semesterItem = semesterItems[0];
            var planItem = planItems[0];
            if (semesterItem.Semester is null || planItem.Plan is null ||
                preview.Items.Any(item => item.Kind == "label" && item.Label is null) ||
                preview.Items.Any(item => item.Kind == "planCourse" && item.Course is null))
            {
                return null;
            }

            var plan = JsonDefaults.Clone(planItem.Plan);
            var semester = JsonDefaults.Clone(semesterItem.Semester);
            semester.SemesterId = plan.SemesterId;
            var selectionPackage = new SelectionPlanPackage
            {
                Semester = semester,
                Labels = preview.Items
                    .Where(item => item.Kind == "label")
                    .Select(item => JsonDefaults.Clone(item.Label!))
                    .ToList(),
                Courses = preview.Items
                    .Where(item => item.Kind == "planCourse")
                    .Select(item => JsonDefaults.Clone(item.Course!))
                    .ToList(),
                Plan = plan
            };
            return PreviewSelectionPlanPackage(document, selectionPackage);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static ImportApplyResult ApplySelectionPlanImport(
        PlannerDocument document,
        ImportPreview preview,
        ImportApplyOptions options)
    {
        var planItem = preview.Items.SingleOrDefault(x => x.Kind == "plan");
        if (planItem?.Plan is null || !CanApplyItem(planItem, options))
            return ImportApplyResult.NotApplied;

        var semesterItem = preview.Items.SingleOrDefault(x => x.Kind == "semester");
        if (semesterItem is null || semesterItem.Status == ImportPreviewStatus.NotImportable)
            return ImportApplyResult.NotApplied;
        if (semesterItem.Status == ImportPreviewStatus.Conflict && !CanApplyItem(semesterItem, options))
            return ImportApplyResult.NotApplied;

        var requiredCourseIds = planItem.Plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requiredCourseIds.Any(string.IsNullOrWhiteSpace))
            return ImportApplyResult.NotApplied;

        var courseItems = preview.Items
            .Where(x => x.Kind == "planCourse" && x.Course is not null)
            .GroupBy(x => x.Course!.OfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var courseId in requiredCourseIds)
        {
            if (document.CourseLibrary.Any(course => course.OfferingId == courseId))
                continue;
            if (!options.SynchronizeMissingPlanCourses ||
                !courseItems.TryGetValue(courseId, out var courseItem) ||
                !courseItem.RequiresCourseLibrarySync ||
                !CanApplyItem(courseItem, options))
            {
                return ImportApplyResult.NotApplied;
            }
        }

        var staged = JsonDefaults.Clone(document);
        ApplySemesterItem(staged, semesterItem, options);

        foreach (var labelItem in preview.Items.Where(x =>
                     x.Kind == "label" &&
                     x.Label is not null &&
                     x.Status == ImportPreviewStatus.Added &&
                     CanApplyItem(x, options)))
        {
            UpsertLabel(staged.Labels, labelItem.Label!);
        }

        foreach (var courseId in requiredCourseIds)
        {
            if (staged.CourseLibrary.Any(course => course.OfferingId == courseId))
                continue;
            UpsertCourse(staged.CourseLibrary, courseItems[courseId].Course!);
        }

        if (requiredCourseIds.Any(courseId => staged.CourseLibrary.All(course => course.OfferingId != courseId)))
            return ImportApplyResult.NotApplied;

        var plan = JsonDefaults.Clone(planItem.Plan);
        if (!PlanRules.ValidateMeetingRows(plan, staged.CourseLibrary).IsValid)
            return ImportApplyResult.NotApplied;
        staged.Plans.Add(plan);
        if (!staged.Settings.OpenPlanIds.Contains(plan.PlanId))
            staged.Settings.OpenPlanIds.Add(plan.PlanId);
        staged.Settings.CurrentPlanId = plan.PlanId;
        staged.Settings.CurrentSemesterId = plan.SemesterId;

        if (!HasConsistentDependencies(staged) || !HasImportSafeDocumentLimits(staged))
            return ImportApplyResult.NotApplied;

        ReplaceDocument(document, staged);
        return ImportApplyResult.Success;
    }

    private static void ApplySemesterItem(
        PlannerDocument document,
        ImportPreviewItem item,
        ImportApplyOptions options)
    {
        if (item.Semester is null)
            return;

        var targetSemesterId = item.TargetSemesterId ?? item.Semester.SemesterId;
        var existingByName = document.Semesters.FirstOrDefault(x =>
            string.Equals(x.SemesterName, item.Semester.SemesterName, StringComparison.OrdinalIgnoreCase));
        var existingById = document.Semesters.FirstOrDefault(x => x.SemesterId == item.Semester.SemesterId);
        var existingTarget = document.Semesters.FirstOrDefault(x => x.SemesterId == targetSemesterId)
                             ?? existingByName
                             ?? existingById;
        if (existingTarget is null)
        {
            document.Semesters.Add(JsonDefaults.Clone(item.Semester));
        }
        else if (options.UpdateExistingSemesterSettings || item.Status == ImportPreviewStatus.Added)
        {
            existingTarget.StartDate = item.Semester.StartDate;
            existingTarget.EndDate = item.Semester.EndDate;
            existingTarget.WeekCount = item.Semester.WeekCount;
            existingTarget.WeekStartDay = item.Semester.WeekStartDay;
            existingTarget.PeriodSchedule = JsonDefaults.Clone(item.Semester.PeriodSchedule);
        }
    }

    private static void ReplaceDocument(PlannerDocument target, PlannerDocument source)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.Semesters = source.Semesters;
        target.Labels = source.Labels;
        target.CourseLibrary = source.CourseLibrary;
        target.Plans = source.Plans;
        target.Settings = source.Settings;
    }

    private static void CommitCourseLibraryImport(PlannerDocument target, PlannerDocument source)
    {
        var committedSemesters = new List<Semester>(source.Semesters.Count);
        foreach (var sourceSemester in source.Semesters)
        {
            var targetSemester = target.Semesters.FirstOrDefault(semester =>
                string.Equals(semester.SemesterId, sourceSemester.SemesterId, StringComparison.Ordinal));
            if (targetSemester is null)
            {
                committedSemesters.Add(JsonDefaults.Clone(sourceSemester));
                continue;
            }

            targetSemester.SemesterName = sourceSemester.SemesterName;
            targetSemester.StartDate = sourceSemester.StartDate;
            targetSemester.EndDate = sourceSemester.EndDate;
            targetSemester.WeekCount = sourceSemester.WeekCount;
            targetSemester.WeekStartDay = sourceSemester.WeekStartDay;
            targetSemester.DisplayOrder = sourceSemester.DisplayOrder;
            targetSemester.PeriodSchedule = JsonDefaults.Clone(sourceSemester.PeriodSchedule);
            committedSemesters.Add(targetSemester);
        }

        target.SchemaVersion = source.SchemaVersion;
        target.Semesters.Clear();
        target.Semesters.AddRange(committedSemesters);
        target.Labels = source.Labels;
        target.CourseLibrary = source.CourseLibrary;
        target.Plans = source.Plans;
        target.Settings = source.Settings;
    }

    private static bool HasConsistentDependencies(PlannerDocument document)
    {
        var semesterIds = document.Semesters
            .Select(semester => semester.SemesterId)
            .ToHashSet(StringComparer.Ordinal);
        if (document.CourseLibrary.Any(course =>
                string.IsNullOrWhiteSpace(course.SemesterId) ||
                !semesterIds.Contains(course.SemesterId)))
        {
            return false;
        }

        var labelKinds = new Dictionary<string, LabelKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in document.Labels)
        {
            if (!Enum.IsDefined(label.Kind))
                continue;
            var identity = TextRules.NormalizeIdentityText(label.Name);
            if (identity.Length > 0)
                labelKinds.TryAdd(identity, label.Kind);
        }
        foreach (var course in document.CourseLibrary)
        {
            if (course.Labels.Any(label => !HasLabelReference(labelKinds, label, LabelKind.Ordinary)) ||
                !HasLabelReference(labelKinds, course.CourseGroupType, LabelKind.CourseGroupType) ||
                !HasLabelReference(labelKinds, course.StudyType, LabelKind.StudyType))
            {
                return false;
            }
        }

        var coursesById = document.CourseLibrary
            .Where(course => !string.IsNullOrWhiteSpace(course.OfferingId))
            .GroupBy(course => course.OfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var plan in document.Plans)
        {
            if (string.IsNullOrWhiteSpace(plan.SemesterId) || !semesterIds.Contains(plan.SemesterId))
                return false;

            foreach (var snapshot in plan.Snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.CourseOfferingId) ||
                    !coursesById.TryGetValue(snapshot.CourseOfferingId, out var course) ||
                    !string.Equals(course.SemesterId, plan.SemesterId, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasLabelReference(
        IReadOnlyDictionary<string, LabelKind> labelKinds,
        string? name,
        LabelKind expectedKind)
    {
        var identity = TextRules.NormalizeIdentityText(name);
        return identity.Length == 0 ||
               labelKinds.TryGetValue(identity, out var actualKind) && actualKind == expectedKind;
    }

    private static void ApplyTargetCatalogLimits(PlannerDocument document, ImportPreview preview)
    {
        if (!string.Equals(preview.Kind, PlannerSchemas.SelectionPlanKind, StringComparison.Ordinal))
        {
            ApplyCourseLibraryTargetLimits(document, preview);
            return;
        }

        MarkExcessAdditions(
            preview.Items.Where(item => item.Kind == "semester" && item.Status == ImportPreviewStatus.Added),
            document.Semesters.Count,
            PlannerDataLimits.MaxSemesters,
            "SemesterCatalogMaximum");
        MarkExcessAdditions(
            preview.Items.Where(item => item.Kind == "label" && item.Status == ImportPreviewStatus.Added),
            document.Labels.Count,
            PlannerDataLimits.MaxLabels,
            "LabelCatalogMaximum");
        MarkExcessAdditions(
            preview.Items.Where(item =>
                (item.Kind == "course" || item.Kind == "planCourse") &&
                item.Course is not null &&
                item.Status is ImportPreviewStatus.Added or ImportPreviewStatus.Warning &&
                document.CourseLibrary.All(course =>
                    !string.Equals(course.OfferingId, item.Course.OfferingId, StringComparison.Ordinal))),
            document.CourseLibrary.Count,
            PlannerDataLimits.MaxCourses,
            "CourseCatalogMaximum");
        MarkExcessAdditions(
            preview.Items.Where(item => item.Kind == "plan" && item.Status == ImportPreviewStatus.Added),
            document.Plans.Count,
            PlannerDataLimits.MaxPlans,
            "PlanCatalogMaximum");

        ApplySelectionPlanTargetLimits(document, preview);
    }

    private static void ApplyCourseLibraryTargetLimits(PlannerDocument document, ImportPreview preview)
    {
        var staged = JsonDefaults.Clone(document);
        var textCharacters = PlannerDocumentTextCapacity.Count(staged);
        var totalLabelReferences = CountLabelReferences(staged);
        var options = CapacityPreviewOptions();

        foreach (var item in preview.Items)
        {
            if (!CanApplyItem(item, options))
                continue;

            var issues = new List<ValidationIssue>();
            long replacedText = 0;
            long replacementText = 0;
            long replacedLabelReferences = 0;
            long replacementLabelReferences = 0;

            if (item.Kind == "semester" && item.Semester is not null)
            {
                var existing = ResolveSemesterTarget(staged, item);
                if (existing is null)
                {
                    issues.AddRange(PlannerCapacityRules.ValidateCanAddSemester(staged.Semesters.Count).Errors);
                    replacementText = PlannerDocumentTextCapacity.Count(item.Semester);
                }
                else
                {
                    replacedText = PlannerDocumentTextCapacity.Count(existing);
                    replacementText = replacedText;
                }
            }
            else if (item.Kind == "label" && item.Label is not null)
            {
                var existing = staged.Labels.FirstOrDefault(label =>
                    label.Kind == item.Label.Kind && TextRules.IsSameLabel(label.Name, item.Label.Name));
                if (existing is null)
                    issues.AddRange(PlannerCapacityRules.ValidateCanAddLabel(staged.Labels.Count).Errors);
                replacedText = existing is null ? 0 : PlannerDocumentTextCapacity.Count(existing);
                replacementText = PlannerDocumentTextCapacity.Count(item.Label);
            }
            else if (item.Kind == "course" && item.Course is not null)
            {
                var existing = staged.CourseLibrary.FirstOrDefault(course =>
                    string.Equals(course.OfferingId, item.Course.OfferingId, StringComparison.Ordinal));
                if (existing is null)
                    issues.AddRange(PlannerCapacityRules.ValidateCanAddCourse(staged.CourseLibrary.Count).Errors);
                issues.AddRange(LabelRules.ValidateCourseReferences(item.Course, staged.Labels).Errors);
                replacedText = existing is null ? 0 : PlannerDocumentTextCapacity.Count(existing);
                replacementText = PlannerDocumentTextCapacity.Count(item.Course);
                replacedLabelReferences = existing is null ? 0 : CountLabelReferences(existing);
                replacementLabelReferences = CountLabelReferences(item.Course);
                if (totalLabelReferences - replacedLabelReferences >
                    PlannerDataLimits.MaxTotalLabelReferences - replacementLabelReferences)
                {
                    issues.Add(Issue(
                        "TotalLabelReferencesMaximum",
                        PlannerDataLimits.MaxTotalLabelReferences.ToString()));
                }
            }
            else
            {
                continue;
            }

            issues.AddRange(PlannerDocumentTextCapacity.ValidateChange(
                textCharacters,
                replacedText,
                replacementText).Errors);
            if (issues.Count > 0)
            {
                MarkNotImportable(item, issues);
                continue;
            }

            if (item.Kind == "semester")
                ApplySemesterItem(staged, item, options);
            else if (item.Kind == "label")
                UpsertLabel(staged.Labels, item.Label!);
            else
                UpsertCourse(staged.CourseLibrary, item.Course!);
            textCharacters = textCharacters - replacedText + replacementText;
            totalLabelReferences = totalLabelReferences - replacedLabelReferences + replacementLabelReferences;
        }
    }

    private static void ApplySelectionPlanTargetLimits(PlannerDocument document, ImportPreview preview)
    {
        var planItem = preview.Items.SingleOrDefault(item => item.Kind == "plan");
        if (planItem?.Plan is null || planItem.Status == ImportPreviewStatus.NotImportable)
            return;

        var options = CapacityPreviewOptions();
        if (!CanApplyItem(planItem, options))
            return;

        var semesterItem = preview.Items.SingleOrDefault(item => item.Kind == "semester");
        if (semesterItem is null ||
            (!CanApplyItem(semesterItem, options) && semesterItem.Status != ImportPreviewStatus.Skipped))
        {
            MarkPlanCapacityDependency(preview, planItem);
            return;
        }

        var staged = JsonDefaults.Clone(document);
        ApplySemesterItem(staged, semesterItem, options);
        foreach (var labelItem in preview.Items.Where(item =>
                     item.Kind == "label" &&
                     item.Label is not null &&
                     item.Status == ImportPreviewStatus.Added &&
                     CanApplyItem(item, options)))
        {
            UpsertLabel(staged.Labels, labelItem.Label!);
        }

        var requiredCourseIds = planItem.Plan.Snapshots
            .Select(snapshot => snapshot.CourseOfferingId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var courseItems = preview.Items
            .Where(item => item.Kind == "planCourse" && item.Course is not null)
            .GroupBy(item => item.Course!.OfferingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var courseId in requiredCourseIds)
        {
            if (staged.CourseLibrary.Any(course =>
                    string.Equals(course.OfferingId, courseId, StringComparison.Ordinal)))
            {
                continue;
            }
            if (!courseItems.TryGetValue(courseId, out var courseItem) || !CanApplyItem(courseItem, options))
            {
                MarkPlanCapacityDependency(preview, planItem);
                return;
            }
            UpsertCourse(staged.CourseLibrary, courseItem.Course!);
        }

        if (!HasConsistentDependencies(staged))
        {
            MarkPlanCapacityDependency(preview, planItem);
            return;
        }

        staged.Plans.Add(JsonDefaults.Clone(planItem.Plan));
        if (!staged.Settings.OpenPlanIds.Contains(planItem.Plan.PlanId, StringComparer.Ordinal))
            staged.Settings.OpenPlanIds.Add(planItem.Plan.PlanId);
        staged.Settings.CurrentPlanId = planItem.Plan.PlanId;
        staged.Settings.CurrentSemesterId = planItem.Plan.SemesterId;

        var existingIssueCodes = GetImportCapacityIssues(document)
            .Select(issue => issue.Code)
            .ToHashSet(StringComparer.Ordinal);
        var introducedIssues = GetImportCapacityIssues(staged)
            .Where(issue => !existingIssueCodes.Contains(issue.Code))
            .ToList();
        if (introducedIssues.Count > 0)
            MarkNotImportable(planItem, introducedIssues);
    }

    private static Semester? ResolveSemesterTarget(PlannerDocument document, ImportPreviewItem item)
    {
        var targetSemesterId = item.TargetSemesterId ?? item.Semester?.SemesterId;
        return document.Semesters.FirstOrDefault(semester =>
                   string.Equals(semester.SemesterId, targetSemesterId, StringComparison.Ordinal))
               ?? document.Semesters.FirstOrDefault(semester =>
                   item.Semester is not null &&
                   TextRules.IsSameIdentityText(semester.SemesterName, item.Semester.SemesterName))
               ?? document.Semesters.FirstOrDefault(semester =>
                   item.Semester is not null &&
                   string.Equals(semester.SemesterId, item.Semester.SemesterId, StringComparison.Ordinal));
    }

    private static ImportApplyOptions CapacityPreviewOptions() => new()
    {
        UpdateExistingSemesterSettings = true,
        ForceSemesterMergeConflicts = true,
        ForceOutOfRangeCourses = true,
        SynchronizeMissingPlanCourses = true
    };

    private static void MarkPlanCapacityDependency(ImportPreview preview, ImportPreviewItem planItem)
    {
        var dependencyIssues = preview.Items
            .Where(item => item.Kind != "plan")
            .SelectMany(item => item.Errors)
            .Where(issue => IsCapacityIssue(issue.Code))
            .GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (dependencyIssues.Count == 0)
            return;
        MarkNotImportable(planItem, dependencyIssues);
    }

    private static bool IsCapacityIssue(string code) => code is
        "SemesterCatalogMaximum" or
        "LabelCatalogMaximum" or
        "CourseCatalogMaximum" or
        "PlanCatalogMaximum" or
        "PlanSnapshotsMaximum" or
        "TotalSnapshotsMaximum" or
        "CourseLabelsMaximum" or
        "TotalLabelReferencesMaximum" or
        "MeetingTimesMaximum" or
        "PeriodScheduleMaximum" or
        "AggregateTextMaximum" or
        "OpenPlanTabsMaximum" or
        "PlanMeetingRowsMaximum";

    private static void MarkNotImportable(
        ImportPreviewItem item,
        IEnumerable<ValidationIssue> issues)
    {
        item.Status = ImportPreviewStatus.NotImportable;
        item.CanApplyWithForcedSemesterMerge = false;
        foreach (var issue in issues)
        {
            if (item.Errors.All(existing => existing.Code != issue.Code))
                item.Errors.Add(issue);
        }
    }

    private static void MarkExcessAdditions(
        IEnumerable<ImportPreviewItem> additions,
        int existingCount,
        int maximum,
        string issueCode)
    {
        var remaining = Math.Max(0, maximum - existingCount);
        foreach (var item in additions.Skip(remaining))
        {
            item.Status = ImportPreviewStatus.NotImportable;
            item.CanApplyWithForcedSemesterMerge = false;
            item.Errors.Add(Issue(issueCode, maximum.ToString()));
        }
    }

    private static bool HasImportSafeDocumentLimits(PlannerDocument document)
    {
        return document.Semesters.Count >= 1 && GetImportCapacityIssues(document).Count == 0;
    }

    private static List<ValidationIssue> GetImportCapacityIssues(PlannerDocument document)
    {
        var issues = new List<ValidationIssue>();
        AddLimitIssue(issues, document.Semesters.Count, PlannerDataLimits.MaxSemesters, "SemesterCatalogMaximum");
        AddLimitIssue(issues, document.Labels.Count, PlannerDataLimits.MaxLabels, "LabelCatalogMaximum");
        AddLimitIssue(issues, document.CourseLibrary.Count, PlannerDataLimits.MaxCourses, "CourseCatalogMaximum");
        AddLimitIssue(issues, document.Plans.Count, PlannerDataLimits.MaxPlans, "PlanCatalogMaximum");
        AddLimitIssue(
            issues,
            document.Settings.OpenPlanIds.Count,
            PlanTabLimits.MaximumOpenPlans,
            "OpenPlanTabsMaximum");
        AddLimitIssue(
            issues,
            PlannerDocumentTextCapacity.Count(document),
            PlannerDataLimits.MaxAggregateTextCharacters,
            "AggregateTextMaximum");

        long totalSnapshots = 0;
        foreach (var plan in document.Plans)
        {
            AddLimitIssue(
                issues,
                plan.Snapshots.Count,
                PlannerDataLimits.MaxSnapshotsPerPlan,
                "PlanSnapshotsMaximum");
            foreach (var issue in PlanRules.ValidateMeetingRows(plan, document.CourseLibrary).Errors)
            {
                if (issues.All(existing => existing.Code != issue.Code))
                    issues.Add(issue);
            }
            totalSnapshots += plan.Snapshots.Count;
        }
        AddLimitIssue(
            issues,
            totalSnapshots,
            PlannerDataLimits.MaxTotalSnapshots,
            "TotalSnapshotsMaximum");

        long totalLabelReferences = 0;
        foreach (var semester in document.Semesters)
        {
            AddLimitIssue(
                issues,
                semester.PeriodSchedule.Count,
                PlannerDataLimits.MaxPeriodsPerSemester,
                "PeriodScheduleMaximum");
        }
        foreach (var course in document.CourseLibrary)
        {
            AddLimitIssue(
                issues,
                course.Labels.Count,
                PlannerDataLimits.MaxLabelsPerCourse,
                "CourseLabelsMaximum");
            AddLimitIssue(
                issues,
                course.MeetingTimes.Count,
                PlannerDataLimits.MaxMeetingsPerCourse,
                "MeetingTimesMaximum");
            totalLabelReferences += CountLabelReferences(course);
        }
        AddLimitIssue(
            issues,
            totalLabelReferences,
            PlannerDataLimits.MaxTotalLabelReferences,
            "TotalLabelReferencesMaximum");
        return issues;
    }

    private static void AddLimitIssue(
        List<ValidationIssue> issues,
        long actual,
        long maximum,
        string code)
    {
        if (actual > maximum && issues.All(issue => issue.Code != code))
            issues.Add(Issue(code, maximum.ToString()));
    }

    private static long CountLabelReferences(PlannerDocument document) =>
        document.CourseLibrary.Sum(CountLabelReferences);

    private static long CountLabelReferences(CourseOffering course) =>
        (long)course.Labels.Count +
        (string.IsNullOrWhiteSpace(course.CourseGroupType) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(course.StudyType) ? 0 : 1);

    public static string CreatePreviewReportJson(ImportPreview preview) =>
        JsonSerializer.Serialize(preview, JsonDefaults.Options);

    public static List<ImportPreviewItem> FilterPreviewItems(ImportPreview preview, ImportPreviewFilter filter)
    {
        var query = preview.Items.AsEnumerable();
        if (filter.Statuses.Count > 0)
            query = query.Where(x => filter.Statuses.Contains(x.Status));
        if (!string.IsNullOrWhiteSpace(filter.SemesterText))
            query = query.Where(x => Contains(x.SemesterName, filter.SemesterText));
        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var text = filter.SearchText;
            query = query.Where(x =>
                Contains(x.Kind, text) ||
                Contains(x.DisplayName, text) ||
                Contains(x.SemesterName, text) ||
                Contains(x.Course?.Teacher, text) ||
                Contains(x.Course?.Location, text) ||
                Contains(x.Course?.Notes, text) ||
                Contains(x.Plan?.PlanName, text) ||
                x.Warnings.Any(warning => Contains(warning, text)) ||
                x.Errors.Any(error => Contains(error, text)));
        }

        if (filter.OrdinaryLabels.Count > 0)
            query = query.Where(x => x.Course?.Labels.Any(label => filter.OrdinaryLabels.Contains(label)) == true);
        if (filter.CourseGroupTypes.Count > 0)
            query = query.Where(x => filter.CourseGroupTypes.Contains(x.Course?.CourseGroupType ?? ""));
        if (filter.StudyTypes.Count > 0)
            query = query.Where(x => filter.StudyTypes.Contains(x.Course?.StudyType ?? ""));

        return query.ToList();
    }

    private static bool CanApplyItem(ImportPreviewItem item, ImportApplyOptions options)
    {
        if (item.Status is ImportPreviewStatus.NotImportable or ImportPreviewStatus.Skipped)
            return false;
        if (item.RequiresForceImport && !options.ForceOutOfRangeCourses)
            return false;
        if (item.Status != ImportPreviewStatus.Conflict)
            return true;
        return options.ForceSemesterMergeConflicts && item.CanApplyWithForcedSemesterMerge;
    }

    private static bool IsSupportedEnvelope(string kind, string schemaVersion, string expectedKind) =>
        string.Equals(kind, expectedKind, StringComparison.Ordinal) &&
        string.Equals(schemaVersion, PlannerSchemas.Current, StringComparison.Ordinal);

    private static string? ValidateCourseLibraryPackage(CourseLibraryPackage package)
    {
        if (package.Semesters.Count > MaxSemestersPerPackage ||
            package.Labels.Count > MaxLabelsPerPackage ||
            package.Courses.Count > MaxCoursesPerPackage)
        {
            return "Import.PackageTooLarge";
        }

        if (package.Semesters.Any(SemesterHasOversizedFields) ||
            package.Labels.Any(LabelHasOversizedFields) ||
            package.Courses.Any(CourseHasOversizedFields) ||
            HasOversizedReferencedLabelSet(package.Courses))
        {
            return "Import.PackageTooLarge";
        }

        if (HasInvalidSharedSemantics(package.Semesters, package.Labels, package.Courses) ||
            HasDuplicateKey(package.Semesters, semester => semester.SemesterId) ||
            HasDuplicateKey(package.Semesters, semester => TextRules.NormalizeIdentityText(semester.SemesterName)) ||
            HasDuplicateKey(package.Courses, course => course.OfferingId) ||
            HasDuplicateGeneratedCourseIdentity(package.Courses))
        {
            return "Import.InvalidJson";
        }

        return null;
    }

    private static string? ValidateSelectionPlanPackage(SelectionPlanPackage package)
    {
        if (package.Labels.Count > MaxLabelsPerPackage ||
            package.Courses.Count > MaxCoursesPerPackage ||
            package.Plan.Snapshots.Count > MaxCoursesPerPackage ||
            SemesterHasOversizedFields(package.Semester) ||
            PlanHasOversizedFields(package.Plan) ||
            package.Labels.Any(LabelHasOversizedFields) ||
            package.Courses.Any(CourseHasOversizedFields) ||
            HasOversizedReferencedLabelSet(package.Courses))
        {
            return "Import.PackageTooLarge";
        }

        if (!PlanRules.ValidateMeetingRows(package.Plan, package.Courses).IsValid)
            return "PlanMeetingRowsMaximum";

        if (HasInvalidSharedSemantics(new[] { package.Semester }, package.Labels, package.Courses) ||
            !PlanRules.Validate(package.Plan, []).IsValid ||
            string.IsNullOrWhiteSpace(package.Plan.PlanId) ||
            string.IsNullOrWhiteSpace(package.Plan.PlanName) ||
            string.IsNullOrWhiteSpace(package.Plan.SemesterId) ||
            !string.Equals(package.Plan.SemesterId, package.Semester.SemesterId, StringComparison.Ordinal) ||
            package.Courses.Any(course =>
                !string.Equals(course.SemesterId, package.Semester.SemesterId, StringComparison.Ordinal)) ||
            HasDuplicateKey(package.Courses, course => course.OfferingId) ||
            HasDuplicateGeneratedCourseIdentity(package.Courses) ||
            package.Plan.Snapshots.Any(snapshot =>
                string.IsNullOrWhiteSpace(snapshot.SnapshotId) ||
                string.IsNullOrWhiteSpace(snapshot.CourseOfferingId)) ||
            HasDuplicateKey(package.Plan.Snapshots, snapshot => snapshot.SnapshotId) ||
            HasDuplicateKey(package.Plan.Snapshots, snapshot => snapshot.CourseOfferingId) ||
            HasInvalidRegistrationOrder(package.Plan))
        {
            return "Import.InvalidJson";
        }

        return null;
    }

    private static bool HasInvalidSharedSemantics(
        IEnumerable<Semester> semesters,
        IEnumerable<CourseLabel> labels,
        IEnumerable<CourseOffering> courses)
    {
        var semesterList = semesters.ToList();
        var labelList = labels.ToList();
        var courseList = courses.ToList();
        return semesterList.Any(semester =>
                   string.IsNullOrWhiteSpace(semester.SemesterId) ||
                   !Enum.IsDefined(semester.WeekStartDay)) ||
               labelList.Any(label => !Enum.IsDefined(label.Kind)) ||
               HasDuplicateLabelIdentity(labelList) ||
               courseList.Any(course =>
                   string.IsNullOrWhiteSpace(course.OfferingId) ||
                   string.IsNullOrWhiteSpace(course.SemesterId) ||
                   !string.Equals(
                       course.OfferingId,
                       CourseIdentityService.GenerateOfferingId(course),
                       StringComparison.Ordinal) ||
                   !CourseColorService.IsValidHex(course.Color) ||
                   !LabelRules.ValidateCourseReferences(course, labelList).IsValid ||
                   HasInvalidOrdinaryLabelReferences(course) ||
                   course.MeetingTimes.Any(meeting => !Enum.IsDefined(meeting.WeekParity)));
    }

    private static bool HasInvalidRegistrationOrder(SelectionPlan plan) =>
        !plan.Snapshots
            .Select(snapshot => snapshot.RegistrationOrder)
            .OrderBy(order => order)
            .SequenceEqual(Enumerable.Range(0, plan.Snapshots.Count).Select(index => (int?)index));

    private static bool HasInvalidOrdinaryLabelReferences(CourseOffering course)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in course.Labels)
        {
            var normalized = TextRules.NormalizeIdentityText(label);
            if (normalized.Length == 0 || !identities.Add(normalized))
                return true;
        }

        return false;
    }

    private static bool HasDuplicateLabelIdentity(IEnumerable<CourseLabel> labels)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            var name = TextRules.NormalizeIdentityText(label.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!identities.Add(name))
                return true;
        }
        return false;
    }

    private static bool HasLocalLabelKindConflict(
        PlannerDocument document,
        string? name,
        LabelKind expectedKind)
    {
        var normalized = TextRules.NormalizeIdentityText(name);
        return normalized.Length > 0 && document.Labels.Any(label =>
            TextRules.IsSameLabel(label.Name, normalized) && label.Kind != expectedKind);
    }

    private static void AddLocalLabelKindConflicts(
        PlannerDocument document,
        CourseOffering course,
        ValidationResult validation)
    {
        var references = course.Labels.Select(name => (Name: name, Kind: LabelKind.Ordinary))
            .Append((Name: course.CourseGroupType ?? "", Kind: LabelKind.CourseGroupType))
            .Append((Name: course.StudyType ?? "", Kind: LabelKind.StudyType));
        foreach (var reference in references
                     .Where(reference => HasLocalLabelKindConflict(document, reference.Name, reference.Kind))
                     .DistinctBy(reference => TextRules.NormalizeIdentityText(reference.Name), StringComparer.OrdinalIgnoreCase))
        {
            validation.Error("LabelNameDuplicate", TextRules.NormalizeIdentityText(reference.Name));
        }
    }

    private static bool HasDuplicateGeneratedCourseIdentity(IEnumerable<CourseOffering> courses) =>
        HasDuplicateKey(courses, CourseIdentityService.GenerateOfferingId);

    private static bool HasDuplicateKey<T>(IEnumerable<T> items, Func<T, string?> keySelector)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = keySelector(item);
            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                return true;
        }
        return false;
    }

    private static List<CourseLabel> ReferencedCourseLabels(
        IEnumerable<CourseLabel> catalog,
        IEnumerable<CourseOffering> courses)
    {
        var references = new List<(string Name, LabelKind Kind)>();
        var referenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var course in courses)
        {
            foreach (var name in course.Labels)
                AddLabelReference(references, referenceKeys, name, LabelKind.Ordinary);
            AddLabelReference(references, referenceKeys, course.CourseGroupType, LabelKind.CourseGroupType);
            AddLabelReference(references, referenceKeys, course.StudyType, LabelKind.StudyType);
        }

        var catalogList = catalog.ToList();
        var resultKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = catalogList
            .Where(label => referenceKeys.Contains(LabelReferenceKey(label.Kind, label.Name)))
            .Where(label => resultKeys.Add(LabelReferenceKey(label.Kind, label.Name)))
            .Select(JsonDefaults.Clone)
            .ToList();
        var nextDisplayOrder = Enum.GetValues<LabelKind>().ToDictionary(
            kind => kind,
            kind => catalogList.Where(label => label.Kind == kind)
                        .Select(label => label.DisplayOrder)
                        .DefaultIfEmpty(-1)
                        .Max() + 1);

        foreach (var reference in references)
        {
            if (!resultKeys.Add(LabelReferenceKey(reference.Kind, reference.Name)))
                continue;

            result.Add(new CourseLabel
            {
                Name = reference.Name,
                Kind = reference.Kind,
                DisplayOrder = nextDisplayOrder[reference.Kind]++
            });
        }

        return result;
    }

    private static void AddLabelReference(
        List<(string Name, LabelKind Kind)> references,
        HashSet<string> referenceKeys,
        string? name,
        LabelKind kind)
    {
        var normalized = TextRules.NormalizeIdentityText(name);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !referenceKeys.Add(LabelReferenceKey(kind, normalized)))
        {
            return;
        }

        references.Add((normalized, kind));
    }

    private static bool HasOversizedReferencedLabelSet(IEnumerable<CourseOffering> courses)
    {
        var uniqueReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenceCount = 0L;
        foreach (var course in courses)
        {
            referenceCount += course.Labels.Count;
            if (!string.IsNullOrWhiteSpace(course.CourseGroupType))
                referenceCount++;
            if (!string.IsNullOrWhiteSpace(course.StudyType))
                referenceCount++;
            if (referenceCount > MaxLabelReferencesPerPackage)
                return true;

            foreach (var name in course.Labels)
            {
                var normalized = TextRules.NormalizeIdentityText(name);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    uniqueReferences.Add(LabelReferenceKey(LabelKind.Ordinary, normalized)) &&
                    uniqueReferences.Count > MaxLabelsPerPackage)
                {
                    return true;
                }
            }

            foreach (var (name, kind) in new[]
                     {
                         (course.CourseGroupType, LabelKind.CourseGroupType),
                         (course.StudyType, LabelKind.StudyType)
                     })
            {
                var normalized = TextRules.NormalizeIdentityText(name);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    uniqueReferences.Add(LabelReferenceKey(kind, normalized)) &&
                    uniqueReferences.Count > MaxLabelsPerPackage)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string LabelReferenceKey(LabelKind kind, string? name) =>
        $"{(int)kind}\0{TextRules.NormalizeIdentityText(name)}";

    private static bool SemesterHasOversizedFields(Semester semester) =>
        IsOversized(semester.SemesterId) ||
        IsOversized(semester.SemesterName) ||
        semester.PeriodSchedule.Count > 128;

    private static bool LabelHasOversizedFields(CourseLabel label) =>
        IsOversized(label.Name);

    private static bool PlanHasOversizedFields(SelectionPlan plan) =>
        IsOversized(plan.PlanId) ||
        IsOversized(plan.SemesterId) ||
        IsOversized(plan.PlanName);

    private static bool CourseHasOversizedFields(CourseOffering course) =>
        IsOversized(course.OfferingId) ||
        IsOversized(course.SemesterId) ||
        IsOversized(course.CourseName) ||
        IsOversized(course.Teacher) ||
        IsOversized(course.Location) ||
        IsOversized(course.CourseGroupType) ||
        IsOversized(course.StudyType) ||
        IsOversized(course.Notes) ||
        IsOversized(course.Color) ||
        course.Labels.Count > MaxLabelsPerCourse ||
        course.Labels.Any(IsOversized) ||
        course.MeetingTimes.Count > MaxMeetingsPerCourse ||
        course.MeetingTimes.Any(meeting => IsOversized(meeting.Weeks));

    private static bool IsOversized(string? value) =>
        value?.Length > MaxTextFieldLength;

    private static Semester? ResolveImportedSemester(PlannerDocument document, List<Semester> imported, string semesterId)
    {
        var importedSemester = imported.FirstOrDefault(x => x.SemesterId == semesterId);
        if (importedSemester is null)
            return document.Semesters.FirstOrDefault(x => x.SemesterId == semesterId);
        return document.Semesters.FirstOrDefault(x =>
                   TextRules.IsSameIdentityText(x.SemesterName, importedSemester.SemesterName))
               ?? document.Semesters.FirstOrDefault(x => x.SemesterId == importedSemester.SemesterId)
               ?? importedSemester;
    }

    private static bool HasForcedSemesterMergeConflict(PlannerDocument document, List<Semester> imported, string semesterId)
    {
        var importedSemester = imported.FirstOrDefault(x => x.SemesterId == semesterId);
        if (importedSemester is null)
            return false;
        var existingById = document.Semesters.FirstOrDefault(x => x.SemesterId == importedSemester.SemesterId);
        return existingById is not null &&
               !TextRules.IsSameIdentityText(existingById.SemesterName, importedSemester.SemesterName);
    }

    private static void UpsertCourse(List<CourseOffering> courses, CourseOffering incoming)
    {
        var index = courses.FindIndex(x => x.OfferingId == incoming.OfferingId);
        if (index >= 0)
            courses[index] = JsonDefaults.Clone(incoming);
        else
            courses.Add(JsonDefaults.Clone(incoming));
    }

    private static void UpsertLabel(List<CourseLabel> labels, CourseLabel incoming)
    {
        incoming.Name = TextRules.NormalizeIdentityText(incoming.Name);
        var index = labels.FindIndex(x => x.Kind == incoming.Kind && TextRules.IsSameLabel(x.Name, incoming.Name));
        if (index >= 0)
        {
            labels[index].Name = incoming.Name;
            labels[index].DisplayOrder = incoming.DisplayOrder;
        }
        else
        {
            labels.Add(JsonDefaults.Clone(incoming));
        }
    }

    private static ImportPreview NotImportablePreview(string code, string kind = "file") =>
        new()
        {
            Kind = kind,
            SchemaVersion = "",
            Items =
            {
                new ImportPreviewItem
                {
                    Status = ImportPreviewStatus.NotImportable,
                    Kind = "file",
                    DisplayName = "",
                    Errors = { Issue(code) }
                }
            }
        };

    private static string SerializeImportablePackage<T>(T package)
    {
        var json = JsonSerializer.Serialize(package, ExchangeJsonOptions);
        if (json.Length > PlannerDataLimits.MaxImportTextCharacters ||
            StrictUtf8.GetByteCount(json) > PlannerDataLimits.MaxImportFileBytes)
        {
            throw new InvalidDataException(
                "The exported package exceeds the application's safe import limits.");
        }

        return json;
    }

    private static bool ExceedsImportLimits(string json)
    {
        if (json.Length > MaxImportJsonCharacters)
            return true;
        try
        {
            return StrictUtf8.GetByteCount(json) > PlannerDataLimits.MaxImportFileBytes;
        }
        catch (EncoderFallbackException)
        {
            // JsonInputGuard reports malformed UTF-16 as invalid JSON. This path
            // remains allocation-free and classifies it as content, not size.
            return false;
        }
    }

    private static bool SemesterSettingsEqual(Semester left, Semester right) =>
        left.StartDate == right.StartDate &&
        left.EndDate == right.EndDate &&
        left.WeekCount == right.WeekCount &&
        left.WeekStartDay == right.WeekStartDay &&
        JsonSerializer.Serialize(left.PeriodSchedule, JsonDefaults.Options) ==
        JsonSerializer.Serialize(right.PeriodSchedule, JsonDefaults.Options);

    private static bool Contains(string? value, string text) =>
        value?.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase) == true;

    private static bool Contains(ValidationIssue issue, string text) =>
        Contains(issue.Code, text) || issue.Parameters.Any(parameter => Contains(parameter, text));

    private static ValidationIssue Issue(string code, params string[] parameters) =>
        new() { Code = code, Parameters = parameters.ToList() };
}

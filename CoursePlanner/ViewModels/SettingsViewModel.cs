using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CoursePlanner.Core;
using CoursePlanner.Services;

namespace CoursePlanner.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private sealed record PendingLabelSelection(string Name, LabelKind Kind);

    private readonly DocumentSession _documents;
    private readonly LocalizationService _localization;
    private readonly IThemeService _theme;
    private readonly ISemesterDeletionBackup _semesterDeletionBackup;
    private readonly CoordinatedObservableCollection<Semester> _semesters = new();
    private readonly CoordinatedObservableCollection<PeriodDefinition> _periods = new();
    private readonly CoordinatedObservableCollection<CourseLabel> _labels = new();
    private Semester? _selectedSemester;
    private CourseLabel? _selectedLabel;
    private bool _selectedSemesterSourceRejected;
    private bool _selectedLabelSourceRejected;
    private string _lastAcceptedStateToken;
    private string? _pendingAcceptedSemesterId;
    private PendingLabelSelection? _pendingAcceptedLabel;
    private string? _acceptedSemesterSelectionId;
    private PendingLabelSelection? _acceptedLabelSelection;

    public SettingsViewModel(
        DocumentSession documents,
        LocalizationService localization,
        IThemeService theme,
        ISemesterDeletionBackup? semesterDeletionBackup = null)
    {
        _documents = documents;
        _localization = localization;
        _theme = theme;
        _semesterDeletionBackup = semesterDeletionBackup ?? new SemesterDeletionBackup();
        _lastAcceptedStateToken = _documents.AcceptedStateToken;
        _documents.StateAccepted += (_, accepted) =>
        {
            _acceptedSemesterSelectionId = _pendingAcceptedSemesterId;
            _acceptedLabelSelection = _pendingAcceptedLabel;
            var isSemanticNoOpSave =
                accepted.Kind == DocumentStateAcceptanceKind.Save &&
                string.Equals(
                    accepted.AcceptedStateToken,
                    _lastAcceptedStateToken,
                    StringComparison.Ordinal);
            _lastAcceptedStateToken = accepted.AcceptedStateToken;
            if (isSemanticNoOpSave)
                Reload();
        };
        _documents.Changed += (_, _) => Reload();
        _documents.RolledBack += (_, _) => Reload();
        _localization.LanguageChanged += (_, _) =>
        {
            var publications = new NonFatalPublicationAggregator();
            publications.Try(() => OnPropertyChanged(nameof(T)));
            publications.Try(() => OnPropertyChanged(nameof(Language)));
            publications.ThrowIfAny();
        };
        Reload();
    }

    public ObservableCollection<Semester> Semesters => _semesters;
    public ObservableCollection<PeriodDefinition> Periods => _periods;
    public ObservableCollection<CourseLabel> Labels => _labels;
    public AppLocalizer T => _localization.Localizer;

    public string DatabasePath => _documents.Repository.DatabasePath;
    public string LogsDirectory => _documents.Repository.LogsDirectory;

    public LanguageMode Language
    {
        get => _documents.Document.Settings.Language;
        set
        {
            if (_documents.Document.Settings.Language != value)
            {
                _localization.ApplyLanguage(value);
                OnPropertyChanged();
            }
        }
    }

    public ThemeMode Theme
    {
        get => _documents.Document.Settings.Theme;
        set
        {
            if (_documents.Document.Settings.Theme != value)
            {
                _theme.ApplyTheme(value);
                OnPropertyChanged();
            }
        }
    }

    public Semester? SelectedSemester
    {
        get => _selectedSemester;
        set
        {
            var isCurrentSource = value is null ||
                                  _documents.Document.Semesters.Any(candidate => ReferenceEquals(candidate, value));
            _selectedSemesterSourceRejected = !isCurrentSource;
            var accepted = isCurrentSource ? value : null;
            if (ReferenceEquals(_selectedSemester, accepted))
                return;

            _selectedSemester = accepted;
            _periods.InstallWithoutNotification(
                accepted?.PeriodSchedule.ToList() ?? []);

            // Both projections are complete before either binding can observe
            // the selection change. A failing subscriber must not prevent the
            // other half from publishing or make a same-value retry necessary.
            var publications = new NonFatalPublicationAggregator();
            publications.Try(() => OnPropertyChanged(nameof(SelectedSemester)));
            publications.Try(_periods.PublishReset);
            publications.ThrowIfAny();
        }
    }

    public CourseLabel? SelectedLabel
    {
        get => _selectedLabel;
        set
        {
            var isCurrentSource = value is null ||
                                  _documents.Document.Labels.Any(candidate => ReferenceEquals(candidate, value));
            _selectedLabelSourceRejected = !isCurrentSource;
            var accepted = isCurrentSource ? value : null;
            if (ReferenceEquals(_selectedLabel, accepted))
                return;

            _selectedLabel = accepted;
            OnPropertyChanged(nameof(SelectedLabel));
        }
    }

    public void Reload()
    {
        var previousSemester = _selectedSemester;
        var previousLabel = _selectedLabel;
        var acceptedSemesterSelectionId = _acceptedSemesterSelectionId;
        var acceptedLabelSelection = _acceptedLabelSelection;
        _acceptedSemesterSelectionId = null;
        _acceptedLabelSelection = null;
        var semesters = _documents.Document.Semesters
            .OrderBy(x => x.DisplayOrder)
            .ToList();
        var labels = _documents.Document.Labels
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToList();
        var selectedSemester = acceptedSemesterSelectionId is not null
            ? semesters.FirstOrDefault(x => string.Equals(
                  x.SemesterId,
                  acceptedSemesterSelectionId,
                  StringComparison.Ordinal))
            : previousSemester is null
                ? semesters.FirstOrDefault()
                : semesters.FirstOrDefault(x => x.SemesterId == previousSemester.SemesterId) ??
                  semesters.FirstOrDefault();
        var selectedLabel = acceptedLabelSelection is not null
            ? labels.FirstOrDefault(x =>
                x.Kind == acceptedLabelSelection.Kind &&
                TextRules.IsSameLabel(x.Name, acceptedLabelSelection.Name))
            : previousLabel is null
                ? labels.FirstOrDefault()
                : labels.FirstOrDefault(x =>
                      x.Kind == previousLabel.Kind &&
                      TextRules.IsSameLabel(x.Name, previousLabel.Name)) ??
                  labels.FirstOrDefault();
        var periods = selectedSemester?.PeriodSchedule.ToList() ?? [];

        // Install the full graph without notifications first. Rollback/reload
        // subscribers may throw, but no observer can ever see Semesters from
        // one document generation paired with Labels or Periods from another.
        _semesters.InstallWithoutNotification(semesters);
        _labels.InstallWithoutNotification(labels);
        _periods.InstallWithoutNotification(periods);
        _selectedSemester = selectedSemester;
        _selectedLabel = selectedLabel;
        _selectedSemesterSourceRejected = false;
        _selectedLabelSourceRejected = false;

        var publications = new NonFatalPublicationAggregator();
        publications.Try(_semesters.PublishReset);
        publications.Try(_labels.PublishReset);
        publications.Try(_periods.PublishReset);
        if (!ReferenceEquals(previousSemester, _selectedSemester))
            publications.Try(() => OnPropertyChanged(nameof(SelectedSemester)));
        if (!ReferenceEquals(previousLabel, _selectedLabel))
            publications.Try(() => OnPropertyChanged(nameof(SelectedLabel)));
        publications.Try(() => OnPropertyChanged(nameof(DatabasePath)));
        publications.Try(() => OnPropertyChanged(nameof(LogsDirectory)));
        publications.ThrowIfAny();
    }

    public Semester AddSemester()
    {
        if (!TryAddSemester(out var semester, out var validation))
            throw new InvalidOperationException(validation.Errors[0].Code);
        return semester!;
    }

    public ValidationResult ValidateCanAddSemester() =>
        PlannerCapacityRules.ValidateCanAddSemester(_documents.Document.Semesters.Count);

    public bool TryAddSemester(out Semester? semester, out ValidationResult validation)
    {
        semester = null;
        validation = ValidateCanAddSemester();
        if (!validation.IsValid)
            return false;

        var id = $"semester-{Guid.NewGuid():N}";
        var created = new Semester
        {
            SemesterId = id,
            SemesterName = _localization.Localizer["NewSemester"],
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            WeekStartDay = WeekStartDay.Monday,
            WeekCount = 16,
            DisplayOrder = _documents.Document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        created.EndDate = SemesterRules.CalculateEndDate(created.StartDate, created.WeekCount, created.WeekStartDay);
        var suffix = 2;
        var baseName = _localization.Localizer["NewSemester"];
        while (_documents.Document.Semesters.Any(x => string.Equals(x.SemesterName, created.SemesterName, StringComparison.OrdinalIgnoreCase)))
            created.SemesterName = $"{baseName} {suffix++}";
        var currentTextCount = PlannerDocumentTextCapacity.Count(_documents.Document);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                currentTextCount,
                PlannerDocumentTextCapacity.Count(created)),
            validation);
        if (!validation.IsValid)
            return false;

        _documents.CaptureUndo();
        _documents.Document.Semesters.Add(created);
        _documents.Document.Settings.CurrentSemesterId = created.SemesterId;
        _documents.Document.Settings.CurrentPlanId = null;
        _pendingAcceptedSemesterId = created.SemesterId;
        try
        {
            _documents.Save("semester.create");
        }
        finally
        {
            _pendingAcceptedSemesterId = null;
        }
        semester = created;
        return true;
    }

    public ValidationResult SaveSelectedSemester(string name, DateOnly start, DateOnly end, WeekStartDay weekStartDay)
    {
        var result = new ValidationResult();
        if (_selectedSemesterSourceRejected)
        {
            result.Error("SemesterEditSourceMissing");
            return result;
        }
        if (SelectedSemester is null)
            return result;
        if (name.Length > PlannerDataLimits.MaxTextFieldLength)
        {
            result.Error("SemesterNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
            return result;
        }

        var updated = JsonDefaults.Clone(SelectedSemester);
        updated.SemesterName = name.Trim();
        updated.StartDate = start;
        updated.EndDate = end;
        updated.WeekStartDay = weekStartDay;
        if (end >= start)
            updated.WeekCount = SemesterRules.CalculateWeekCount(start, end, weekStartDay);

        result = SemesterRules.ValidateSemester(updated, _documents.Document.Semesters.Where(x => x != SelectedSemester));
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateChange(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                PlannerDocumentTextCapacity.Count(SelectedSemester),
                PlannerDocumentTextCapacity.Count(updated)),
            result);
        if (!result.IsValid)
            return result;

        _documents.CaptureUndo();
        SelectedSemester.SemesterName = updated.SemesterName;
        SelectedSemester.StartDate = updated.StartDate;
        SelectedSemester.EndDate = updated.EndDate;
        SelectedSemester.WeekCount = updated.WeekCount;
        SelectedSemester.WeekStartDay = updated.WeekStartDay;
        SelectedSemester.PeriodSchedule = updated.PeriodSchedule;
        _documents.Save("semester.save");
        return result;
    }

    public bool DeleteSelectedSemester()
    {
        if (SelectedSemester is null || _documents.Document.Semesters.Count <= 1)
            return false;

        var semester = SelectedSemester;
        _documents.EnsureMutationAllowed();
        try
        {
            _semesterDeletionBackup.Create(
                _documents.Repository.DatabasePath,
                _documents.Repository.DataDirectory);
        }
        catch (SemesterDeletionBackupException)
        {
            throw;
        }
        catch (Exception exception) when (SemesterDeletionBackupFailure.IsExpected(exception))
        {
            throw new SemesterDeletionBackupException(exception);
        }
        _documents.CaptureUndo();
        _documents.Document.CourseLibrary.RemoveAll(x => x.SemesterId == semester.SemesterId);
        _documents.Document.Plans.RemoveAll(x => x.SemesterId == semester.SemesterId);
        _documents.Document.Semesters.Remove(semester);
        _documents.Document.Settings.OpenPlanIds.RemoveAll(id => _documents.Document.Plans.All(x => x.PlanId != id));
        _documents.Save("semester.delete");
        return true;
    }

    public bool ClearCurrentSemesterCourses()
    {
        if (SelectedSemester is null)
            return false;
        var removedCourseIds = _documents.Document.CourseLibrary
            .Where(x => x.SemesterId == SelectedSemester.SemesterId)
            .Select(x => x.OfferingId)
            .ToList();
        if (removedCourseIds.Count == 0)
            return false;

        _documents.CaptureUndo();
        _documents.Document.CourseLibrary.RemoveAll(x => x.SemesterId == SelectedSemester.SemesterId);
        PlannerDomainService.RemoveCourseReferences(_documents.Document, removedCourseIds);
        _documents.Save("semester.clear-courses");
        return true;
    }

    public PeriodDefinition? AddPeriodAfter(int? selectedPeriodNumber)
    {
        if (SelectedSemester is null)
            return null;
        if (!ValidateCanAddPeriod().IsValid)
            return null;

        _documents.EnsureMutationAllowed();
        var before = JsonDefaults.Clone(_documents.Document);
        var period = PeriodScheduleService.AddPeriodAfter(
            SelectedSemester,
            selectedPeriodNumber,
            SemesterLibraryCourses(),
            SemesterPlanSnapshots());
        _documents.UndoRedo.Capture(before);
        _documents.Save("semester.period-add");
        return period;
    }

    public ValidationResult ValidateCanAddPeriod() =>
        SelectedSemester is null
            ? Error("SemesterRequired")
            : PlannerCapacityRules.ValidateCanAddPeriod(SelectedSemester.PeriodSchedule.Count);

    public void UpdatePeriodTime(int periodNumber, TimeOnly start, TimeOnly end)
    {
        if (SelectedSemester is null)
            return;

        _documents.EnsureMutationAllowed();
        var before = JsonDefaults.Clone(_documents.Document);
        PeriodScheduleService.UpdatePeriodTime(SelectedSemester, periodNumber, start, end);
        _documents.UndoRedo.Capture(before);
        _documents.Save("semester.period-time");
    }

    public void DeletePeriod(int periodNumber)
    {
        if (SelectedSemester is null)
            return;
        _documents.EnsureMutationAllowed();
        var before = JsonDefaults.Clone(_documents.Document);
        PeriodScheduleService.DeletePeriod(
            SelectedSemester,
            periodNumber,
            SemesterLibraryCourses(),
            SemesterPlanSnapshots());
        _documents.UndoRedo.Capture(before);
        _documents.Save("semester.period-delete");
    }

    private IEnumerable<CourseOffering> SemesterLibraryCourses() =>
        SelectedSemester is null
            ? Enumerable.Empty<CourseOffering>()
            : _documents.Document.CourseLibrary.Where(x => x.SemesterId == SelectedSemester.SemesterId);

    private IEnumerable<PlanCourseSnapshot> SemesterPlanSnapshots() =>
        SelectedSemester is null
            ? Enumerable.Empty<PlanCourseSnapshot>()
            : _documents.Document.Plans
                .Where(x => x.SemesterId == SelectedSemester.SemesterId)
                .SelectMany(x => x.Snapshots);

    public void ResetDefaultPeriods()
    {
        if (SelectedSemester is null)
            return;
        _documents.EnsureMutationAllowed();
        var before = JsonDefaults.Clone(_documents.Document);
        PeriodScheduleService.ResetToDefault(
            SelectedSemester,
            SemesterLibraryCourses(),
            SemesterPlanSnapshots());
        _documents.UndoRedo.Capture(before);
        _documents.Save("semester.period-reset");
    }

    public IReadOnlyList<string> ReadLogs() => _documents.Repository.ReadEventSummaries();

    public ValidationResult UpsertLabel(string name, LabelKind kind)
    {
        if (_selectedLabelSourceRejected)
            return Error("LabelEditSourceMissing");

        var candidate = new CourseLabel { Name = name, Kind = kind };
        var result = LabelRules.Validate(
            candidate,
            _documents.Document.Labels,
            SelectedLabel);
        if (!result.IsValid)
            return result;
        name = name.Trim();
        candidate.Name = name;
        var referenceTextDelta = 0L;
        if (SelectedLabel is not null && SelectedLabel.Kind == kind)
        {
            var referenceCount = CountLabelReferences(SelectedLabel.Name, kind);
            var currentReferenceTextLength = CountLabelReferenceText(SelectedLabel.Name, kind);
            referenceTextDelta = (referenceCount * name.Length) - currentReferenceTextLength;
        }
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                PlannerDocumentTextCapacity.Count(candidate) -
                (SelectedLabel is null ? 0 : PlannerDocumentTextCapacity.Count(SelectedLabel)) +
                referenceTextDelta),
            result);
        if (!result.IsValid)
            return result;

        if (SelectedLabel is not null &&
            SelectedLabel.Kind != kind &&
            IsLabelReferenced(SelectedLabel.Name, SelectedLabel.Kind))
        {
            result.Error("LabelKindInUse");
            return result;
        }

        _documents.CaptureUndo();
        CourseLabel? createdLabel = null;
        if (SelectedLabel is null)
        {
            createdLabel = new CourseLabel
            {
                Name = name,
                Kind = kind,
                DisplayOrder = _documents.Document.Labels.Count(x => x.Kind == kind)
            };
            _documents.Document.Labels.Add(createdLabel);
        }
        else
        {
            var oldKind = SelectedLabel.Kind;
            if (SelectedLabel.Kind == kind)
                RenameCourseLabelReferences(SelectedLabel.Name, name, kind);
            SelectedLabel.Name = name;
            SelectedLabel.Kind = kind;
            if (oldKind != kind)
            {
                SelectedLabel.DisplayOrder = _documents.Document.Labels.Count(label =>
                    label != SelectedLabel && label.Kind == kind);
                NormalizeLabelDisplayOrders(oldKind);
                NormalizeLabelDisplayOrders(kind);
            }
        }

        if (createdLabel is not null)
            _pendingAcceptedLabel = new PendingLabelSelection(createdLabel.Name, createdLabel.Kind);
        try
        {
            _documents.Save("label.upsert");
        }
        finally
        {
            _pendingAcceptedLabel = null;
        }
        return result;
    }

    private static ValidationResult Error(string code)
    {
        var result = new ValidationResult();
        result.Error(code);
        return result;
    }

    private static void CopyValidation(ValidationResult source, ValidationResult target)
    {
        foreach (var issue in source.Errors)
            target.Error(issue.Code, issue.Parameters.ToArray());
        foreach (var issue in source.Warnings)
            target.Warning(issue.Code, issue.Parameters.ToArray());
    }

    private bool IsLabelReferenced(string name, LabelKind kind) =>
        _documents.Document.CourseLibrary.Any(course => kind switch
        {
            LabelKind.Ordinary => course.Labels.Any(label => TextRules.IsSameLabel(label, name)),
            LabelKind.CourseGroupType => TextRules.IsSameLabel(course.CourseGroupType, name),
            LabelKind.StudyType => TextRules.IsSameLabel(course.StudyType, name),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        });

    private long CountLabelReferences(string name, LabelKind kind) =>
        _documents.Document.CourseLibrary.Sum(course => kind switch
        {
            LabelKind.Ordinary => (long)course.Labels.Count(label =>
                TextRules.IsSameLabel(label, name)),
            LabelKind.CourseGroupType =>
                TextRules.IsSameLabel(course.CourseGroupType, name) ? 1L : 0L,
            LabelKind.StudyType =>
                TextRules.IsSameLabel(course.StudyType, name) ? 1L : 0L,
            _ => 0L
        });

    private long CountLabelReferenceText(string name, LabelKind kind) =>
        _documents.Document.CourseLibrary.Sum(course => kind switch
        {
            LabelKind.Ordinary => course.Labels
                .Where(label => TextRules.IsSameLabel(label, name))
                .Sum(label => (long)label.Length),
            LabelKind.CourseGroupType when TextRules.IsSameLabel(course.CourseGroupType, name) =>
                course.CourseGroupType?.Length ?? 0L,
            LabelKind.StudyType when TextRules.IsSameLabel(course.StudyType, name) =>
                course.StudyType?.Length ?? 0L,
            _ => 0L
        });

    private void NormalizeLabelDisplayOrders(LabelKind kind)
    {
        var labels = _documents.Document.Labels
            .Where(label => label.Kind == kind)
            .OrderBy(label => label.DisplayOrder)
            .ThenBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var index = 0; index < labels.Count; index++)
            labels[index].DisplayOrder = index;
    }

    private void RenameCourseLabelReferences(string oldName, string newName, LabelKind kind)
    {
        foreach (var course in _documents.Document.CourseLibrary)
        {
            switch (kind)
            {
                case LabelKind.Ordinary:
                    for (var index = 0; index < course.Labels.Count; index++)
                    {
                        if (TextRules.IsSameLabel(course.Labels[index], oldName))
                            course.Labels[index] = newName;
                    }
                    course.Labels = course.Labels
                        .DistinctBy(TextRules.NormalizeIdentityText, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
                case LabelKind.CourseGroupType:
                    if (TextRules.IsSameLabel(course.CourseGroupType, oldName))
                        course.CourseGroupType = newName;
                    break;
                case LabelKind.StudyType:
                    if (TextRules.IsSameLabel(course.StudyType, oldName))
                        course.StudyType = newName;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    public CourseLabel NewLabelTemplate()
    {
        SelectedLabel = null;
        return new CourseLabel { Name = "", Kind = LabelKind.Ordinary };
    }

    public void DeleteSelectedLabel()
    {
        if (SelectedLabel is null)
            return;
        _documents.CaptureUndo();
        var name = SelectedLabel.Name;
        _documents.Document.Labels.Remove(SelectedLabel);
        foreach (var course in _documents.Document.CourseLibrary)
        {
            course.Labels.RemoveAll(label => TextRules.IsSameLabel(label, name));
            if (TextRules.IsSameLabel(course.CourseGroupType, name))
                course.CourseGroupType = null;
            if (TextRules.IsSameLabel(course.StudyType, name))
                course.StudyType = null;
        }
        _documents.Save("label.delete");
    }

    public void MoveSelectedLabel(int direction)
    {
        if (SelectedLabel is null || direction == 0)
            return;

        var sameKind = _documents.Document.Labels
            .Where(x => x.Kind == SelectedLabel.Kind)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToList();
        var index = sameKind.FindIndex(x => ReferenceEquals(x, SelectedLabel) ||
                                            string.Equals(x.Name, SelectedLabel.Name, StringComparison.OrdinalIgnoreCase));
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= sameKind.Count)
            return;

        _documents.CaptureUndo();
        (sameKind[index], sameKind[targetIndex]) = (sameKind[targetIndex], sameKind[index]);
        for (var i = 0; i < sameKind.Count; i++)
            sameKind[i].DisplayOrder = i;
        _documents.Save("label.reorder");
    }

    public void ClearLogs()
    {
        _documents.Repository.ClearLogs();
        _documents.Repository.Log("Info", "logs.clear", "Logs cleared.");
    }
}

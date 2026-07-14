using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Services;

namespace CoursePlanner.ViewModels;

public sealed class BulkAddPlanDecision
{
    public SelectionPlan Plan { get; init; } = new();
    public DuplicateResolution DuplicateResolution { get; init; } = DuplicateResolution.SkipExisting;
    public ConflictResolution ConflictResolution { get; init; } = ConflictResolution.KeepConflict;
}

public sealed class CourseEditSession
{
    private readonly string _originalFingerprint;

    public CourseEditSession(CourseOffering course, CourseOffering? sourceCourse, string? addToPlanId = null)
    {
        Course = JsonDefaults.Clone(course);
        SourceCourse = sourceCourse;
        OriginalOfferingId = sourceCourse?.OfferingId ?? course.OfferingId;
        AddToPlanId = addToPlanId;
        _originalFingerprint = CourseEditFingerprint.Capture(Course);
    }

    public CourseOffering Course { get; }
    public CourseOffering? SourceCourse { get; }
    public string OriginalOfferingId { get; }
    public string? AddToPlanId { get; }
    public bool IsNew => SourceCourse is null;
    public bool HasChanges => _originalFingerprint != CourseEditFingerprint.Capture(Course);
}

public sealed class PlannerViewModel : ObservableObject
{
    private sealed class TabCreatedPlanBaseline
    {
        private readonly byte[] _initialPlanJson;
        private int _wasModified;

        public TabCreatedPlanBaseline(SelectionPlan plan)
        {
            _initialPlanJson = CaptureSemanticState(plan);
        }

        // This latch is deliberately shared by every immutable history state
        // that refers to this provenance. Undo/redo may restore membership, but
        // it must never make an edited tab-created plan pristine again.
        public bool WasModified => Volatile.Read(ref _wasModified) != 0;

        public void MarkModified() => Interlocked.Exchange(ref _wasModified, 1);

        public bool Matches(SelectionPlan plan) =>
            _initialPlanJson.AsSpan().SequenceEqual(
                CaptureSemanticState(plan));

        private static byte[] CaptureSemanticState(SelectionPlan plan)
        {
            // DisplayOrder is presentation state, and ApplyPlanDisplayOrder also
            // advances ModifiedAt as an implementation detail. Neither may turn
            // an otherwise pristine, tab-created plan into durable catalog data;
            // every real plan edit is still represented by another content field.
            var normalized = JsonDefaults.Clone(plan);
            normalized.DisplayOrder = 0;
            normalized.ModifiedAt = default;
            return JsonSerializer.SerializeToUtf8Bytes(normalized, JsonDefaults.CompactOptions);
        }
    }

    private sealed class TabCreatedPlanHistoryState : PlannerUndoRedoState
    {
        public static readonly TabCreatedPlanHistoryState Empty = new(
            new Dictionary<string, TabCreatedPlanBaseline>(StringComparer.Ordinal));

        private readonly Dictionary<string, TabCreatedPlanBaseline> _baselines;

        private TabCreatedPlanHistoryState(
            Dictionary<string, TabCreatedPlanBaseline> baselines)
        {
            _baselines = baselines;
        }

        public IEnumerable<KeyValuePair<string, TabCreatedPlanBaseline>> Entries => _baselines;

        public bool Contains(string planId) => _baselines.ContainsKey(planId);

        public bool TryGetValue(string planId, [NotNullWhen(true)] out TabCreatedPlanBaseline? baseline) =>
            _baselines.TryGetValue(planId, out baseline);

        public bool IsEquivalentTo(TabCreatedPlanHistoryState other) =>
            _baselines.Count == other._baselines.Count &&
            _baselines.All(entry =>
                other._baselines.TryGetValue(entry.Key, out var otherBaseline) &&
                ReferenceEquals(entry.Value, otherBaseline));

        public TabCreatedPlanHistoryState Set(string planId, TabCreatedPlanBaseline baseline)
        {
            var updated = new Dictionary<string, TabCreatedPlanBaseline>(_baselines, StringComparer.Ordinal)
            {
                [planId] = baseline
            };
            return new TabCreatedPlanHistoryState(updated);
        }

        public TabCreatedPlanHistoryState Remove(string planId)
        {
            if (!_baselines.ContainsKey(planId))
                return this;

            var updated = new Dictionary<string, TabCreatedPlanBaseline>(_baselines, StringComparer.Ordinal);
            updated.Remove(planId);
            return updated.Count == 0 ? Empty : new TabCreatedPlanHistoryState(updated);
        }

        public TabCreatedPlanHistoryState Retain(ISet<string> livePlanIds)
        {
            if (_baselines.Keys.All(livePlanIds.Contains))
                return this;

            var retained = _baselines
                .Where(entry => livePlanIds.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return retained.Count == 0 ? Empty : new TabCreatedPlanHistoryState(retained);
        }
    }

    private sealed class TabCreatedPlanHistoryParticipant(PlannerViewModel owner)
        : IPlannerUndoRedoStateParticipant<TabCreatedPlanHistoryState>
    {
        public TabCreatedPlanHistoryState CaptureState(PlannerDocument document) =>
            owner.CaptureTabCreatedPlanHistoryState(document);

        public bool AreEquivalent(
            TabCreatedPlanHistoryState left,
            TabCreatedPlanHistoryState right) =>
            ReferenceEquals(left, right) || left.IsEquivalentTo(right);

        public void RestoreState(TabCreatedPlanHistoryState state) =>
            owner._tabCreatedPlanHistoryState = state;

        public void ClearState() =>
            owner._tabCreatedPlanHistoryState = TabCreatedPlanHistoryState.Empty;
    }
    private sealed record PreparedCourseAdd(
        SelectionPlan Plan,
        CourseOffering Source,
        DuplicateResolution DuplicateResolution,
        ConflictResolution ConflictResolution,
        AddCourseResult Preview,
        int SnapshotDelta,
        bool AddsLibraryCourse,
        long PlanTextDelta,
        int ProjectedMeetingRowCount);
    private sealed record CourseAddContext(
        HashSet<SelectionPlan> LivePlans,
        Dictionary<string, Semester> SemestersById,
        Semester? NullIdSemester,
        Dictionary<string, CourseOffering> CoursesById,
        Dictionary<string, CourseOffering> PreparedSourcesBySemesterId);
    private sealed record LibraryProjection(
        IReadOnlyList<CourseOffering> Courses,
        IReadOnlyList<LibraryGroup> Groups,
        bool ClearSelectedCourse);
    private sealed record PlannerTransientContextSnapshot(
        string BaselineAcceptedStateToken,
        string? CurrentSemesterId,
        string? CurrentPlanId,
        int CurrentWeek,
        string? BaseComparePlanId,
        PlannerViewMode ViewMode,
        string[] SelectedComparisonPlanIds,
        string? SelectedCourseId,
        bool IsDetailOpen);
    private sealed record PlannerPublicationState(
        Semester? CurrentSemester,
        SelectionPlan? CurrentPlan,
        int CurrentWeek,
        SelectionPlan? BaseComparePlan,
        PlannerViewMode ViewMode,
        CourseOffering? SelectedCourse,
        CourseEditSession? ActiveEdit,
        bool IsDetailOpen,
        string[] SelectedComparisonPlanIds);

    private readonly DocumentSession _documents;
    private readonly LocalizationService _localization;
    private readonly CoordinatedObservableCollection<Semester> _semesters = new();
    private readonly CoordinatedObservableCollection<SelectionPlan> _openPlans = new();
    private readonly CoordinatedObservableCollection<SelectionPlan> _allPlans = new();
    private readonly CoordinatedObservableCollection<CourseOffering> _libraryCourses = new();
    private readonly CoordinatedObservableCollection<LibraryGroup> _libraryGroups = new();
    private readonly CoordinatedObservableCollection<CourseLabel> _labels = new();
    private TabCreatedPlanHistoryState _tabCreatedPlanHistoryState = TabCreatedPlanHistoryState.Empty;
    private PlannerTransientContextSnapshot? _transientContextBeforePendingSave;
    private bool _restorePendingTransientContextOnNextChanged;
    private CourseOffering? _selectedCourse;
    private CourseEditSession? _activeEdit;
    private SelectionPlan? _currentPlan;
    private Semester? _currentSemester;
    private int _currentWeek = 1;
    private string _searchText = "";
    private string _labelFilterText = "";
    private string _groupFilterText = "";
    private string _studyFilterText = "";
    private string _teacherFilterText = "";
    private string _locationFilterText = "";
    private bool _allSemesters;
    private bool _isLibraryOpen = true;
    private bool _isDetailOpen;
    private bool _isComparisonModifierPressed;
    private PlannerViewMode _viewMode = PlannerViewMode.Week;
    private SelectionPlan? _baseComparePlan;
    private readonly List<string> _selectedComparisonPlanIds = new();

    public PlannerViewModel(DocumentSession documents, LocalizationService localization)
    {
        _documents = documents;
        _localization = localization;
        _documents.UndoRedo.RegisterStateParticipant(
            new TabCreatedPlanHistoryParticipant(this),
            _documents.Document);
        _documents.StateAccepted += (_, accepted) =>
            HandleAcceptedDocumentState(accepted);
        _documents.Changed += (_, _) =>
        {
            ObserveTabCreatedPlanModifications();
            var restoredTransientContext = _restorePendingTransientContextOnNextChanged
                ? ConsumePendingTransientContext()
                : null;
            _restorePendingTransientContextOnNextChanged = false;
            ReloadFromDocumentCore(
                clearActiveEdit: true,
                restoredTransientContext);
        };
        _documents.RolledBack += (_, rollback) =>
            HandleDocumentRollback(rollback);
        _localization.LanguageChanged += (_, _) =>
        {
            var publications = new NonFatalPublicationAggregator();
            publications.Try(() => OnPropertyChanged(nameof(T)));
            publications.Try(RefreshLibrary);
            publications.Try(() => OnPropertyChanged(nameof(CurrentPlan)));
            publications.Try(() => OnPropertyChanged(nameof(OpenPlans)));
            publications.ThrowIfAny();
        };
        ReloadFromDocument();
    }

    public ObservableCollection<Semester> Semesters => _semesters;
    public ObservableCollection<SelectionPlan> OpenPlans => _openPlans;
    public ObservableCollection<SelectionPlan> AllPlans => _allPlans;
    public ObservableCollection<CourseOffering> LibraryCourses => _libraryCourses;
    public ObservableCollection<LibraryGroup> LibraryGroups => _libraryGroups;
    public ObservableCollection<CourseLabel> Labels => _labels;

    public AppLocalizer T => _localization.Localizer;

    public IReadOnlyList<string> SelectedComparisonPlanIds => _selectedComparisonPlanIds;

    public bool IsComparisonModifierPressed => _isComparisonModifierPressed;

    public bool CanOpenSelectedComparison =>
        _isComparisonModifierPressed && TryGetSelectedComparison(out _, out _);

    public Semester? CurrentSemester
    {
        get => _currentSemester;
        set
        {
            if (value is null)
                return;

            value = _documents.Document.Semesters.FirstOrDefault(candidate => ReferenceEquals(candidate, value))
                    ?? _documents.Document.Semesters.FirstOrDefault(candidate =>
                        string.Equals(candidate.SemesterId, value.SemesterId, StringComparison.Ordinal));
            if (value is null || ReferenceEquals(_currentSemester, value))
                return;

            _documents.EnsureMutationAllowed();
            CapturePendingTransientContext();
            var publicationState = CapturePublicationState();
            var selectedCourseId = _selectedCourse?.OfferingId;
            _currentSemester = value;
            _documents.Document.Settings.CurrentSemesterId = value.SemesterId;
            _documents.Document.Settings.CurrentPlanId = null;

            var (_, resolvedPlan) =
                DocumentConsistencyService.ResolveCurrentContext(_documents.Document);
            _currentPlan = resolvedPlan;
            _documents.Document.Settings.CurrentPlanId = resolvedPlan?.PlanId;

            ReconcileComparisonWithCurrentContextSilently();
            _currentWeek = Math.Clamp(_currentWeek, 1, Math.Max(1, value.WeekCount));
            var projection = BuildLibraryProjection();
            RemapSelectedCourse(
                projection.Courses,
                projection.ClearSelectedCourse,
                selectedCourseId);
            InstallLibraryProjectionWithoutNotification(projection);
            _documents.Save("planner.semester", notify: false);

            var publications = new NonFatalPublicationAggregator();
            PublishInstalledLibraryProjection(publications);
            PublishStateChanges(publicationState, publications: publications);
            publications.ThrowIfAny();
        }
    }

    public SelectionPlan? CurrentPlan
    {
        get => _currentPlan;
        set
        {
            if (value is null)
                return;

            TryOpenPlan(value, out _);
        }
    }

    public bool TryOpenPlan(SelectionPlan plan, out ValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        validation = new ValidationResult();
        var livePlan = _documents.Document.Plans.FirstOrDefault(candidate => ReferenceEquals(candidate, plan))
                       ?? _documents.Document.Plans.FirstOrDefault(candidate =>
                           string.Equals(candidate.PlanId, plan.PlanId, StringComparison.Ordinal));
        if (livePlan is null)
        {
            validation.Error("PlanUnavailable");
            return false;
        }

        var planSemester = _documents.Document.Semesters.FirstOrDefault(semester =>
            string.Equals(semester.SemesterId, livePlan.SemesterId, StringComparison.Ordinal));
        if (planSemester is null)
        {
            validation.Error("SemesterRequired");
            return false;
        }

        var alreadyOpen = _documents.Document.Settings.OpenPlanIds.Contains(
            livePlan.PlanId,
            StringComparer.Ordinal);
        CopyValidation(
            PlanTabLimits.ValidateCanOpen(
                _documents.Document.Settings.OpenPlanIds.Count,
                alreadyOpen),
            validation);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                alreadyOpen ? 0 : livePlan.PlanId.Length),
            validation);
        if (!validation.IsValid)
            return false;

        SetCurrentPlanCore(livePlan, planSemester);
        return true;
    }

    private void SetCurrentPlanCore(SelectionPlan value, Semester? planSemester = null)
    {
        var alreadyOpen = _documents.Document.Settings.OpenPlanIds.Contains(
            value.PlanId,
            StringComparer.Ordinal);
        var listedOpenPlan = OpenPlans.FirstOrDefault(candidate =>
            string.Equals(candidate.PlanId, value.PlanId, StringComparison.Ordinal));
        if (ReferenceEquals(_currentPlan, value) &&
            alreadyOpen &&
            listedOpenPlan is not null &&
            string.Equals(
                _documents.Document.Settings.CurrentPlanId,
                value.PlanId,
                StringComparison.Ordinal) &&
            string.Equals(
                _documents.Document.Settings.CurrentSemesterId,
                value.SemesterId,
                StringComparison.Ordinal))
        {
            return;
        }

        _documents.EnsureMutationAllowed();
        CapturePendingTransientContext();
        var publicationState = CapturePublicationState();
        var selectedCourseId = _selectedCourse?.OfferingId;
        planSemester ??= _documents.Document.Semesters.First(semester =>
            string.Equals(semester.SemesterId, value.SemesterId, StringComparison.Ordinal));
        _currentPlan = value;
        _documents.Document.Settings.CurrentPlanId = value.PlanId;
        _documents.Document.Settings.CurrentSemesterId = planSemester.SemesterId;
        if (!alreadyOpen)
            _documents.Document.Settings.OpenPlanIds.Add(value.PlanId);
        int? addedOpenPlanIndex = null;
        if (listedOpenPlan is null)
            addedOpenPlanIndex = _openPlans.AddWithoutNotification(value);
        if (!ReferenceEquals(_currentSemester, planSemester))
        {
            _currentSemester = planSemester;
            _currentWeek = Math.Clamp(_currentWeek, 1, Math.Max(1, planSemester.WeekCount));
        }
        ReconcileComparisonWithCurrentContextSilently();
        var projection = BuildLibraryProjection();
        RemapSelectedCourse(projection.Courses, projection.ClearSelectedCourse, selectedCourseId);
        InstallLibraryProjectionWithoutNotification(projection);
        _documents.Save("planner.plan", notify: false);

        var publications = new NonFatalPublicationAggregator();
        if (addedOpenPlanIndex is { } index)
            publications.Try(() => _openPlans.PublishAdd(value, index));
        PublishInstalledLibraryProjection(publications);
        if (listedOpenPlan is null)
            publications.Try(() => OnPropertyChanged(nameof(OpenPlans)));
        PublishStateChanges(publicationState, publications: publications);
        publications.ThrowIfAny();
    }

    public SelectionPlan? BaseComparePlan
    {
        get => _baseComparePlan;
        set
        {
            if (value is not null)
            {
                value = ResolveLivePlan(value);
                if (value is null)
                    return;
            }
            SetProperty(ref _baseComparePlan, value);
        }
    }

    public int CurrentWeek
    {
        get => _currentWeek;
        set
        {
            var max = CurrentSemester?.WeekCount ?? 1;
            SetProperty(ref _currentWeek, Math.Clamp(value, 1, Math.Max(1, max)));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetLibraryFilter(ref _searchText, BoundFilterText(value));
    }

    public string LabelFilterText
    {
        get => _labelFilterText;
        set => SetLibraryFilter(ref _labelFilterText, BoundFilterText(value));
    }

    public string GroupFilterText
    {
        get => _groupFilterText;
        set => SetLibraryFilter(ref _groupFilterText, BoundFilterText(value));
    }

    public string StudyFilterText
    {
        get => _studyFilterText;
        set => SetLibraryFilter(ref _studyFilterText, BoundFilterText(value));
    }

    public string TeacherFilterText
    {
        get => _teacherFilterText;
        set => SetLibraryFilter(ref _teacherFilterText, BoundFilterText(value));
    }

    public string LocationFilterText
    {
        get => _locationFilterText;
        set => SetLibraryFilter(ref _locationFilterText, BoundFilterText(value));
    }

    public bool AllSemesters
    {
        get => _allSemesters;
        set => SetLibraryFilter(ref _allSemesters, value);
    }

    public bool IsLibraryOpen
    {
        get => _isLibraryOpen;
        set => SetProperty(ref _isLibraryOpen, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetProperty(ref _isDetailOpen, value);
    }

    public PlannerViewMode ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }

    public CourseOffering? SelectedCourse
    {
        get => _selectedCourse;
        set
        {
            if (value is not null)
            {
                value = ResolveLiveCourse(value);
                if (value is null)
                    return;
            }
            if (SetProperty(ref _selectedCourse, value) && value is not null)
                IsDetailOpen = true;
        }
    }

    public CourseEditSession? ActiveEdit
    {
        get => _activeEdit;
        private set
        {
            if (SetProperty(ref _activeEdit, value))
                OnPropertyChanged(nameof(HasUnsavedCourseEdit));
        }
    }

    public bool HasUnsavedCourseEdit => ActiveEdit?.HasChanges == true;

    public bool IsPlanSelectedForComparison(SelectionPlan plan) =>
        _selectedComparisonPlanIds.Contains(plan.PlanId, StringComparer.Ordinal);

    public void ToggleComparisonPlanSelection(SelectionPlan plan)
    {
        var livePlan = OpenPlans.FirstOrDefault(candidate => ReferenceEquals(candidate, plan))
                       ?? OpenPlans.FirstOrDefault(candidate =>
                           string.Equals(candidate.PlanId, plan.PlanId, StringComparison.Ordinal));
        if (livePlan is null)
            return;

        var existing = _selectedComparisonPlanIds.FindIndex(id =>
            string.Equals(id, livePlan.PlanId, StringComparison.Ordinal));
        if (existing >= 0)
            _selectedComparisonPlanIds.RemoveAt(existing);
        else
        {
            if (_selectedComparisonPlanIds.Count >= 2)
                _selectedComparisonPlanIds.Clear();
            _selectedComparisonPlanIds.Add(livePlan.PlanId);
        }

        NotifyComparisonSelectionChanged();
    }

    public void ClearComparisonPlanSelection()
    {
        if (_selectedComparisonPlanIds.Count == 0)
            return;
        _selectedComparisonPlanIds.Clear();
        NotifyComparisonSelectionChanged();
    }

    public void SetComparisonModifierPressed(bool isPressed)
    {
        if (!SetProperty(ref _isComparisonModifierPressed, isPressed, nameof(IsComparisonModifierPressed)))
            return;

        OnPropertyChanged(nameof(CanOpenSelectedComparison));
    }

    public bool TryGetSelectedComparison([NotNullWhen(true)] out SelectionPlan? basePlan, [NotNullWhen(true)] out SelectionPlan? currentPlan)
    {
        basePlan = null;
        currentPlan = null;
        if (_selectedComparisonPlanIds.Count != 2)
            return false;

        basePlan = OpenPlans.FirstOrDefault(x => string.Equals(x.PlanId, _selectedComparisonPlanIds[0], StringComparison.Ordinal));
        currentPlan = OpenPlans.FirstOrDefault(x => string.Equals(x.PlanId, _selectedComparisonPlanIds[1], StringComparison.Ordinal));
        return basePlan is not null &&
               currentPlan is not null &&
               string.Equals(basePlan.SemesterId, currentPlan.SemesterId, StringComparison.Ordinal);
    }

    public bool OpenSelectedComparison()
    {
        if (!CanOpenSelectedComparison ||
            !TryGetSelectedComparison(out var basePlan, out var currentPlan))
            return false;

        OpenComparison(basePlan, currentPlan);
        return true;
    }

    public void ReloadFromDocument() => ReloadFromDocumentCore(clearActiveEdit: true);

    private void ReloadFromDocumentCore(
        bool clearActiveEdit,
        PlannerTransientContextSnapshot? restoredTransientContext = null)
    {
        var publicationState = CapturePublicationState();
        var selectedCourseId = restoredTransientContext is null
            ? _selectedCourse?.OfferingId
            : restoredTransientContext.SelectedCourseId;
        var baseComparePlanId = restoredTransientContext is null
            ? _baseComparePlan?.PlanId
            : restoredTransientContext.BaseComparePlanId;
        var semesters = _documents.Document.Semesters
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.SemesterName)
            .ToList();
        var labels = _documents.Document.Labels
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.DisplayOrder)
            .ToList();
        var allPlans = _documents.Document.Plans
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.PlanName)
            .ToList();
        var openIds = _documents.Document.Settings.OpenPlanIds.ToHashSet(StringComparer.Ordinal);
        var openPlans = _documents.Document.Plans
            .Where(x => openIds.Contains(x.PlanId))
            .OrderBy(x => x.DisplayOrder)
            .ToList();
        // Comparison reconciliation reads OpenPlans, so install this part of
        // the silent snapshot before deriving comparison state. No subscriber
        // can observe it until the coordinated publication batch below.
        _openPlans.InstallWithoutNotification(openPlans);
        var liveOpenPlanIds = openPlans.Select(x => x.PlanId).ToHashSet(StringComparer.Ordinal);
        Semester? currentSemester;
        SelectionPlan? currentPlan;
        if (restoredTransientContext is null)
        {
            _selectedComparisonPlanIds.RemoveAll(id => !liveOpenPlanIds.Contains(id));
            (currentSemester, currentPlan) =
                DocumentConsistencyService.ResolveCurrentContext(_documents.Document);
        }
        else
        {
            currentSemester = restoredTransientContext.CurrentSemesterId is null
                ? null
                : _documents.Document.Semesters.FirstOrDefault(semester =>
                    string.Equals(
                        semester.SemesterId,
                        restoredTransientContext.CurrentSemesterId,
                        StringComparison.Ordinal));
            currentPlan = restoredTransientContext.CurrentPlanId is null
                ? null
                : _documents.Document.Plans.FirstOrDefault(plan =>
                    string.Equals(
                        plan.PlanId,
                        restoredTransientContext.CurrentPlanId,
                        StringComparison.Ordinal));
            _selectedComparisonPlanIds.Clear();
            _selectedComparisonPlanIds.AddRange(
                restoredTransientContext.SelectedComparisonPlanIds);
        }

        _currentPlan = currentPlan;
        _currentSemester = currentSemester;
        _baseComparePlan = baseComparePlanId is null
            ? null
            : openPlans.FirstOrDefault(plan => string.Equals(plan.PlanId, baseComparePlanId, StringComparison.Ordinal));
        _documents.Document.Settings.CurrentPlanId = _currentPlan?.PlanId;
        _documents.Document.Settings.CurrentSemesterId = currentSemester?.SemesterId;
        if (restoredTransientContext is null)
        {
            ReconcileComparisonWithCurrentContextSilently();
            _currentWeek = Math.Clamp(
                _currentWeek,
                1,
                Math.Max(1, currentSemester?.WeekCount ?? 1));
        }
        else
        {
            _currentWeek = Math.Clamp(
                restoredTransientContext.CurrentWeek,
                1,
                Math.Max(1, currentSemester?.WeekCount ?? 1));
            _viewMode = restoredTransientContext.ViewMode;
        }

        if (clearActiveEdit)
            _activeEdit = null;
        if (restoredTransientContext is not null)
            _isDetailOpen = restoredTransientContext.IsDetailOpen;

        var projection = BuildLibraryProjection();
        RemapSelectedCourse(projection.Courses, projection.ClearSelectedCourse, selectedCourseId);
        _semesters.InstallWithoutNotification(semesters);
        _labels.InstallWithoutNotification(labels);
        _allPlans.InstallWithoutNotification(allPlans);
        InstallLibraryProjectionWithoutNotification(projection);

        var publications = new NonFatalPublicationAggregator();
        publications.Try(_semesters.PublishReset);
        publications.Try(_labels.PublishReset);
        publications.Try(_allPlans.PublishReset);
        PublishInstalledLibraryProjection(publications);
        publications.Try(_openPlans.PublishReset);
        PublishStateChanges(
            publicationState,
            forceCurrentContext: true,
            forceComparisonSelection: restoredTransientContext is not null,
            publications: publications);
        publications.ThrowIfAny();
    }

    private PlannerPublicationState CapturePublicationState() => new(
        _currentSemester,
        _currentPlan,
        _currentWeek,
        _baseComparePlan,
        _viewMode,
        _selectedCourse,
        _activeEdit,
        _isDetailOpen,
        [.. _selectedComparisonPlanIds]);

    private void PublishStateChanges(
        PlannerPublicationState previous,
        bool forceCurrentContext = false,
        bool forceComparisonSelection = false,
        NonFatalPublicationAggregator? publications = null)
    {
        var ownsPublications = publications is null;
        publications ??= new NonFatalPublicationAggregator();
        if (forceCurrentContext || !ReferenceEquals(previous.CurrentSemester, _currentSemester))
            publications.Try(() => OnPropertyChanged(nameof(CurrentSemester)));
        if (forceCurrentContext || !ReferenceEquals(previous.CurrentPlan, _currentPlan))
            publications.Try(() => OnPropertyChanged(nameof(CurrentPlan)));
        if (previous.CurrentWeek != _currentWeek)
            publications.Try(() => OnPropertyChanged(nameof(CurrentWeek)));
        if (!ReferenceEquals(previous.BaseComparePlan, _baseComparePlan))
            publications.Try(() => OnPropertyChanged(nameof(BaseComparePlan)));
        if (previous.ViewMode != _viewMode)
            publications.Try(() => OnPropertyChanged(nameof(ViewMode)));
        if (forceComparisonSelection ||
            !previous.SelectedComparisonPlanIds.SequenceEqual(
                _selectedComparisonPlanIds,
                StringComparer.Ordinal))
        {
            publications.Try(() => OnPropertyChanged(nameof(SelectedComparisonPlanIds)));
            publications.Try(() => OnPropertyChanged(nameof(CanOpenSelectedComparison)));
        }
        if (!ReferenceEquals(previous.SelectedCourse, _selectedCourse))
            publications.Try(() => OnPropertyChanged(nameof(SelectedCourse)));
        if (!ReferenceEquals(previous.ActiveEdit, _activeEdit))
        {
            publications.Try(() => OnPropertyChanged(nameof(ActiveEdit)));
            publications.Try(() => OnPropertyChanged(nameof(HasUnsavedCourseEdit)));
        }
        if (previous.IsDetailOpen != _isDetailOpen)
            publications.Try(() => OnPropertyChanged(nameof(IsDetailOpen)));

        if (ownsPublications)
            publications.ThrowIfAny();
    }

    private void RemapSelectedCourse(
        IEnumerable<CourseOffering> courses,
        bool clearSelectedCourse,
        string? selectedCourseId)
    {
        _selectedCourse = clearSelectedCourse || selectedCourseId is null
            ? null
            : courses.FirstOrDefault(course =>
                string.Equals(course.OfferingId, selectedCourseId, StringComparison.Ordinal));
        if (_selectedCourse is not null)
            _isDetailOpen = true;
    }

    public void RefreshLibrary()
    {
        PublishLibraryProjection(BuildLibraryProjection());
    }

    private LibraryProjection BuildLibraryProjection()
    {
        if (CurrentSemester is null)
            return new LibraryProjection([], [], ClearSelectedCourse: true);

        var filter = new CourseFilter
        {
            SearchText = CanonicalizeSearchText(SearchText),
            AllSemesters = AllSemesters
        };
        filter.OrdinaryLabels.UnionWith(ParseFilterTokens(LabelFilterText).Select(CanonicalizeLabelToken));
        filter.CourseGroupTypes.UnionWith(ParseFilterTokens(GroupFilterText).Select(CanonicalizeLabelToken));
        filter.StudyTypes.UnionWith(ParseFilterTokens(StudyFilterText).Select(CanonicalizeLabelToken));
        filter.Teachers.UnionWith(ParseFilterTokens(TeacherFilterText));
        filter.Locations.UnionWith(ParseFilterTokens(LocationFilterText));
        var courses = LibraryFilterService.Filter(_documents.Document.CourseLibrary, filter, CurrentSemester.SemesterId).ToList();
        var groups = LibraryFilterService.Group(courses, _documents.Document.Semesters, _documents.Document.Labels);
        return new LibraryProjection(courses, groups, ClearSelectedCourse: false);
    }

    private void PublishLibraryProjection(LibraryProjection projection)
    {
        // Install both halves before either collection can notify. A collection
        // handler may synchronously change another filter; that nested refresh
        // then installs the latest complete pair, and this outer publication has
        // no stale item writes left to perform when the handler returns.
        var previousSelectedCourse = _selectedCourse;
        InstallLibraryProjectionWithoutNotification(projection);
        if (projection.ClearSelectedCourse)
            _selectedCourse = null;

        var publications = new NonFatalPublicationAggregator();
        PublishInstalledLibraryProjection(publications);
        if (!ReferenceEquals(previousSelectedCourse, _selectedCourse))
            publications.Try(() => OnPropertyChanged(nameof(SelectedCourse)));
        publications.ThrowIfAny();
    }

    private void InstallLibraryProjectionWithoutNotification(LibraryProjection projection)
    {
        _libraryCourses.InstallWithoutNotification(projection.Courses);
        _libraryGroups.InstallWithoutNotification(projection.Groups);
    }

    private void PublishInstalledLibraryProjection(
        NonFatalPublicationAggregator publications)
    {
        publications.Try(_libraryCourses.PublishReset);
        publications.Try(_libraryGroups.PublishReset);
    }

    private void SetLibraryFilter<T>(
        ref T field,
        T value,
        [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        var previousValue = field;
        OnPropertyChanging(propertyName);
        field = value;
        LibraryProjection projection;
        try
        {
            projection = BuildLibraryProjection();
        }
        catch
        {
            field = previousValue;
            throw;
        }

        // Publication exceptions come from external subscribers. The field and
        // complete projection are already installed, so every publication in
        // this batch must be attempted before the aggregated error escapes.
        var previousSelectedCourse = _selectedCourse;
        InstallLibraryProjectionWithoutNotification(projection);
        if (projection.ClearSelectedCourse)
            _selectedCourse = null;

        var publications = new NonFatalPublicationAggregator();
        PublishInstalledLibraryProjection(publications);
        if (!ReferenceEquals(previousSelectedCourse, _selectedCourse))
            publications.Try(() => OnPropertyChanged(nameof(SelectedCourse)));
        publications.Try(() => OnPropertyChanged(propertyName));
        publications.ThrowIfAny();
    }

    public SelectionPlan CreatePlan(string? name = null)
    {
        if (!TryCreatePlan(name, out var plan, out var validation))
            throw new InvalidOperationException(validation.Errors[0].Code);
        return plan!;
    }

    public ValidationResult ValidateCanCreatePlan()
        => ValidateCanCreatePlan(
            _documents.Document,
            CurrentSemester?.SemesterId);

    private static ValidationResult ValidateCanCreatePlan(
        PlannerDocument document,
        string? semesterId)
        => ValidateCanCreatePlan(
            document.Plans.Count,
            document.Settings.OpenPlanIds.Count,
            semesterId is not null && document.Semesters.Any(semester =>
                string.Equals(semester.SemesterId, semesterId, StringComparison.Ordinal)));

    private static ValidationResult ValidateCanCreatePlan(
        int planCount,
        int openPlanCount,
        bool semesterAvailable)
    {
        if (!semesterAvailable)
            return Error("SemesterRequired");

        var validation = PlannerCapacityRules.ValidateCanAddPlan(planCount);
        CopyValidation(
            PlanTabLimits.ValidateCanOpen(
                openPlanCount,
                alreadyOpen: false),
            validation);
        return validation;
    }

    public bool TryCreatePlan(
        string? name,
        out SelectionPlan? plan,
        out ValidationResult validation) =>
        TryCreatePlanCore(name, trackTabCreation: false, out plan, out validation);

    private bool TryCreatePlanCore(
        string? name,
        bool trackTabCreation,
        out SelectionPlan? plan,
        out ValidationResult validation)
    {
        if (!TryPreparePlanCandidate(name, out plan, out validation) || plan is null)
            return false;

        _documents.CaptureUndo();
        _documents.Document.Plans.Add(plan);
        _documents.Document.Settings.OpenPlanIds.Add(plan.PlanId);
        _documents.Document.Settings.CurrentPlanId = plan.PlanId;
        _documents.Document.Settings.CurrentSemesterId = plan.SemesterId;
        if (trackTabCreation)
        {
            _tabCreatedPlanHistoryState = _tabCreatedPlanHistoryState.Set(
                plan.PlanId,
                new TabCreatedPlanBaseline(plan));
        }
        _documents.Save("plan.create");
        return true;
    }

    public ValidationResult ValidateCanCreatePlanFromTab()
    {
        TryPreparePlanCandidate(name: null, out _, out var validation);
        return validation;
    }

    public ValidationResult ValidateLastPlanTabReplacement(SelectionPlan closingPlan)
    {
        ArgumentNullException.ThrowIfNull(closingPlan);
        TryPrepareLastPlanTabReplacement(
            closingPlan,
            out _,
            out _,
            out _,
            out var validation);
        return validation;
    }

    public bool TryReplaceLastPlanTab(
        SelectionPlan closingPlan,
        out SelectionPlan? replacement,
        out ValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(closingPlan);
        if (!TryPrepareLastPlanTabReplacement(
                closingPlan,
                out var liveClosingPlan,
                out replacement,
                out var deleteClosingPlan,
                out validation) ||
            liveClosingPlan is null ||
            replacement is null)
        {
            return false;
        }

        _documents.CaptureUndo();
        _documents.Document.Settings.OpenPlanIds.Remove(liveClosingPlan.PlanId);
        if (deleteClosingPlan)
            _documents.Document.Plans.Remove(liveClosingPlan);
        if (string.Equals(
                _documents.Document.Settings.CurrentPlanId,
                liveClosingPlan.PlanId,
                StringComparison.Ordinal))
        {
            _documents.Document.Settings.CurrentPlanId = null;
        }

        _documents.Document.Plans.Add(replacement);
        _documents.Document.Settings.OpenPlanIds.Add(replacement.PlanId);
        _documents.Document.Settings.CurrentPlanId = replacement.PlanId;
        _documents.Document.Settings.CurrentSemesterId = replacement.SemesterId;
        _tabCreatedPlanHistoryState = _tabCreatedPlanHistoryState
            .Remove(liveClosingPlan.PlanId)
            .Set(replacement.PlanId, new TabCreatedPlanBaseline(replacement));
        _documents.Save("plan.replace-last-tab");
        return true;
    }

    private bool TryPrepareLastPlanTabReplacement(
        SelectionPlan closingPlan,
        out SelectionPlan? liveClosingPlan,
        out SelectionPlan? replacement,
        out bool deleteClosingPlan,
        out ValidationResult validation)
    {
        liveClosingPlan = ResolveLivePlan(closingPlan);
        replacement = null;
        deleteClosingPlan = false;
        if (liveClosingPlan is null ||
            OpenPlans.Count != 1 ||
            !string.Equals(OpenPlans[0].PlanId, liveClosingPlan.PlanId, StringComparison.Ordinal))
        {
            validation = Error("PlanUnavailable");
            return false;
        }

        deleteClosingPlan = IsUnmodifiedPlanCreatedFromTab(liveClosingPlan);
        var document = _documents.Document;
        var projectedTextCount =
            PlannerDocumentTextCapacity.Count(document) -
            liveClosingPlan.PlanId.Length;
        if (deleteClosingPlan)
            projectedTextCount -= PlannerDocumentTextCapacity.Count(liveClosingPlan);

        var projectedPlanCount = document.Plans.Count - (deleteClosingPlan ? 1 : 0);
        var closingPlanId = liveClosingPlan.PlanId;
        var projectedPlans = deleteClosingPlan
            ? document.Plans.Where(plan =>
                !string.Equals(plan.PlanId, closingPlanId, StringComparison.Ordinal))
            : document.Plans;
        var semesterId = CurrentSemester?.SemesterId;
        var semesterAvailable = semesterId is not null && document.Semesters.Any(semester =>
            string.Equals(semester.SemesterId, semesterId, StringComparison.Ordinal));

        return TryPreparePlanCandidate(
            projectedPlans,
            projectedPlanCount,
            openPlanCount: 0,
            projectedTextCount,
            semesterId,
            semesterAvailable,
            name: null,
            out replacement,
            out validation);
    }

    private bool TryPreparePlanCandidate(
        string? name,
        out SelectionPlan? plan,
        out ValidationResult validation) =>
        TryPreparePlanCandidate(
            _documents.Document,
            CurrentSemester?.SemesterId,
            name,
            out plan,
            out validation);

    private bool TryPreparePlanCandidate(
        PlannerDocument document,
        string? semesterId,
        string? name,
        out SelectionPlan? plan,
        out ValidationResult validation) =>
        TryPreparePlanCandidate(
            document.Plans,
            document.Plans.Count,
            document.Settings.OpenPlanIds.Count,
            PlannerDocumentTextCapacity.Count(document),
            semesterId,
            semesterId is not null && document.Semesters.Any(semester =>
                string.Equals(semester.SemesterId, semesterId, StringComparison.Ordinal)),
            name,
            out plan,
            out validation);

    private bool TryPreparePlanCandidate(
        IEnumerable<SelectionPlan> existingPlans,
        int existingPlanCount,
        int openPlanCount,
        long currentTextCount,
        string? semesterId,
        bool semesterAvailable,
        string? name,
        out SelectionPlan? plan,
        out ValidationResult validation)
    {
        plan = null;
        validation = ValidateCanCreatePlan(existingPlanCount, openPlanCount, semesterAvailable);
        if (!validation.IsValid || semesterId is null)
            return false;
        if (name?.Length > PlannerDataLimits.MaxTextFieldLength)
        {
            validation.Error("PlanNameTooLong", PlannerDataLimits.MaxTextFieldLength.ToString());
            return false;
        }

        var baseName = string.IsNullOrWhiteSpace(name) ? _localization.Localizer["NewPlan"] : name.Trim();
        var unique = UniquePlanName(existingPlans, semesterId, baseName);
        var created = new SelectionPlan
        {
            SemesterId = semesterId,
            PlanName = unique,
            DisplayOrder = existingPlanCount
        };
        CopyValidation(PlanRules.Validate(created, existingPlans), validation);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                currentTextCount,
                PlannerDocumentTextCapacity.Count(created) +
                created.PlanId.Length),
            validation);
        if (!validation.IsValid)
            return false;

        plan = created;
        return true;
    }

    public SelectionPlan CreatePlanFromTab()
    {
        if (!TryCreatePlanFromTab(out var plan, out var validation))
            throw new InvalidOperationException(validation.Errors[0].Code);
        return plan!;
    }

    public bool TryCreatePlanFromTab(
        out SelectionPlan? plan,
        out ValidationResult validation)
    {
        return TryCreatePlanCore(
            name: null,
            trackTabCreation: true,
            out plan,
            out validation);
    }

    public SelectionPlan CopyCurrentPlan()
    {
        if (!TryCopyCurrentPlan(out var copy, out var validation))
            throw new InvalidOperationException(validation.Errors[0].Code);
        return copy!;
    }

    public ValidationResult ValidateCanCopyCurrentPlan()
    {
        return CurrentPlan is null
            ? ValidateCanCreatePlan()
            : ValidateCanCopyPlan(CurrentPlan);
    }

    public ValidationResult ValidateCanCopyPlan(SelectionPlan sourcePlan)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);
        var liveSource = ResolveLivePlan(sourcePlan);
        if (liveSource is null)
            return Error("PlanUnavailable");

        var validation = ValidateCanCreatePlan();
        if (!validation.IsValid)
            return validation;

        CopyValidation(
            PlanRules.ValidateMeetingRows(liveSource, _documents.Document.CourseLibrary),
            validation);
        CopyValidation(
            PlannerCapacityRules.ValidateSnapshotAddition(
                existingInPlan: 0,
                totalSnapshotCount: TotalSnapshotCount(),
                additionalCount: liveSource.Snapshots.Count),
            validation);
        return validation;
    }

    public bool TryCopyCurrentPlan(
        out SelectionPlan? copy,
        out ValidationResult validation)
    {
        if (CurrentPlan is null)
            return TryCreatePlan(_localization.Localizer["CopiedPlan"], out copy, out validation);

        return TryCopyPlan(CurrentPlan, out copy, out validation);
    }

    public bool TryCopyPlan(
        SelectionPlan sourcePlan,
        out SelectionPlan? copy,
        out ValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(sourcePlan);
        copy = null;
        var liveSource = ResolveLivePlan(sourcePlan);
        if (liveSource is null)
        {
            validation = Error("PlanUnavailable");
            return false;
        }

        validation = ValidateCanCopyPlan(liveSource);
        if (!validation.IsValid)
            return false;

        var created = JsonDefaults.Clone(liveSource);
        created.PlanId = Guid.NewGuid().ToString("N");
        foreach (var snapshot in created.Snapshots)
            snapshot.SnapshotId = Guid.NewGuid().ToString("N");
        created.PlanName = UniquePlanName(
            created.SemesterId,
            string.Format(_localization.Localizer["CopiedPlanNameFormat"], liveSource.PlanName));
        created.CreatedAt = DateTimeOffset.UtcNow;
        created.ModifiedAt = DateTimeOffset.UtcNow;
        CopyValidation(PlanRules.Validate(created, _documents.Document.Plans), validation);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                PlannerDocumentTextCapacity.Count(created) +
                created.PlanId.Length),
            validation);
        if (!validation.IsValid)
            return false;

        _documents.CaptureUndo();
        _documents.Document.Plans.Add(created);
        _documents.Document.Settings.OpenPlanIds.Add(created.PlanId);
        _documents.Document.Settings.CurrentPlanId = created.PlanId;
        _documents.Document.Settings.CurrentSemesterId = created.SemesterId;
        _documents.Save("plan.copy");
        copy = created;
        return true;
    }

    public ValidationResult RenamePlan(SelectionPlan plan, string newName)
    {
        var livePlan = ResolveLivePlan(plan);
        if (livePlan is null)
            return Error("PlanUnavailable");
        plan = livePlan;

        var candidate = JsonDefaults.Clone(plan);
        var trimmed = newName.Trim();
        candidate.PlanName = trimmed;
        var validation = PlanRules.Validate(candidate, _documents.Document.Plans, plan);
        if (!validation.IsValid)
            return validation;
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateChange(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                PlannerDocumentTextCapacity.Count(plan),
                PlannerDocumentTextCapacity.Count(candidate)),
            validation);
        if (!validation.IsValid)
            return validation;
        if (string.Equals(plan.PlanName, trimmed, StringComparison.Ordinal))
            return validation;

        _documents.CaptureUndo();
        plan.PlanName = trimmed;
        plan.ModifiedAt = DateTimeOffset.UtcNow;
        _documents.Save("plan.rename");
        return validation;
    }

    public void ClosePlanTab(SelectionPlan plan, bool persist = true)
    {
        if (!_documents.Document.Plans.Any(candidate => ReferenceEquals(candidate, plan)) ||
            !_documents.Document.Settings.OpenPlanIds.Contains(plan.PlanId, StringComparer.Ordinal))
            return;

        var deletePlan = IsUnmodifiedPlanCreatedFromTab(plan);
        _documents.EnsureMutationAllowed();
        CapturePendingTransientContext();
        var publicationState = CapturePublicationState();
        if (deletePlan)
            _documents.CaptureUndo();
        _tabCreatedPlanHistoryState = _tabCreatedPlanHistoryState.Remove(plan.PlanId);

        _documents.Document.Settings.OpenPlanIds.Remove(plan.PlanId);

        var allPlansChanged = false;
        if (deletePlan)
        {
            var documentPlan = _documents.Document.Plans.FirstOrDefault(candidate =>
                string.Equals(candidate.PlanId, plan.PlanId, StringComparison.Ordinal));
            if (documentPlan is not null)
            {
                _documents.Document.Plans.Remove(documentPlan);
                allPlansChanged = true;
            }
        }
        if (allPlansChanged)
        {
            _allPlans.InstallWithoutNotification(
                _documents.Document.Plans
                    .OrderBy(candidate => candidate.DisplayOrder)
                    .ThenBy(candidate => candidate.PlanName)
                    .ToList());
        }

        var openPlan = _openPlans.FirstOrDefault(candidate =>
            string.Equals(candidate.PlanId, plan.PlanId, StringComparison.Ordinal));
        var closingIndex = openPlan is null ? -1 : _openPlans.IndexOf(openPlan);
        var remainingOpenPlans = _openPlans
            .Where(candidate => !string.Equals(
                candidate.PlanId,
                plan.PlanId,
                StringComparison.Ordinal))
            .ToList();
        _openPlans.InstallWithoutNotification(remainingOpenPlans);
        var remainingOpenPlanIds = remainingOpenPlans
            .Select(candidate => candidate.PlanId)
            .ToHashSet(StringComparer.Ordinal);
        _selectedComparisonPlanIds.RemoveAll(id => !remainingOpenPlanIds.Contains(id));

        LibraryProjection? projection = null;
        if (string.Equals(_documents.Document.Settings.CurrentPlanId, plan.PlanId, StringComparison.Ordinal))
        {
            var nextPlan = closingIndex >= 0 && closingIndex < remainingOpenPlans.Count
                ? remainingOpenPlans[closingIndex]
                : remainingOpenPlans.LastOrDefault();
            var nextPlanId = nextPlan?.PlanId;
            _documents.Document.Settings.CurrentPlanId = nextPlanId;
            _currentPlan = nextPlan;
            if (nextPlan is not null)
            {
                var nextSemester = _documents.Document.Semesters.First(semester =>
                    string.Equals(semester.SemesterId, nextPlan.SemesterId, StringComparison.Ordinal));
                _documents.Document.Settings.CurrentSemesterId = nextSemester.SemesterId;
                if (!ReferenceEquals(_currentSemester, nextSemester))
                {
                    _currentSemester = nextSemester;
                    _currentWeek = Math.Clamp(
                        _currentWeek,
                        1,
                        Math.Max(1, nextSemester.WeekCount));
                }
            }
            projection = BuildLibraryProjection();
            InstallLibraryProjectionWithoutNotification(projection);
        }

        ReconcileComparisonWithCurrentContextSilently();
        if (persist)
            PersistPlanTabState();

        var publications = new NonFatalPublicationAggregator();
        if (allPlansChanged)
            publications.Try(_allPlans.PublishReset);
        if (openPlan is not null)
            publications.Try(() => _openPlans.PublishRemove(openPlan, closingIndex));
        if (projection is not null)
            PublishInstalledLibraryProjection(publications);
        publications.Try(() => OnPropertyChanged(nameof(OpenPlans)));
        PublishStateChanges(publicationState, publications: publications);

        try
        {
            publications.ThrowIfAny();
        }
        catch (Exception publicationException)
            when (!persist && !RuntimeOperationExceptionPolicy.IsFatal(publicationException))
        {
            try
            {
                // A deferred close normally batches several gestures into one
                // write. If its publication fails, the caller may never reach
                // its timer setup, so commit this already-visible batch now.
                PersistPlanTabState();
            }
            catch (Exception saveException)
                when (!RuntimeOperationExceptionPolicy.IsFatal(saveException))
            {
                throw new AggregateException(
                    "Publishing and persisting the deferred plan-tab close both failed.",
                    publicationException,
                    saveException);
            }

            ExceptionDispatchInfo.Capture(publicationException).Throw();
            throw;
        }
    }

    private bool IsUnmodifiedPlanCreatedFromTab(SelectionPlan plan) =>
        _tabCreatedPlanHistoryState.TryGetValue(plan.PlanId, out var baseline) &&
        !baseline.WasModified &&
        baseline.Matches(plan);

    private void ObserveTabCreatedPlanModifications() =>
        ObserveTabCreatedPlanModifications(_documents.Document);

    private void ObserveTabCreatedPlanModifications(PlannerDocument document)
    {
        foreach (var (planId, baseline) in _tabCreatedPlanHistoryState.Entries)
        {
            if (baseline.WasModified)
                continue;

            var livePlan = document.Plans.FirstOrDefault(plan =>
                string.Equals(plan.PlanId, planId, StringComparison.Ordinal));
            if (livePlan is not null && !baseline.Matches(livePlan))
                baseline.MarkModified();
        }
    }

    private TabCreatedPlanHistoryState CaptureTabCreatedPlanHistoryState(PlannerDocument document)
    {
        var livePlanIds = document.Plans
            .Select(plan => plan.PlanId)
            .ToHashSet(StringComparer.Ordinal);
        return _tabCreatedPlanHistoryState.Retain(livePlanIds);
    }

    public void PersistPlanTabState()
    {
        _documents.Save("plan.close-tab", notify: false);
    }

    private void CommitPendingPlanTabTransientState()
    {
        _restorePendingTransientContextOnNextChanged = false;
        _transientContextBeforePendingSave = null;
    }

    private PlannerTransientContextSnapshot? ConsumePendingTransientContext()
    {
        var snapshot = _transientContextBeforePendingSave;
        _transientContextBeforePendingSave = null;
        _restorePendingTransientContextOnNextChanged = false;
        return snapshot;
    }

    private void HandleAcceptedDocumentState(DocumentStateAcceptedEventArgs accepted)
    {
        _restorePendingTransientContextOnNextChanged = false;
        var pending = _transientContextBeforePendingSave;
        if (pending is null)
            return;

        switch (accepted.Kind)
        {
            case DocumentStateAcceptanceKind.Undo:
            case DocumentStateAcceptanceKind.Redo:
            case DocumentStateAcceptanceKind.Reload:
                if (string.Equals(
                        accepted.AcceptedStateToken,
                        pending.BaselineAcceptedStateToken,
                        StringComparison.Ordinal))
                {
                    _restorePendingTransientContextOnNextChanged = true;
                }
                else
                {
                    CommitPendingPlanTabTransientState();
                }
                break;
            case DocumentStateAcceptanceKind.Save:
            case DocumentStateAcceptanceKind.Replace:
            case DocumentStateAcceptanceKind.Restore:
            default:
                CommitPendingPlanTabTransientState();
                break;
        }
    }

    private void HandleDocumentRollback(DocumentRolledBackEventArgs rollback)
    {
        _restorePendingTransientContextOnNextChanged = false;
        var pending = _transientContextBeforePendingSave;
        if (pending is null || rollback.TargetKind == DocumentRollbackTargetKind.OperationStart)
        {
            ReloadFromDocumentCore(clearActiveEdit: false);
            return;
        }

        if (!string.Equals(
                rollback.TargetStateToken,
                pending.BaselineAcceptedStateToken,
                StringComparison.Ordinal))
        {
            CommitPendingPlanTabTransientState();
            ReloadFromDocumentCore(clearActiveEdit: false);
            return;
        }

        // A known rollback terminates this speculative generation before any
        // external publication runs. Otherwise a failing collection/property
        // subscriber could skip cleanup and let a later failure revive this
        // already-consumed week/comparison/selection snapshot.
        var restoredTransientContext = rollback.DurableOutcomeKnown
            ? ConsumePendingTransientContext()
            : pending;
        ReloadFromDocumentCore(
            clearActiveEdit: false,
            restoredTransientContext);
    }

    private bool CapturePendingTransientContext()
    {
        if (_transientContextBeforePendingSave is not null)
            return false;

        _transientContextBeforePendingSave = new PlannerTransientContextSnapshot(
            _documents.AcceptedStateToken,
            CurrentSemester?.SemesterId,
            CurrentPlan?.PlanId,
            CurrentWeek,
            BaseComparePlan?.PlanId,
            ViewMode,
            [.. _selectedComparisonPlanIds],
            _selectedCourse?.OfferingId,
            _isDetailOpen);
        return true;
    }

    public void PersistOpenPlanOrder(IReadOnlyList<string> orderedOpenPlanIds)
    {
        if (orderedOpenPlanIds.Count == 0)
            return;

        var currentSet = _documents.Document.Settings.OpenPlanIds.ToHashSet(StringComparer.Ordinal);
        var ordered = orderedOpenPlanIds
            .Where(currentSet.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(_documents.Document.Settings.OpenPlanIds.Where(id => !ordered.Contains(id, StringComparer.Ordinal)));
        if (_documents.Document.Settings.OpenPlanIds.SequenceEqual(ordered, StringComparer.Ordinal) &&
            ordered.Select((id, index) => _documents.Document.Plans.FirstOrDefault(x => x.PlanId == id)?.DisplayOrder == index).All(x => x))
        {
            return;
        }

        _documents.EnsureMutationAllowed();
        _documents.Document.Settings.OpenPlanIds = ordered;
        PlannerDomainService.ApplyPlanDisplayOrder(_documents.Document.Plans, ordered);
        _documents.Save("plan.reorder-tabs");
        ReloadFromDocument();
    }

    public void DeleteCurrentPlan()
    {
        if (CurrentPlan is not null)
            DeletePlan(CurrentPlan);
    }

    public ValidationResult DeletePlan(SelectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var livePlan = ResolveLivePlan(plan);
        if (livePlan is null)
            return Error("PlanUnavailable");

        _documents.CaptureUndo();
        _tabCreatedPlanHistoryState = _tabCreatedPlanHistoryState.Remove(livePlan.PlanId);
        _documents.Document.Plans.Remove(livePlan);
        _documents.Document.Settings.OpenPlanIds.Remove(livePlan.PlanId);
        if (string.Equals(
                _documents.Document.Settings.CurrentPlanId,
                livePlan.PlanId,
                StringComparison.Ordinal))
        {
            _documents.Document.Settings.CurrentPlanId = null;
        }
        _documents.Save("plan.delete");
        return new ValidationResult();
    }

    public void ClearCurrentPlan()
    {
        if (CurrentPlan is not null)
            ClearPlan(CurrentPlan);
    }

    public ValidationResult ClearPlan(SelectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var livePlan = ResolveLivePlan(plan);
        if (livePlan is null)
            return Error("PlanUnavailable");
        if (livePlan.Snapshots.Count == 0)
            return new ValidationResult();

        _documents.CaptureUndo();
        livePlan.Snapshots.Clear();
        livePlan.ModifiedAt = DateTimeOffset.UtcNow;
        _documents.Save("plan.clear");
        return new ValidationResult();
    }

    public bool PersistRegistrationOrder(IReadOnlyList<string> orderedSnapshotIds)
    {
        if (CurrentPlan is not { } currentPlan)
            return false;

        return PersistRegistrationOrder(currentPlan.PlanId, orderedSnapshotIds, notify: true);
    }

    public bool PersistRegistrationOrder(
        string planId,
        IReadOnlyList<string> orderedSnapshotIds,
        bool notify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentNullException.ThrowIfNull(orderedSnapshotIds);

        var plan = _documents.Document.Plans.FirstOrDefault(candidate =>
            string.Equals(candidate.PlanId, planId, StringComparison.Ordinal));
        if (plan is null)
            return false;

        var expectedSnapshotIds = plan.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .ToHashSet(StringComparer.Ordinal);
        var requestedSnapshotIds = orderedSnapshotIds.ToHashSet(StringComparer.Ordinal);
        if (orderedSnapshotIds.Count != plan.Snapshots.Count ||
            requestedSnapshotIds.Count != orderedSnapshotIds.Count ||
            !requestedSnapshotIds.SetEquals(expectedSnapshotIds))
        {
            return false;
        }

        var currentOrder = RegistrationPriorityService.Analyze(
                plan,
                _documents.Document.CourseLibrary)
            .OrderBy(item => item.CurrentOrder)
            .Select(item => item.SnapshotId)
            .ToList();
        if (currentOrder.SequenceEqual(orderedSnapshotIds, StringComparer.Ordinal))
            return false;

        _documents.CaptureUndo();
        if (!RegistrationPriorityService.ApplyOrder(plan, orderedSnapshotIds))
            return false;

        plan.ModifiedAt = DateTimeOffset.UtcNow;
        _documents.Save("plan.reorder-registration", notify);
        if (!notify)
            ObserveTabCreatedPlanModifications();
        if (!notify && string.Equals(CurrentPlan?.PlanId, plan.PlanId, StringComparison.Ordinal))
            OnPropertyChanged(nameof(CurrentPlan));
        return true;
    }

    public AddCourseResult AddCourseToCurrentPlan(CourseOffering course, DuplicateResolution duplicate, ConflictResolution conflict)
    {
        if (CurrentPlan is null || CurrentSemester is null)
            return new AddCourseResult { Cancelled = true };
        return AddCourseToPlan(CurrentPlan, course, duplicate, conflict);
    }

    public AddCourseResult AddCourseToPlan(SelectionPlan plan, CourseOffering course, DuplicateResolution duplicate, ConflictResolution conflict)
    {
        var context = CreateCourseAddContext();
        var prepared = PrepareCourseAdd(plan, course, duplicate, conflict, context);
        if (!prepared.Preview.Added)
            return prepared.Preview;

        var capacity = ValidatePreparedCourseAdd(
            prepared,
            TotalSnapshotCount(),
            PlannerDocumentTextCapacity.Count(_documents.Document),
            prepared.AddsLibraryCourse,
            pendingNewCourseCount: 0);
        if (!capacity.IsValid)
            return CancelPreparedCourseAdd(prepared, capacity);

        _documents.CaptureUndo();
        var result = CommitCourseAdd(prepared, context);
        _documents.Save("plan.add-course");
        return result;
    }

    public BulkAddCourseResult AddCourseToPlans(IEnumerable<SelectionPlan> plans, CourseOffering course, DuplicateResolution duplicate, ConflictResolution conflict)
    {
        var decisions = plans
            .DistinctBy(x => x.PlanId)
            .Select(plan => new BulkAddPlanDecision
            {
                Plan = plan,
                DuplicateResolution = duplicate,
                ConflictResolution = conflict
            })
            .ToList();
        return AddCourseToPlans(decisions, course);
    }

    public BulkAddCourseResult AddCourseToPlans(IEnumerable<BulkAddPlanDecision> planDecisions, CourseOffering course)
    {
        var decisions = planDecisions.DistinctBy(x => x.Plan.PlanId).ToList();
        var result = new BulkAddCourseResult { TargetCount = decisions.Count };
        if (decisions.Count == 0)
            return result;

        var context = CreateCourseAddContext();
        var preparedAdds = decisions
            .Select(decision => PrepareCourseAdd(
                decision.Plan,
                course,
                decision.DuplicateResolution,
                decision.ConflictResolution,
                context))
            .ToList();
        var totalSnapshots = TotalSnapshotCount();
        var pendingSnapshotDelta = 0;
        var effectiveTextCount = PlannerDocumentTextCapacity.Count(_documents.Document);
        var pendingTextDelta = 0L;
        var acceptedNewCourseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prepared in preparedAdds.Where(candidate => candidate.Preview.Added))
        {
            var addsDistinctCourse = prepared.AddsLibraryCourse &&
                                     !acceptedNewCourseIds.Contains(prepared.Source.OfferingId);
            var capacity = ValidatePreparedCourseAdd(
                prepared,
                totalSnapshots + pendingSnapshotDelta,
                effectiveTextCount + pendingTextDelta,
                addsDistinctCourse,
                acceptedNewCourseIds.Count);
            if (!capacity.IsValid)
            {
                CancelPreparedCourseAdd(prepared, capacity);
                continue;
            }

            pendingSnapshotDelta += prepared.SnapshotDelta;
            pendingTextDelta += prepared.PlanTextDelta;
            if (addsDistinctCourse)
                pendingTextDelta += PlannerDocumentTextCapacity.Count(prepared.Source);
            if (addsDistinctCourse)
                acceptedNewCourseIds.Add(prepared.Source.OfferingId);
        }
        var hasMutation = preparedAdds.Any(prepared => prepared.Preview.Added);
        if (hasMutation)
            _documents.CaptureUndo();

        foreach (var prepared in preparedAdds)
        {
            var plan = prepared.Plan;
            var addResult = prepared.Preview.Added
                ? CommitCourseAdd(prepared, context)
                : prepared.Preview;
            CopyValidation(addResult.Validation, result.Validation);
            if (addResult.Cancelled)
            {
                result.Cancelled++;
                result.CancelledPlanNames.Add(plan.PlanName);
                continue;
            }

            if (addResult.ConflictingCourses.Count > 0)
            {
                result.ConflictPlans++;
                result.ConflictPlanNames.Add(plan.PlanName);
                if (prepared.ConflictResolution == ConflictResolution.RemoveConflictingThenAdd)
                    result.ConflictingCoursesRemoved += addResult.ConflictingCourses.Count;
            }

            if (addResult.ReplacedDuplicate)
                result.ReplacedDuplicates++;
            if (addResult.Added)
            {
                result.Added++;
                result.AddedPlanNames.Add(plan.PlanName);
            }
            else
            {
                result.Skipped++;
                result.SkippedPlanNames.Add(plan.PlanName);
            }
        }

        if (hasMutation)
            _documents.Save("plan.bulk-add-course");
        return result;
    }

    private PreparedCourseAdd PrepareCourseAdd(
        SelectionPlan plan,
        CourseOffering course,
        DuplicateResolution duplicate,
        ConflictResolution conflict,
        CourseAddContext context)
    {
        if (!context.LivePlans.Contains(plan))
        {
            return new PreparedCourseAdd(
                plan,
                course,
                duplicate,
                conflict,
                new AddCourseResult { Cancelled = true },
                SnapshotDelta: 0,
                AddsLibraryCourse: false,
                PlanTextDelta: 0,
                ProjectedMeetingRowCount: 0);
        }

        var semester = plan.SemesterId is null
            ? context.NullIdSemester
            : context.SemestersById.GetValueOrDefault(plan.SemesterId);
        if (semester is null)
        {
            return new PreparedCourseAdd(
                plan,
                course,
                duplicate,
                conflict,
                new AddCourseResult { Cancelled = true },
                SnapshotDelta: 0,
                AddsLibraryCourse: false,
                PlanTextDelta: 0,
                ProjectedMeetingRowCount: 0);
        }

        var source = ResolvePreparedSource(course, semester, context);
        var previewPlan = JsonDefaults.Clone(plan);
        var preview = PlannerDomainService.AddCourseToPlan(
            previewPlan,
            source,
            semester,
            duplicate,
            conflict,
            context.CoursesById);
        return new PreparedCourseAdd(
            plan,
            source,
            duplicate,
            conflict,
            preview,
            previewPlan.Snapshots.Count - plan.Snapshots.Count,
            !context.CoursesById.ContainsKey(source.OfferingId),
            PlannerDocumentTextCapacity.Count(previewPlan) - PlannerDocumentTextCapacity.Count(plan),
            PlanRules.CountMeetingRows(
                previewPlan,
                context.CoursesById,
                source));
    }

    private ValidationResult ValidatePreparedCourseAdd(
        PreparedCourseAdd prepared,
        int effectiveTotalSnapshotCount,
        long effectiveTextCharacterCount,
        bool addsNewLibraryCourse,
        int pendingNewCourseCount)
    {
        var validation = new ValidationResult();
        CopyValidation(
            PlannerCapacityRules.ValidateSnapshotChange(
                prepared.Plan.Snapshots.Count,
                effectiveTotalSnapshotCount,
                prepared.SnapshotDelta),
            validation);
        CopyValidation(
            PlanRules.ValidateMeetingRowCount(prepared.ProjectedMeetingRowCount),
            validation);
        if (addsNewLibraryCourse)
        {
            CopyValidation(
                PlannerCapacityRules.ValidateCourseAddition(
                    _documents.Document.CourseLibrary.Count + pendingNewCourseCount,
                    additionalCount: 1),
                validation);
        }
        var textDelta = prepared.PlanTextDelta +
                        (addsNewLibraryCourse
                            ? PlannerDocumentTextCapacity.Count(prepared.Source)
                            : 0);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                effectiveTextCharacterCount,
                textDelta),
            validation);

        return validation;
    }

    private static AddCourseResult CancelPreparedCourseAdd(
        PreparedCourseAdd prepared,
        ValidationResult validation)
    {
        prepared.Preview.Added = false;
        prepared.Preview.Cancelled = true;
        CopyValidation(validation, prepared.Preview.Validation);
        return prepared.Preview;
    }

    private AddCourseResult CommitCourseAdd(PreparedCourseAdd prepared, CourseAddContext context)
    {
        if (!context.CoursesById.TryGetValue(prepared.Source.OfferingId, out var source))
        {
            source = prepared.Source;
            _documents.Document.CourseLibrary.Add(source);
            context.CoursesById.Add(source.OfferingId, source);
        }

        var semester = prepared.Plan.SemesterId is null
            ? context.NullIdSemester
            : context.SemestersById.GetValueOrDefault(prepared.Plan.SemesterId);
        if (semester is null)
            throw new InvalidOperationException("The prepared plan semester is no longer available.");
        return PlannerDomainService.AddCourseToPlan(
            prepared.Plan,
            source,
            semester,
            prepared.DuplicateResolution,
            prepared.ConflictResolution,
            context.CoursesById);
    }

    private CourseAddContext CreateCourseAddContext()
    {
        var document = _documents.Document;
        return new CourseAddContext(
            new HashSet<SelectionPlan>(document.Plans, ReferenceEqualityComparer.Instance),
            document.Semesters
                .Where(semester => semester.SemesterId is not null)
                .DistinctBy(semester => semester.SemesterId, StringComparer.Ordinal)
                .ToDictionary(semester => semester.SemesterId!, StringComparer.Ordinal),
            document.Semesters.FirstOrDefault(semester => semester.SemesterId is null),
            PlanCourseResolver.BuildCourseIndex(document.CourseLibrary),
            new Dictionary<string, CourseOffering>(StringComparer.Ordinal));
    }

    private CourseOffering ResolvePreparedSource(
        CourseOffering course,
        Semester semester,
        CourseAddContext context)
    {
        if (semester.SemesterId is not null &&
            context.PreparedSourcesBySemesterId.TryGetValue(semester.SemesterId, out var prepared))
        {
            return prepared;
        }

        var candidate = string.Equals(course.SemesterId, semester.SemesterId, StringComparison.Ordinal)
            ? JsonDefaults.Clone(course)
            : PlannerDomainService.CopyCourseToSemester(
                course,
                semester.SemesterId!,
                _documents.Document.CourseLibrary.Count);
        CourseIdentityService.AssignOfferingId(candidate);
        var source = context.CoursesById.GetValueOrDefault(candidate.OfferingId) ?? candidate;
        if (semester.SemesterId is not null)
            context.PreparedSourcesBySemesterId.Add(semester.SemesterId, source);
        return source;
    }

    public void RemoveCourseFromCurrentPlan(string offeringId)
    {
        if (CurrentPlan is null ||
            !CurrentPlan.Snapshots.Any(snapshot =>
                string.Equals(snapshot.CourseOfferingId, offeringId, StringComparison.Ordinal)))
            return;
        _documents.CaptureUndo();
        CurrentPlan.Snapshots.RemoveAll(x => string.Equals(x.CourseOfferingId, offeringId, StringComparison.Ordinal));
        CurrentPlan.ModifiedAt = DateTimeOffset.UtcNow;
        _documents.Save("plan.remove-course");
    }

    public void DeleteLibraryCourse(CourseOffering course)
    {
        if (!_documents.Document.CourseLibrary.Any(candidate => ReferenceEquals(candidate, course)))
            return;

        _documents.CaptureUndo();
        _documents.Document.CourseLibrary.Remove(course);
        PlannerDomainService.RemoveCourseReferences(_documents.Document, [course.OfferingId]);
        var clearSelectedCourse = SelectedCourse is not null &&
                                  string.Equals(SelectedCourse.OfferingId, course.OfferingId, StringComparison.Ordinal);
        var clearActiveEdit = ActiveEdit is not null &&
                              string.Equals(ActiveEdit.OriginalOfferingId, course.OfferingId, StringComparison.Ordinal);
        _documents.Save("library.delete-course");
        if (clearSelectedCourse)
            SelectedCourse = null;
        if (clearActiveEdit)
            ActiveEdit = null;
    }

    public ValidationResult SaveLibraryCourseEdit(CourseOffering editedCourse, string? originalOfferingId, bool forceOutOfRange = false)
    {
        return SaveCourseEdit(editedCourse, originalOfferingId, addToPlanId: null, clearActiveEdit: false, forceOutOfRange);
    }

    public CourseEditSession BeginNewCourseEdit(int? weekday = null, int? period = null, bool addToCurrentPlan = false)
    {
        if (CurrentSemester is null)
            throw new InvalidOperationException("No semester is selected.");
        var course = CourseFactory.CreateBlank(
            CurrentSemester,
            _documents.Document.CourseLibrary.Count,
            weekday ?? 1,
            period ?? 1);
        var edit = new CourseEditSession(course, sourceCourse: null, addToCurrentPlan ? CurrentPlan?.PlanId : null);
        ActivateEdit(edit, sourceCourse: null);
        return edit;
    }

    public void BeginEditLibraryCourse(CourseOffering course)
    {
        var liveCourse = ResolveLiveCourse(course);
        if (liveCourse is null)
            return;
        ActivateEdit(new CourseEditSession(liveCourse, liveCourse), liveCourse);
    }

    public void BeginEditPlanSnapshot(PlanCourseSnapshot snapshot)
    {
        var course = PlanCourseResolver.CourseForSnapshot(snapshot, _documents.Document.CourseLibrary);
        if (course is not null)
            BeginEditLibraryCourse(course);
    }

    public ValidationResult SaveActiveCourseEdit(bool forceOutOfRange = false)
    {
        if (ActiveEdit is null)
        {
            var validation = new ValidationResult();
            validation.Error("CourseEditNotActive");
            return validation;
        }

        return SaveCourseEdit(ActiveEdit.Course, ActiveEdit.OriginalOfferingId, ActiveEdit.AddToPlanId, clearActiveEdit: true, forceOutOfRange);
    }

    private ValidationResult SaveCourseEdit(
        CourseOffering editedCourse,
        string? originalOfferingId,
        string? addToPlanId,
        bool clearActiveEdit,
        bool forceOutOfRange)
    {
        var course = JsonDefaults.Clone(editedCourse);
        var semester = _documents.Document.Semesters.FirstOrDefault(x => x.SemesterId == course.SemesterId)
                       ?? CurrentSemester;
        var validation = new ValidationResult();
        if (semester is null)
        {
            validation.Error("SemesterRequired");
            return validation;
        }

        var originalCourse = string.IsNullOrWhiteSpace(originalOfferingId)
            ? null
            : _documents.Document.CourseLibrary.FirstOrDefault(existing =>
                string.Equals(existing.OfferingId, originalOfferingId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(originalOfferingId) && originalCourse is null)
        {
            validation.Error("CourseEditSourceMissing");
            return validation;
        }
        if (originalCourse is not null &&
            !string.Equals(originalCourse.SemesterId, semester.SemesterId, StringComparison.Ordinal) &&
            _documents.Document.Plans.Any(plan => plan.Snapshots.Any(snapshot =>
                string.Equals(snapshot.CourseOfferingId, originalCourse.OfferingId, StringComparison.Ordinal))))
        {
            validation.Error("ReferencedCourseSemesterChange");
            return validation;
        }

        course.SemesterId = semester.SemesterId;
        validation = CourseValidator.Validate(course, semester);
        if (originalCourse is null)
        {
            CopyValidation(
                PlannerCapacityRules.ValidateCanAddCourse(_documents.Document.CourseLibrary.Count),
                validation);
        }
        CopyValidation(
            PlannerCapacityRules.ValidateCourseLabelReferenceChange(
                TotalCourseLabelReferenceCount(),
                originalCourse is null ? 0 : CourseLabelReferenceCount(originalCourse),
                CourseLabelReferenceCount(course)),
            validation);
        if (!validation.IsValid)
            return validation;

        ValidateCourseLabels(course, validation);
        if (!validation.IsValid)
            return validation;

        course.Color = CourseColorService.NormalizeUserInput(course.Color);
        CourseIdentityService.AssignOfferingId(course);
        if (_documents.Document.CourseLibrary.Any(existing =>
                string.Equals(existing.OfferingId, course.OfferingId, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(originalOfferingId) ||
                 !string.Equals(existing.OfferingId, originalOfferingId, StringComparison.Ordinal))))
        {
            validation.Error("CourseIdentityDuplicate");
        }

        PreparedCourseAdd? preparedPlanAdd = null;
        if (addToPlanId is not null)
        {
            var plan = _documents.Document.Plans.FirstOrDefault(x => x.PlanId == addToPlanId);
            if (plan is not null)
            {
                preparedPlanAdd = PrepareCourseAdd(
                    plan,
                    course,
                    DuplicateResolution.ReplaceExisting,
                    ConflictResolution.KeepConflict,
                    CreateCourseAddContext());
                CopyValidation(preparedPlanAdd.Preview.Validation, validation);
                if (preparedPlanAdd.Preview.Added)
                {
                    CopyValidation(
                        PlannerCapacityRules.ValidateSnapshotChange(
                            plan.Snapshots.Count,
                            TotalSnapshotCount(),
                            preparedPlanAdd.SnapshotDelta),
                        validation);
                }
            }
        }
        var originalCourseTextCount = originalCourse is null
            ? 0
            : PlannerDocumentTextCapacity.Count(originalCourse);
        var newLabelTextCount = EnumerateCourseLabels(course)
            .Where(label => !_documents.Document.Labels.Any(existing =>
                TextRules.IsSameLabel(existing.Name, label.Name)))
            .Sum(label => (long)label.Name.Length);
        var oldOfferingId = originalCourse?.OfferingId ?? "";
        var changedSnapshotReferenceCount = oldOfferingId.Length == 0
            ? 0L
            : _documents.Document.Plans.Sum(plan =>
                (long)plan.Snapshots.Count(snapshot =>
                    string.Equals(snapshot.CourseOfferingId, oldOfferingId, StringComparison.Ordinal)));
        var snapshotReferenceTextDelta = changedSnapshotReferenceCount *
                                         (course.OfferingId.Length - oldOfferingId.Length);
        var textDelta =
            PlannerDocumentTextCapacity.Count(course) -
            originalCourseTextCount +
            newLabelTextCount +
            snapshotReferenceTextDelta +
            (preparedPlanAdd is { Preview.Added: true } ? preparedPlanAdd.PlanTextDelta : 0);
        CopyValidation(
            PlannerDocumentTextCapacity.ValidateDelta(
                PlannerDocumentTextCapacity.Count(_documents.Document),
                textDelta),
            validation);
        if (!validation.IsValid || (validation.RequiresForce && !forceOutOfRange))
            return validation;

        if (originalCourse is not null &&
            addToPlanId is null &&
            string.Equals(course.OfferingId, originalCourse.OfferingId, StringComparison.Ordinal) &&
            string.Equals(
                CourseEditFingerprint.Capture(course),
                CourseEditFingerprint.Capture(originalCourse),
                StringComparison.Ordinal) &&
            AreCourseLabelsCatalogued(course))
        {
            var publicationState = CapturePublicationState();
            _selectedCourse = originalCourse;
            _isDetailOpen = true;
            if (clearActiveEdit)
                _activeEdit = null;
            PublishStateChanges(publicationState);
            return validation;
        }

        var previousSelectedCourse = _selectedCourse;
        var previousIsDetailOpen = _isDetailOpen;
        _documents.CaptureUndo();
        EnsureCourseLabels(course);
        course.ModifiedAt = DateTimeOffset.UtcNow;
        var oldId = string.IsNullOrWhiteSpace(originalOfferingId) ? course.OfferingId : originalOfferingId;
        UpsertLibraryCourse(course, oldId);
        PlannerDomainService.UpdateCourseReferenceId(_documents.Document, oldId, course.OfferingId);
        if (preparedPlanAdd is not null && preparedPlanAdd.Preview.Added)
        {
            PlannerDomainService.AddCourseToPlan(
                preparedPlanAdd.Plan,
                course,
                semester,
                DuplicateResolution.ReplaceExisting,
                ConflictResolution.KeepConflict,
                _documents.Document.CourseLibrary);
        }

        _selectedCourse = _documents.Document.CourseLibrary.FirstOrDefault(x =>
            x.OfferingId == course.OfferingId);
        if (_selectedCourse is not null)
            _isDetailOpen = true;
        var acceptedStateBeforeSave = _documents.AcceptedStateToken;
        Exception? acceptedSaveException = null;
        try
        {
            _documents.Save("library.course-edit");
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            if (string.Equals(
                    _documents.AcceptedStateToken,
                    acceptedStateBeforeSave,
                    StringComparison.Ordinal))
            {
                throw;
            }

            // DocumentSession may report a post-commit Changed subscriber
            // failure. The course is already durable; finish the selected-course
            // publication batch and then aggregate that accepted-save failure.
            acceptedSaveException = exception;
        }

        var publications = new NonFatalPublicationAggregator();
        if (acceptedSaveException is not null)
        {
            publications.Try(() =>
                ExceptionDispatchInfo.Capture(acceptedSaveException).Throw());
        }
        if (!ReferenceEquals(previousSelectedCourse, _selectedCourse))
            publications.Try(() => OnPropertyChanged(nameof(SelectedCourse)));
        if (previousIsDetailOpen != _isDetailOpen)
            publications.Try(() => OnPropertyChanged(nameof(IsDetailOpen)));
        publications.ThrowIfAny();
        return validation;
    }

    private void UpsertLibraryCourse(CourseOffering course, string? originalOfferingId)
    {
        if (!string.IsNullOrWhiteSpace(originalOfferingId))
            _documents.Document.CourseLibrary.RemoveAll(x =>
                string.Equals(x.OfferingId, originalOfferingId, StringComparison.Ordinal) &&
                !string.Equals(x.OfferingId, course.OfferingId, StringComparison.Ordinal));

        var existing = _documents.Document.CourseLibrary.FindIndex(x => x.OfferingId == course.OfferingId);
        if (existing >= 0)
            _documents.Document.CourseLibrary[existing] = course;
        else
            _documents.Document.CourseLibrary.Add(course);
    }

    private void ValidateCourseLabels(CourseOffering course, ValidationResult validation)
    {
        var requested = EnumerateCourseLabels(course).ToList();
        foreach (var group in requested.GroupBy(
                     x => TextRules.NormalizeIdentityText(x.Name),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(x => x.Kind).Distinct().Count() > 1)
                validation.Error("LabelNameDuplicate");
        }

        foreach (var label in requested)
        {
            var existing = _documents.Document.Labels.FirstOrDefault(x =>
                TextRules.IsSameLabel(x.Name, label.Name));
            if (existing is not null && existing.Kind != label.Kind)
                validation.Error("LabelNameDuplicate", label.Name);
        }

        var newLabelCount = requested.Count(label =>
            !_documents.Document.Labels.Any(existing =>
                TextRules.IsSameLabel(existing.Name, label.Name)));
        CopyValidation(
            PlannerCapacityRules.ValidateLabelAddition(
                _documents.Document.Labels.Count,
                newLabelCount),
            validation);
    }

    private void EnsureCourseLabels(CourseOffering course)
    {
        foreach (var label in EnumerateCourseLabels(course))
        {
            if (_documents.Document.Labels.Any(x => TextRules.IsSameLabel(x.Name, label.Name)))
                continue;
            _documents.Document.Labels.Add(new CourseLabel
            {
                Name = label.Name,
                Kind = label.Kind,
                DisplayOrder = _documents.Document.Labels.Count(x => x.Kind == label.Kind)
            });
        }
    }

    private bool AreCourseLabelsCatalogued(CourseOffering course) =>
        EnumerateCourseLabels(course).All(requested =>
            _documents.Document.Labels.Any(existing =>
                existing.Kind == requested.Kind &&
                TextRules.IsSameLabel(existing.Name, requested.Name)));

    private static IEnumerable<(string Name, LabelKind Kind)> EnumerateCourseLabels(CourseOffering course)
    {
        foreach (var label in course.Labels.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return (label, LabelKind.Ordinary);
        if (!string.IsNullOrWhiteSpace(course.CourseGroupType))
            yield return (course.CourseGroupType.Trim(), LabelKind.CourseGroupType);
        if (!string.IsNullOrWhiteSpace(course.StudyType))
            yield return (course.StudyType.Trim(), LabelKind.StudyType);
    }

    public void UpdateActiveCourseEdit(Action<CourseOffering> update)
    {
        if (ActiveEdit is null)
            return;
        update(ActiveEdit.Course);
        OnPropertyChanged(nameof(ActiveEdit));
        OnPropertyChanged(nameof(HasUnsavedCourseEdit));
    }

    public void DiscardActiveCourseEdit()
    {
        if (ActiveEdit is null)
            return;
        ActiveEdit = null;
        SelectedCourse = null;
    }

    public ImportPreview PreviewImportJson(string text) =>
        ImportExportService.PreviewJson(_documents.Document, text);

    public ImportApplyResult ApplyImportPreview(ImportPreview preview, ImportApplyOptions options)
    {
        var candidate = JsonDefaults.Clone(_documents.Document);
        var result = ImportExportService.ApplyImport(candidate, preview, options);
        if (!result.Applied)
            return result;

        _documents.CaptureUndo();
        _documents.ReplaceDocument(candidate, "import.apply");
        return result;
    }

    public List<SlotDifference> GetCurrentDifferences()
    {
        if (BaseComparePlan is null || CurrentPlan is null || CurrentSemester is null)
            return new List<SlotDifference>();
        return PlannerDomainService.Compare(BaseComparePlan, CurrentPlan, CurrentSemester, CurrentWeek, _documents.Document.CourseLibrary);
    }

    public void SwapComparison()
    {
        if (BaseComparePlan is null || CurrentPlan is null)
            return;
        _documents.EnsureMutationAllowed();
        var capturedHere = CapturePendingTransientContext();
        var previousBase = BaseComparePlan;
        var previousCurrent = CurrentPlan;
        if (!TryOpenPlan(previousBase, out _))
        {
            if (capturedHere)
                CommitPendingPlanTabTransientState();
            return;
        }

        BaseComparePlan = previousCurrent;
        ViewMode = PlannerViewMode.Comparison;
    }

    public void OpenComparison(SelectionPlan basePlan, SelectionPlan currentPlan)
    {
        var liveBasePlan = ResolveLivePlan(basePlan);
        var liveCurrentPlan = ResolveLivePlan(currentPlan);
        if (liveBasePlan is null ||
            liveCurrentPlan is null ||
            string.Equals(liveBasePlan.PlanId, liveCurrentPlan.PlanId, StringComparison.Ordinal) ||
            OpenPlans.All(openPlan =>
                !string.Equals(openPlan.PlanId, liveBasePlan.PlanId, StringComparison.Ordinal)) ||
            !string.Equals(liveBasePlan.SemesterId, liveCurrentPlan.SemesterId, StringComparison.Ordinal))
        {
            return;
        }
        if (!TryOpenPlan(liveCurrentPlan, out _))
            return;

        BaseComparePlan = liveBasePlan;
        ViewMode = PlannerViewMode.Comparison;
    }

    private SelectionPlan? ResolveLivePlan(SelectionPlan plan) =>
        _documents.Document.Plans.FirstOrDefault(candidate => ReferenceEquals(candidate, plan))
        ?? _documents.Document.Plans.FirstOrDefault(candidate =>
            string.Equals(candidate.PlanId, plan.PlanId, StringComparison.Ordinal));

    private CourseOffering? ResolveLiveCourse(CourseOffering course) =>
        _documents.Document.CourseLibrary.FirstOrDefault(candidate => ReferenceEquals(candidate, course))
        ?? _documents.Document.CourseLibrary.FirstOrDefault(candidate =>
            string.Equals(candidate.OfferingId, course.OfferingId, StringComparison.Ordinal));

    private void PruneComparisonSelection(ISet<string> openPlanIds)
    {
        var before = _selectedComparisonPlanIds.Count;
        _selectedComparisonPlanIds.RemoveAll(id => !openPlanIds.Contains(id));
        if (before != _selectedComparisonPlanIds.Count)
            NotifyComparisonSelectionChanged();
    }

    private void NotifyComparisonSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedComparisonPlanIds));
        OnPropertyChanged(nameof(CanOpenSelectedComparison));
    }

    private void ReconcileComparisonWithCurrentContext()
    {
        var previousBaseComparePlan = _baseComparePlan;
        var previousViewMode = _viewMode;
        ReconcileComparisonWithCurrentContextSilently();
        if (!ReferenceEquals(previousBaseComparePlan, _baseComparePlan))
            OnPropertyChanged(nameof(BaseComparePlan));
        if (previousViewMode != _viewMode)
            OnPropertyChanged(nameof(ViewMode));
    }

    private void ReconcileComparisonWithCurrentContextSilently()
    {
        if (_baseComparePlan is null)
        {
            if (_viewMode == PlannerViewMode.Comparison)
                _viewMode = PlannerViewMode.Week;
            return;
        }

        var basePlanIsOpen = OpenPlans.Any(plan =>
            string.Equals(plan.PlanId, _baseComparePlan.PlanId, StringComparison.Ordinal));
        var currentPlanIsOpen = _currentPlan is not null && OpenPlans.Any(plan =>
            string.Equals(plan.PlanId, _currentPlan.PlanId, StringComparison.Ordinal));
        var hasDistinctPlans = _currentPlan is not null &&
                               !string.Equals(
                                   _baseComparePlan.PlanId,
                                   _currentPlan.PlanId,
                                   StringComparison.Ordinal);
        var hasConsistentSemester = _currentSemester is not null &&
                                    _currentPlan is not null &&
                                    string.Equals(
                                        _baseComparePlan.SemesterId,
                                        _currentPlan.SemesterId,
                                        StringComparison.Ordinal) &&
                                    string.Equals(
                                        _currentPlan.SemesterId,
                                        _currentSemester.SemesterId,
                                        StringComparison.Ordinal);
        if (basePlanIsOpen && currentPlanIsOpen && hasDistinctPlans && hasConsistentSemester)
            return;

        _baseComparePlan = null;
        if (_viewMode == PlannerViewMode.Comparison)
            _viewMode = PlannerViewMode.Week;
    }

    private void ActivateEdit(CourseEditSession edit, CourseOffering? sourceCourse)
    {
        ActiveEdit = edit;
        SelectedCourse = sourceCourse;
        IsDetailOpen = true;
    }

    private int TotalSnapshotCount()
    {
        var total = 0L;
        foreach (var plan in _documents.Document.Plans)
            total += plan.Snapshots.Count;
        return (int)Math.Min(total, int.MaxValue);
    }

    private int TotalCourseLabelReferenceCount()
    {
        var total = 0L;
        foreach (var course in _documents.Document.CourseLibrary)
            total += CourseLabelReferenceCount(course);
        return (int)Math.Min(total, int.MaxValue);
    }

    private static int CourseLabelReferenceCount(CourseOffering course) =>
        course.Labels.Count +
        (string.IsNullOrWhiteSpace(course.CourseGroupType) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(course.StudyType) ? 0 : 1);

    private static ValidationResult Error(string code)
    {
        var validation = new ValidationResult();
        validation.Error(code);
        return validation;
    }

    private static void CopyValidation(ValidationResult source, ValidationResult target)
    {
        foreach (var issue in source.Errors)
        {
            if (!ContainsIssue(target.Errors, issue))
                target.Error(issue.Code, issue.Parameters.ToArray());
        }
        foreach (var issue in source.Warnings)
        {
            if (!ContainsIssue(target.Warnings, issue))
                target.Warning(issue.Code, issue.Parameters.ToArray());
        }
    }

    private static bool ContainsIssue(
        IEnumerable<ValidationIssue> existing,
        ValidationIssue candidate) =>
        existing.Any(issue =>
            string.Equals(issue.Code, candidate.Code, StringComparison.Ordinal) &&
            issue.Parameters.SequenceEqual(candidate.Parameters, StringComparer.Ordinal));

    private string UniquePlanName(string semesterId, string baseName)
        => UniquePlanName(_documents.Document.Plans, semesterId, baseName);

    private static string UniquePlanName(
        IEnumerable<SelectionPlan> existingPlans,
        string semesterId,
        string baseName)
    {
        var existingNames = existingPlans
            .Where(plan => string.Equals(plan.SemesterId, semesterId, StringComparison.Ordinal))
            .Select(plan => plan.PlanName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        while (true)
        {
            var suffix = index == 1 ? "" : $" {index}";
            var name = FitGeneratedPlanName(baseName, suffix);
            if (!existingNames.Contains(name))
                return name;

            index++;
        }
    }

    private static string FitGeneratedPlanName(string baseName, string suffix)
    {
        var maximumBaseLength = WindowsFileNameRules.MaxComponentLength - suffix.Length;
        if (maximumBaseLength <= 0)
            throw new InvalidOperationException("The generated plan-name suffix exceeds the filename limit.");

        var trimmedBase = baseName.Trim();
        if (trimmedBase.Length > maximumBaseLength)
        {
            var elementStarts = StringInfo.ParseCombiningCharacters(trimmedBase);
            var boundaryIndex = Array.BinarySearch(elementStarts, maximumBaseLength);
            var boundary = boundaryIndex >= 0
                ? maximumBaseLength
                : (~boundaryIndex > 0 ? elementStarts[~boundaryIndex - 1] : 0);
            trimmedBase = trimmedBase[..boundary];
        }

        trimmedBase = trimmedBase.TrimEnd(' ', '.');
        if (trimmedBase.Length == 0)
            trimmedBase = "Plan";
        return trimmedBase + suffix;
    }

    private static IEnumerable<string> ParseFilterTokens(string value) =>
        TextTokenParser.SplitTokens(value);

    private static string BoundFilterText(string? value)
        => TextRules.TruncateUtf16(value, PlannerDataLimits.MaxTextFieldLength);

    private string CanonicalizeSearchText(string value)
    {
        var canonical = _localization.Localizer.CanonicalizeKnownLabel(value);
        return string.IsNullOrWhiteSpace(canonical) ? value.Trim() : canonical;
    }

    private string CanonicalizeLabelToken(string value)
    {
        var canonical = _localization.Localizer.CanonicalizeKnownLabel(value);
        return string.IsNullOrWhiteSpace(canonical) ? value.Trim() : canonical;
    }
}

internal sealed class CoordinatedObservableCollection<T> : ObservableCollection<T>
{
    private readonly object _subscriberGate = new();
    private NotifyCollectionChangedEventHandler? _collectionChanged;
    private PropertyChangedEventHandler? _propertyChanged;
    private int _collectionNotificationDepth;

    public override event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add
        {
            lock (_subscriberGate)
                _collectionChanged += value;
        }
        remove
        {
            lock (_subscriberGate)
                _collectionChanged -= value;
        }
    }

    protected override event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            lock (_subscriberGate)
                _propertyChanged += value;
        }
        remove
        {
            lock (_subscriberGate)
                _propertyChanged -= value;
        }
    }

    public void InstallWithoutNotification(IReadOnlyList<T> values)
    {
        Items.Clear();
        foreach (var value in values)
            Items.Add(value);
    }

    protected override void ClearItems()
    {
        CheckCoordinatedReentrancy();
        base.ClearItems();
    }

    protected override void InsertItem(int index, T item)
    {
        CheckCoordinatedReentrancy();
        base.InsertItem(index, item);
    }

    protected override void MoveItem(int oldIndex, int newIndex)
    {
        CheckCoordinatedReentrancy();
        base.MoveItem(oldIndex, newIndex);
    }

    protected override void RemoveItem(int index)
    {
        CheckCoordinatedReentrancy();
        base.RemoveItem(index);
    }

    protected override void SetItem(int index, T item)
    {
        CheckCoordinatedReentrancy();
        base.SetItem(index, item);
    }

    public void PublishReset()
    {
        var propertySubscribers = SnapshotPropertyChangedSubscribers();
        var collectionSubscribers = SnapshotCollectionChangedSubscribers();
        var publications = new NonFatalPublicationAggregator();
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs(nameof(Count))));
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs("Item[]")));
        publications.Try(() => InvokeCollectionChangedSubscribers(
            collectionSubscribers,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)));
        publications.ThrowIfAny();
    }

    public int AddWithoutNotification(T value)
    {
        Items.Add(value);
        return Items.Count - 1;
    }

    public void PublishAdd(T value, int index)
    {
        var propertySubscribers = SnapshotPropertyChangedSubscribers();
        var collectionSubscribers = SnapshotCollectionChangedSubscribers();
        var publications = new NonFatalPublicationAggregator();
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs(nameof(Count))));
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs("Item[]")));
        publications.Try(() => InvokeCollectionChangedSubscribers(
            collectionSubscribers,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                value,
                index)));
        publications.ThrowIfAny();
    }

    public void PublishRemove(T value, int index)
    {
        var propertySubscribers = SnapshotPropertyChangedSubscribers();
        var collectionSubscribers = SnapshotCollectionChangedSubscribers();
        var publications = new NonFatalPublicationAggregator();
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs(nameof(Count))));
        publications.Try(() => InvokePropertyChangedSubscribers(
            propertySubscribers,
            new PropertyChangedEventArgs("Item[]")));
        publications.Try(() => InvokeCollectionChangedSubscribers(
            collectionSubscribers,
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                value,
                index)));
        publications.ThrowIfAny();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e) =>
        InvokePropertyChangedSubscribers(SnapshotPropertyChangedSubscribers(), e);

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e) =>
        InvokeCollectionChangedSubscribers(SnapshotCollectionChangedSubscribers(), e);

    private PropertyChangedEventHandler[] SnapshotPropertyChangedSubscribers()
    {
        lock (_subscriberGate)
        {
            return _propertyChanged?.GetInvocationList()
                       .Cast<PropertyChangedEventHandler>()
                       .ToArray() ?? [];
        }
    }

    private NotifyCollectionChangedEventHandler[] SnapshotCollectionChangedSubscribers()
    {
        lock (_subscriberGate)
        {
            return _collectionChanged?.GetInvocationList()
                       .Cast<NotifyCollectionChangedEventHandler>()
                       .ToArray() ?? [];
        }
    }

    private void InvokePropertyChangedSubscribers(
        IReadOnlyList<PropertyChangedEventHandler> subscribers,
        PropertyChangedEventArgs args)
    {
        var publications = new NonFatalPublicationAggregator();
        foreach (var subscriber in subscribers)
            publications.Try(() => subscriber(this, args));
        publications.ThrowIfAny();
    }

    private void InvokeCollectionChangedSubscribers(
        IReadOnlyList<NotifyCollectionChangedEventHandler> subscribers,
        NotifyCollectionChangedEventArgs args)
    {
        var publications = new NonFatalPublicationAggregator();
        _collectionNotificationDepth++;
        try
        {
            using (BlockReentrancy())
            {
                foreach (var subscriber in subscribers)
                    publications.Try(() => subscriber(this, args));
            }
        }
        finally
        {
            _collectionNotificationDepth--;
        }
        publications.ThrowIfAny();
    }

    private void CheckCoordinatedReentrancy()
    {
        // ObservableCollection's base monitor cannot observe the backing field
        // of this overridden event. Recreate its contract explicitly: one
        // listener may reenter, but mutation with multiple listeners is rejected.
        if (_collectionNotificationDepth > 0 &&
            SnapshotCollectionChangedSubscribers().Length > 1)
        {
            throw new InvalidOperationException(
                "Cannot change an ObservableCollection during a CollectionChanged event.");
        }
    }
}

internal sealed class NonFatalPublicationAggregator
{
    private List<Exception>? _failures;

    public void Try(Action publication)
    {
        try
        {
            publication();
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            (_failures ??= []).Add(exception);
        }
    }

    public void ThrowIfAny()
    {
        if (_failures is null)
            return;

        if (_failures.Count == 1)
            ExceptionDispatchInfo.Capture(_failures[0]).Throw();

        throw new AggregateException(
            "One or more application-state publications failed.",
            _failures);
    }
}

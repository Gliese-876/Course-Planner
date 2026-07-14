using CoursePlanner.Core;
using CoursePlanner.Controls;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using Windows.UI;

namespace CoursePlanner.Pages;

public sealed partial class CourseLibraryPage : Page
{
    private bool _loadingEditor;
    private bool _editingNewCourse;
    private string? _editingOriginalOfferingId;
    private CourseOffering? _editingCourse;
    private readonly CourseEditSessionState _editSession = new();
    private bool _beginLibraryEditInProgress;
    private Task<bool>? _courseEditLeaveOperation;
    private string _lastTreeSignature = "";
    private ApplicationServices? _services;

    public CourseLibraryPage()
    {
        InitializeComponent();
        LibraryCreditsBox.Maximum = (double)CourseNumericRules.MaximumCredits;
        LibraryEnrolledBox.Maximum = CourseNumericRules.MaximumPeopleCount;
        LibraryCapacityBox.Maximum = CourseNumericRules.MaximumPeopleCount;
        ManagementCommandBarStyle.Apply(LibraryCommandBar);
        NewCourseButton.Icon = AppCommandIcons.NewCourse(22);
        AppTypography.Apply(this);
        LibraryTree.CourseInvoked += LibraryTree_CourseInvoked;
        LibraryTree.CourseContextRequested += LibraryTree_CourseContextRequested;
        LibrarySemesterBox.SelectionChanged += LibrarySemesterBox_SelectionChanged;
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth);
        Unloaded += CourseLibraryPage_Unloaded;
    }

    public PlannerViewModel ViewModel { get; private set; } = null!;

    private DocumentSession Documents => _services!.Documents;

    private CourseDisplayFormatter Display => new(ViewModel.T);

    private CourseOffering? SelectedCourse { get; set; }

    public bool HasUnsavedCourseEdit
    {
        get
        {
            if (!_editSession.IsActive)
                return false;
            if (!TryReadEditorCourse(out var course, out _))
                return true;
            return _editSession.HasUnsavedChanges(CourseEditFingerprint.Capture(course));
        }
    }

    public Task<bool> ConfirmLeavingCourseEditAsync()
    {
        if (_courseEditLeaveOperation is not null)
            return _courseEditLeaveOperation;

        var operation = CourseEditLeaveGuard.TryLeaveAsync(
            _editSession.IsActive,
            HasUnsavedCourseEdit,
            PromptCourseEditLeaveChoiceAsync,
            SaveLibraryEditAsync,
            CloseLibraryEditor);
        _courseEditLeaveOperation = operation;
        return AwaitCourseEditLeaveAndClearAsync(operation);
    }

    private async Task<bool> AwaitCourseEditLeaveAndClearAsync(Task<bool> operation)
    {
        try
        {
            return await operation;
        }
        finally
        {
            if (ReferenceEquals(_courseEditLeaveOperation, operation))
                _courseEditLeaveOperation = null;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ApplicationServices services)
            throw new InvalidOperationException("CourseLibraryPage requires ApplicationServices navigation parameter.");
        if (_services is not null)
            return;

        _services = services;
        ViewModel = services.Planner;
        DataContext = ViewModel;
        ViewModel.AllSemesters = true;
        LibrarySemesterBox.ItemsSource = ViewModel.Semesters;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Documents.Changed += Documents_Changed;
        services.Theme.ThemeChanged += Theme_ThemeChanged;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
        ApplyText();
        SyncFilterFields();
        Refresh();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLibraryFilterProperty(e.PropertyName))
            SyncFilterFields();
        Refresh();
    }

    private void Documents_Changed(object? sender, EventArgs e) => Refresh();

    private void CourseLibraryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        LibraryTree.CourseInvoked -= LibraryTree_CourseInvoked;
        LibraryTree.CourseContextRequested -= LibraryTree_CourseContextRequested;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Documents.Changed -= Documents_Changed;
        _services!.Theme.ThemeChanged -= Theme_ThemeChanged;
        _services!.Localization.LanguageChanged -= Localization_LanguageChanged;
        _editSession.End();
        Unloaded -= CourseLibraryPage_Unloaded;
    }

    private void Theme_ThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        _lastTreeSignature = "";
        Refresh();
        UpdateLibraryColorIndicator();
    }

    private void Localization_LanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        ApplyText();
        _lastTreeSignature = "";
        Refresh();
        UpdateLibraryColorIndicator();
    }

    private void ApplyText()
    {
        var t = ViewModel.T;
        PageTitle.Text = t["CourseLibrary"];
        LibraryFilters.ApplyText(key => t[key]);
        NewCourseButton.Label = t["NewCourse"];
        EditCourseButton.Label = t["Edit"];
        AddToPlanButton.Label = t["AddToCurrentPlanCaption"];
        DeleteCourseButton.Label = t["Delete"];
        LibraryEditorTitle.Text = _editingNewCourse ? t["NewCourse"] : t["EditCourseInLibrary"];
        LibraryCourseNameBox.Header = t["Name"];
        LibrarySemesterBox.Header = t["Semesters"];
        LibraryCreditsBox.Header = t["Credits"];
        LibraryTeacherBox.Header = t["Teacher"];
        LibraryLocationBox.Header = t["Location"];
        LibraryEnrolledBox.Header = t["Enrolled"];
        LibraryCapacityBox.Header = t["Capacity"];
        LibraryGroupTypeBox.Header = t["GroupType"];
        LibraryStudyTypeBox.Header = t["StudyType"];
        LibraryLabelsBox.Header = t["Labels"];
        LibraryMeetingsBox.ApplyText(t);
        LibraryColorBox.Header = t["CourseColor"];
        RecommendLibraryColorButton.Content = t["RecommendedColor"];
        LibraryNotesBox.Header = t["Notes"];
        SaveLibraryEditButton.Content = t["Save"];
        DiscardLibraryEditButton.Content = t["Discard"];
        UpdateSelectedCourse();
    }

    private static bool IsLibraryFilterProperty(string? propertyName) =>
        propertyName is nameof(PlannerViewModel.SearchText)
            or nameof(PlannerViewModel.LabelFilterText)
            or nameof(PlannerViewModel.GroupFilterText)
            or nameof(PlannerViewModel.StudyFilterText)
            or nameof(PlannerViewModel.TeacherFilterText)
            or nameof(PlannerViewModel.LocationFilterText);

    private void SyncFilterFields()
    {
        LibraryFilters.SetValues(
            ViewModel.SearchText,
            ViewModel.LabelFilterText,
            ViewModel.GroupFilterText,
            ViewModel.StudyFilterText,
            ViewModel.TeacherFilterText,
            ViewModel.LocationFilterText);
    }

    private void Refresh()
    {
        BuildLibraryTree();
        UpdateSelectedCourse();
    }

    private void BuildLibraryTree()
    {
        var signature = CourseLibraryRenderSignature.Build(
            ViewModel.T.ResolvedLanguage,
            ViewModel.CurrentSemester,
            ViewModel.CurrentPlan,
            ViewModel.CurrentWeek,
            Documents.Document.CourseLibrary,
            ViewModel.LibraryGroups);
        if (string.Equals(signature, _lastTreeSignature, StringComparison.Ordinal))
            return;

        LibraryTree.CourseStatusAutomationName = ViewModel.T["CourseStatusInCurrentPlan"];
        LibraryTree.EmptyText = Documents.Document.CourseLibrary.Count == 0
            ? ViewModel.T["NoCoursesInLibrary"]
            : ViewModel.T["NoMatchingCourses"];
        var statusIndex = CourseLibraryStatusService.CreateIndex(
            ViewModel.CurrentPlan,
            ViewModel.CurrentSemester,
            ViewModel.CurrentWeek,
            Documents.Document.CourseLibrary,
            key => ViewModel.T[key]);
        LibraryTree.SetGroups(CourseLibraryTreeRenderModelBuilder.Build(
            ViewModel.LibraryGroups,
            ViewModel.T,
            Display.CourseListSummary,
            statusIndex.Resolve));
        _lastTreeSignature = signature;
    }

    private void UpdateSelectedCourse()
    {
        var course = SelectedCourse;
        var hasCourse = course is not null;
        EditCourseButton.IsEnabled = hasCourse;
        AddToPlanButton.IsEnabled = hasCourse && ViewModel.CurrentPlan is not null;
        DeleteCourseButton.IsEnabled = hasCourse;

        if (course is null)
        {
            SelectedCourseTitle.Text = ViewModel.T["NoCourseSelected"];
            SelectedCourseSemester.Text = "";
            SelectedCourseTeacher.Text = "";
            SelectedCourseMeetings.ItemsSource = null;
            SelectedCourseLabels.Text = "";
            UpdateLibraryContentScrollMode();
            return;
        }

        var semesterName = ViewModel.Semesters.FirstOrDefault(x => x.SemesterId == course.SemesterId)?.SemesterName ?? "";
        SelectedCourseTitle.Text = course.CourseName;
        SelectedCourseSemester.Text = semesterName;
        SelectedCourseTeacher.Text = $"{course.Teacher} / {course.Location} / {course.Credits:0.#} {ViewModel.T["CreditsShort"]}";
        SelectedCourseMeetings.ItemsSource = Display.MeetingDetails(course);
        SelectedCourseLabels.Text = Display.CourseLabels(course);
        UpdateLibraryContentScrollMode();
    }

    private void UpdateLibraryContentScrollMode()
    {
        var canScroll = SelectedCourse is not null || LibraryEditorPanel.Visibility == Visibility.Visible;
        LibraryContentScrollViewer.VerticalScrollMode = canScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
        LibraryContentScrollViewer.VerticalScrollBarVisibility = canScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void LibraryFilters_SearchTextChanged(object? sender, CourseLibrarySearchChangedEventArgs e)
    {
        ViewModel.SearchText = e.Text;
        LibraryFilters.SetSearchSuggestions(ViewModel.LibraryCourses.Select(x => x.CourseName).Take(8));
    }

    private void LibraryFilters_SearchSubmitted(object? sender, CourseLibrarySearchSubmittedEventArgs e)
    {
        ViewModel.SearchText = e.QueryText;
        LibraryTree.Focus(FocusState.Programmatic);
    }

    private void LibraryFilters_FilterTextChanged(object? sender, EventArgs e)
    {
        var values = LibraryFilters.Values;
        ViewModel.LabelFilterText = values.LabelText;
        ViewModel.GroupFilterText = values.GroupText;
        ViewModel.StudyFilterText = values.StudyText;
        ViewModel.TeacherFilterText = values.TeacherText;
        ViewModel.LocationFilterText = values.LocationText;
    }

    private void SelectCourse(CourseOffering course)
    {
        SelectedCourse = course;
        ViewModel.SelectedCourse = course;
        UpdateSelectedCourse();
    }

    private CourseOffering? ResolveTreeCourse(CourseLibraryCourseEventArgs e) =>
        ViewModel.LibraryCourses.FirstOrDefault(x => string.Equals(x.OfferingId, e.OfferingId, StringComparison.Ordinal))
        ?? Documents.Document.CourseLibrary.FirstOrDefault(x => string.Equals(x.OfferingId, e.OfferingId, StringComparison.Ordinal));

    private void LibraryTree_CourseInvoked(object? sender, CourseLibraryCourseEventArgs e)
    {
        if (ResolveTreeCourse(e) is { } course)
            SelectCourse(course);
    }

    private void LibraryTree_CourseContextRequested(object? sender, CourseLibraryCourseEventArgs e)
    {
        if (ResolveTreeCourse(e) is { } course)
        {
            SelectCourse(course);
            ShowLibraryContextMenu(e.Target, e.Position);
        }
    }

    private void ShowLibraryContextMenu(FrameworkElement target, Windows.Foundation.Point point)
    {
        if (SelectedCourse is null)
            return;
        var menu = AppMaterialLayer.CreateTransientMenuFlyout();
        menu.Items.Add(MenuItem(ViewModel.T["Details"], Symbol.Edit, async (_, _) => await BeginLibraryEditAsync(SelectedCourse)));
        menu.Items.Add(MenuItem(ViewModel.T["AddToCurrentPlanCaption"], Symbol.Add, async (_, _) => await AddSelectedToCurrentPlanAsync()));
        menu.Items.Add(MenuItem(ViewModel.T["Delete"], Symbol.Delete, async (_, _) => await DeleteSelectedCourseAsync()));
        menu.ShowAt(target, point);
    }

    private static MenuFlyoutItem MenuItem(string text, Symbol symbol, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(symbol) };
        item.Click += click;
        return item;
    }

    private async void NewCourse_Click(object sender, RoutedEventArgs e) => await BeginLibraryEditAsync(null);

    private async void EditCourse_Click(object sender, RoutedEventArgs e) => await BeginLibraryEditAsync(SelectedCourse);

    private async void AddToPlan_Click(object sender, RoutedEventArgs e) => await AddSelectedToCurrentPlanAsync();

    private async Task AddSelectedToCurrentPlanAsync()
    {
        if (SelectedCourse is null || ViewModel.CurrentPlan is null || ViewModel.CurrentSemester is null)
            return;

        var source = SelectedCourse.SemesterId == ViewModel.CurrentSemester.SemesterId
            ? SelectedCourse
            : PlannerDomainService.CopyCourseToSemester(SelectedCourse, ViewModel.CurrentSemester.SemesterId, Documents.Document.CourseLibrary.Count);
        var duplicateResolution = DuplicateResolution.SkipExisting;
        if (ViewModel.CurrentPlan.Snapshots.Any(x => string.Equals(x.CourseOfferingId, source.OfferingId, StringComparison.Ordinal)))
        {
            var duplicateDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["DuplicateCourse"],
                Content = SelectedCourse.CourseName,
                PrimaryButtonText = ViewModel.T["ReplaceExisting"],
                CloseButtonText = ViewModel.T["SkipExisting"],
                DefaultButton = ContentDialogButton.Primary
            };
            if (await ContentDialogCoordinator.ShowAsync(duplicateDialog) != ContentDialogResult.Primary)
                return;
            duplicateResolution = DuplicateResolution.ReplaceExisting;
        }

        var conflictResolution = ConflictResolution.KeepConflict;
        var conflicts = PlannerDomainService.FindConflicts(ViewModel.CurrentPlan, source, ViewModel.CurrentSemester, Documents.Document.CourseLibrary).ToList();
        if (conflicts.Count > 0)
        {
            var conflictDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ViewModel.T["Conflict"],
                Content = string.Format(ViewModel.T["ConflictsWith"], string.Join(", ", conflicts.Select(x => x.CourseName))),
                PrimaryButtonText = ViewModel.T["KeepConflict"],
                SecondaryButtonText = ViewModel.T["RemoveConflictsThenAdd"],
                CloseButtonText = ViewModel.T["Cancel"],
                DefaultButton = ContentDialogButton.Close
            };
            var decision = await ContentDialogCoordinator.ShowAsync(conflictDialog);
            if (decision == ContentDialogResult.None)
                return;
            conflictResolution = decision == ContentDialogResult.Secondary
                ? ConflictResolution.RemoveConflictingThenAdd
                : ConflictResolution.KeepConflict;
        }

        var result = ViewModel.AddCourseToCurrentPlan(SelectedCourse, duplicateResolution, conflictResolution);
        if (!result.Validation.IsValid)
        {
            LibraryInfoBar.Title = ViewModel.T["ValidationFailed"];
            LibraryInfoBar.Message = ViewModel.T.ValidationSummary(result.Validation.Errors);
            LibraryInfoBar.Severity = InfoBarSeverity.Error;
            LibraryInfoBar.IsOpen = true;
            return;
        }
        LibraryInfoBar.Title = result.Cancelled ? ViewModel.T["Conflict"] : ViewModel.T["AddedCourse"];
        LibraryInfoBar.Message = result.Cancelled
            ? ViewModel.T["CancelAddForThisPlan"]
            : string.Format(ViewModel.T["AddedCourse"], SelectedCourse.CourseName);
        LibraryInfoBar.Severity = result.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        LibraryInfoBar.IsOpen = true;
    }

    private async void DeleteCourse_Click(object sender, RoutedEventArgs e) => await DeleteSelectedCourseAsync();

    private async Task DeleteSelectedCourseAsync()
    {
        if (SelectedCourse is null)
            return;
        var course = SelectedCourse;
        var affectedPlans = Documents.Document.Plans
            .Where(plan => plan.Snapshots.Any(snapshot => string.Equals(snapshot.CourseOfferingId, course.OfferingId, StringComparison.Ordinal)))
            .Select(plan => plan.PlanName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var message = string.Format(ViewModel.T["DeleteFromLibraryConfirm"], course.CourseName);
        if (affectedPlans.Count > 0)
            message += Environment.NewLine + string.Format(ViewModel.T["DeleteFromLibraryImpact"], affectedPlans.Count, string.Join(", ", affectedPlans));
        if (!await ConfirmAsync(ViewModel.T["Delete"], message))
            return;
        var closesActiveEditor = string.Equals(
            _editingOriginalOfferingId,
            course.OfferingId,
            StringComparison.Ordinal);
        ViewModel.DeleteLibraryCourse(course);
        if (closesActiveEditor)
            CloseLibraryEditor();
        SelectedCourse = null;
        Refresh();
    }

    private async Task BeginLibraryEditAsync(CourseOffering? course)
    {
        if (_beginLibraryEditInProgress)
            return;

        _beginLibraryEditInProgress = true;
        try
        {
            if (!await ConfirmLeavingCourseEditAsync())
                return;

            BeginLibraryEditCore(course);
        }
        finally
        {
            _beginLibraryEditInProgress = false;
        }
    }

    private void BeginLibraryEditCore(CourseOffering? course)
    {
        _loadingEditor = true;
        try
        {
            _editingNewCourse = course is null;
            _editingOriginalOfferingId = course?.OfferingId;
            _editingCourse = course is null ? CreateBlankLibraryCourse() : JsonDefaults.Clone(course);
            LibraryEditorPanel.Visibility = Visibility.Visible;
            LibraryEditorTitle.Text = _editingNewCourse ? ViewModel.T["NewCourse"] : ViewModel.T["EditCourseInLibrary"];
            LoadEditorFields(_editingCourse);
            var displayedCourse = TryReadEditorCourse(out var currentCourse, out _)
                ? currentCourse
                : _editingCourse;
            _editSession.Begin(CourseEditFingerprint.Capture(displayedCourse));
            UpdateLibraryContentScrollMode();
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private CourseOffering CreateBlankLibraryCourse()
    {
        var semester = ViewModel.CurrentSemester ?? ViewModel.Semesters.FirstOrDefault();
        return CourseFactory.CreateBlank(semester, Documents.Document.CourseLibrary.Count);
    }

    private void LoadEditorFields(CourseOffering course)
    {
        var model = CourseEditMapper.FromCourse(course, ViewModel.T);
        ApplyMeetingBoundsForSemester(course.SemesterId);
        LoadLibraryEditModel(model);
        LibrarySemesterBox.SelectedItem = ViewModel.Semesters.FirstOrDefault(x => x.SemesterId == course.SemesterId);
        UpdateLibraryColorIndicator();
    }

    private void LibrarySemesterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || LibrarySemesterBox.SelectedItem is not Semester semester)
            return;

        ApplyMeetingBoundsForSemester(semester.SemesterId);
    }

    private void LibraryNumeric_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingEditor || _editingCourse is null)
            return;

        var numeric = CourseEditNumericInput.Map(LibraryCreditsBox.Value, LibraryEnrolledBox.Value, LibraryCapacityBox.Value);
        if (!numeric.IsValid)
            ShowLibraryInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(numeric.Errors), InfoBarSeverity.Error);
    }

    private void ApplyMeetingBoundsForSemester(string? semesterId)
    {
        var semester = ViewModel.Semesters.FirstOrDefault(candidate =>
            string.Equals(candidate.SemesterId, semesterId, StringComparison.Ordinal));
        LibraryMeetingsBox.MaxPeriod = Math.Max(1, semester?.PeriodSchedule.Count ?? 20);
        LibraryMeetingsBox.WeekCount = Math.Max(1, semester?.WeekCount ?? 16);
    }

    private bool TryReadEditorCourse(out CourseOffering course, out ValidationResult validation)
    {
        course = _editingCourse is null ? CreateBlankLibraryCourse() : JsonDefaults.Clone(_editingCourse);
        var numeric = CourseEditNumericInput.Map(LibraryCreditsBox.Value, LibraryEnrolledBox.Value, LibraryCapacityBox.Value);
        validation = numeric.Validation;
        if (!numeric.IsValid)
            return false;

        CourseEditMapper.ApplyToCourse(course, ReadLibraryEditModel(course.SemesterId, numeric.Value!), ViewModel.T);
        return true;
    }

    private void LoadLibraryEditModel(CourseEditModel model)
    {
        LibraryCourseNameBox.Text = model.CourseName;
        LibraryCreditsBox.Value = (double)model.Credits;
        LibraryTeacherBox.Text = model.Teacher;
        LibraryLocationBox.Text = model.Location;
        LibraryEnrolledBox.Value = model.EnrolledCount ?? double.NaN;
        LibraryCapacityBox.Value = model.Capacity ?? double.NaN;
        LibraryGroupTypeBox.Text = model.CourseGroupType;
        LibraryStudyTypeBox.Text = model.StudyType;
        LibraryLabelsBox.Text = model.LabelsText;
        LibraryMeetingsBox.SetMeetings(model.Meetings);
        LibraryColorBox.Text = model.Color;
        LibraryNotesBox.Text = model.Notes;
    }

    private CourseEditModel ReadLibraryEditModel(string fallbackSemesterId, CourseEditNumericValues numeric) => new()
    {
        SemesterId = (LibrarySemesterBox.SelectedItem as Semester)?.SemesterId
                     ?? ViewModel.CurrentSemester?.SemesterId
                     ?? fallbackSemesterId,
        CourseName = LibraryCourseNameBox.Text,
        Teacher = LibraryTeacherBox.Text,
        Location = LibraryLocationBox.Text,
        Credits = numeric.Credits,
        EnrolledCount = numeric.EnrolledCount,
        Capacity = numeric.Capacity,
        CourseGroupType = LibraryGroupTypeBox.Text,
        StudyType = LibraryStudyTypeBox.Text,
        LabelsText = LibraryLabelsBox.Text,
        Meetings = LibraryMeetingsBox.GetMeetings().ToList(),
        Color = LibraryColorBox.Text,
        Notes = LibraryNotesBox.Text
    };

    private void RecommendLibraryColor_Click(object sender, RoutedEventArgs e)
    {
        LibraryColorBox.Text = CourseColorService.Generate(Documents.Document.CourseLibrary.Count);
    }

    private void LibraryColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLibraryColorIndicator();
    }

    private void UpdateLibraryColorIndicator()
    {
        var valid = CourseColorService.IsValidHex(LibraryColorBox.Text);
        LibraryColorSwatch.Background = valid ? AppBrushes.FromHex(LibraryColorBox.Text) : AppBrushes.Transparent();
        var message = !valid
            ? ViewModel.T["EnterValidHexColorFirst"]
            : CourseColorService.ViolatesGeneratedColorGuidance(LibraryColorBox.Text)
                ? ViewModel.T["CourseColorBrightnessWarning"]
                : "";
        var hasMessage = !string.IsNullOrWhiteSpace(message);
        LibraryColorWarningIcon.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        LibraryColorHintText.Text = message;
        LibraryColorHintText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(LibraryColorWarningIcon, hasMessage ? message : null);
    }

    private async void SaveLibraryEdit_Click(object sender, RoutedEventArgs e) => await SaveLibraryEditAsync();

    private async Task<bool> SaveLibraryEditAsync()
    {
        if (_loadingEditor || _editingCourse is null)
            return false;

        if (!TryReadEditorCourse(out var course, out var inputValidation))
        {
            ShowLibraryInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(inputValidation.Errors), InfoBarSeverity.Error);
            return false;
        }

        var result = ViewModel.SaveLibraryCourseEdit(course, _editingOriginalOfferingId);
        if (!result.IsValid)
        {
            ShowLibraryInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(result.Errors), InfoBarSeverity.Error);
            return false;
        }

        if (result.RequiresForce)
        {
            var force = await ConfirmAsync(ViewModel.T["OutOfRange"], ViewModel.T.ValidationSummary(result.Warnings) + " " + ViewModel.T["SaveAnyway"]);
            if (!force)
                return false;
            result = ViewModel.SaveLibraryCourseEdit(course, _editingOriginalOfferingId, forceOutOfRange: true);
            if (!result.IsValid)
            {
                ShowLibraryInfo(ViewModel.T["ValidationFailed"], ViewModel.T.ValidationSummary(result.Errors), InfoBarSeverity.Error);
                return false;
            }
        }

        SelectedCourse = ViewModel.SelectedCourse;
        CloseLibraryEditor();
        ShowLibraryInfo(ViewModel.T["Saved"], ViewModel.T["LibraryCourseSynced"], InfoBarSeverity.Success);
        Refresh();
        return true;
    }

    private void DiscardLibraryEdit_Click(object sender, RoutedEventArgs e) => CloseLibraryEditor();

    private void CloseLibraryEditor()
    {
        LibraryEditorPanel.Visibility = Visibility.Collapsed;
        _editingCourse = null;
        _editingOriginalOfferingId = null;
        _editingNewCourse = false;
        _editSession.End();
        UpdateLibraryContentScrollMode();
    }

    private async Task<CourseEditLeaveChoice> PromptCourseEditLeaveChoiceAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.T["UnsavedChanges"],
            Content = new TextBlock
            {
                Text = ViewModel.T["SaveCourseEditBeforeChangingDetails"],
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = ViewModel.T["Save"],
            SecondaryButtonText = ViewModel.T["DontSave"],
            CloseButtonText = ViewModel.T["Cancel"],
            DefaultButton = ContentDialogButton.Primary
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => CourseEditLeaveChoice.Save,
            ContentDialogResult.Secondary => CourseEditLeaveChoice.Discard,
            _ => CourseEditLeaveChoice.Cancel
        };
    }

    private void ShowLibraryInfo(string title, string message, InfoBarSeverity severity)
    {
        LibraryInfoBar.Title = title;
        LibraryInfoBar.Message = message;
        LibraryInfoBar.Severity = severity;
        LibraryInfoBar.IsOpen = true;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var responsiveWidth = TwoPaneLayoutService.ResolveWidth(this, width);
        var compact = responsiveWidth < TwoPaneLayoutService.CompactBreakpoint;
        PageTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TwoPaneLayoutService.Apply(this, RootGrid, TreePane, responsiveWidth);
        TwoPaneLayoutService.SizeScrollableContent(LibraryContentScrollViewer, LibraryContentHost, LibraryContentStack, responsiveWidth);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = ViewModel.T["OK"],
            CloseButtonText = ViewModel.T["Cancel"]
        };
        return await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}

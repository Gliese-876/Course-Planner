using CoursePlanner.Controls;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Export;
using CoursePlanner.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace CoursePlanner.Services;

/// <summary>
/// Owns the complete user-facing import/export workflow. Pages only provide the
/// XAML owner and a place to surface non-blocking status messages.
/// </summary>
public sealed class ImportExportCoordinator
{
    private readonly DocumentSession _documents;
    private readonly PlannerViewModel _planner;
    private readonly LocalizationService _localization;
    private readonly IThemeService _theme;
    private readonly BackgroundOperationService _backgroundOperations;

    public ImportExportCoordinator(
        DocumentSession documents,
        PlannerViewModel planner,
        LocalizationService localization,
        IThemeService theme,
        BackgroundOperationService backgroundOperations)
    {
        _documents = documents;
        _planner = planner;
        _localization = localization;
        _theme = theme;
        _backgroundOperations = backgroundOperations;
    }

    public async Task ImportAsync(
        FrameworkElement owner,
        Action<string, InfoBarSeverity, string?> showStatus)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(showStatus);

        if (_backgroundOperations.IsBusy)
            return;

        try
        {
            var picker = new FileOpenPicker(ResolveWindowId(owner))
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            ImportPreview? preview = null;
            await _backgroundOperations.RunAsync(
                Text["ImportCheckingFile"],
                async () =>
                {
                    string json;
                    try
                    {
                        json = await BoundedTextFileReader.ReadAsync(
                            file.Path,
                            PlannerDataLimits.MaxImportFileBytes,
                            PlannerDataLimits.MaxImportTextCharacters);
                    }
                    catch (TextFileLimitExceededException exception)
                    {
                        throw new InvalidDataException(Text["Import.FileTooLarge"], exception);
                    }
                    preview = await Task.Run(() => _planner.PreviewImportJson(json));
                });

            if (preview is null)
                return;

            var decision = await ShowImportPreviewDialogAsync(owner, preview);
            if (decision.Result == ContentDialogResult.Secondary)
            {
                await SaveImportReportAsync(owner, preview, showStatus);
                return;
            }

            if (decision.Result != ContentDialogResult.Primary)
                return;

            decision.Options.SynchronizeMissingPlanCourses = preview.RequiresCourseLibrarySync;

            var result = _planner.ApplyImportPreview(preview, decision.Options);
            showStatus(
                Text[result.Applied ? "ImportCompleted" : "ImportNotApplied"],
                result.Applied ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                null);
        }
        catch (Exception ex) when (IsExpectedWorkflowException(ex))
        {
            await ShowMessageAsync(owner, Text["ImportFailed"], ex.Message);
        }
    }

    public async Task ExportAsync(
        FrameworkElement owner,
        Action<string, InfoBarSeverity, string?> showStatus)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(showStatus);

        if (_backgroundOperations.IsBusy)
            return;

        ExportContentSelection? selectedContent = null;
        ExportSelectionState? optionsDraft = null;
        while (true)
        {
            var content = await ShowExportContentStepAsync(owner, selectedContent);
            if (content is null)
                return;

            if (optionsDraft is not null && optionsDraft.Content != content.Value)
                optionsDraft = null;
            selectedContent = content;

            if (UsesSingleStepExport(content.Value))
            {
                try
                {
                    await ExecuteExportAsync(
                        owner,
                        CreateSingleStepExportState(content.Value),
                        showStatus);
                }
                catch (Exception ex) when (IsExpectedWorkflowException(ex))
                {
                    await ShowMessageAsync(owner, Text["ExportFailed"], ex.Message);
                }
                return;
            }

            var optionsDecision = await ShowExportOptionsStepAsync(owner, content.Value, optionsDraft);
            if (optionsDecision.Result == ExportOptionsResult.Back)
            {
                optionsDraft = optionsDecision.State;
                continue;
            }
            if (optionsDecision.Result != ExportOptionsResult.Accepted || optionsDecision.State is null)
                return;

            try
            {
                await ExecuteExportAsync(owner, optionsDecision.State, showStatus);
            }
            catch (Exception ex) when (IsExpectedWorkflowException(ex))
            {
                await ShowMessageAsync(owner, Text["ExportFailed"], ex.Message);
            }
            return;
        }
    }

    private AppLocalizer Text => _localization.Localizer;

    private async Task<ExportContentSelection?> ShowExportContentStepAsync(
        FrameworkElement owner,
        ExportContentSelection? initialSelection)
    {
        var contentBox = new WheelSafeComboBox
        {
            Header = Text["ExportContentType"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 360
        };
        AutomationProperties.SetAutomationId(contentBox, "ExportContentComboBox");
        AutomationProperties.SetName(contentBox, Text["ExportContentType"]);

        var choices = new[]
        {
            Choice(ExportContentSelection.CurrentWeek, "ExportContentCurrentWeek"),
            Choice(ExportContentSelection.WeekRange, "ExportContentWeekRange"),
            Choice(ExportContentSelection.DetailedSemester, "ExportContentDetailedSemester"),
            Choice(ExportContentSelection.CurrentPlan, "ExportContentCurrentPlan"),
            Choice(ExportContentSelection.CourseLibrary, "ExportContentCourseLibrary"),
            Choice(ExportContentSelection.ShareText, "ExportContentShareText")
        };

        ComboBoxItem? firstEnabled = null;
        ComboBoxItem? contextualDefault = null;
        ComboBoxItem? initialItem = null;
        foreach (var choice in choices)
        {
            var enabled = CanExport(choice.Value);
            var item = new ComboBoxItem
            {
                Tag = choice.Value,
                IsEnabled = enabled,
                Content = choice.Title
            };
            AutomationProperties.SetAutomationId(item, $"ExportContent{choice.Value}");
            AutomationProperties.SetName(item, choice.Title);
            if (!enabled)
                AutomationProperties.SetHelpText(item, Text["ExportContentUnavailable"]);
            contentBox.Items.Add(item);
            firstEnabled ??= enabled ? item : null;
            if (enabled && IsContextualDefault(choice.Value))
                contextualDefault = item;
            if (enabled && initialSelection == choice.Value)
                initialItem = item;
        }

        contentBox.SelectedItem = initialItem ?? contextualDefault ?? firstEnabled;
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = Text["ExportChooseContent"],
                    Style = AppTypography.TextStyle(AppTextRole.Body),
                    TextWrapping = TextWrapping.Wrap
                },
                contentBox
            }
        };
        var dialog = CreateDialog(
            owner,
            Text["Export"],
            panel,
            Text["Next"],
            closeText: Text["Cancel"]);

        void UpdatePrimaryAction()
        {
            var selectedItem = contentBox.SelectedItem as ComboBoxItem;
            dialog.IsPrimaryButtonEnabled = selectedItem is { IsEnabled: true };
            dialog.PrimaryButtonText = selectedItem?.Tag switch
            {
                ExportContentSelection.ShareText => Text["Copy"],
                ExportContentSelection.CurrentPlan or ExportContentSelection.CourseLibrary => Text["Export"],
                _ => Text["Next"]
            };
        }

        contentBox.SelectionChanged += (_, _) => UpdatePrimaryAction();
        UpdatePrimaryAction();

        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return (contentBox.SelectedItem as ComboBoxItem)?.Tag is ExportContentSelection selected
            ? selected
            : null;
    }

    private async Task<ExportOptionsDecision> ShowExportOptionsStepAsync(
        FrameworkElement owner,
        ExportContentSelection content,
        ExportSelectionState? draft)
    {
        if (!IsVisual(content))
            throw new ArgumentOutOfRangeException(nameof(content), content, "Only visual exports have an options step.");

        var hasDraft = draft?.Content == content;
        var formatBox = new RadioButtons
        {
            Header = Text["ExportFormat"],
            MaxColumns = 2,
            SelectedIndex = hasDraft && draft!.Output == ExportOutputSelection.Pdf ? 1 : 0
        };
        AutomationProperties.SetAutomationId(formatBox, "ExportFormatOptions");
        formatBox.Items.Add(Text["ExportFormatPng"]);
        formatBox.Items.Add(Text["ExportFormatPdf"]);

        var rangeStartBox = new NumberBox
        {
            Header = Text["StartWeek"],
            Minimum = 1,
            Maximum = _planner.CurrentSemester?.WeekCount ?? 1,
            Value = hasDraft ? draft!.StartWeek : 1
        };
        var rangeEndBox = new NumberBox
        {
            Header = Text["EndWeek"],
            Minimum = 1,
            Maximum = _planner.CurrentSemester?.WeekCount ?? 1,
            Value = hasDraft ? draft!.EndWeek : _planner.CurrentSemester?.WeekCount ?? 1
        };
        AutomationProperties.SetAutomationId(rangeStartBox, "ExportStartWeekBox");
        AutomationProperties.SetAutomationId(rangeEndBox, "ExportEndWeekBox");
        var rangeGrid = TwoColumnGrid(rangeStartBox, rangeEndBox);
        rangeGrid.Visibility = content == ExportContentSelection.WeekRange
            ? Visibility.Visible
            : Visibility.Collapsed;

        var themeBox = new RadioButtons
        {
            Header = Text["ExportTheme"],
            MaxColumns = 3,
            SelectedIndex = hasDraft
                ? draft!.Theme switch
                {
                    ThemeMode.Light => 1,
                    ThemeMode.Dark => 2,
                    _ => 0
                }
                : 0
        };
        themeBox.Items.Add(string.Format(
            Text["ExportThemeFollowSystemFormat"],
            Text[_theme.ResolveTheme(ThemeMode.FollowSystem) == ResolvedThemeMode.Dark ? "Dark" : "Light"]));
        themeBox.Items.Add(Text["Light"]);
        themeBox.Items.Add(Text["Dark"]);
        AutomationProperties.SetAutomationId(themeBox, "ExportThemeOptions");

        var clarityBox = new WheelSafeComboBox
        {
            Header = Text["ExportClarity"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 360,
            SelectedIndex = hasDraft
                ? draft!.Clarity switch
                {
                    ImageClarity.Standard => 0,
                    ImageClarity.Ultra => 2,
                    ImageClarity.Extreme => 3,
                    ImageClarity.Maximum => 4,
                    _ => 1
                }
                : 1
        };
        clarityBox.Items.Add(Text["ExportClarityStandard"]);
        clarityBox.Items.Add(Text["ExportClarityHigh"]);
        clarityBox.Items.Add(Text["ExportClarityUltra"]);
        clarityBox.Items.Add(Text["ExportClarityExtreme"]);
        clarityBox.Items.Add(Text["ExportClarityMaximum"]);
        AutomationProperties.SetAutomationId(clarityBox, "ExportClarityOptions");

        var fields = CreateCourseFieldControls(
            content == ExportContentSelection.DetailedSemester,
            hasDraft ? draft!.Fields : null);

        var summary = new TextBlock
        {
            Style = AppTypography.TextStyle(AppTextRole.Caption),
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppBrushes.Resource("AppTextSecondaryBrush")
        };
        AutomationProperties.SetAutomationId(summary, "ExportSelectionSummary");
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);

        void UpdateConditionalOptions()
        {
            var pngSelected = formatBox.SelectedIndex == 0;
            clarityBox.Visibility = pngSelected ? Visibility.Visible : Visibility.Collapsed;
            summary.Text = BuildExportSummary(content, formatBox.SelectedIndex, themeBox.SelectedIndex, fields.Values);
        }

        formatBox.SelectionChanged += (_, _) => UpdateConditionalOptions();
        themeBox.SelectionChanged += (_, _) => UpdateConditionalOptions();
        foreach (var (checkBox, _) in fields.Values)
            checkBox.Checked += (_, _) => UpdateConditionalOptions();
        foreach (var (checkBox, _) in fields.Values)
            checkBox.Unchecked += (_, _) => UpdateConditionalOptions();
        UpdateConditionalOptions();

        var panel = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                formatBox,
                rangeGrid,
                themeBox,
                clarityBox,
                fields.Host,
                summary
            }
        };
        var scroll = new ScrollViewer
        {
            MaxHeight = 620,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        var dialog = CreateDialog(
            owner,
            Text["ExportSettings"],
            scroll,
            Text["Export"],
            Text["Back"],
            Text["Cancel"]);

        var result = await ContentDialogCoordinator.ShowAsync(dialog);
        ExportSelectionState CaptureState()
        {
            var maximumWeek = _planner.CurrentSemester?.WeekCount ?? 1;
            var startWeek = SafeWeek(rangeStartBox.Value, 1, maximumWeek);
            var endWeek = SafeWeek(rangeEndBox.Value, maximumWeek, maximumWeek);
            if (endWeek < startWeek)
                (startWeek, endWeek) = (endWeek, startWeek);

            return new ExportSelectionState
            {
                Content = content,
                Output = formatBox.SelectedIndex == 1
                    ? ExportOutputSelection.Pdf
                    : ExportOutputSelection.Png,
                Theme = themeBox.SelectedIndex switch
                {
                    1 => ThemeMode.Light,
                    2 => ThemeMode.Dark,
                    _ => ThemeMode.FollowSystem
                },
                Clarity = clarityBox.SelectedIndex switch
                {
                    0 => ImageClarity.Standard,
                    2 => ImageClarity.Ultra,
                    3 => ImageClarity.Extreme,
                    4 => ImageClarity.Maximum,
                    _ => ImageClarity.High
                },
                Fields = SelectedFields(fields.Values),
                StartWeek = startWeek,
                EndWeek = endWeek
            };
        }

        if (result == ContentDialogResult.Secondary)
            return new ExportOptionsDecision(ExportOptionsResult.Back, CaptureState());
        if (result != ContentDialogResult.Primary)
            return new ExportOptionsDecision(ExportOptionsResult.Cancelled, null);

        return new ExportOptionsDecision(ExportOptionsResult.Accepted, CaptureState());
    }

    private async Task ExecuteExportAsync(
        FrameworkElement owner,
        ExportSelectionState state,
        Action<string, InfoBarSeverity, string?> showStatus)
    {
        if (state.Output == ExportOutputSelection.Clipboard)
        {
            var sharePlan = RequireCurrentPlan();
            var text = new CourseDisplayFormatter(Text)
                .PlanText(_documents.Document, sharePlan, _planner.CurrentWeek);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            showStatus(Text["ShareTextCopied"], InfoBarSeverity.Success, null);
            return;
        }

        if (state.Output == ExportOutputSelection.Json)
        {
            var isPlan = state.Content == ExportContentSelection.CurrentPlan;
            var suggested = isPlan
                ? WindowsFileNameRules.CreateBoundedSuggestion(
                    $"{RequireCurrentSemester().SemesterName}-{RequireCurrentPlan().PlanName}-plan",
                    ".json")
                : "course-library.json";
            if (isPlan && !await ValidateExportNamesAsync(
                    owner,
                    RequireCurrentSemester(),
                    RequireCurrentPlan(),
                    suggested))
            {
                return;
            }
            await SaveTextAsync(
                owner,
                suggested,
                ".json",
                () => isPlan
                    ? ImportExportService.ExportSelectionPlanJson(_documents.Document, RequireCurrentPlan())
                    : ImportExportService.ExportCourseLibraryJson(_documents.Document),
                showStatus);
            return;
        }

        var semester = RequireCurrentSemester();
        var plan = RequireCurrentPlan();
        var extension = state.Output == ExportOutputSelection.Pdf ? ".pdf" : ".png";
        var suggestedName = WindowsFileNameRules.CreateBoundedSuggestion(
            BuildVisualExportBaseName(state),
            extension);
        if (!await ValidateExportNamesAsync(owner, semester, plan, suggestedName))
            return;

        var visualOptions = new TimetableExportOptions
        {
            ContentKind = state.Content switch
            {
                ExportContentSelection.WeekRange => ExportContentKind.WeekRange,
                ExportContentSelection.DetailedSemester => ExportContentKind.DetailedSemester,
                _ => ExportContentKind.CurrentWeek
            },
            FileFormat = state.Output == ExportOutputSelection.Pdf ? ExportFileFormat.Pdf : ExportFileFormat.Png,
            CourseBlockFields = state.Fields,
            ImageClarity = state.Output == ExportOutputSelection.Png ? state.Clarity : null,
            StartWeek = state.StartWeek,
            EndWeek = state.EndWeek
        };
        TimetableExportOptionsValidator.ValidateAndThrow(visualOptions, semester);

        var request = TimetableExportRequestFactory.Create(
            _planner,
            new CourseDisplayFormatter(Text),
            _documents.Document.CourseLibrary,
            visualOptions,
            _theme.ResolveTheme(state.Theme),
            _planner.CurrentWeek);

        var path = await PickSavePathAsync(owner, suggestedName, extension);
        if (path is null)
            return;

        bool completed;
        try
        {
            completed = await _backgroundOperations.RunAsync(
                Text["Export"],
                () => Task.Run(() =>
                {
                    if (state.Output == ExportOutputSelection.Pdf)
                        TimetableExportService.ExportPdf(request, path);
                    else
                        TimetableExportService.ExportPng(request, path);
                }));
        }
        catch (TimetableExportLimitExceededException exception)
        {
            await ShowMessageAsync(
                owner,
                Text["ExportFailed"],
                string.Format(
                    Text[$"ExportLimit.{exception.Kind}"],
                    exception.Actual,
                    exception.Maximum));
            return;
        }
        if (!completed)
            return;
        showStatus(
            string.Format(Text["SavedFile"], Path.GetFileName(path)),
            InfoBarSeverity.Success, path);
    }

    private async Task<ImportDialogDecision> ShowImportPreviewDialogAsync(
        FrameworkElement owner,
        ImportPreview preview)
    {
        var display = new CourseDisplayFormatter(Text);
        var projection = await Task.Run(() =>
            ImportMergePreviewProjectionService.Create(preview, display));

        var reportBox = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            MinHeight = 300,
            MaxHeight = 460,
            Text = projection.Text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(reportBox, "ImportMergeReportBox");
        AutomationProperties.SetName(reportBox, Text["ImportMergeResultsTitle"]);

        var forceMergeBox = OptionCheckBox(
            "ImportForceSemesterMergeBox",
            Text["ForceSemesterMergeConflicts"],
            preview.Items.Any(x => x.CanApplyWithForcedSemesterMerge));
        var updateSemesterBox = OptionCheckBox(
            "ImportUpdateSemesterSettingsBox",
            Text["UpdateLocalSemesterSettings"],
            preview.Items.Any(x => x.RequiresSemesterSettingsDecision));
        var forceOutOfRangeBox = OptionCheckBox(
            "ImportForceOutOfRangeBox",
            Text["ForceImportOutOfRangeCourses"],
            preview.Items.Any(x => x.RequiresForceImport));

        var hasMergeDecisions = forceMergeBox.Visibility == Visibility.Visible ||
                                updateSemesterBox.Visibility == Visibility.Visible ||
                                forceOutOfRangeBox.Visibility == Visibility.Visible;
        var impactMessages = new List<string>();
        if (preview.RequiresCourseLibrarySync)
            impactMessages.Add(Text["ImportCourseSyncPreviewWarning"]);
        if (hasMergeDecisions)
            impactMessages.Add(Text["ImportDecisionWarning"]);

        var impactBar = new InfoBar
        {
            Title = Text["ImportChangesTitle"],
            Message = string.Join(Environment.NewLine, impactMessages),
            Severity = InfoBarSeverity.Warning,
            IsClosable = false,
            IsOpen = impactMessages.Count > 0
        };
        AutomationProperties.SetAutomationId(impactBar, "ImportImpactInfoBar");

        var mergeOptionsPanel = new StackPanel
        {
            Spacing = 8,
            Visibility = hasMergeDecisions ? Visibility.Visible : Visibility.Collapsed,
            Children =
            {
                new TextBlock
                {
                    Text = Text["ImportMergeOptionsTitle"],
                    Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
                },
                forceMergeBox,
                updateSemesterBox,
                forceOutOfRangeBox
            }
        };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                impactBar,
                new TextBlock
                {
                    Text = Text["ImportMergeResultsTitle"],
                    Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
                },
                reportBox,
                mergeOptionsPanel
            }
        };
        var scroll = new ScrollViewer
        {
            MaxHeight = 620,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        var dialog = CreateDialog(
            owner,
            $"{Text["Import"]}: {display.ImportKind(preview.Kind)}",
            scroll,
            preview.CanApply
                ? Text[preview.RequiresCourseLibrarySync ? "ImportSyncAndApply" : "ConfirmImport"]
                : "",
            Text["SaveReport"],
            Text["CancelImport"]);
        dialog.DefaultButton = preview.CanApply ? ContentDialogButton.Primary : ContentDialogButton.Close;
        var result = await ContentDialogCoordinator.ShowAsync(dialog);
        return new ImportDialogDecision
        {
            Result = result,
            Options = new ImportApplyOptions
            {
                ForceSemesterMergeConflicts = forceMergeBox.IsChecked == true,
                UpdateExistingSemesterSettings = updateSemesterBox.IsChecked == true,
                ForceOutOfRangeCourses = forceOutOfRangeBox.IsChecked == true
            }
        };
    }

    private async Task SaveImportReportAsync(
        FrameworkElement owner,
        ImportPreview preview,
        Action<string, InfoBarSeverity, string?> showStatus)
    {
        var formatBox = new RadioButtons
        {
            Header = Text["ReportFormat"],
            MaxColumns = 2,
            SelectedIndex = 0
        };
        formatBox.Items.Add(Text["ReadableText"]);
        formatBox.Items.Add("JSON");
        AutomationProperties.SetAutomationId(formatBox, "ImportReportFormatOptions");
        var dialog = CreateDialog(
            owner,
            Text["SaveImportReport"],
            formatBox,
            Text["Save"],
            closeText: Text["Cancel"]);
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary)
            return;

        if (formatBox.SelectedIndex == 1)
        {
            await SaveTextAsync(
                owner,
                "import-report.json",
                ".json",
                () => ImportExportService.CreatePreviewReportJson(preview),
                showStatus);
        }
        else
        {
            await SaveTextAsync(
                owner,
                "import-report.txt",
                ".txt",
                () => new CourseDisplayFormatter(Text).ImportPreviewReport(preview),
                showStatus);
        }
    }

    private async Task SaveTextAsync(
        FrameworkElement owner,
        string suggested,
        string extension,
        Func<string> createText,
        Action<string, InfoBarSeverity, string?> showStatus)
    {
        ArgumentNullException.ThrowIfNull(createText);
        var path = await PickSavePathAsync(owner, suggested, extension);
        if (path is null)
            return;

        var completed = await _backgroundOperations.RunAsync(
            Text["Export"],
            async () =>
            {
                var text = await Task.Run(createText);
                await AtomicTextFileWriter.WriteAllTextAsync(path, text);
            });
        if (!completed)
            return;
        showStatus(
            string.Format(Text["SavedFile"], Path.GetFileName(path)),
            InfoBarSeverity.Success, path);
    }

    private async Task<string?> PickSavePathAsync(
        FrameworkElement owner,
        string suggested,
        string extension)
    {
        var picker = new FileSavePicker(ResolveWindowId(owner))
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggested
        };
        picker.FileTypeChoices.Add(
            extension.TrimStart('.').ToUpperInvariant(),
            new List<string> { extension });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<bool> ValidateExportNamesAsync(
        FrameworkElement owner,
        Semester semester,
        SelectionPlan plan,
        string completeFileName)
    {
        foreach (var (key, value) in new[]
                 {
                     ("Semester", semester.SemesterName),
                     ("CurrentPlan", plan.PlanName)
                 })
        {
            var result = WindowsFileNameRules.ValidateFileComponent(value.Trim());
            if (result.IsValid)
                continue;
            await ShowMessageAsync(
                owner,
                string.Format(Text["CannotExportInvalidName"], Text[key]),
                Text.ValidationSummary(result.Errors));
            return false;
        }

        var completeNameResult = WindowsFileNameRules.ValidateFileComponent(completeFileName);
        if (!completeNameResult.IsValid)
        {
            await ShowMessageAsync(
                owner,
                string.Format(Text["CannotExportInvalidName"], Text["Export"]),
                Text.ValidationSummary(completeNameResult.Errors));
            return false;
        }
        return true;
    }

    private string BuildWeekExportBaseName(PlannerViewMode mode, int week)
    {
        var semester = RequireCurrentSemester();
        var plan = RequireCurrentPlan();
        var baseName = $"{semester.SemesterName}-{plan.PlanName}";
        return mode == PlannerViewMode.Comparison
            ? $"{baseName}-comparison-week-{week}-{CompactDateRange(SemesterRules.WeekRangeText(semester, week))}"
            : $"{baseName}-week-{week}-{CompactDateRange(SemesterRules.WeekRangeText(semester, week))}";
    }

    private string BuildVisualExportBaseName(ExportSelectionState state)
    {
        var semester = RequireCurrentSemester();
        var plan = RequireCurrentPlan();
        var baseName = $"{semester.SemesterName}-{plan.PlanName}";
        return state.Content switch
        {
            ExportContentSelection.WeekRange =>
                $"{baseName}-weeks-{state.StartWeek}-{state.EndWeek}",
            ExportContentSelection.DetailedSemester =>
                $"{baseName}-whole-semester-{DateDisplay.CompactDate(semester.StartDate)}-{DateDisplay.CompactDate(semester.EndDate)}",
            _ => BuildWeekExportBaseName(
                _planner.ViewMode == PlannerViewMode.Comparison ? PlannerViewMode.Comparison : PlannerViewMode.Week,
                _planner.CurrentWeek)
        };
    }

    private string BuildExportSummary(
        ExportContentSelection content,
        int formatIndex,
        int themeIndex,
        IReadOnlyList<(CheckBox CheckBox, CourseBlockFields Field)> fields)
    {
        var contentText = Text[ContentResourceKey(content)];
        var format = formatIndex == 1 ? Text["ExportFormatPdf"] : Text["ExportFormatPng"];
        var theme = themeIndex switch
        {
            1 => Text["Light"],
            2 => Text["Dark"],
            _ => Text["FollowSystem"]
        };
        var count = fields.Count(item => item.CheckBox.IsChecked == true);
        return string.Format(Text["ExportSummaryVisualFormat"], contentText, format, theme, count);
    }

    private CourseFieldControls CreateCourseFieldControls(
        bool selectAll,
        CourseBlockFields? initialFields)
    {
        var selectedFields = initialFields ?? (selectAll ? CourseBlockFields.All : CourseBlockFields.Default);
        var values = new List<(CheckBox CheckBox, CourseBlockFields Field)>
        {
            (FieldCheckBox("ExportFieldCourseName", Text["ExportFieldCourseName"], true, false), CourseBlockFields.CourseName),
            (FieldCheckBox("ExportFieldTeacher", Text["Teacher"], selectedFields.HasFlag(CourseBlockFields.Teacher)), CourseBlockFields.Teacher),
            (FieldCheckBox("ExportFieldLocation", Text["Location"], selectedFields.HasFlag(CourseBlockFields.Location)), CourseBlockFields.Location),
            (FieldCheckBox("ExportFieldCredits", Text["Credits"], selectedFields.HasFlag(CourseBlockFields.Credits)), CourseBlockFields.Credits),
            (FieldCheckBox("ExportFieldCapacity", Text["Capacity"], selectedFields.HasFlag(CourseBlockFields.Capacity)), CourseBlockFields.Capacity),
            (FieldCheckBox("ExportFieldCourseGroup", Text["GroupType"], selectedFields.HasFlag(CourseBlockFields.CourseGroupType)), CourseBlockFields.CourseGroupType),
            (FieldCheckBox("ExportFieldStudyType", Text["StudyType"], selectedFields.HasFlag(CourseBlockFields.StudyType)), CourseBlockFields.StudyType),
            (FieldCheckBox("ExportFieldLabels", Text["Labels"], selectedFields.HasFlag(CourseBlockFields.Labels)), CourseBlockFields.Labels),
            (FieldCheckBox("ExportFieldNotes", Text["Notes"], selectedFields.HasFlag(CourseBlockFields.Notes)), CourseBlockFields.Notes)
        };
        AutomationProperties.SetHelpText(values[0].CheckBox, Text["ExportCourseNameRequired"]);

        var fieldList = new StackPanel { Spacing = 4 };
        foreach (var (checkBox, _) in values)
            fieldList.Children.Add(checkBox);
        var host = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = Text["ExportCourseBlockContent"],
                    Style = AppTypography.TextStyle(AppTextRole.BodyStrong)
                },
                new TextBlock
                {
                    Text = Text["ExportCourseNameRequired"],
                    Style = AppTypography.TextStyle(AppTextRole.Caption),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AppBrushes.Resource("AppTextSecondaryBrush")
                },
                fieldList
            }
        };
        AutomationProperties.SetAutomationId(host, "ExportCourseFieldsPanel");
        return new CourseFieldControls(host, values);
    }

    private static CourseBlockFields SelectedFields(
        IEnumerable<(CheckBox CheckBox, CourseBlockFields Field)> fields)
    {
        var selected = CourseBlockFields.CourseName;
        foreach (var item in fields.Where(item => item.CheckBox.IsChecked == true))
            selected |= item.Field;
        return selected;
    }

    private CheckBox FieldCheckBox(
        string automationId,
        string text,
        bool isChecked,
        bool isEnabled = true)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            IsEnabled = isEnabled
        };
        AutomationProperties.SetAutomationId(checkBox, automationId);
        AutomationProperties.SetName(checkBox, text);
        return checkBox;
    }

    private static CheckBox OptionCheckBox(
        string automationId,
        string text,
        bool visible)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(checkBox, automationId);
        AutomationProperties.SetName(checkBox, text);
        return checkBox;
    }

    private static Grid TwoColumnGrid(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private ContentDialog CreateDialog(
        FrameworkElement owner,
        string title,
        object content,
        string primaryText = "",
        string secondaryText = "",
        string closeText = "")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = AppTypography.TextBlock(title, AppTextRole.Subtitle, TextWrapping.Wrap),
            Content = content,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
            DefaultButton = string.IsNullOrWhiteSpace(primaryText)
                ? ContentDialogButton.Close
                : ContentDialogButton.Primary
        };
        AppTypography.Apply(dialog);
        AppMaterialLayer.ApplySurface(dialog, AppMaterialSurface.Dialog);
        AutomationProperties.SetAutomationId(dialog, "ImportExportDialog");
        return dialog;
    }

    private async Task ShowMessageAsync(
        FrameworkElement owner,
        string title,
        string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = AppTypography.TextBlock(title, AppTextRole.Subtitle, TextWrapping.Wrap),
            Content = AppTypography.TextBlock(message, AppTextRole.Body, TextWrapping.Wrap),
            CloseButtonText = Text["OK"],
            DefaultButton = ContentDialogButton.Close
        };
        AppTypography.Apply(dialog);
        AppMaterialLayer.ApplySurface(dialog, AppMaterialSurface.Dialog);
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private static Microsoft.UI.WindowId ResolveWindowId(FrameworkElement owner) =>
        owner.XamlRoot?.ContentIslandEnvironment?.AppWindowId
        ?? throw new InvalidOperationException("The import/export surface is not attached to a window.");

    private bool CanExport(ExportContentSelection content) =>
        content == ExportContentSelection.CourseLibrary ||
        (_planner.CurrentPlan is not null && _planner.CurrentSemester is not null);

    private bool IsContextualDefault(ExportContentSelection content)
    {
        if (_planner.CurrentPlan is null || _planner.CurrentSemester is null)
            return content == ExportContentSelection.CourseLibrary;
        return _planner.ViewMode switch
        {
            PlannerViewMode.SemesterOverview => content == ExportContentSelection.DetailedSemester,
            _ => content == ExportContentSelection.CurrentWeek
        };
    }

    private static bool IsVisual(ExportContentSelection content) =>
        content is ExportContentSelection.CurrentWeek or
            ExportContentSelection.WeekRange or
            ExportContentSelection.DetailedSemester;

    private static bool UsesSingleStepExport(ExportContentSelection content) =>
        content is ExportContentSelection.CurrentPlan or
            ExportContentSelection.CourseLibrary or
            ExportContentSelection.ShareText;

    private ExportSelectionState CreateSingleStepExportState(ExportContentSelection content) =>
        new()
        {
            Content = content,
            Output = content == ExportContentSelection.ShareText
                ? ExportOutputSelection.Clipboard
                : ExportOutputSelection.Json,
            Theme = ThemeMode.FollowSystem,
            Clarity = ImageClarity.High,
            Fields = CourseBlockFields.Default,
            StartWeek = 1,
            EndWeek = _planner.CurrentSemester?.WeekCount ?? 1
        };

    private ExportContentChoice Choice(
        ExportContentSelection value,
        string titleKey) =>
        new(value, Text[titleKey]);

    private static string ContentResourceKey(ExportContentSelection content) => content switch
    {
        ExportContentSelection.CurrentWeek => "ExportContentCurrentWeek",
        ExportContentSelection.WeekRange => "ExportContentWeekRange",
        ExportContentSelection.DetailedSemester => "ExportContentDetailedSemester",
        ExportContentSelection.CurrentPlan => "ExportContentCurrentPlan",
        ExportContentSelection.CourseLibrary => "ExportContentCourseLibrary",
        ExportContentSelection.ShareText => "ExportContentShareText",
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, null)
    };

    private Semester RequireCurrentSemester() =>
        _planner.CurrentSemester ?? throw new ExportWorkflowPreconditionException(Text["ExportRequiresSemester"]);

    private SelectionPlan RequireCurrentPlan() =>
        _planner.CurrentPlan ?? throw new ExportWorkflowPreconditionException(Text["ExportRequiresPlan"]);

    private static int SafeWeek(double value, int fallback, int maximum) =>
        Math.Clamp(double.IsNaN(value) ? fallback : (int)value, 1, maximum);

    private static string CompactDateRange(string range) =>
        range.Replace(" - ", "-to-", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static bool IsExpectedWorkflowException(Exception exception) =>
        exception is ExportWorkflowPreconditionException ||
        RuntimeOperationExceptionPolicy.IsRecoverable(exception);

    private sealed class ExportWorkflowPreconditionException(string message) : Exception(message);

    private enum ExportContentSelection
    {
        CurrentWeek,
        WeekRange,
        DetailedSemester,
        CurrentPlan,
        CourseLibrary,
        ShareText
    }

    private enum ExportOutputSelection
    {
        Png,
        Pdf,
        Json,
        Clipboard
    }

    private enum ExportOptionsResult
    {
        Cancelled,
        Back,
        Accepted
    }

    private sealed record ExportContentChoice(
        ExportContentSelection Value,
        string Title);

    private sealed class ExportSelectionState
    {
        public ExportContentSelection Content { get; init; }
        public ExportOutputSelection Output { get; init; }
        public ThemeMode Theme { get; init; }
        public ImageClarity Clarity { get; init; }
        public CourseBlockFields Fields { get; init; }
        public int StartWeek { get; init; }
        public int EndWeek { get; init; }
    }

    private sealed record ExportOptionsDecision(
        ExportOptionsResult Result,
        ExportSelectionState? State);

    private sealed record CourseFieldControls(
        FrameworkElement Host,
        IReadOnlyList<(CheckBox CheckBox, CourseBlockFields Field)> Values);

    private sealed class ImportDialogDecision
    {
        public ContentDialogResult Result { get; init; }
        public ImportApplyOptions Options { get; init; } = new();
    }

}

using System.Collections.Specialized;
using System.ComponentModel;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class SettingsPublicationExceptionAdversarialTests
{
    [Fact]
    public void PeriodPublicationFailureCannotSkipPersistenceOrLeaveAHalfInstalledProjection()
    {
        using var fixture = Fixture.Create();
        var initialPeriodCount = fixture.Settings.Periods.Count;
        var renderedPeriodCount = initialPeriodCount;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected Periods subscriber failure.");
        fixture.Settings.Periods.CollectionChanged += throwingSubscriber;
        fixture.Settings.Periods.CollectionChanged += (_, _) =>
            renderedPeriodCount = fixture.Settings.Periods.Count;

        var failure = Record.Exception(() => fixture.Settings.AddPeriodAfter(initialPeriodCount));

        Assert.Equal(
            new PeriodMutationOutcome(
                ContainsPublicationFailure: true,
                SaveEvents: "semester.period-add",
                LivePeriodCount: initialPeriodCount + 1,
                DurablePeriodCount: initialPeriodCount + 1,
                InstalledPeriodCount: initialPeriodCount + 1,
                RenderedPeriodCount: initialPeriodCount + 1,
                MatchingPeriods: true),
            new PeriodMutationOutcome(
                ContainsPublicationFailure: Flatten(failure).Any(exception =>
                    exception is InvalidOperationException &&
                    exception.Message.Contains("Periods subscriber", StringComparison.Ordinal)),
                SaveEvents: string.Join('|', fixture.SaveEvents),
                LivePeriodCount: fixture.SelectedLiveSemester.PeriodSchedule.Count,
                DurablePeriodCount: fixture.SelectedDurableSemester.PeriodSchedule.Count,
                InstalledPeriodCount: fixture.Settings.Periods.Count,
                RenderedPeriodCount: renderedPeriodCount,
                MatchingPeriods: PeriodSignature(fixture.Settings.Periods) ==
                                 PeriodSignature(fixture.SelectedLiveSemester.PeriodSchedule)));
    }

    [Theory]
    [InlineData("update", "semester.period-time")]
    [InlineData("delete", "semester.period-delete")]
    [InlineData("reset", "semester.period-reset")]
    public void EveryPeriodMutationPersistsBeforePublishingItsProjection(
        string operation,
        string expectedSaveEvent)
    {
        using var fixture = Fixture.Create(selectSecondSemester: operation == "reset");
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected Periods subscriber failure.");
        fixture.Settings.Periods.CollectionChanged += throwingSubscriber;
        var first = fixture.Settings.Periods[0];

        var failure = Record.Exception(() =>
        {
            switch (operation)
            {
                case "update":
                    fixture.Settings.UpdatePeriodTime(
                        first.Period,
                        first.Start.AddMinutes(1),
                        first.End.AddMinutes(1));
                    break;
                case "delete":
                    fixture.Settings.DeletePeriod(first.Period);
                    break;
                case "reset":
                    fixture.Settings.ResetDefaultPeriods();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        });

        Assert.Contains(Flatten(failure), exception =>
            exception is InvalidOperationException &&
            exception.Message.Contains("Periods subscriber", StringComparison.Ordinal));
        Assert.Equal(expectedSaveEvent, Assert.Single(fixture.SaveEvents));
        Assert.Equal(
            PeriodSignature(fixture.SelectedDurableSemester.PeriodSchedule),
            PeriodSignature(fixture.SelectedLiveSemester.PeriodSchedule));
        Assert.Equal(
            PeriodSignature(fixture.SelectedLiveSemester.PeriodSchedule),
            PeriodSignature(fixture.Settings.Periods));
    }

    [Fact]
    public void SaveRollbackPublicationFailureStillPublishesEverySettingsProjectionAndLaterRollbackSubscriber()
    {
        using var fixture = Fixture.Create(selectSecondSemester: true);
        var expectedSemesterNames = ItemNames(fixture.Settings.Semesters, semester => semester.SemesterName);
        var expectedLabelNames = ItemNames(fixture.Settings.Labels, label => label.Name);
        var expectedPeriods = PeriodSignature(fixture.Settings.Periods);
        var semesterResetCount = 0;
        var labelResetCount = 0;
        var periodResetCount = 0;
        var selectedSemesterPropertyCount = 0;
        var selectedLabelPropertyCount = 0;
        var databasePathPropertyCount = 0;
        var logsDirectoryPropertyCount = 0;
        var everyObservedProjectionWasCoherent = true;
        var laterRollbackSubscriberCalls = 0;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected Semesters subscriber failure.");
        fixture.Settings.Semesters.CollectionChanged += throwingSubscriber;
        fixture.Settings.Semesters.CollectionChanged += (_, _) =>
        {
            semesterResetCount++;
            everyObservedProjectionWasCoherent &= IsCoherentSettingsProjection(fixture.Settings);
        };
        fixture.Settings.Labels.CollectionChanged += (_, _) =>
        {
            labelResetCount++;
            everyObservedProjectionWasCoherent &= IsCoherentSettingsProjection(fixture.Settings);
        };
        fixture.Settings.Periods.CollectionChanged += (_, _) =>
        {
            periodResetCount++;
            everyObservedProjectionWasCoherent &= IsCoherentSettingsProjection(fixture.Settings);
        };
        fixture.Settings.PropertyChanged += (_, args) =>
        {
            everyObservedProjectionWasCoherent &= IsCoherentSettingsProjection(fixture.Settings);
            switch (args.PropertyName)
            {
                case nameof(SettingsViewModel.SelectedSemester):
                    selectedSemesterPropertyCount++;
                    break;
                case nameof(SettingsViewModel.SelectedLabel):
                    selectedLabelPropertyCount++;
                    break;
                case nameof(SettingsViewModel.DatabasePath):
                    databasePathPropertyCount++;
                    break;
                case nameof(SettingsViewModel.LogsDirectory):
                    logsDirectoryPropertyCount++;
                    break;
            }
        };
        fixture.Session.RolledBack += (_, _) => laterRollbackSubscriberCalls++;
        fixture.FailEventName = "semester.period-time";
        var original = fixture.Settings.Periods[0];

        var failure = Record.Exception(() => fixture.Settings.UpdatePeriodTime(
            original.Period,
            original.Start.AddMinutes(1),
            original.End.AddMinutes(1)));
        var failures = Flatten(failure).ToList();

        Assert.Equal(
            new RollbackPublicationOutcome(
                ContainsSaveFailure: true,
                ContainsPublicationFailure: true,
                SemesterResetCount: 1,
                LabelResetCount: 1,
                PeriodResetCount: 1,
                SelectedSemesterPropertyCount: 1,
                SelectedLabelPropertyCount: 1,
                DatabasePathPropertyCount: 1,
                LogsDirectoryPropertyCount: 1,
                LaterRollbackSubscriberCalls: 1,
                SemesterNames: expectedSemesterNames,
                LabelNames: expectedLabelNames,
                Periods: expectedPeriods,
                EveryObservedProjectionWasCoherent: true),
            new RollbackPublicationOutcome(
                ContainsSaveFailure: failures.Any(exception =>
                    exception is IOException &&
                    exception.Message.Contains("Injected semester.period-time save failure", StringComparison.Ordinal)),
                ContainsPublicationFailure: failures.Any(exception =>
                    exception is InvalidOperationException &&
                    exception.Message.Contains("Semesters subscriber", StringComparison.Ordinal)),
                SemesterResetCount: semesterResetCount,
                LabelResetCount: labelResetCount,
                PeriodResetCount: periodResetCount,
                SelectedSemesterPropertyCount: selectedSemesterPropertyCount,
                SelectedLabelPropertyCount: selectedLabelPropertyCount,
                DatabasePathPropertyCount: databasePathPropertyCount,
                LogsDirectoryPropertyCount: logsDirectoryPropertyCount,
                LaterRollbackSubscriberCalls: laterRollbackSubscriberCalls,
                SemesterNames: ItemNames(fixture.Settings.Semesters, semester => semester.SemesterName),
                LabelNames: ItemNames(fixture.Settings.Labels, label => label.Name),
                Periods: PeriodSignature(fixture.Settings.Periods),
                EveryObservedProjectionWasCoherent: everyObservedProjectionWasCoherent));
    }

    [Fact]
    public void SelectingASemesterInstallsDependentPeriodsBeforePublishingEitherProjection()
    {
        using var fixture = Fixture.Create();
        var target = fixture.Settings.Semesters.Single(semester =>
            semester.SemesterId == Fixture.SecondSemesterId);
        var expectedPeriods = PeriodSignature(target.PeriodSchedule);
        var propertyObservedCoherentState = false;
        var periodObservedCoherentState = false;
        var renderedPeriods = PeriodSignature(fixture.Settings.Periods);
        PropertyChangedEventHandler throwingSubscriber = (_, args) =>
        {
            if (args.PropertyName != nameof(SettingsViewModel.SelectedSemester))
                return;

            propertyObservedCoherentState =
                ReferenceEquals(fixture.Settings.SelectedSemester, target) &&
                PeriodSignature(fixture.Settings.Periods) == expectedPeriods;
            throw new InvalidOperationException("Injected SelectedSemester subscriber failure.");
        };
        fixture.Settings.PropertyChanged += throwingSubscriber;
        fixture.Settings.Periods.CollectionChanged += (_, _) =>
        {
            periodObservedCoherentState =
                ReferenceEquals(fixture.Settings.SelectedSemester, target) &&
                PeriodSignature(fixture.Settings.Periods) == expectedPeriods;
            renderedPeriods = PeriodSignature(fixture.Settings.Periods);
        };

        var failure = Record.Exception(() => fixture.Settings.SelectedSemester = target);
        fixture.Settings.PropertyChanged -= throwingSubscriber;
        fixture.Settings.SelectedSemester = target;

        Assert.Equal(
            new SelectionPublicationOutcome(
                FailureType: nameof(InvalidOperationException),
                InstalledSemester: "Second semester",
                InstalledPeriods: expectedPeriods,
                RenderedPeriods: expectedPeriods,
                PropertyObservedCoherentState: true,
                PeriodObservedCoherentState: true),
            new SelectionPublicationOutcome(
                FailureType: failure?.GetType().Name,
                InstalledSemester: fixture.Settings.SelectedSemester?.SemesterName,
                InstalledPeriods: PeriodSignature(fixture.Settings.Periods),
                RenderedPeriods: renderedPeriods,
                PropertyObservedCoherentState: propertyObservedCoherentState,
                PeriodObservedCoherentState: periodObservedCoherentState));
    }

    [Fact]
    public void NewLabelSelectionPublicationFailureCannotSkipTheAcceptedMutation()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.NewLabelTemplate();
        PropertyChangedEventHandler throwingSubscriber = (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.SelectedLabel))
            {
                throw new InvalidOperationException(
                    "Injected SelectedLabel subscriber failure.");
            }
        };
        fixture.Settings.PropertyChanged += throwingSubscriber;

        var failure = Record.Exception(() =>
            fixture.Settings.UpsertLabel("Adversarial label", LabelKind.Ordinary));

        Assert.Equal(
            new LabelMutationOutcome(
                ContainsPublicationFailure: true,
                SaveEvents: "label.upsert",
                LiveMatches: 1,
                DurableMatches: 1,
                InstalledMatches: 1,
                SelectedLabel: "Adversarial label"),
            new LabelMutationOutcome(
                ContainsPublicationFailure: Flatten(failure).Any(exception =>
                    exception is InvalidOperationException &&
                    exception.Message.Contains("SelectedLabel subscriber", StringComparison.Ordinal)),
                SaveEvents: string.Join('|', fixture.SaveEvents),
                LiveMatches: fixture.Session.Document.Labels.Count(label =>
                    label.Name == "Adversarial label"),
                DurableMatches: fixture.DurableDocument.Labels.Count(label =>
                    label.Name == "Adversarial label"),
                InstalledMatches: fixture.Settings.Labels.Count(label =>
                    label.Name == "Adversarial label"),
                SelectedLabel: fixture.Settings.SelectedLabel?.Name));
    }

    [Fact]
    public void AddSemesterPublicationFailureStillInstallsTheAcceptedSemesterAsTheCompleteSelection()
    {
        using var fixture = Fixture.Create(selectSecondSemester: true);
        var initialSemesterCount = fixture.Settings.Semesters.Count;
        NotifyCollectionChangedEventHandler throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("Injected Semesters subscriber failure.");
        fixture.Settings.Semesters.CollectionChanged += throwingSubscriber;

        var failure = Record.Exception(() =>
            fixture.Settings.TryAddSemester(out _, out _));

        Assert.Contains(Flatten(failure), exception =>
            exception is InvalidOperationException &&
            exception.Message.Contains("Semesters subscriber", StringComparison.Ordinal));
        Assert.Equal("semester.create", Assert.Single(fixture.SaveEvents));
        Assert.Equal(initialSemesterCount + 1, fixture.DurableDocument.Semesters.Count);
        Assert.Equal(initialSemesterCount + 1, fixture.Settings.Semesters.Count);
        Assert.Equal(fixture.Session.Document.Settings.CurrentSemesterId,
            fixture.Settings.SelectedSemester?.SemesterId);
        Assert.Equal(
            PeriodSignature(fixture.Settings.SelectedSemester!.PeriodSchedule),
            PeriodSignature(fixture.Settings.Periods));
    }

    [Fact]
    public void SuccessfulLabelMutationPublishesOneSettingsSnapshotInsteadOfReloadingTwice()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.NewLabelTemplate();
        var semesterPublications = 0;
        var labelPublications = 0;
        var periodPublications = 0;
        fixture.Settings.Semesters.CollectionChanged += (_, _) => semesterPublications++;
        fixture.Settings.Labels.CollectionChanged += (_, _) => labelPublications++;
        fixture.Settings.Periods.CollectionChanged += (_, _) => periodPublications++;

        var validation = fixture.Settings.UpsertLabel("Single publication", LabelKind.Ordinary);

        Assert.True(validation.IsValid);
        Assert.Equal((1, 1, 1), (semesterPublications, labelPublications, periodPublications));
    }

    [Fact]
    public void SemanticNoOpSaveThatReplacesThePeriodGraphRepublishesLiveReferencesExactlyOnce()
    {
        using var fixture = Fixture.Create();
        var oldFirstPeriod = fixture.Settings.Periods[0];
        var periodPublications = 0;
        fixture.Settings.Periods.CollectionChanged += (_, _) => periodPublications++;

        fixture.Settings.ResetDefaultPeriods();

        Assert.Empty(fixture.SaveEvents);
        Assert.Equal(1, periodPublications);
        Assert.NotSame(oldFirstPeriod, fixture.SelectedLiveSemester.PeriodSchedule[0]);
        Assert.True(fixture.Settings.Periods
            .Zip(fixture.SelectedLiveSemester.PeriodSchedule)
            .All(pair => ReferenceEquals(pair.First, pair.Second)));
    }

    private static bool IsCoherentSettingsProjection(SettingsViewModel settings) =>
        settings.SelectedSemester is null
            ? settings.Periods.Count == 0
            : settings.Semesters.Any(semester => ReferenceEquals(semester, settings.SelectedSemester)) &&
              settings.Periods.SequenceEqual(settings.SelectedSemester.PeriodSchedule) &&
              (settings.SelectedLabel is null ||
               settings.Labels.Any(label => ReferenceEquals(label, settings.SelectedLabel)));

    private static string ItemNames<T>(IEnumerable<T> values, Func<T, string> selector) =>
        string.Join('|', values.Select(selector));

    private static string PeriodSignature(IEnumerable<PeriodDefinition> periods) =>
        string.Join('|', periods.Select(period =>
            $"{period.Period}:{period.Start:HH\\:mm}-{period.End:HH\\:mm}"));

    private static IEnumerable<Exception> Flatten(Exception? exception)
    {
        if (exception is null)
            yield break;

        yield return exception;
        switch (exception)
        {
            case DocumentSessionRollbackException rollback:
                foreach (var nested in Flatten(rollback.OperationException))
                    yield return nested;
                foreach (var nested in Flatten(rollback.RollbackException))
                    yield return nested;
                break;
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                    foreach (var nested in Flatten(inner))
                        yield return nested;
                break;
            default:
                foreach (var nested in Flatten(exception.InnerException))
                    yield return nested;
                break;
        }
    }

    private sealed record PeriodMutationOutcome(
        bool ContainsPublicationFailure,
        string SaveEvents,
        int LivePeriodCount,
        int DurablePeriodCount,
        int InstalledPeriodCount,
        int RenderedPeriodCount,
        bool MatchingPeriods);

    private sealed record RollbackPublicationOutcome(
        bool ContainsSaveFailure,
        bool ContainsPublicationFailure,
        int SemesterResetCount,
        int LabelResetCount,
        int PeriodResetCount,
        int SelectedSemesterPropertyCount,
        int SelectedLabelPropertyCount,
        int DatabasePathPropertyCount,
        int LogsDirectoryPropertyCount,
        int LaterRollbackSubscriberCalls,
        string SemesterNames,
        string LabelNames,
        string Periods,
        bool EveryObservedProjectionWasCoherent);

    private sealed record SelectionPublicationOutcome(
        string? FailureType,
        string? InstalledSemester,
        string InstalledPeriods,
        string RenderedPeriods,
        bool PropertyObservedCoherentState,
        bool PeriodObservedCoherentState);

    private sealed record LabelMutationOutcome(
        bool ContainsPublicationFailure,
        string SaveEvents,
        int LiveMatches,
        int DurableMatches,
        int InstalledMatches,
        string? SelectedLabel);

    private sealed class Fixture : IDisposable
    {
        public const string SecondSemesterId = "second-semester";

        private Fixture(
            string directory,
            DocumentSession session,
            SettingsViewModel settings,
            PlannerDocument durableDocument)
        {
            Directory = directory;
            Session = session;
            Settings = settings;
            DurableDocument = durableDocument;
        }

        private string Directory { get; }
        public DocumentSession Session { get; }
        public SettingsViewModel Settings { get; }
        public PlannerDocument DurableDocument { get; private set; }
        public List<string> SaveEvents { get; } = [];
        public string? FailEventName { get; set; }

        public Semester SelectedLiveSemester => Assert.IsType<Semester>(Settings.SelectedSemester);

        public Semester SelectedDurableSemester => DurableDocument.Semesters.Single(semester =>
            semester.SemesterId == SelectedLiveSemester.SemesterId);

        public static Fixture Create(bool selectSecondSemester = false)
        {
            var document = SeedData.Create("First semester", "Persisted plan");
            var second = JsonDefaults.Clone(document.Semesters[0]);
            second.SemesterId = SecondSemesterId;
            second.SemesterName = "Second semester";
            second.DisplayOrder = 1;
            second.PeriodSchedule = PeriodScheduleFactory.CreateDefault12().Take(3).ToList();
            document.Semesters.Add(second);
            DocumentConsistencyService.Ensure(document);

            Fixture? fixture = null;
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"course-planner-settings-publication-{Guid.NewGuid():N}");
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(
                repository,
                loadDocument: () => document,
                saveDocument: (_, eventName) =>
                {
                    fixture?.SaveEvents.Add(eventName);
                    if (eventName == fixture?.FailEventName)
                    {
                        throw new IOException(
                            $"Injected {eventName} save failure.");
                    }
                    if (fixture is not null)
                        fixture.DurableDocument = JsonDefaults.Clone(fixture.Session.Document);
                });
            var settings = new SettingsViewModel(
                session,
                new LocalizationService(session),
                new TestThemeService());
            fixture = new Fixture(
                directory,
                session,
                settings,
                JsonDefaults.Clone(session.Document));
            if (selectSecondSemester)
            {
                settings.SelectedSemester = settings.Semesters.Single(semester =>
                    semester.SemesterId == SecondSemesterId);
                settings.SelectedLabel = settings.Labels.Skip(1).FirstOrDefault();
            }
            return fixture;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class TestThemeService : IThemeService
    {
        public ThemeMode RequestedTheme { get; private set; } = ThemeMode.FollowSystem;
        public ResolvedThemeMode ResolvedTheme => ResolveTheme(RequestedTheme);
        public bool IsHighContrast => false;

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) =>
            requestedTheme == ThemeMode.Dark ? ResolvedThemeMode.Dark : ResolvedThemeMode.Light;

        public void ApplyTheme(ThemeMode theme)
        {
            RequestedTheme = theme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(theme, ResolvedTheme, IsHighContrast));
        }

        public void RefreshTheme() =>
            ThemeChanged?.Invoke(
                this,
                new ThemeChangedEventArgs(RequestedTheme, ResolvedTheme, IsHighContrast));
    }
}

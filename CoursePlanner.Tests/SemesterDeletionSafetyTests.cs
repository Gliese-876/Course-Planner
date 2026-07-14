using System.Collections.Concurrent;
using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class SemesterDeletionSafetyTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    [InlineData("invalid-data")]
    public void BackupFailureLeavesDocumentEventsAndUndoHistoryUntouched(string failureKind)
    {
        using var fixture = Fixture.Create(new ThrowingSemesterDeletionBackup(failureKind));
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        var changedCount = 0;
        fixture.Session.Changed += (_, _) => changedCount++;

        var exception = Record.Exception(() => fixture.Settings.DeleteSelectedSemester());

        var backupException = Assert.IsType<SemesterDeletionBackupException>(exception);
        Assert.NotNull(backupException.InnerException);
        Assert.True(SemesterDeletionBackupFailure.IsExpected(backupException.InnerException));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.Equal(0, changedCount);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.UndoRedo.CanRedo);
    }

    [Fact]
    public void UndoCaptureOccursOnlyAfterTheSafetyBackupCompletes()
    {
        Fixture? fixture = null;
        var backup = new RecordingSemesterDeletionBackup(() =>
        {
            Assert.NotNull(fixture);
            Assert.False(fixture.Session.UndoRedo.CanUndo);
        });
        using (fixture = Fixture.Create(backup))
        {
            var deleted = fixture.Settings.DeleteSelectedSemester();

            Assert.True(deleted);
            Assert.True(backup.Completed);
            Assert.True(fixture.Session.UndoRedo.CanUndo);
            Assert.DoesNotContain(fixture.TargetSemesterId, fixture.Session.Document.Semesters.Select(x => x.SemesterId));
        }
    }

    [Fact]
    public void RepeatedSuccessfulDeletionCreatesTwoDistinctSafetyArtifacts()
    {
        using var fixture = Fixture.Create(new SemesterDeletionBackup());

        Assert.True(fixture.Settings.DeleteSelectedSemester());
        var backupDirectory = Path.Combine(fixture.DirectoryPath, "automatic-backups");
        var firstBackup = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.zip"));
        Assert.True(new FileInfo(firstBackup).Length > 0);

        Assert.True(fixture.Session.Undo());
        fixture.Settings.Reload();
        fixture.Settings.SelectedSemester = fixture.Settings.Semesters.Single(semester =>
            semester.SemesterId == fixture.TargetSemesterId);
        Assert.True(fixture.Settings.DeleteSelectedSemester());

        var backups = Directory.EnumerateFiles(backupDirectory, "*.zip").ToList();
        Assert.Collection(
            backups,
            path => Assert.True(new FileInfo(path).Length > 0),
            path => Assert.True(new FileInfo(path).Length > 0));
        Assert.True(backups.Distinct(StringComparer.OrdinalIgnoreCase).Count() == backups.Count);
    }

    [Fact]
    public void UnexpectedBackupFailurePropagatesWithoutStartingTheTransaction()
    {
        using var fixture = Fixture.Create(new UnexpectedSemesterDeletionBackup());
        var before = JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        Assert.Throws<InvalidOperationException>(() => fixture.Settings.DeleteSelectedSemester());

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Session.Document, JsonDefaults.Options));
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void AutomaticBackupPathsAreUniqueAtTheSameInstant()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 12, 34, 56, 789, TimeSpan.Zero);
        var paths = new ConcurrentBag<string>();

        Parallel.For(1, 513, index =>
        {
            var nonce = Guid.Parse($"{index:x8}-0000-0000-0000-000000000000");
            paths.Add(AutomaticBackupPathFactory.BeforeSemesterDeletion("C:\\data", timestamp, nonce));
        });

        Assert.True(paths.Count == 512);
        Assert.True(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Count);
        Assert.All(paths, path =>
        {
            Assert.Contains("automatic-backups", path, StringComparison.Ordinal);
            Assert.Contains("before-delete-semester-20260713-123456-789-", path, StringComparison.Ordinal);
            Assert.EndsWith(".zip", path, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ClearingAnEmptySemesterAndEmptyPlanCreatesNoUndoOrSave()
    {
        using var fixture = Fixture.Create(new RecordingSemesterDeletionBackup());
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        var changedCount = 0;
        var modifiedAt = fixture.Planner.CurrentPlan!.ModifiedAt;
        fixture.Session.Changed += (_, _) => changedCount++;

        var clearedSemester = fixture.Settings.ClearCurrentSemesterCourses();
        fixture.Planner.ClearCurrentPlan();

        Assert.False(clearedSemester);
        Assert.Equal(modifiedAt, fixture.Planner.CurrentPlan!.ModifiedAt);
        Assert.Equal(eventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.Equal(0, changedCount);
        Assert.False(fixture.Session.UndoRedo.CanUndo);
        Assert.False(fixture.Session.UndoRedo.CanRedo);
    }

    [Fact]
    public void ClearingNonemptySemesterCoursesSavesOnceAndIsUndoable()
    {
        using var fixture = Fixture.Create(new RecordingSemesterDeletionBackup());
        var populatedSemester = fixture.Session.Document.Semesters.First(semester =>
            semester.SemesterId != fixture.TargetSemesterId);
        fixture.Settings.SelectedSemester = populatedSemester;
        var originalCourseIds = fixture.Session.Document.CourseLibrary
            .Where(course => course.SemesterId == populatedSemester.SemesterId)
            .Select(course => course.OfferingId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(originalCourseIds);
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        Assert.True(fixture.Settings.ClearCurrentSemesterCourses());

        Assert.DoesNotContain(fixture.Session.Document.CourseLibrary, course =>
            course.SemesterId == populatedSemester.SemesterId);
        Assert.True(fixture.Session.Repository.ReadEventSummaries().Count == eventCount + 1);
        Assert.True(fixture.Session.Undo());
        Assert.True(originalCourseIds.SetEquals(
            fixture.Session.Document.CourseLibrary
                .Where(course => course.SemesterId == populatedSemester.SemesterId)
                .Select(course => course.OfferingId)));
    }

    [Fact]
    public void ClearingNonemptyPlanSavesOnceAndIsUndoable()
    {
        using var fixture = Fixture.Create(new RecordingSemesterDeletionBackup());
        var populatedPlan = fixture.Session.Document.Plans.First(plan => plan.Snapshots.Count > 0);
        fixture.Planner.CurrentPlan = populatedPlan;
        fixture.Session.UndoRedo.Clear();
        var originalSnapshotIds = populatedPlan.Snapshots
            .Select(snapshot => snapshot.SnapshotId)
            .ToList();
        var eventCount = fixture.Session.Repository.ReadEventSummaries().Count;

        fixture.Planner.ClearCurrentPlan();

        Assert.Empty(populatedPlan.Snapshots);
        Assert.True(fixture.Session.Repository.ReadEventSummaries().Count == eventCount + 1);
        Assert.True(fixture.Session.Undo());
        var restoredPlan = fixture.Session.Document.Plans.Single(plan => plan.PlanId == populatedPlan.PlanId);
        Assert.Equal(originalSnapshotIds, restoredPlan.Snapshots.Select(snapshot => snapshot.SnapshotId));
    }

    [Fact]
    public void DeletingNonCurrentSemesterPreservesCurrentPlanAndSemester()
    {
        using var fixture = Fixture.Create(new RecordingSemesterDeletionBackup());
        var preservedPlan = fixture.Session.Document.Plans.First(plan =>
            plan.SemesterId != fixture.TargetSemesterId &&
            fixture.Session.Document.Settings.OpenPlanIds.Contains(plan.PlanId, StringComparer.Ordinal));
        var preservedSemester = fixture.Session.Document.Semesters.Single(semester =>
            semester.SemesterId == preservedPlan.SemesterId);
        fixture.Planner.CurrentPlan = preservedPlan;
        fixture.Settings.SelectedSemester = fixture.Session.Document.Semesters.Single(semester =>
            semester.SemesterId == fixture.TargetSemesterId);

        Assert.True(fixture.Settings.DeleteSelectedSemester());

        Assert.Same(preservedPlan, fixture.Planner.CurrentPlan);
        Assert.Same(preservedSemester, fixture.Planner.CurrentSemester);
        Assert.Equal(preservedPlan.PlanId, fixture.Session.Document.Settings.CurrentPlanId);
        Assert.Equal(preservedSemester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(preservedPlan.PlanId, reloaded.Settings.CurrentPlanId);
        Assert.Equal(preservedSemester.SemesterId, reloaded.Settings.CurrentSemesterId);
    }

    [Fact]
    public void DeletingCurrentSemesterResolvesAConsistentRemainingContext()
    {
        using var fixture = Fixture.Create(new RecordingSemesterDeletionBackup());
        var expectedSemester = fixture.Session.Document.Semesters.First(semester =>
            semester.SemesterId != fixture.TargetSemesterId);
        var expectedPlan = fixture.Session.Document.Settings.OpenPlanIds
            .Select(planId => fixture.Session.Document.Plans.First(plan => plan.PlanId == planId))
            .FirstOrDefault(plan => plan.SemesterId == expectedSemester.SemesterId);

        Assert.True(fixture.Settings.DeleteSelectedSemester());

        Assert.Same(expectedSemester, fixture.Planner.CurrentSemester);
        Assert.Same(expectedPlan, fixture.Planner.CurrentPlan);
        Assert.Equal(expectedSemester.SemesterId, fixture.Session.Document.Settings.CurrentSemesterId);
        Assert.Equal(expectedPlan?.PlanId, fixture.Session.Document.Settings.CurrentPlanId);
        var reloaded = new SqliteAppRepository(fixture.DirectoryPath).LoadOrCreate();
        Assert.Equal(expectedSemester.SemesterId, reloaded.Settings.CurrentSemesterId);
        Assert.Equal(expectedPlan?.PlanId, reloaded.Settings.CurrentPlanId);
    }

    [Fact]
    public void PageDelegatesDeletionToAwaitableExpectedFailureHandling()
    {
        var page = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "CoursePlanner", "Pages", "SemestersPage.xaml.cs"));

        Assert.Contains("private async Task DeleteSemesterAsync()", page);
        Assert.Contains("await DeleteSemesterAsync();", page);
        Assert.Contains("catch (SemesterDeletionBackupException)", page);
        Assert.Contains("DeleteSemesterBackupFailed", page);
    }

    [Theory]
    [InlineData(LanguageMode.English)]
    [InlineData(LanguageMode.SimplifiedChinese)]
    public void BackupFailureMessageIsLocalized(LanguageMode language)
    {
        var localizer = new AppLocalizer(language);

        Assert.False(string.IsNullOrWhiteSpace(localizer["DeleteSemesterBackupFailed"]));
        Assert.False(SemesterDeletionBackupFailure.IsExpected(new InvalidOperationException("unexpected")));
    }

    private static string FindRepositoryRoot() => RepositoryPaths.Root;

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string directoryPath,
            DocumentSession session,
            PlannerViewModel planner,
            SettingsViewModel settings,
            string targetSemesterId)
        {
            DirectoryPath = directoryPath;
            Session = session;
            Planner = planner;
            Settings = settings;
            TargetSemesterId = targetSemesterId;
        }

        public string DirectoryPath { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel Planner { get; }
        public SettingsViewModel Settings { get; }
        public string TargetSemesterId { get; }

        public static Fixture Create(ISemesterDeletionBackup backup)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var session = new DocumentSession(new SqliteAppRepository(directory));
            var document = TestDocumentFactory.CreatePopulated();
            var targetSemester = JsonDefaults.Clone(document.Semesters[0]);
            targetSemester.SemesterId = "semester-delete-target";
            targetSemester.SemesterName = "Delete target";
            targetSemester.DisplayOrder = document.Semesters.Count;
            document.Semesters.Add(targetSemester);
            var emptyPlan = new SelectionPlan
            {
                PlanId = "empty-delete-target-plan",
                SemesterId = targetSemester.SemesterId,
                PlanName = "Empty target plan",
                DisplayOrder = document.Plans.Count
            };
            document.Plans.Add(emptyPlan);
            document.Settings.CurrentSemesterId = targetSemester.SemesterId;
            document.Settings.CurrentPlanId = emptyPlan.PlanId;
            document.Settings.OpenPlanIds.Add(emptyPlan.PlanId);
            session.ReplaceDocument(document, "test.seed-deletion");
            session.UndoRedo.Clear();
            var localization = new LocalizationService(session);
            var planner = new PlannerViewModel(session, localization);
            var settings = new SettingsViewModel(session, localization, new TestThemeService(), backup)
            {
                SelectedSemester = targetSemester
            };
            return new Fixture(directory, session, planner, settings, targetSemester.SemesterId);
        }

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ThrowingSemesterDeletionBackup(string failureKind) : ISemesterDeletionBackup
    {
        public string Create(string databasePath, string dataDirectory) => throw failureKind switch
        {
            "io" => new IOException("simulated"),
            "unauthorized" => new UnauthorizedAccessException("simulated"),
            "invalid-data" => new InvalidDataException("simulated"),
            _ => new InvalidOperationException("unknown failure kind")
        };
    }

    private sealed class RecordingSemesterDeletionBackup(Action? beforeComplete = null) : ISemesterDeletionBackup
    {
        public bool Completed { get; private set; }

        public string Create(string databasePath, string dataDirectory)
        {
            beforeComplete?.Invoke();
            Completed = true;
            return Path.Combine(dataDirectory, "recorded.zip");
        }
    }

    private sealed class UnexpectedSemesterDeletionBackup : ISemesterDeletionBackup
    {
        public string Create(string databasePath, string dataDirectory) =>
            throw new InvalidOperationException("unexpected");
    }

    private sealed class TestThemeService : IThemeService
    {
        public ThemeMode RequestedTheme { get; private set; }
        public ResolvedThemeMode ResolvedTheme => ResolvedThemeMode.Light;
        public bool IsHighContrast => false;
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
        public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) => ResolvedThemeMode.Light;
        public void ApplyTheme(ThemeMode theme)
        {
            RequestedTheme = theme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(theme, ResolvedTheme, false));
        }
        public void RefreshTheme() => ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(RequestedTheme, ResolvedTheme, false));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;

namespace CoursePlanner.Tests;

public sealed class PlanMeetingRowLimitTests
{
    [Fact]
    public void CoreRuleAllowsExactBoundaryCountsEachReferencedCourseOnce()
    {
        var document = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan);
        var plan = Assert.Single(document.Plans);
        plan.Snapshots.Add(new PlanCourseSnapshot
        {
            SnapshotId = "duplicate-reference-for-counting",
            CourseOfferingId = plan.Snapshots[0].CourseOfferingId
        });

        var validation = PlanRules.ValidateMeetingRows(plan, document.CourseLibrary);

        Assert.True(validation.IsValid);
        Assert.Equal(
            PlannerDataLimits.MaxMeetingRowsPerPlan,
            PlanRules.CountMeetingRows(plan, document.CourseLibrary));
    }

    [Fact]
    public void CoreRuleStopsEnumeratingOnceEveryDistinctReferenceIsResolved()
    {
        var document = CreateDocument(1);
        var plan = Assert.Single(document.Plans);
        var course = Assert.Single(document.CourseLibrary);

        var count = PlanRules.CountMeetingRows(plan, CourseThenThrow(course));

        Assert.Equal(1, count);
    }

    [Fact]
    public void CoreRuleCountsTheFirstCourseWhenInvalidLibraryIdsAreDuplicated()
    {
        var document = CreateDocument(1);
        var plan = Assert.Single(document.Plans);
        var first = Assert.Single(document.CourseLibrary);
        var duplicate = JsonDefaults.Clone(first);
        duplicate.MeetingTimes.Add(new MeetingTime
        {
            Weekday = 2,
            StartPeriod = 2,
            EndPeriod = 2,
            Weeks = "1"
        });

        Assert.Equal(1, PlanRules.CountMeetingRows(plan, [first, duplicate]));
    }

    [Fact]
    public void PersistenceRejectsPlanAboveMeetingRowLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteAppRepository(directory);
            var boundaryDocument = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan);
            repository.Save(boundaryDocument);
            var persistedBefore = Serialize(repository.LoadOrCreate());
            var rejectedDocument = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan + 1);

            var exception = Assert.Throws<RepositoryStateValidationException>(() =>
                repository.Save(rejectedDocument));

            Assert.Contains("Plan.MeetingRows.TooMany", exception.IssueCodes);
            Assert.Equal(persistedBefore, Serialize(repository.LoadOrCreate()));
            Assert.Equal(
                PlannerDataLimits.MaxMeetingRowsPerPlan + 1,
                PlanRules.CountMeetingRows(
                    Assert.Single(rejectedDocument.Plans),
                    rejectedDocument.CourseLibrary));
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            DeleteDirectoryWithRetry(directory);
        }
    }

    [Fact]
    public void CopyPlanTryApiRejectsPlanAboveMeetingRowLimit()
    {
        using var fixture = Fixture.Create(CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan + 1));
        var beforeCount = fixture.Session.Document.Plans.Count;
        var before = Serialize(fixture.Session.Document);

        var copied = fixture.ViewModel.TryCopyCurrentPlan(out var copy, out var validation);

        Assert.False(copied);
        Assert.Null(copy);
        Assert.Contains(validation.Errors, issue => issue.Code == "PlanMeetingRowsMaximum");
        Assert.Equal(beforeCount, fixture.Session.Document.Plans.Count);
        Assert.Equal(before, Serialize(fixture.Session.Document));
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void CrossSemesterAddUsesCopiedCourseMeetingRowsBeforeMutation()
    {
        var document = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan - 16);
        var targetSemester = document.Semesters[0];
        var sourceSemester = JsonDefaults.Clone(targetSemester);
        sourceSemester.SemesterId = "source-semester";
        sourceSemester.SemesterName = "Source semester";
        document.Semesters.Add(sourceSemester);
        var source = CreateCourse(sourceSemester, "Cross-semester source", 17);
        document.CourseLibrary.Add(source);
        using var fixture = Fixture.Create(document);
        var beforeCourses = fixture.Session.Document.CourseLibrary.Count;
        var before = Serialize(fixture.Session.Document);

        var result = fixture.ViewModel.AddCourseToPlan(
            fixture.ViewModel.CurrentPlan!,
            source,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.True(result.Cancelled);
        Assert.False(result.Added);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "PlanMeetingRowsMaximum");
        Assert.Equal(beforeCourses, fixture.Session.Document.CourseLibrary.Count);
        Assert.Equal(before, Serialize(fixture.Session.Document));
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void CoreAddRejectsMeetingRowOverflowBeforeMutatingThePlan()
    {
        var document = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan);
        var semester = Assert.Single(document.Semesters);
        var plan = Assert.Single(document.Plans);
        var source = CreateCourse(semester, "Core overflow source", 1);
        document.CourseLibrary.Add(source);
        var before = Serialize(document);

        var result = PlannerDomainService.AddCourseToPlan(
            plan,
            source,
            semester,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict,
            document.CourseLibrary);

        Assert.True(result.Cancelled);
        Assert.False(result.Added);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "PlanMeetingRowsMaximum");
        Assert.Equal(before, Serialize(document));
        Assert.Equal(
            PlannerDataLimits.MaxMeetingRowsPerPlan,
            PlanRules.CountMeetingRows(plan, document.CourseLibrary));
    }

    [Fact]
    public void BulkAddRejectsMeetingRowOverflowWithoutMutatingAnyTargetOrLibrary()
    {
        var document = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan);
        var sourceSemester = JsonDefaults.Clone(document.Semesters[0]);
        sourceSemester.SemesterId = "bulk-source-semester";
        sourceSemester.SemesterName = "Bulk source semester";
        document.Semesters.Add(sourceSemester);
        var source = CreateCourse(sourceSemester, "Bulk overflow source", 1);
        document.CourseLibrary.Add(source);
        using var fixture = Fixture.Create(document);
        var before = Serialize(fixture.Session.Document);

        var result = fixture.ViewModel.AddCourseToPlans(
            [fixture.ViewModel.CurrentPlan!],
            source,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Cancelled);
        Assert.Contains(result.Validation.Errors, issue => issue.Code == "PlanMeetingRowsMaximum");
        Assert.Equal(before, Serialize(fixture.Session.Document));
        Assert.False(fixture.Session.UndoRedo.CanUndo);
    }

    [Fact]
    public void SelectionPlanImportRejectsMeetingRowExplosionDuringPreview()
    {
        var source = CreateDocument(PlannerDataLimits.MaxMeetingRowsPerPlan + 1);
        var package = new SelectionPlanPackage
        {
            Semester = JsonDefaults.Clone(source.Semesters[0]),
            Courses = JsonDefaults.Clone(source.CourseLibrary),
            Plan = JsonDefaults.Clone(source.Plans[0])
        };

        var target = new PlannerDocument();
        var before = Serialize(target);
        var exchangeJsonOptions = new JsonSerializerOptions(JsonDefaults.Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            JsonSerializer.Serialize(package, exchangeJsonOptions));

        Assert.False(preview.CanApply);
        Assert.Contains(
            preview.Items.SelectMany(item => item.Errors),
            issue => issue.Code == "PlanMeetingRowsMaximum");
        Assert.False(
            ImportExportService.ApplyImport(target, preview, new ImportApplyOptions()).Applied);
        Assert.Equal(before, Serialize(target));
        Assert.Equal(
            PlannerDataLimits.MaxMeetingRowsPerPlan + 1,
            PlanRules.CountMeetingRows(package.Plan, package.Courses));
    }

    [Theory]
    [InlineData(LanguageMode.English)]
    [InlineData(LanguageMode.SimplifiedChinese)]
    public void MeetingRowLimitMessageIsLocalized(LanguageMode language)
    {
        var localizer = new AppLocalizer(language);
        var message = localizer.ValidationMessage(new ValidationIssue
        {
            Code = "PlanMeetingRowsMaximum",
            Parameters = [PlannerDataLimits.MaxMeetingRowsPerPlan.ToString()]
        });

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("PlanMeetingRowsMaximum", message, StringComparison.Ordinal);
    }

    private static PlannerDocument CreateDocument(int meetingRows)
    {
        var semester = JsonDefaults.Clone(SeedData.Create().Semesters[0]);
        semester.SemesterId = "meeting-limit-semester";
        semester.SemesterName = "Meeting limit semester";
        var document = new PlannerDocument { Semesters = [semester] };
        var plan = new SelectionPlan
        {
            PlanId = "meeting-limit-plan",
            SemesterId = semester.SemesterId,
            PlanName = "Meeting limit plan"
        };
        var remaining = meetingRows;
        var index = 0;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, PlannerDataLimits.MaxMeetingsPerCourse);
            var course = CreateCourse(semester, $"Course {index}", count);
            document.CourseLibrary.Add(course);
            plan.Snapshots.Add(new PlanCourseSnapshot
            {
                SnapshotId = $"meeting-limit-snapshot-{index}",
                CourseOfferingId = course.OfferingId,
                RegistrationOrder = index
            });
            remaining -= count;
            index++;
        }
        document.Plans.Add(plan);
        document.Settings.CurrentSemesterId = semester.SemesterId;
        document.Settings.CurrentPlanId = plan.PlanId;
        document.Settings.OpenPlanIds.Add(plan.PlanId);
        return document;
    }

    private static string Serialize(PlannerDocument document) =>
        JsonSerializer.Serialize(document, JsonDefaults.Options);

    private static IEnumerable<CourseOffering> CourseThenThrow(CourseOffering course)
    {
        yield return course;
        throw new InvalidOperationException("The unneeded tail must not be enumerated.");
    }

    private static CourseOffering CreateCourse(Semester semester, string name, int meetingRows)
    {
        var course = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = name,
            Teacher = "Teacher",
            Location = "Room",
            Credits = 1m,
            Color = "#123456"
        };
        for (var index = 0; index < meetingRows; index++)
        {
            course.MeetingTimes.Add(new MeetingTime
            {
                Weekday = index % 7 + 1,
                StartPeriod = 1,
                EndPeriod = 1,
                Weeks = (index / 7 + 1).ToString(),
                WeekParity = WeekParity.All
            });
        }
        CourseIdentityService.AssignOfferingId(course);
        return course;
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string directory, DocumentSession session, PlannerViewModel viewModel)
        {
            Directory = directory;
            Session = session;
            ViewModel = viewModel;
        }

        public string Directory { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }

        public static Fixture Create(PlannerDocument document)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directory);
            var session = new DocumentSession(
                repository,
                loadDocument: () => document,
                saveDocument: (_, _) => { });
            var localization = new LocalizationService(session);
            return new Fixture(directory, session, new PlannerViewModel(session, localization));
        }

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            DeleteDirectoryWithRetry(Directory);
        }
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

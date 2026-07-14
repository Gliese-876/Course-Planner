using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(PerformanceSensitiveTestCollection.Name)]
public sealed class ExchangeRoundTripTests
{
    public static TheoryData<string, string, string> RequiredCurrentSchemaFields => new()
    {
        { PlannerSchemas.CourseLibraryKind, "root", "kind" },
        { PlannerSchemas.CourseLibraryKind, "root", "schemaVersion" },
        { PlannerSchemas.CourseLibraryKind, "root", "semesters" },
        { PlannerSchemas.CourseLibraryKind, "root", "labels" },
        { PlannerSchemas.CourseLibraryKind, "root", "courses" },

        { PlannerSchemas.SelectionPlanKind, "root", "kind" },
        { PlannerSchemas.SelectionPlanKind, "root", "schemaVersion" },
        { PlannerSchemas.SelectionPlanKind, "root", "semester" },
        { PlannerSchemas.SelectionPlanKind, "root", "labels" },
        { PlannerSchemas.SelectionPlanKind, "root", "courses" },
        { PlannerSchemas.SelectionPlanKind, "root", "plan" },

        { PlannerSchemas.CourseLibraryKind, "semester", "semesterId" },
        { PlannerSchemas.CourseLibraryKind, "semester", "semesterName" },
        { PlannerSchemas.CourseLibraryKind, "semester", "startDate" },
        { PlannerSchemas.CourseLibraryKind, "semester", "endDate" },
        { PlannerSchemas.CourseLibraryKind, "semester", "weekCount" },
        { PlannerSchemas.CourseLibraryKind, "semester", "weekStartDay" },
        { PlannerSchemas.CourseLibraryKind, "semester", "displayOrder" },
        { PlannerSchemas.CourseLibraryKind, "semester", "periodSchedule" },

        { PlannerSchemas.CourseLibraryKind, "period", "period" },
        { PlannerSchemas.CourseLibraryKind, "period", "start" },
        { PlannerSchemas.CourseLibraryKind, "period", "end" },

        { PlannerSchemas.CourseLibraryKind, "label", "name" },
        { PlannerSchemas.CourseLibraryKind, "label", "kind" },
        { PlannerSchemas.CourseLibraryKind, "label", "displayOrder" },

        { PlannerSchemas.CourseLibraryKind, "course", "offeringId" },
        { PlannerSchemas.CourseLibraryKind, "course", "semesterId" },
        { PlannerSchemas.CourseLibraryKind, "course", "courseName" },
        { PlannerSchemas.CourseLibraryKind, "course", "teacher" },
        { PlannerSchemas.CourseLibraryKind, "course", "location" },
        { PlannerSchemas.CourseLibraryKind, "course", "credits" },
        { PlannerSchemas.CourseLibraryKind, "course", "courseGroupType" },
        { PlannerSchemas.CourseLibraryKind, "course", "studyType" },
        { PlannerSchemas.CourseLibraryKind, "course", "labels" },
        { PlannerSchemas.CourseLibraryKind, "course", "meetingTimes" },
        { PlannerSchemas.CourseLibraryKind, "course", "notes" },
        { PlannerSchemas.CourseLibraryKind, "course", "enrolledCount" },
        { PlannerSchemas.CourseLibraryKind, "course", "capacity" },
        { PlannerSchemas.CourseLibraryKind, "course", "color" },
        { PlannerSchemas.CourseLibraryKind, "course", "modifiedAt" },

        { PlannerSchemas.CourseLibraryKind, "meeting", "weekday" },
        { PlannerSchemas.CourseLibraryKind, "meeting", "startPeriod" },
        { PlannerSchemas.CourseLibraryKind, "meeting", "endPeriod" },
        { PlannerSchemas.CourseLibraryKind, "meeting", "weeks" },
        { PlannerSchemas.CourseLibraryKind, "meeting", "weekParity" },

        { PlannerSchemas.SelectionPlanKind, "plan", "planId" },
        { PlannerSchemas.SelectionPlanKind, "plan", "semesterId" },
        { PlannerSchemas.SelectionPlanKind, "plan", "planName" },
        { PlannerSchemas.SelectionPlanKind, "plan", "displayOrder" },
        { PlannerSchemas.SelectionPlanKind, "plan", "createdAt" },
        { PlannerSchemas.SelectionPlanKind, "plan", "modifiedAt" },
        { PlannerSchemas.SelectionPlanKind, "plan", "snapshots" },

        { PlannerSchemas.SelectionPlanKind, "snapshot", "snapshotId" },
        { PlannerSchemas.SelectionPlanKind, "snapshot", "courseOfferingId" },
        { PlannerSchemas.SelectionPlanKind, "snapshot", "registrationOrder" },
        { PlannerSchemas.SelectionPlanKind, "snapshot", "snapshotAt" }
    };

    [Fact]
    public async Task ASuccessfulLibraryExportIsAlwaysReadableByItsOwnImporter()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-exchange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = CreateNearMaximumEscapedTextLibrary();
            var repository = new SqliteAppRepository(Path.Combine(directory, "data"));
            repository.Save(source, "prove-export-source-is-persistable");

            var exportStarted = Stopwatch.StartNew();
            var exportAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var json = ImportExportService.ExportCourseLibraryJson(source);
            var exportAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - exportAllocatedBefore;
            exportStarted.Stop();
            Assert.InRange(json.Length, 20_000_000, PlannerDataLimits.MaxImportTextCharacters);
            Assert.InRange(
                Encoding.UTF8.GetByteCount(json),
                20_000_000,
                PlannerDataLimits.MaxImportFileBytes);

            var path = Path.Combine(directory, "library.json");
            await AtomicTextFileWriter.WriteAllTextAsync(path, json);
            var importedJson = await BoundedTextFileReader.ReadAsync(
                path,
                PlannerDataLimits.MaxImportFileBytes,
                PlannerDataLimits.MaxImportTextCharacters);

            var previewStarted = Stopwatch.StartNew();
            var previewAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var preview = ImportExportService.PreviewJson(new PlannerDocument(), importedJson);
            var previewAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - previewAllocatedBefore;
            previewStarted.Stop();
            Assert.True(preview.CanApply);
            Assert.True(exportStarted.Elapsed < TimeSpan.FromSeconds(15),
                $"Near-limit export took {exportStarted.Elapsed}.");
            Assert.True(previewStarted.Elapsed < TimeSpan.FromSeconds(20),
                $"Near-limit preview took {previewStarted.Elapsed}.");
            Assert.InRange(exportAllocatedBytes, 1, 768L * 1024 * 1024);
            Assert.InRange(previewAllocatedBytes, 1, 1_024L * 1024 * 1024);
        }
        finally
        {
            using var poolKey = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "data", "course-planner.sqlite")
            }.ToString());
            SqliteConnection.ClearPool(poolKey);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportTextAboveTheUnified64MiBLimitIsRejectedBeforeJsonParsing()
    {
        var oversized = new string('x', PlannerDataLimits.MaxImportTextCharacters + 1);
        var stopwatch = Stopwatch.StartNew();

        var preview = ImportExportService.PreviewJson(new PlannerDocument(), oversized);

        stopwatch.Stop();
        Assert.False(preview.CanApply);
        Assert.Contains(
            preview.Items.SelectMany(item => item.Errors),
            issue => issue.Code == "Import.FileTooLarge");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Oversized input was not rejected before parsing; rejection took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void MultibyteImportAboveThe64MiBByteLimitIsRejectedBeforeJsonParsing()
    {
        var characterCount = checked((int)(PlannerDataLimits.MaxImportFileBytes / 3) + 1);
        var oversized = new string('课', characterCount);
        Assert.True(oversized.Length < PlannerDataLimits.MaxImportTextCharacters);
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > PlannerDataLimits.MaxImportFileBytes);
        var stopwatch = Stopwatch.StartNew();

        var preview = ImportExportService.PreviewJson(new PlannerDocument(), oversized);

        stopwatch.Stop();
        Assert.False(preview.CanApply);
        Assert.Contains(
            preview.Items.SelectMany(item => item.Errors),
            issue => issue.Code == "Import.FileTooLarge");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"UTF-8 byte overflow reached JSON parsing; rejection took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void ImportAtTargetCapacityIsRejectedBeforeApplyOrPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-import-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var repository = new SqliteAppRepository(Path.Combine(directory, "data"));
        try
        {
            var target = SeedData.Create("Existing semester 000", "Existing plan");
            var template = target.Semesters[0];
            for (var index = 1; index < PlannerDataLimits.MaxSemesters; index++)
            {
                var semester = JsonDefaults.Clone(template);
                semester.SemesterId = $"existing-semester-{index:D3}";
                semester.SemesterName = $"Existing semester {index:D3}";
                semester.DisplayOrder = index;
                target.Semesters.Add(semester);
            }
            repository.Save(target, "capacity-target");
            var before = Canonical(target);

            var source = SeedData.Create("Imported semester", "Unused plan");
            source.Semesters[0].SemesterId = "imported-semester";
            source.Labels.Clear();
            var json = ImportExportService.ExportCourseLibraryJson(source);
            var preview = ImportExportService.PreviewJson(target, json);
            var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());
            var saveException = Record.Exception(() => repository.Save(target, "post-import"));

            Assert.False(preview.CanApply);
            Assert.Contains(
                preview.Items.SelectMany(item => item.Errors),
                issue => issue.Code == "SemesterCatalogMaximum");
            Assert.False(result.Applied);
            Assert.Null(saveException);
            Assert.Equal(before, Canonical(target));
        }
        finally
        {
            using var poolKey = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = repository.DatabasePath
            }.ToString());
            SqliteConnection.ClearPool(poolKey);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectionPlanPreviewRejectsAggregateTextOverflowBeforeApply()
    {
        var target = CreateNearMaximumEscapedTextLibrary();
        var source = CreateSelectionSource(target, target.CourseLibrary[0], "aggregate-overflow-plan");
        var before = Canonical(target);

        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        var planItem = Assert.Single(preview.Items, item => item.Kind == "plan");
        Assert.Equal(ImportPreviewStatus.NotImportable, planItem.Status);
        Assert.Contains(planItem.Errors, issue => issue.Code == "AggregateTextMaximum");
        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanPreviewRejectsTheSeventeenthOpenPlanBeforeApply()
    {
        var target = CreateRichDocument();
        target.Plans.Clear();
        target.Settings.OpenPlanIds.Clear();
        target.Settings.CurrentPlanId = null;
        for (var index = 0; index < PlanTabLimits.MaximumOpenPlans; index++)
        {
            var plan = new SelectionPlan
            {
                PlanId = $"open-plan-{index:D2}",
                PlanName = $"Open plan {index:D2}",
                SemesterId = target.Semesters[0].SemesterId,
                DisplayOrder = index
            };
            target.Plans.Add(plan);
            target.Settings.OpenPlanIds.Add(plan.PlanId);
        }
        var source = CreateSelectionSource(target, target.CourseLibrary[0], "seventeenth-open-plan");
        var before = Canonical(target);

        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        var planItem = Assert.Single(preview.Items, item => item.Kind == "plan");
        Assert.Contains(planItem.Errors, issue => issue.Code == "OpenPlanTabsMaximum");
        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanPreviewRejectsTotalLabelReferenceOverflowBeforeApply()
    {
        var target = SeedData.Create("Reference capacity semester", "Unused plan");
        target.Plans.Clear();
        target.Settings.OpenPlanIds.Clear();
        target.Settings.CurrentPlanId = null;
        target.Labels.Clear();
        target.CourseLibrary.Clear();
        for (var index = 0; index < PlannerDataLimits.MaxLabelsPerCourse; index++)
        {
            target.Labels.Add(new CourseLabel
            {
                Name = $"Reference {index:D3}",
                Kind = LabelKind.Ordinary,
                DisplayOrder = index
            });
        }

        var fullCourses = PlannerDataLimits.MaxTotalLabelReferences / PlannerDataLimits.MaxLabelsPerCourse;
        var remainder = PlannerDataLimits.MaxTotalLabelReferences % PlannerDataLimits.MaxLabelsPerCourse;
        for (var index = 0; index < fullCourses + (remainder == 0 ? 0 : 1); index++)
        {
            var referenceCount = index < fullCourses ? PlannerDataLimits.MaxLabelsPerCourse : remainder;
            var course = new CourseOffering
            {
                SemesterId = target.Semesters[0].SemesterId,
                CourseName = $"Reference carrier {index:D3}",
                Color = "#336699"
            };
            course.Labels.AddRange(target.Labels.Take(referenceCount).Select(label => label.Name));
            CourseIdentityService.AssignOfferingId(course);
            target.CourseLibrary.Add(course);
        }

        var importedCourse = new CourseOffering
        {
            SemesterId = target.Semesters[0].SemesterId,
            CourseName = "One reference too many",
            Color = "#336699",
            Labels = { target.Labels[0].Name }
        };
        CourseIdentityService.AssignOfferingId(importedCourse);
        var source = CreateSelectionSource(target, importedCourse, "label-reference-overflow-plan");
        var before = Canonical(target);

        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.False(preview.CanApply);
        var planItem = Assert.Single(preview.Items, item => item.Kind == "plan");
        Assert.Contains(planItem.Errors, issue => issue.Code == "TotalLabelReferencesMaximum");
        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanPreviewReportsTotalSnapshotOverflowBeforeApply()
    {
        var target = SeedData.Create("Snapshot capacity semester", "Unused plan");
        target.Plans.Clear();
        target.Settings.OpenPlanIds.Clear();
        target.Settings.CurrentPlanId = null;
        target.Labels.Clear();
        target.CourseLibrary.Clear();
        for (var courseIndex = 0; courseIndex < PlannerDataLimits.MaxCourses; courseIndex++)
        {
            var course = new CourseOffering
            {
                SemesterId = target.Semesters[0].SemesterId,
                CourseName = $"Snapshot course {courseIndex:D4}",
                Color = "#336699"
            };
            CourseIdentityService.AssignOfferingId(course);
            target.CourseLibrary.Add(course);
        }

        var planCount = PlannerDataLimits.MaxTotalSnapshots / PlannerDataLimits.MaxSnapshotsPerPlan;
        for (var planIndex = 0; planIndex < planCount; planIndex++)
        {
            var plan = new SelectionPlan
            {
                PlanId = $"snapshot-plan-{planIndex:D2}",
                PlanName = $"Snapshot plan {planIndex:D2}",
                SemesterId = target.Semesters[0].SemesterId,
                DisplayOrder = planIndex
            };
            for (var courseIndex = 0; courseIndex < target.CourseLibrary.Count; courseIndex++)
            {
                plan.Snapshots.Add(new PlanCourseSnapshot
                {
                    SnapshotId = $"s{planIndex:x2}{courseIndex:x4}",
                    CourseOfferingId = target.CourseLibrary[courseIndex].OfferingId,
                    RegistrationOrder = courseIndex
                });
            }
            target.Plans.Add(plan);
        }

        var source = CreateSelectionSource(target, target.CourseLibrary[0], "total-snapshot-overflow-plan");

        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));

        Assert.False(preview.CanApply);
        var planItem = Assert.Single(preview.Items, item => item.Kind == "plan");
        Assert.Contains(planItem.Errors, issue => issue.Code == "TotalSnapshotsMaximum");
    }

    [Fact]
    public void CourseLibraryCapacitySelectionIsDeterministicAndAppliesTheFirstFittingItem()
    {
        var target = CreateNearMaximumEscapedTextLibrary();
        var first = JsonDefaults.Clone(target.CourseLibrary[0]);
        target.CourseLibrary.RemoveAt(0);
        var second = JsonDefaults.Clone(first);
        second.CourseName = "Second 0000";
        CourseIdentityService.AssignOfferingId(second);
        Assert.Equal(
            PlannerDocumentTextCapacity.Count(first),
            PlannerDocumentTextCapacity.Count(second));

        var source = new PlannerDocument
        {
            Semesters = { JsonDefaults.Clone(target.Semesters[0]) },
            CourseLibrary = { first, second }
        };
        var json = ImportExportService.ExportCourseLibraryJson(source);

        var firstPreview = ImportExportService.PreviewCourseLibrary(target, json);
        var secondPreview = ImportExportService.PreviewCourseLibrary(target, json);
        var firstStatuses = firstPreview.Items
            .Where(item => item.Kind == "course")
            .Select(item => new
            {
                item.Course!.OfferingId,
                item.Status,
                Errors = item.Errors.Select(error => error.Code).ToArray()
            })
            .ToArray();
        var secondStatuses = secondPreview.Items
            .Where(item => item.Kind == "course")
            .Select(item => new
            {
                item.Course!.OfferingId,
                item.Status,
                Errors = item.Errors.Select(error => error.Code).ToArray()
            })
            .ToArray();

        Assert.Equal(Canonical(firstStatuses), Canonical(secondStatuses));
        Assert.Equal(ImportPreviewStatus.Added, firstStatuses[0].Status);
        Assert.Equal(ImportPreviewStatus.NotImportable, firstStatuses[1].Status);
        Assert.Contains("AggregateTextMaximum", firstStatuses[1].Errors);

        var result = ImportExportService.ApplyImport(target, firstPreview, new ImportApplyOptions());

        Assert.True(result.Applied);
        Assert.Contains(target.CourseLibrary, course => course.OfferingId == first.OfferingId);
        Assert.DoesNotContain(target.CourseLibrary, course => course.OfferingId == second.OfferingId);
        Assert.Equal(
            PlannerDataLimits.MaxAggregateTextCharacters,
            PlannerDocumentTextCapacity.Count(target));
    }

    [Fact]
    public async Task MaximumStructuralSelectionPlanFitsTheUnifiedImportBudgetAndPreviews()
    {
        var source = CreateMaximumStructuralSelectionPlan();
        var json = ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        Assert.InRange(json.Length, 2_000_000, PlannerDataLimits.MaxImportTextCharacters);
        Assert.InRange(byteCount, 2_000_000, PlannerDataLimits.MaxImportFileBytes);

        var directory = Path.Combine(Path.GetTempPath(), $"course-planner-plan-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "maximum-plan.json");
            await AtomicTextFileWriter.WriteAllTextAsync(path, json);
            var importedJson = await BoundedTextFileReader.ReadAsync(
                path,
                PlannerDataLimits.MaxImportFileBytes,
                PlannerDataLimits.MaxImportTextCharacters);
            var preview = ImportExportService.PreviewJson(new PlannerDocument(), importedJson);

            Assert.True(preview.CanApply);
            Assert.Equal(
                PlannerDataLimits.MaxCourses,
                preview.Items.Count(item => item.Kind == "planCourse"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CourseLibraryJsonRoundTripsEveryPersistedFieldIntoEmptyDocument()
    {
        var source = CreateRichDocument();
        var json = ImportExportService.ExportCourseLibraryJson(source);
        var target = new PlannerDocument();

        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.True(result.Applied);
        Assert.Equal(Canonical(source.Semesters), Canonical(target.Semesters));
        Assert.Equal(Canonical(source.Labels), Canonical(target.Labels));
        Assert.Equal(Canonical(source.CourseLibrary), Canonical(target.CourseLibrary));
        Assert.Empty(target.CourseLibrary.Single(course => course.CourseName == "Independent Study").MeetingTimes);
    }

    [Fact]
    public void SelectionPlanJsonRoundTripsEveryFieldAndReferencedLabelsIntoEmptyDocument()
    {
        var source = CreateRichDocument();
        var sourcePlan = Assert.Single(source.Plans);
        var json = ImportExportService.ExportSelectionPlanJson(source, sourcePlan);
        var package = JsonSerializer.Deserialize<SelectionPlanPackage>(json, JsonDefaults.Options);
        var target = new PlannerDocument();

        Assert.NotNull(package);
        Assert.Equal(
            Canonical(source.Labels.Where(label => label.Name != "Unused label").ToList()),
            Canonical(package!.Labels));

        var preview = ImportExportService.PreviewJson(target, json);
        Assert.True(preview.RequiresCourseLibrarySync);
        Assert.All(
            preview.Items.Where(item => item.Kind == "planCourse"),
            item => Assert.True(item.RequiresCourseLibrarySync));

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.True(result.Applied);
        var importedPlan = Assert.Single(target.Plans);
        Assert.Equal(Canonical(sourcePlan), Canonical(importedPlan));
        Assert.Equal(Canonical(source.Semesters), Canonical(target.Semesters));
        Assert.Equal(Canonical(package.Labels), Canonical(target.Labels));
        Assert.Equal(Canonical(package.Courses), Canonical(target.CourseLibrary));
        Assert.All(
            importedPlan.Snapshots,
            snapshot => Assert.NotNull(PlanCourseResolver.CourseForSnapshot(snapshot, target.CourseLibrary)));
    }

    [Fact]
    public void SelectionPlanPackageRejectsNonCanonicalCourseIdsInsteadOfRemappingLegacyAliases()
    {
        var source = CreateRichDocument();
        var localSemester = JsonDefaults.Clone(Assert.Single(source.Semesters));
        localSemester.SemesterId = "local-2030-spring";
        var target = new PlannerDocument { Semesters = { localSemester } };
        var expectedCourses = source.CourseLibrary.Select(JsonDefaults.Clone).ToList();
        foreach (var course in expectedCourses)
        {
            course.SemesterId = localSemester.SemesterId;
            CourseIdentityService.AssignOfferingId(course);
        }

        var firstOriginalId = "legacy-first-course-id";
        var secondOriginalId = expectedCourses[0].OfferingId;
        Assert.NotEqual(secondOriginalId, expectedCourses[1].OfferingId);
        var json = MutateJson(
            ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans)),
            root =>
            {
                var courses = root["courses"]!.AsArray();
                courses[0]!["offeringId"] = firstOriginalId;
                courses[1]!["offeringId"] = secondOriginalId;
                var snapshots = root["plan"]!["snapshots"]!.AsArray();
                snapshots[0]!["courseOfferingId"] = firstOriginalId;
                snapshots[1]!["courseOfferingId"] = secondOriginalId;
            });

        var before = Canonical(target);
        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Items.SelectMany(item => item.Errors), issue => issue.Code == "Import.InvalidJson");
        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanImportWithoutCourseLibrarySyncConsentIsAtomicNoOp()
    {
        var source = CreateRichDocument();
        var json = ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var target = new PlannerDocument();
        var before = Canonical(target);
        var preview = ImportExportService.PreviewJson(target, json);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(result.Applied);
        Assert.True(preview.RequiresCourseLibrarySync);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanImportAddsOnlyMissingCoursesAndKeepsExistingLibraryCourse()
    {
        var source = CreateRichDocument();
        var sourcePlan = Assert.Single(source.Plans);
        var existingSourceCourse = source.CourseLibrary[0];
        var localCourse = JsonDefaults.Clone(existingSourceCourse);
        localCourse.Credits = 9.25m;
        localCourse.Notes = "Newer local notes must win.";
        localCourse.Color = "#112233";
        localCourse.ModifiedAt = new DateTimeOffset(2035, 6, 7, 8, 9, 10, TimeSpan.FromHours(8));
        var target = new PlannerDocument
        {
            Semesters = JsonDefaults.Clone(source.Semesters),
            Labels = JsonDefaults.Clone(source.Labels.Where(label => label.Name != "Unused label").ToList()),
            CourseLibrary = { localCourse }
        };
        var json = ImportExportService.ExportSelectionPlanJson(source, sourcePlan);
        var preview = ImportExportService.PreviewJson(target, json);

        var existingItem = Assert.Single(preview.Items, item =>
            item.Kind == "planCourse" && item.Course?.OfferingId == localCourse.OfferingId);
        Assert.False(existingItem.RequiresCourseLibrarySync);
        Assert.Single(preview.Items, item => item.Kind == "planCourse" && item.RequiresCourseLibrarySync);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.True(result.Applied);
        Assert.Equal(2, target.CourseLibrary.Count);
        var preserved = Assert.Single(target.CourseLibrary, course => course.OfferingId == localCourse.OfferingId);
        Assert.Equal(localCourse.Credits, preserved.Credits);
        Assert.Equal(localCourse.Notes, preserved.Notes);
        Assert.Equal(localCourse.Color, preserved.Color);
        Assert.Equal(localCourse.ModifiedAt, preserved.ModifiedAt);
        var importedPlan = Assert.Single(target.Plans);
        Assert.Equal(Canonical(sourcePlan.Snapshots), Canonical(importedPlan.Snapshots));
        Assert.All(
            importedPlan.Snapshots,
            snapshot => Assert.NotNull(PlanCourseResolver.CourseForSnapshot(snapshot, target.CourseLibrary)));
    }

    [Fact]
    public void SelectionPlanImportWithAllCoursesInLibraryNeedsNoSyncAndDoesNotOverwriteThem()
    {
        var source = CreateRichDocument();
        var sourcePlan = Assert.Single(source.Plans);
        var target = new PlannerDocument
        {
            Semesters = JsonDefaults.Clone(source.Semesters),
            Labels = JsonDefaults.Clone(source.Labels),
            CourseLibrary = JsonDefaults.Clone(source.CourseLibrary)
        };
        target.CourseLibrary[0].Notes = "Local notes stay authoritative.";
        target.CourseLibrary[0].Credits = 8.5m;
        var localLibrary = Canonical(target.CourseLibrary);
        var json = ImportExportService.ExportSelectionPlanJson(source, sourcePlan);
        var preview = ImportExportService.PreviewJson(target, json);

        Assert.False(preview.RequiresCourseLibrarySync);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.True(result.Applied);
        Assert.Equal(localLibrary, Canonical(target.CourseLibrary));
        Assert.Equal(Canonical(sourcePlan), Canonical(Assert.Single(target.Plans)));
    }

    [Fact]
    public void ReapplyingTheSameSelectionPreviewIsAnAtomicNoOp()
    {
        var source = CreateRichDocument();
        var target = CreateSelectionTargetWithExistingLibrary(source);
        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));

        Assert.True(ImportExportService.ApplyImport(target, preview, new ImportApplyOptions()).Applied);
        var afterFirstApply = Canonical(target);

        var second = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(second.Applied);
        Assert.Equal(afterFirstApply, Canonical(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleSelectionPreviewCannotCreateDuplicatePlanIdentityOrName(bool duplicateId)
    {
        var source = CreateRichDocument();
        var sourcePlan = source.Plans[0];
        var target = CreateSelectionTargetWithExistingLibrary(source);
        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, sourcePlan));
        target.Plans.Add(new SelectionPlan
        {
            PlanId = duplicateId ? sourcePlan.PlanId : "local-plan-id",
            PlanName = duplicateId ? "Local plan name" : sourcePlan.PlanName,
            SemesterId = sourcePlan.SemesterId
        });
        var before = Canonical(target);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void TamperedSelectionPreviewCannotCommitAnInvalidPlanIdentity()
    {
        var source = CreateRichDocument();
        var target = CreateSelectionTargetWithExistingLibrary(source);
        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]));
        Assert.Single(preview.Items, item => item.Kind == "plan").Plan!.PlanId = "";
        var before = Canonical(target);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void StaleSelectionPreviewRechecksLabelKindConflictsAndCapacity()
    {
        var source = CreateRichDocument();
        var target = new PlannerDocument
        {
            Semesters = JsonDefaults.Clone(source.Semesters),
            Settings = new AppSettings { CurrentSemesterId = source.Semesters[0].SemesterId }
        };
        var json = ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]);
        var labelPreview = ImportExportService.PreviewSelectionPlan(target, json);
        target.Labels.Add(new CourseLabel { Name = "Studio", Kind = LabelKind.StudyType });
        var beforeLabelApply = Canonical(target);

        var labelResult = ImportExportService.ApplyImport(target, labelPreview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.False(labelResult.Applied);
        Assert.Equal(beforeLabelApply, Canonical(target));

        target = CreateSelectionTargetWithExistingLibrary(source);
        for (var index = 0; index < PlanTabLimits.MaximumOpenPlans - 1; index++)
        {
            var openPlan = new SelectionPlan
            {
                PlanId = $"preexisting-open-{index:D2}",
                PlanName = $"Preexisting open {index:D2}",
                SemesterId = source.Semesters[0].SemesterId
            };
            target.Plans.Add(openPlan);
            target.Settings.OpenPlanIds.Add(openPlan.PlanId);
        }
        target.Settings.CurrentPlanId = target.Settings.OpenPlanIds[0];
        var capacityPreview = ImportExportService.PreviewSelectionPlan(target, json);
        Assert.True(capacityPreview.CanApply);
        var lastSlot = new SelectionPlan
        {
            PlanId = "last-local-open-slot",
            PlanName = "Last local open slot",
            SemesterId = source.Semesters[0].SemesterId
        };
        target.Plans.Add(lastSlot);
        target.Settings.OpenPlanIds.Add(lastSlot.PlanId);
        var beforeCapacityApply = Canonical(target);

        var capacityResult = ImportExportService.ApplyImport(target, capacityPreview, new ImportApplyOptions());

        Assert.False(capacityResult.Applied);
        Assert.Equal(beforeCapacityApply, Canonical(target));
    }

    [Fact]
    public void CourseLibraryImportWithNoDataDifferenceReturnsNotApplied()
    {
        var document = CreateRichDocument();
        var json = ImportExportService.ExportCourseLibraryJson(document);
        var preview = ImportExportService.PreviewJson(document, json);
        var before = Canonical(document);

        var result = ImportExportService.ApplyImport(document, preview, new ImportApplyOptions());

        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(document));
    }

    [Fact]
    public void CourseLibraryApplyAtomicallyRejectsCoursesWhoseImportedSemesterIsInvalid()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportCourseLibraryJson(source),
            root => root["semesters"]!.AsArray()[0]!["endDate"] = "2030-02-09");
        var target = new PlannerDocument();
        var before = Canonical(target);
        var preview = ImportExportService.PreviewJson(target, json);

        Assert.Equal(
            ImportPreviewStatus.NotImportable,
            Assert.Single(preview.Items, item => item.Kind == "semester").Status);
        Assert.Contains(
            preview.Items,
            item => item.Kind == "course" && item.Status != ImportPreviewStatus.NotImportable);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void SelectionPlanExportWithMissingCourseReferenceFailsClearly()
    {
        var document = CreateRichDocument();
        var plan = Assert.Single(document.Plans);
        plan.Snapshots.Add(new PlanCourseSnapshot
        {
            SnapshotId = "missing-snapshot",
            CourseOfferingId = "missing-course",
            RegistrationOrder = 2,
            SnapshotAt = new DateTimeOffset(2031, 1, 2, 3, 4, 5, TimeSpan.Zero)
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            ImportExportService.ExportSelectionPlanJson(document, plan));

        Assert.Contains("missing-course", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNullCollectionsAndArrayItemsReturnInvalidPreviewInsteadOfThrowing()
    {
        var source = CreateRichDocument();
        var libraryJson = ImportExportService.ExportCourseLibraryJson(source);
        var planJson = ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var malformedPackages = new[]
        {
            MutateJson(libraryJson, root => root["semesters"] = null),
            MutateJson(libraryJson, root => root["labels"] = null),
            MutateJson(libraryJson, root => root["courses"] = null),
            MutateJson(libraryJson, root => root["semesters"]!.AsArray()[0] = null),
            MutateJson(libraryJson, root => root["labels"]!.AsArray()[0] = null),
            MutateJson(libraryJson, root => root["courses"]!.AsArray()[0] = null),
            MutateJson(libraryJson, root => root["semesters"]!.AsArray()[0]!["periodSchedule"] = null),
            MutateJson(libraryJson, root => root["courses"]!.AsArray()[0]!["labels"] = null),
            MutateJson(libraryJson, root => root["courses"]!.AsArray()[0]!["labels"]!.AsArray()[0] = null),
            MutateJson(libraryJson, root => root["courses"]!.AsArray()[0]!["meetingTimes"] = null),
            MutateJson(libraryJson, root => root["courses"]!.AsArray()[0]!["meetingTimes"]!.AsArray()[0] = null),
            MutateJson(planJson, root => root["semester"] = null),
            MutateJson(planJson, root => root["plan"] = null),
            MutateJson(planJson, root => root["plan"]!["snapshots"] = null),
            MutateJson(planJson, root => root["plan"]!["snapshots"]!.AsArray()[0] = null)
        };

        foreach (var json in malformedPackages)
            AssertInvalidJsonPreview(json);
    }

    [Theory]
    [MemberData(nameof(RequiredCurrentSchemaFields))]
    public void CurrentSchemaImportRequiresEveryDeclaredFieldAtomically(
        string packageKind,
        string objectKind,
        string propertyName)
    {
        var source = CreateRichDocument();
        var json = string.Equals(packageKind, PlannerSchemas.CourseLibraryKind, StringComparison.Ordinal)
            ? ImportExportService.ExportCourseLibraryJson(source)
            : ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidDataException("Expected a JSON object.");
        var containingObject = SelectExchangeObject(root, packageKind, objectKind);
        Assert.True(
            containingObject.Remove(propertyName),
            $"The valid {packageKind} fixture did not contain {objectKind}.{propertyName}.");
        var incompleteJson = root.ToJsonString(JsonDefaults.Options);
        var target = new PlannerDocument();
        var before = Canonical(target);

        var preview = ImportExportService.PreviewJson(target, incompleteJson);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            ForceOutOfRangeCourses = true,
            ForceSemesterMergeConflicts = true,
            SynchronizeMissingPlanCourses = true,
            UpdateExistingSemesterSettings = true
        });

        Assert.False(preview.CanApply);
        Assert.Contains(
            preview.Items.SelectMany(item => item.Errors),
            error => error.Code == "Import.InvalidJson");
        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Fact]
    public void CurrentSchemaExportsNullableFieldsExplicitlyWithoutTreatingNullAsMissing()
    {
        var source = CreateRichDocument();
        var nullableCourse = source.CourseLibrary[1];
        Assert.Null(nullableCourse.CourseGroupType);
        Assert.Null(nullableCourse.StudyType);
        Assert.Null(nullableCourse.EnrolledCount);
        Assert.Null(nullableCourse.Capacity);
        source.Plans[0].Snapshots[1].RegistrationOrder = null;

        var libraryJson = ImportExportService.ExportCourseLibraryJson(source);
        var libraryRoot = JsonNode.Parse(libraryJson)!.AsObject();
        var exportedCourse = libraryRoot["courses"]!.AsArray()[1]!.AsObject();
        AssertExplicitJsonNull(exportedCourse, "courseGroupType");
        AssertExplicitJsonNull(exportedCourse, "studyType");
        AssertExplicitJsonNull(exportedCourse, "enrolledCount");
        AssertExplicitJsonNull(exportedCourse, "capacity");
        Assert.True(ImportExportService.PreviewJson(new PlannerDocument(), libraryJson).CanApply);

        var planJson = ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var planRoot = JsonNode.Parse(planJson)!.AsObject();
        var exportedSnapshot = planRoot["plan"]!["snapshots"]!.AsArray()[1]!.AsObject();
        AssertExplicitJsonNull(exportedSnapshot, "registrationOrder");
        var planPreview = ImportExportService.PreviewJson(new PlannerDocument(), planJson);
        Assert.False(planPreview.CanApply);
        Assert.Contains(
            planPreview.Items.SelectMany(item => item.Errors),
            error => error.Code == "Import.InvalidJson");
    }

    [Fact]
    public void LegitimateExplicitEmptyPackageCollectionsRemainImportable()
    {
        var librarySource = CreateRichDocument();
        librarySource.Labels.Clear();
        librarySource.CourseLibrary.Clear();
        var libraryJson = ImportExportService.ExportCourseLibraryJson(librarySource);
        var libraryRoot = JsonNode.Parse(libraryJson)!.AsObject();
        Assert.Empty(libraryRoot["labels"]!.AsArray());
        Assert.Empty(libraryRoot["courses"]!.AsArray());
        var libraryTarget = new PlannerDocument();
        var libraryPreview = ImportExportService.PreviewJson(libraryTarget, libraryJson);

        Assert.True(libraryPreview.CanApply);
        Assert.True(ImportExportService.ApplyImport(
            libraryTarget,
            libraryPreview,
            new ImportApplyOptions()).Applied);
        Assert.Single(libraryTarget.Semesters);
        Assert.Empty(libraryTarget.Labels);
        Assert.Empty(libraryTarget.CourseLibrary);

        var planSource = CreateRichDocument();
        planSource.Plans[0].Snapshots.Clear();
        var planJson = ImportExportService.ExportSelectionPlanJson(planSource, Assert.Single(planSource.Plans));
        var planRoot = JsonNode.Parse(planJson)!.AsObject();
        Assert.Empty(planRoot["labels"]!.AsArray());
        Assert.Empty(planRoot["courses"]!.AsArray());
        Assert.Empty(planRoot["plan"]!["snapshots"]!.AsArray());
        var planTarget = new PlannerDocument();
        var planPreview = ImportExportService.PreviewJson(planTarget, planJson);

        Assert.True(planPreview.CanApply);
        Assert.True(ImportExportService.ApplyImport(
            planTarget,
            planPreview,
            new ImportApplyOptions()).Applied);
        Assert.Single(planTarget.Semesters);
        Assert.Single(planTarget.Plans);
        Assert.Empty(planTarget.Plans[0].Snapshots);
    }

    [Fact]
    public void LegitimateExplicitEmptyCourseCollectionsRemainImportable()
    {
        var source = CreateRichDocument();
        source.CourseLibrary[0].Labels.Clear();
        source.CourseLibrary[0].MeetingTimes.Clear();
        CourseIdentityService.AssignOfferingId(source.CourseLibrary[0]);
        var json = ImportExportService.ExportCourseLibraryJson(source);
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Empty(root["courses"]!.AsArray()[0]!["labels"]!.AsArray());
        Assert.Empty(root["courses"]!.AsArray()[0]!["meetingTimes"]!.AsArray());

        var preview = ImportExportService.PreviewJson(new PlannerDocument(), json);

        Assert.True(preview.CanApply);
        Assert.DoesNotContain(
            preview.Items.SelectMany(item => item.Errors),
            error => error.Code == "Import.InvalidJson");
    }

    [Fact]
    public void ExplicitEmptyPeriodScheduleIsPresentButRejectedBySemesterRules()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportCourseLibraryJson(source),
            root => root["semesters"]!.AsArray()[0]!["periodSchedule"] = new JsonArray());

        var target = new PlannerDocument();
        var before = Canonical(target);
        var preview = ImportExportService.PreviewJson(target, json);
        var semesterItem = Assert.Single(preview.Items, item => item.Kind == "semester");

        Assert.Equal(ImportPreviewStatus.NotImportable, semesterItem.Status);
        Assert.Contains(
            semesterItem.Errors,
            error => error.Code == "PeriodScheduleRequired");
        Assert.DoesNotContain(
            preview.Items.SelectMany(item => item.Errors),
            error => error.Code == "Import.InvalidJson");
        Assert.False(ImportExportService.ApplyImport(
            target,
            preview,
            new ImportApplyOptions()).Applied);
        Assert.Equal(before, Canonical(target));
    }

    [Theory]
    [InlineData(PlannerSchemas.CourseLibraryKind)]
    [InlineData(PlannerSchemas.SelectionPlanKind)]
    public void UnknownAdditiveFieldsRemainTolerated(string packageKind)
    {
        var source = CreateRichDocument();
        var json = string.Equals(packageKind, PlannerSchemas.CourseLibraryKind, StringComparison.Ordinal)
            ? ImportExportService.ExportCourseLibraryJson(source)
            : ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var extendedJson = MutateJson(json, root => AddUnknownFields(root, packageKind));
        var target = new PlannerDocument();

        var preview = ImportExportService.PreviewJson(target, extendedJson);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.True(preview.CanApply);
        Assert.True(result.Applied);
        Assert.Single(target.Semesters);
    }

    [Fact]
    public void InvalidLabelKindIsRejectedAsInvalidPackageSemantics()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportCourseLibraryJson(source),
            root => root["labels"]!.AsArray()[0]!["kind"] = 999);

        AssertInvalidJsonPreview(json);
    }

    [Fact]
    public void SelectionPlanPackageWithOversizedNestedCourseLabelsIsRejectedBeforeReferenceExpansion()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans)),
            root =>
            {
                var labels = new JsonArray();
                for (var index = 0; index < 129; index++)
                    labels.Add($"adversarial-label-{index}");
                root["courses"]!.AsArray()[0]!["labels"] = labels;
            });

        var preview = ImportExportService.PreviewSelectionPlan(new PlannerDocument(), json);

        Assert.False(preview.CanApply);
        var item = Assert.Single(preview.Items);
        Assert.Contains(item.Errors, error => error.Code == "Import.PackageTooLarge");
    }

    [Theory]
    [InlineData("planId", "")]
    [InlineData("planName", "   ")]
    public void SelectionPlanRequiresNonEmptyIdentityAndName(string propertyName, string value)
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans)),
            root => root["plan"]![propertyName] = value);

        AssertInvalidJsonPreview(json);
    }

    [Fact]
    public void DuplicateCriticalIdsAreRejected()
    {
        var source = CreateRichDocument();
        var libraryJson = ImportExportService.ExportCourseLibraryJson(source);
        var planJson = ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans));
        var malformedPackages = new[]
        {
            MutateJson(libraryJson, root =>
            {
                var semesters = root["semesters"]!.AsArray();
                semesters.Add(semesters[0]!.DeepClone());
            }),
            MutateJson(libraryJson, root =>
            {
                var courses = root["courses"]!.AsArray();
                courses[1]!["offeringId"] = courses[0]!["offeringId"]!.GetValue<string>();
            }),
            MutateJson(planJson, root =>
            {
                var snapshots = root["plan"]!["snapshots"]!.AsArray();
                snapshots[1]!["snapshotId"] = snapshots[0]!["snapshotId"]!.GetValue<string>();
            })
        };

        foreach (var json in malformedPackages)
            AssertInvalidJsonPreview(json);
    }

    [Fact]
    public void CurrentPlanPackageWithoutEmbeddedLabelsIsRejected()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportSelectionPlanJson(source, Assert.Single(source.Plans)),
            root => root.Remove("labels"));
        var target = new PlannerDocument();

        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true
        });

        Assert.False(preview.CanApply);
        Assert.False(result.Applied);
        Assert.Empty(target.Plans);
        Assert.Empty(target.Labels);
    }

    [Fact]
    public void CurrentCourseLibraryPackageWithoutEmbeddedLabelsIsRejected()
    {
        var source = CreateRichDocument();
        var json = MutateJson(
            ImportExportService.ExportCourseLibraryJson(source),
            root => root.Remove("labels"));
        var target = new PlannerDocument();

        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.False(result.Applied);
        Assert.Empty(target.CourseLibrary);
        Assert.Empty(target.Labels);
    }

    [Fact]
    public void CourseLibraryPackageRejectsBlankAndCanonicalDuplicateCourseLabels()
    {
        var source = CreateRichDocument();
        var exported = ImportExportService.ExportCourseLibraryJson(source);
        var malformedPackages = new[]
        {
            MutateJson(exported, root =>
                root["courses"]!.AsArray()[0]!["labels"]!.AsArray().Add("   ")),
            MutateJson(exported, root =>
                root["courses"]!.AsArray()[0]!["labels"]!.AsArray().Add(" Studio "))
        };

        foreach (var json in malformedPackages)
        {
            var target = new PlannerDocument();
            var preview = ImportExportService.PreviewJson(target, json);

            Assert.False(preview.CanApply);
            Assert.Equal(ImportPreviewStatus.NotImportable, Assert.Single(preview.Items).Status);
            Assert.False(ImportExportService.ApplyImport(target, preview, new ImportApplyOptions()).Applied);
            Assert.Empty(target.CourseLibrary);
        }
    }

    [Fact]
    public void CourseLibraryImportCannotCreateCrossKindLabelDuplicatesOrBrokenCourseReferences()
    {
        var source = CreateRichDocument();
        var target = new PlannerDocument
        {
            Labels =
            {
                new CourseLabel { Name = "Studio", Kind = LabelKind.StudyType }
            }
        };
        var preview = ImportExportService.PreviewCourseLibrary(
            target,
            ImportExportService.ExportCourseLibraryJson(source));

        var labelItem = Assert.Single(preview.Items, item =>
            item.Kind == "label" && item.DisplayName == "Studio");
        Assert.Equal(ImportPreviewStatus.NotImportable, labelItem.Status);
        Assert.Contains(labelItem.Errors, issue => issue.Code == "LabelNameDuplicate");
        var dependentCourse = Assert.Single(preview.Items, item =>
            item.Kind == "course" && item.DisplayName == "Interaction Studio");
        Assert.Equal(ImportPreviewStatus.NotImportable, dependentCourse.Status);
        Assert.Contains(dependentCourse.Errors, issue => issue.Code == "LabelNameDuplicate");

        ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.Single(target.Labels, label => TextRules.IsSameLabel(label.Name, "Studio"));
        Assert.DoesNotContain(target.CourseLibrary, course => course.CourseName == "Interaction Studio");
    }

    [Fact]
    public void SelectionPlanPreviewIsNotApplicableWhenRequiredCourseHasLocalLabelKindConflict()
    {
        var source = CreateRichDocument();
        var sourcePlan = Assert.Single(source.Plans);
        var target = new PlannerDocument
        {
            Labels =
            {
                new CourseLabel { Name = "Studio", Kind = LabelKind.StudyType }
            }
        };
        var before = Canonical(target);

        var preview = ImportExportService.PreviewSelectionPlan(
            target,
            ImportExportService.ExportSelectionPlanJson(source, sourcePlan));

        Assert.False(preview.CanApply);
        var planItem = Assert.Single(preview.Items, item => item.Kind == "plan");
        Assert.Equal(ImportPreviewStatus.NotImportable, planItem.Status);
        Assert.Contains(planItem.Errors, issue => issue.Code == "Import.PlanCourseNotImportable");
        Assert.Equal(
            ImportPreviewStatus.NotImportable,
            Assert.Single(preview.Items, item => item.Kind == "planCourse" && item.DisplayName == "Interaction Studio").Status);

        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
        {
            SynchronizeMissingPlanCourses = true,
            ForceOutOfRangeCourses = true,
            ForceSemesterMergeConflicts = true
        });

        Assert.False(result.Applied);
        Assert.Equal(before, Canonical(target));
    }

    private static PlannerDocument CreateRichDocument()
    {
        var semester = new Semester
        {
            SemesterId = "2030-spring",
            SemesterName = "2030 Spring",
            StartDate = new DateOnly(2030, 2, 10),
            WeekStartDay = WeekStartDay.Sunday,
            WeekCount = 12,
            DisplayOrder = 4,
            PeriodSchedule =
            {
                new PeriodDefinition { Period = 1, Start = new TimeOnly(8, 5), End = new TimeOnly(8, 50) },
                new PeriodDefinition { Period = 2, Start = new TimeOnly(9, 0), End = new TimeOnly(9, 45) },
                new PeriodDefinition { Period = 3, Start = new TimeOnly(10, 10), End = new TimeOnly(11, 0) }
            }
        };
        semester.EndDate = SemesterRules.CalculateEndDate(
            semester.StartDate,
            semester.WeekCount,
            semester.WeekStartDay);

        var labels = new List<CourseLabel>
        {
            new() { Name = "Studio", Kind = LabelKind.Ordinary, DisplayOrder = 4 },
            new() { Name = "Unused label", Kind = LabelKind.Ordinary, DisplayOrder = 9 },
            new() { Name = "Major", Kind = LabelKind.CourseGroupType, DisplayOrder = 2 },
            new() { Name = "Elective", Kind = LabelKind.StudyType, DisplayOrder = 3 }
        };
        var studio = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = "Interaction Studio",
            Teacher = "Prof. Ada",
            Location = "Design Lab 7",
            Credits = 3.75m,
            CourseGroupType = "Major",
            StudyType = "Elective",
            Labels = { "Studio" },
            MeetingTimes =
            {
                new MeetingTime
                {
                    Weekday = 2,
                    StartPeriod = 1,
                    EndPeriod = 2,
                    Weeks = "1-12",
                    WeekParity = WeekParity.All
                },
                new MeetingTime
                {
                    Weekday = 5,
                    StartPeriod = 3,
                    EndPeriod = 3,
                    Weeks = "2-12",
                    WeekParity = WeekParity.Even
                }
            },
            Notes = "Line one.\nLine two.",
            EnrolledCount = 17,
            Capacity = 24,
            Color = "#336699",
            ModifiedAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(8))
        };
        CourseIdentityService.AssignOfferingId(studio);

        var independentStudy = new CourseOffering
        {
            SemesterId = semester.SemesterId,
            CourseName = "Independent Study",
            Teacher = "Dr. Turing",
            Location = "By arrangement",
            Credits = 1.25m,
            Notes = "Schedule pending.",
            EnrolledCount = null,
            Capacity = null,
            Color = "#884422",
            ModifiedAt = new DateTimeOffset(2030, 1, 3, 4, 5, 6, TimeSpan.FromHours(-5))
        };
        CourseIdentityService.AssignOfferingId(independentStudy);

        var plan = new SelectionPlan
        {
            PlanId = "plan-2030-studio",
            SemesterId = semester.SemesterId,
            PlanName = "Studio Plan",
            DisplayOrder = 7,
            CreatedAt = new DateTimeOffset(2029, 12, 1, 2, 3, 4, TimeSpan.FromHours(8)),
            ModifiedAt = new DateTimeOffset(2030, 1, 5, 6, 7, 8, TimeSpan.FromHours(8)),
            Snapshots =
            {
                new PlanCourseSnapshot
                {
                    SnapshotId = "snapshot-studio",
                    CourseOfferingId = studio.OfferingId,
                    RegistrationOrder = 1,
                    SnapshotAt = new DateTimeOffset(2030, 1, 4, 5, 6, 7, TimeSpan.FromHours(8))
                },
                new PlanCourseSnapshot
                {
                    SnapshotId = "snapshot-independent",
                    CourseOfferingId = independentStudy.OfferingId,
                    RegistrationOrder = 0,
                    SnapshotAt = new DateTimeOffset(2030, 1, 4, 6, 7, 8, TimeSpan.FromHours(8))
                }
            }
        };

        return new PlannerDocument
        {
            Semesters = { semester },
            Labels = labels,
            CourseLibrary = { studio, independentStudy },
            Plans = { plan },
            Settings = new AppSettings
            {
                Language = LanguageMode.English,
                Theme = ThemeMode.Dark,
                CurrentSemesterId = semester.SemesterId,
                OpenPlanIds = { plan.PlanId },
                CurrentPlanId = plan.PlanId
            }
        };
    }

    private static string Canonical<T>(T value) =>
        JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static PlannerDocument CreateNearMaximumEscapedTextLibrary()
    {
        var document = SeedData.Create("Exportable semester", "Exportable plan");
        document.CourseLibrary.Clear();
        var totalCharacters = PlannerDocumentTextCapacity.Count(document);
        for (var index = 0; index < PlannerDataLimits.MaxCourses; index++)
        {
            var course = new CourseOffering
            {
                SemesterId = document.Semesters[0].SemesterId,
                CourseName = $"Course {index:D4}",
                Notes = new string('\0', PlannerDataLimits.MaxTextFieldLength),
                Color = "#336699"
            };
            CourseIdentityService.AssignOfferingId(course);
            var courseCharacters = PlannerDocumentTextCapacity.Count(course);
            if (totalCharacters + courseCharacters > PlannerDataLimits.MaxAggregateTextCharacters)
            {
                course.Notes = "";
                var baseCourseCharacters = PlannerDocumentTextCapacity.Count(course);
                var remaining = PlannerDataLimits.MaxAggregateTextCharacters - totalCharacters;
                if (remaining >= baseCourseCharacters)
                {
                    course.Notes = new string('\0', checked((int)(remaining - baseCourseCharacters)));
                    document.CourseLibrary.Add(course);
                    totalCharacters += PlannerDocumentTextCapacity.Count(course);
                }
                break;
            }
            document.CourseLibrary.Add(course);
            totalCharacters += courseCharacters;
        }

        Assert.True(document.CourseLibrary.Count > 2_000);
        Assert.Equal(PlannerDataLimits.MaxAggregateTextCharacters, totalCharacters);
        return document;
    }

    private static PlannerDocument CreateMaximumStructuralSelectionPlan()
    {
        var document = SeedData.Create("Maximum structure semester", "Maximum structure plan");
        document.Labels.Clear();
        document.CourseLibrary.Clear();
        var plan = document.Plans[0];
        plan.Snapshots.Clear();
        for (var index = 0; index < PlannerDataLimits.MaxCourses; index++)
        {
            var course = new CourseOffering
            {
                SemesterId = document.Semesters[0].SemesterId,
                CourseName = $"Course {index:D4}",
                Color = "#336699"
            };
            CourseIdentityService.AssignOfferingId(course);
            document.CourseLibrary.Add(course);
            plan.Snapshots.Add(new PlanCourseSnapshot
            {
                SnapshotId = $"snapshot-{index:D4}",
                CourseOfferingId = course.OfferingId,
                RegistrationOrder = index
            });
        }
        return document;
    }

    private static PlannerDocument CreateSelectionSource(
        PlannerDocument target,
        CourseOffering course,
        string planId)
    {
        var plan = new SelectionPlan
        {
            PlanId = planId,
            PlanName = planId,
            SemesterId = target.Semesters[0].SemesterId,
            Snapshots =
            {
                new PlanCourseSnapshot
                {
                    SnapshotId = $"snapshot-{planId}",
                    CourseOfferingId = course.OfferingId,
                    RegistrationOrder = 0
                }
            }
        };
        return new PlannerDocument
        {
            Semesters = { JsonDefaults.Clone(target.Semesters[0]) },
            Labels = JsonDefaults.Clone(target.Labels),
            CourseLibrary = { JsonDefaults.Clone(course) },
            Plans = { plan }
        };
    }

    private static PlannerDocument CreateSelectionTargetWithExistingLibrary(PlannerDocument source) => new()
    {
        Semesters = JsonDefaults.Clone(source.Semesters),
        Labels = JsonDefaults.Clone(source.Labels),
        CourseLibrary = JsonDefaults.Clone(source.CourseLibrary),
        Settings = new AppSettings { CurrentSemesterId = source.Semesters[0].SemesterId }
    };

    private static string MutateJson(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidDataException("Expected a JSON object.");
        mutate(root);
        return root.ToJsonString(JsonDefaults.Options);
    }

    private static JsonObject SelectExchangeObject(
        JsonObject root,
        string packageKind,
        string objectKind)
    {
        var course = root["courses"]?.AsArray()[0]?.AsObject();
        return objectKind switch
        {
            "root" => root,
            "semester" when string.Equals(
                packageKind,
                PlannerSchemas.CourseLibraryKind,
                StringComparison.Ordinal) => root["semesters"]!.AsArray()[0]!.AsObject(),
            "semester" => root["semester"]!.AsObject(),
            "period" when string.Equals(
                packageKind,
                PlannerSchemas.CourseLibraryKind,
                StringComparison.Ordinal) => root["semesters"]!.AsArray()[0]!["periodSchedule"]!.AsArray()[0]!.AsObject(),
            "period" => root["semester"]!["periodSchedule"]!.AsArray()[0]!.AsObject(),
            "label" => root["labels"]!.AsArray()[0]!.AsObject(),
            "course" => course
                        ?? throw new InvalidDataException("Expected a course object."),
            "meeting" => course?["meetingTimes"]!.AsArray()[0]!.AsObject()
                         ?? throw new InvalidDataException("Expected a meeting object."),
            "plan" => root["plan"]!.AsObject(),
            "snapshot" => root["plan"]!["snapshots"]!.AsArray()[0]!.AsObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(objectKind), objectKind, null)
        };
    }

    private static void AssertExplicitJsonNull(JsonObject containingObject, string propertyName)
    {
        Assert.True(containingObject.ContainsKey(propertyName));
        Assert.Null(containingObject[propertyName]);
    }

    private static void AddUnknownFields(JsonObject root, string packageKind)
    {
        root["futureRoot"] = new JsonObject
        {
            ["enabled"] = true,
            ["values"] = new JsonArray(1, 2, 3)
        };
        var semester = SelectExchangeObject(root, packageKind, "semester");
        semester["futureSemester"] = "extension";
        SelectExchangeObject(root, packageKind, "period")["futurePeriod"] = 42;

        if (root["labels"]!.AsArray().Count > 0)
            SelectExchangeObject(root, packageKind, "label")["futureLabel"] = false;
        if (root["courses"]!.AsArray().Count > 0)
        {
            SelectExchangeObject(root, packageKind, "course")["futureCourse"] = new JsonArray("a", "b");
            SelectExchangeObject(root, packageKind, "meeting")["futureMeeting"] = new JsonObject
            {
                ["mode"] = "hybrid"
            };
        }
        if (string.Equals(packageKind, PlannerSchemas.SelectionPlanKind, StringComparison.Ordinal))
        {
            SelectExchangeObject(root, packageKind, "plan")["futurePlan"] = 99;
            SelectExchangeObject(root, packageKind, "snapshot")["futureSnapshot"] = null;
        }
    }

    private static void AssertInvalidJsonPreview(string json)
    {
        ImportPreview? preview = null;
        var exception = Record.Exception(() =>
            preview = ImportExportService.PreviewJson(new PlannerDocument(), json));

        Assert.Null(exception);
        Assert.NotNull(preview);
        Assert.False(preview!.CanApply);
        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewStatus.NotImportable, item.Status);
        Assert.Contains(item.Errors, error => error.Code == "Import.InvalidJson");
    }
}

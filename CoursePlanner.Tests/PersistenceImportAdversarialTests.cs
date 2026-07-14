using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoursePlanner.Core;
using CoursePlanner.Exchange;
using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class PersistenceImportAdversarialTests
{
    [Theory]
    [InlineData("kind", "\"unknown-kind\"")]
    [InlineData("schemaVersion", "\"1.0.0\"")]
    public void ImportPreviewRejectsDuplicateEnvelopePropertiesEvenWhenLastValueLooksValid(
        string propertyName,
        string firstValue)
    {
        var document = TestDocumentFactory.CreatePopulated();
        var json = ImportExportService.ExportCourseLibraryJson(document);
        var marker = $"\"{propertyName}\":";
        var propertyIndex = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(propertyIndex >= 0);
        var ambiguousJson = json.Insert(propertyIndex, $"\"{propertyName}\": {firstValue},\n  ");
        var before = JsonSerializer.Serialize(document, JsonDefaults.Options);

        var preview = ImportExportService.PreviewJson(document, ambiguousJson);
        var applied = ImportExportService.ApplyImport(document, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Items.SelectMany(item => item.Errors), error => error.Code == "Import.InvalidJson");
        Assert.False(applied.Applied);
        Assert.Equal(before, JsonSerializer.Serialize(document, JsonDefaults.Options));
    }

    [Fact]
    public void ImportRejectsEscapedUnpairedSurrogateWithoutReplacingUserText()
    {
        var target = TestDocumentFactory.CreatePopulated();
        var json = ImportExportService.ExportCourseLibraryJson(target);
        var exported = JsonNode.Parse(json)?.AsObject()
                       ?? throw new InvalidDataException("Expected an exported JSON object.");
        Assert.Equal(
            "Data Structures",
            exported["courses"]!.AsArray()[0]!["courseName"]!.GetValue<string>());
        var corruptJson = ReplaceJsonStringValueWithRawLiteral(
            json,
            "courseName",
            "Data Structures",
            "\"\\uD800\"");
        var before = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(target, JsonDefaults.Options));

        var preview = ImportExportService.PreviewJson(target, corruptJson);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Items.SelectMany(item => item.Errors), issue => issue.Code == "Import.InvalidJson");
        Assert.False(result.Applied);
        Assert.Equal(
            before,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target, JsonDefaults.Options)));
    }

    [Fact]
    public void RepositoryRejectsDuplicateSchemaPropertyWithoutSeedingOrChangingRawState()
    {
        using var workspace = new RepositoryWorkspace();
        var currentJson = JsonSerializer.Serialize(SeedData.Create(), JsonDefaults.Options);
        var marker = "\"schemaVersion\":";
        var propertyIndex = currentJson.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(propertyIndex >= 0);
        var ambiguousJson = currentJson.Insert(propertyIndex, "\"schemaVersion\": \"1.0.0\",\n  ");
        workspace.WriteRawState(ambiguousJson);
        var seedCalls = 0;
        var repository = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());

        Assert.Equal(0, seedCalls);
        Assert.Equal(ambiguousJson, workspace.ReadRawState());
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("99.0.0")]
    public void UnsupportedStoredSchemaIsRejectedWithZeroDatabaseOrRecoveryWrites(string schemaVersion)
    {
        using var workspace = new RepositoryWorkspace();
        var rawJson = $"  {{ \"schemaVersion\": \"{schemaVersion}\", \"preserve\": \"用户数据\" }}  ";
        workspace.WriteRawState(rawJson);
        var originalDatabase = File.ReadAllBytes(workspace.DatabasePath);
        var seedCalls = 0;
        var repository = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());
        SqliteConnection.ClearAllPools();

        Assert.Contains("Document.SchemaVersion.Unsupported", exception.IssueCodes);
        Assert.Equal(0, seedCalls);
        Assert.Equal(rawJson, workspace.ReadRawState());
        Assert.Equal(originalDatabase, File.ReadAllBytes(workspace.DatabasePath));
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Fact]
    public void OversizedSchemaVersionEnvelopeIsRejectedWithoutRecoveryOrDatabaseWrites()
    {
        using var workspace = new RepositoryWorkspace();
        var root = JsonNode.Parse(JsonSerializer.Serialize(SeedData.Create(), JsonDefaults.Options))!.AsObject();
        root["schemaVersion"] = new string('9', PlannerDataLimits.MaxSchemaVersionLength + 1);
        var rawJson = root.ToJsonString(JsonDefaults.Options);
        workspace.WriteRawState(rawJson);
        var originalDatabase = File.ReadAllBytes(workspace.DatabasePath);
        var seedCalls = 0;
        var repository = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());
        SqliteConnection.ClearAllPools();

        Assert.Contains("Document.SchemaVersion.TooLong", exception.IssueCodes);
        Assert.Equal(0, seedCalls);
        Assert.Equal(rawJson, workspace.ReadRawState());
        Assert.Equal(originalDatabase, File.ReadAllBytes(workspace.DatabasePath));
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedUnsupportedSchemaIsRejectedBeforeDestructiveRecovery(
        bool schemaAfterLargeWhitespacePrefix)
    {
        using var workspace = new RepositoryWorkspace();
        const int maximumStateBytes = 16 * 1024;
        var rawJson = schemaAfterLargeWhitespacePrefix
            ? new string(' ', 80 * 1024) + "{ \"schemaVersion\": \"99.0.0\", \"future\": true }"
            : "{ \"schemaVersion\": \"99.0.0\", \"padding\": \"" + new string('x', 80 * 1024) + "\" }";
        workspace.WriteRawState(rawJson);
        var originalDatabase = File.ReadAllBytes(workspace.DatabasePath);
        var seedCalls = 0;
        var repository = new SqliteAppRepository(
            workspace.Path,
            () =>
            {
                seedCalls++;
                return SeedData.Create();
            },
            maximumStateJsonBytes: maximumStateBytes);

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());
        SqliteConnection.ClearAllPools();

        Assert.Contains("Document.SchemaVersion.Unsupported", exception.IssueCodes);
        Assert.Equal(0, seedCalls);
        Assert.Equal(rawJson, workspace.ReadRawState());
        Assert.Equal(originalDatabase, File.ReadAllBytes(workspace.DatabasePath));
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Theory]
    [InlineData("empty-sqlite")]
    [InlineData("legacy-table-only")]
    [InlineData("incomplete-app-state")]
    public void ExistingDatabaseWithoutTheExactCurrentStorageSchemaIsRejectedWithZeroWrites(
        string databaseShape)
    {
        using var workspace = new RepositoryWorkspace();
        workspace.CreateDatabase(databaseShape switch
        {
            "empty-sqlite" => "VACUUM;",
            "legacy-table-only" =>
                "CREATE TABLE legacy_state (payload TEXT NOT NULL); " +
                "INSERT INTO legacy_state(payload) VALUES ('preserve me');",
            "incomplete-app-state" =>
                "CREATE TABLE app_state (id TEXT PRIMARY KEY NOT NULL, payload TEXT NOT NULL); " +
                "INSERT INTO app_state(id, payload) VALUES ('default', 'preserve me');",
            _ => throw new ArgumentOutOfRangeException(nameof(databaseShape), databaseShape, null)
        });
        var originalDatabase = File.ReadAllBytes(workspace.DatabasePath);
        var originalTables = workspace.ReadUserTableNames();
        var seedCalls = 0;
        var repository = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var loadException = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());
        var initializeException = Assert.Throws<RepositoryStateValidationException>(() => repository.Initialize());
        var saveException = Assert.Throws<RepositoryStateValidationException>(() =>
            repository.Save(SeedData.Create()));
        SqliteConnection.ClearAllPools();

        Assert.All(
            new[] { loadException, initializeException, saveException },
            exception => Assert.Contains("Document.StorageSchema.Unsupported", exception.IssueCodes));
        Assert.Equal(0, seedCalls);
        Assert.Equal(originalDatabase, File.ReadAllBytes(workspace.DatabasePath));
        Assert.Equal(originalTables, workspace.ReadUserTableNames());
        Assert.False(Directory.Exists(repository.LogsDirectory));
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Fact]
    public void CourseLibraryImportRejectsCanonicallyEquivalentSemesterNamesAtomically()
    {
        var target = TestDocumentFactory.CreatePopulated();
        var first = JsonDefaults.Clone(target.Semesters[0]);
        first.SemesterId = "semester-normalized-a";
        first.SemesterName = "Caf\u00e9";
        var second = JsonDefaults.Clone(first);
        second.SemesterId = "semester-normalized-b";
        second.SemesterName = "Cafe\u0301";
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = [first, second]
        }, JsonDefaults.Options);
        var before = JsonSerializer.Serialize(target, JsonDefaults.Options);

        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.False(result.Applied);
        Assert.Equal(before, JsonSerializer.Serialize(target, JsonDefaults.Options));
    }

    [Theory]
    [InlineData(1899, 12, 25, 1899, 12, 31)]
    [InlineData(2101, 1, 3, 2101, 1, 9)]
    public void ImportRejectsSemesterDatesTheCalendarCannotRepresentWithoutChangingThem(
        int startYear,
        int startMonth,
        int startDay,
        int endYear,
        int endMonth,
        int endDay)
    {
        var target = TestDocumentFactory.CreatePopulated();
        var semester = JsonDefaults.Clone(target.Semesters[0]);
        semester.SemesterId = $"outside-calendar-{startYear}";
        semester.SemesterName = $"Outside calendar {startYear}";
        semester.StartDate = new DateOnly(startYear, startMonth, startDay);
        semester.EndDate = new DateOnly(endYear, endMonth, endDay);
        semester.WeekCount = 1;
        semester.WeekStartDay = WeekStartDay.Monday;
        var json = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = [semester]
        }, JsonDefaults.Options);
        var before = JsonSerializer.Serialize(target, JsonDefaults.Options);

        var preview = ImportExportService.PreviewJson(target, json);
        var result = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions());

        Assert.False(preview.CanApply);
        Assert.Contains(
            preview.Items.SelectMany(item => item.Errors),
            error => error.Code == "SemesterDateSupportedRange");
        Assert.False(result.Applied);
        Assert.Equal(before, JsonSerializer.Serialize(target, JsonDefaults.Options));
    }

    [Fact]
    public void RepositorySaveRejectsOutOfRangeSemesterDateWithoutReplacingStoredState()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = new SqliteAppRepository(workspace.Path);
        var document = repository.LoadOrCreate();
        var originalJson = workspace.ReadRawState();
        document.Semesters[0].StartDate = new DateOnly(1899, 12, 25);
        document.Semesters[0].EndDate = new DateOnly(1899, 12, 31);
        document.Semesters[0].WeekCount = 1;
        document.Semesters[0].WeekStartDay = WeekStartDay.Monday;

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Contains("Semester.SemesterDateSupportedRange", exception.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Theory]
    [InlineData("invalid-color")]
    [InlineData("noncanonical-course-id")]
    [InlineData("duplicate-registration-order")]
    [InlineData("missing-current-semester")]
    [InlineData("missing-current-plan")]
    public void CurrentSchemaStateThatNeedsConsistencyRepairIsRecoveredInsteadOfSilentlyRewritten(
        string corruption)
    {
        using var workspace = new RepositoryWorkspace();
        var writer = new SqliteAppRepository(workspace.Path);
        writer.Save(TestDocumentFactory.CreatePopulated());
        var root = JsonNode.Parse(workspace.ReadRawState())!.AsObject();
        ApplyConsistencyCorruption(root, corruption);
        var corruptJson = root.ToJsonString(JsonDefaults.Options);
        workspace.WriteRawState(corruptJson);
        var seedCalls = 0;
        var reader = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var loaded = reader.LoadOrCreate();

        Assert.Equal(1, seedCalls);
        Assert.NotNull(reader.LastRecoveryArtifactPath);
        Assert.NotEqual(corruptJson, workspace.ReadRawState());
        Assert.Equal(PlannerSchemas.Current, loaded.SchemaVersion);
    }

    [Theory]
    [InlineData("invalid-color")]
    [InlineData("noncanonical-course-id")]
    [InlineData("duplicate-registration-order")]
    [InlineData("missing-current-semester")]
    [InlineData("missing-current-plan")]
    public void RepositorySaveRejectsConsistencyRepairWithoutMutatingInputOrStoredState(string corruption)
    {
        using var workspace = new RepositoryWorkspace();
        var repository = new SqliteAppRepository(workspace.Path);
        var document = TestDocumentFactory.CreatePopulated();
        repository.Save(document);
        var storedBefore = workspace.ReadRawState();
        ApplyConsistencyCorruption(document, corruption);
        var inputBefore = JsonSerializer.Serialize(document, JsonDefaults.Options);

        Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Equal(inputBefore, JsonSerializer.Serialize(document, JsonDefaults.Options));
        Assert.Equal(storedBefore, workspace.ReadRawState());
    }

    [Theory]
    [InlineData(false, "invalid-color")]
    [InlineData(false, "noncanonical-course-id")]
    [InlineData(false, "missing-label-definition")]
    [InlineData(true, "invalid-color")]
    [InlineData(true, "noncanonical-course-id")]
    [InlineData(true, "missing-label-definition")]
    [InlineData(true, "duplicate-registration-order")]
    public void CurrentSchemaImportsRejectLossyRepairInsteadOfNormalizingCorruptPackages(
        bool selectionPlan,
        string corruption)
    {
        var source = TestDocumentFactory.CreatePopulated();
        var json = selectionPlan
            ? ImportExportService.ExportSelectionPlanJson(source, source.Plans[0])
            : ImportExportService.ExportCourseLibraryJson(source);
        var root = JsonNode.Parse(json)!.AsObject();
        ApplyImportPackageCorruption(root, corruption);
        var corruptJson = root.ToJsonString(JsonDefaults.Options);
        var target = SeedData.Create("Target semester", "Target plan");
        var before = JsonSerializer.Serialize(target, JsonDefaults.Options);

        var preview = ImportExportService.PreviewJson(target, corruptJson);
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
            issue => issue.Code == "Import.InvalidJson");
        Assert.False(result.Applied);
        Assert.Equal(before, JsonSerializer.Serialize(target, JsonDefaults.Options));
    }

    [Theory]
    [InlineData(0x13579BDF)]
    [InlineData(0x2468ACE)]
    [InlineData(0x5EED1234)]
    public void RandomJsonTokenMutationsNeverEscapePreviewAsUnhandledExceptions(int seed)
    {
        var baseline = TestDocumentFactory.CreatePopulated();
        var json = ImportExportService.ExportCourseLibraryJson(baseline);
        var random = new Random(seed);
        char[] replacements =
        [
            '\0', '\t', '\n', ' ', '"', '\\', '{', '}', '[', ']', ',', ':',
            '-', '+', '.', '0', '9', 'e', 'n', 'u', 'l', 't', 'f', '\uD800', '\uDC00'
        ];

        for (var iteration = 0; iteration < 256; iteration++)
        {
            var mutated = json.ToCharArray();
            var mutationCount = random.Next(1, 5);
            for (var mutation = 0; mutation < mutationCount; mutation++)
                mutated[random.Next(mutated.Length)] = replacements[random.Next(replacements.Length)];
            var candidateJson = new string(mutated);
            var target = JsonDefaults.Clone(baseline);

            var exception = Record.Exception(() =>
            {
                var preview = ImportExportService.PreviewJson(target, candidateJson);
                _ = ImportExportService.ApplyImport(target, preview, new ImportApplyOptions
                {
                    ForceOutOfRangeCourses = true,
                    ForceSemesterMergeConflicts = true,
                    SynchronizeMissingPlanCourses = true,
                    UpdateExistingSemesterSettings = true
                });
                _ = JsonSerializer.Serialize(target, JsonDefaults.CompactOptions);
            });

            Assert.True(
                exception is null,
                $"Seed {seed}, iteration {iteration} escaped as {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    [Fact]
    public void RepositoryContentRecoveryDoesNotClassifyProgrammingFailuresAsUserDataErrors()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Persistence",
            "SqliteAppRepository.cs"));
        var policyStart = source.IndexOf(
            "private static bool IsStateContentException",
            StringComparison.Ordinal);
        Assert.True(policyStart >= 0);
        var policy = source[policyStart..];

        Assert.DoesNotContain("InvalidOperationException", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReferenceException", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyNotFoundException", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupContentValidationDoesNotClassifyProgrammingFailuresAsCorruptData()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Persistence",
            "BackupService.cs"));
        var validationStart = source.IndexOf(
            "private static void ValidateSqliteDatabase",
            StringComparison.Ordinal);
        var validationEnd = source.IndexOf(
            "private static bool HasSqliteHeader",
            validationStart,
            StringComparison.Ordinal);
        Assert.True(validationStart >= 0);
        Assert.True(validationEnd > validationStart);
        var validation = source[validationStart..validationEnd];

        Assert.Contains("RepositoryStateValidationException", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReferenceException", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatException", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("OverflowException", validation, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportExportWorkflowPolicyDoesNotHideProgrammingOrRollbackFailures()
    {
        var source = File.ReadAllText(ProjectFilePath(
            "CoursePlanner",
            "Services",
            "ImportExportCoordinator.cs"));
        var policyStart = source.IndexOf(
            "private static bool IsExpectedWorkflowException",
            StringComparison.Ordinal);
        Assert.True(policyStart >= 0);
        var policy = source[policyStart..];

        Assert.Contains("RuntimeOperationExceptionPolicy.IsRecoverable(exception)", policy, StringComparison.Ordinal);
        Assert.Contains("ExportWorkflowPreconditionException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException or", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentSessionRollbackException", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentSchemaPersistenceUsesPureMappingAndNeverRunsRepairBeforeCommit()
    {
        var mapper = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Persistence",
            "PersistenceDtos.cs"));
        var validator = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Persistence",
            "PlannerDocumentPersistenceValidator.cs"));
        var repository = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Persistence",
            "SqliteAppRepository.cs"));
        var persistStart = repository.IndexOf("private void PersistDocument", StringComparison.Ordinal);
        var persistEnd = repository.IndexOf("public void Log", persistStart, StringComparison.Ordinal);
        Assert.True(persistStart >= 0 && persistEnd > persistStart);
        var persist = repository[persistStart..persistEnd];

        Assert.DoesNotContain("ensureConsistency", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentConsistencyService.Ensure", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentConsistencyService.Ensure", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentConsistencyService.Ensure", persist, StringComparison.Ordinal);
        Assert.Contains("JsonDefaults.CompactOptions", persist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CON", "FileNameReserved")]
    [InlineData("bad.", "FileNameTrailing")]
    [InlineData("a/b", "FileNameIllegalCharacters")]
    [InlineData(null, "FileNameTooLong")]
    public void SemesterAndPlanDomainValidationShareExportSafeNameRules(
        string? name,
        string expectedCode)
    {
        name ??= new string('n', WindowsFileNameRules.MaxComponentLength + 1);
        var document = TestDocumentFactory.CreatePopulated();
        var semester = JsonDefaults.Clone(document.Semesters[0]);
        semester.SemesterName = name;
        var plan = JsonDefaults.Clone(document.Plans[0]);
        plan.PlanName = name;

        var semesterValidation = SemesterRules.ValidateSemester(semester, []);
        var planValidation = PlanRules.Validate(plan, []);

        Assert.Contains(semesterValidation.Errors, issue => issue.Code == expectedCode);
        Assert.Contains(planValidation.Errors, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void PersistenceAndBothImportKindsRejectExportUnsafeNames()
    {
        var source = TestDocumentFactory.CreatePopulated();
        var importedSemester = JsonDefaults.Clone(source.Semesters[0]);
        importedSemester.SemesterId = "unsafe-name-semester";
        importedSemester.SemesterName = "CON";
        var courseLibraryJson = JsonSerializer.Serialize(new CourseLibraryPackage
        {
            Semesters = [importedSemester]
        }, JsonDefaults.Options);
        var courseLibraryTarget = TestDocumentFactory.CreatePopulated();
        var coursePreview = ImportExportService.PreviewJson(courseLibraryTarget, courseLibraryJson);

        var planJson = ImportExportService.ExportSelectionPlanJson(source, source.Plans[0]);
        var planNode = JsonNode.Parse(planJson)!.AsObject();
        planNode["plan"]!["planName"] = "bad.";
        var planTarget = SeedData.Create("Target semester", "Target plan");
        var planPreview = ImportExportService.PreviewJson(
            planTarget,
            planNode.ToJsonString(JsonDefaults.Options));

        using var workspace = new RepositoryWorkspace();
        var repository = new SqliteAppRepository(workspace.Path);
        var persisted = repository.LoadOrCreate();
        var originalJson = workspace.ReadRawState();
        persisted.Plans[0].PlanName = "a/b";
        var persistenceException = Assert.Throws<RepositoryStateValidationException>(
            () => repository.Save(persisted));

        Assert.False(coursePreview.CanApply);
        Assert.Contains(
            coursePreview.Items.SelectMany(item => item.Errors),
            issue => issue.Code == "FileNameReserved");
        Assert.False(planPreview.CanApply);
        Assert.Contains(
            planPreview.Items.SelectMany(item => item.Errors),
            issue => issue.Code == "Import.InvalidJson");
        Assert.Contains("Plan.FileNameIllegalCharacters", persistenceException.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Theory]
    [InlineData(0, "Course.LabelReference.Missing")]
    [InlineData(1, "Course.LabelReference.KindMismatch")]
    [InlineData(2, "Course.LabelReference.Missing")]
    [InlineData(3, "Course.LabelReference.KindMismatch")]
    [InlineData(4, "Course.LabelReference.Missing")]
    [InlineData(5, "Course.LabelReference.KindMismatch")]
    public void RepositoryRejectsCourseLabelReferencesOutsideTheirTypedCatalog(
        int scenario,
        string expectedIssueCode)
    {
        using var workspace = new RepositoryWorkspace();
        var repository = new SqliteAppRepository(workspace.Path);
        var document = TestDocumentFactory.CreatePopulated();
        repository.Save(document);
        var originalJson = workspace.ReadRawState();
        var course = document.CourseLibrary[0];

        switch (scenario)
        {
            case 0:
                course.Labels.Add("Uncatalogued ordinary label");
                break;
            case 1:
                course.Labels.Add(PlannerLabels.Major);
                break;
            case 2:
                course.CourseGroupType = "Uncatalogued course group";
                break;
            case 3:
                course.CourseGroupType = PlannerLabels.Core;
                break;
            case 4:
                course.StudyType = "Uncatalogued study type";
                break;
            case 5:
                course.StudyType = PlannerLabels.Major;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Contains(expectedIssueCode, exception.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);

    private static string ReplaceJsonStringValueWithRawLiteral(
        string json,
        string propertyName,
        string expectedValue,
        string replacementLiteral)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals(propertyName) ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.String ||
                !string.Equals(reader.GetString(), expectedValue, StringComparison.Ordinal))
            {
                continue;
            }

            var valueStart = checked((int)reader.TokenStartIndex);
            var valueEnd = checked((int)reader.BytesConsumed);
            return string.Concat(
                Encoding.UTF8.GetString(utf8.AsSpan(0, valueStart)),
                replacementLiteral,
                Encoding.UTF8.GetString(utf8.AsSpan(valueEnd)));
        }

        throw new InvalidDataException(
            $"Could not find JSON string property '{propertyName}' with the expected semantic value.");
    }

    private static void ApplyConsistencyCorruption(PlannerDocument document, string corruption)
    {
        var course = document.CourseLibrary[0];
        var plan = document.Plans.First(candidate => candidate.Snapshots.Count >= 2);
        switch (corruption)
        {
            case "invalid-color":
                course.Color = "not-a-color";
                break;
            case "noncanonical-course-id":
                var originalId = course.OfferingId;
                course.OfferingId = "noncanonical-course-id";
                foreach (var snapshot in document.Plans.SelectMany(candidate => candidate.Snapshots)
                             .Where(snapshot => snapshot.CourseOfferingId == originalId))
                {
                    snapshot.CourseOfferingId = course.OfferingId;
                }
                break;
            case "duplicate-registration-order":
                plan.Snapshots[0].RegistrationOrder = 0;
                plan.Snapshots[1].RegistrationOrder = 0;
                break;
            case "missing-current-semester":
                document.Settings.CurrentSemesterId = null;
                break;
            case "missing-current-plan":
                document.Settings.CurrentPlanId = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void ApplyConsistencyCorruption(JsonObject root, string corruption)
    {
        var courses = root["courseLibrary"]!.AsArray();
        var course = courses[0]!.AsObject();
        var plans = root["plans"]!.AsArray();
        var plan = plans.Select(node => node!.AsObject())
            .First(candidate => candidate["snapshots"]!.AsArray().Count >= 2);
        switch (corruption)
        {
            case "invalid-color":
                course["color"] = "not-a-color";
                break;
            case "noncanonical-course-id":
                var originalId = course["offeringId"]!.GetValue<string>();
                const string replacementId = "noncanonical-course-id";
                course["offeringId"] = replacementId;
                foreach (var snapshot in plans
                             .SelectMany(node => node!["snapshots"]!.AsArray())
                             .Select(node => node!.AsObject())
                             .Where(snapshot => snapshot["courseOfferingId"]!.GetValue<string>() == originalId))
                {
                    snapshot["courseOfferingId"] = replacementId;
                }
                break;
            case "duplicate-registration-order":
                var snapshots = plan["snapshots"]!.AsArray();
                snapshots[0]!["registrationOrder"] = 0;
                snapshots[1]!["registrationOrder"] = 0;
                break;
            case "missing-current-semester":
                root["settings"]!["currentSemesterId"] = null;
                break;
            case "missing-current-plan":
                root["settings"]!["currentPlanId"] = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void ApplyImportPackageCorruption(JsonObject root, string corruption)
    {
        var courses = root["courses"]!.AsArray();
        var course = courses[0]!.AsObject();
        switch (corruption)
        {
            case "invalid-color":
                course["color"] = "not-a-color";
                break;
            case "noncanonical-course-id":
                var originalId = course["offeringId"]!.GetValue<string>();
                const string replacementId = "noncanonical-imported-course";
                course["offeringId"] = replacementId;
                if (root["plan"] is JsonObject plan)
                {
                    foreach (var snapshot in plan["snapshots"]!.AsArray()
                                 .Select(node => node!.AsObject())
                                 .Where(snapshot => snapshot["courseOfferingId"]!.GetValue<string>() == originalId))
                    {
                        snapshot["courseOfferingId"] = replacementId;
                    }
                }
                break;
            case "missing-label-definition":
                var referencedName = course["labels"]!.AsArray()[0]!.GetValue<string>();
                var labels = root["labels"]!.AsArray();
                var definition = labels.First(node =>
                    node!["kind"]!.GetValue<int>() == (int)LabelKind.Ordinary &&
                    TextRules.IsSameLabel(node["name"]!.GetValue<string>(), referencedName));
                labels.Remove(definition);
                break;
            case "duplicate-registration-order":
                var snapshots = root["plan"]!["snapshots"]!.AsArray();
                Assert.True(snapshots.Count >= 2);
                snapshots[0]!["registrationOrder"] = 0;
                snapshots[1]!["registrationOrder"] = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private sealed class RepositoryWorkspace : IDisposable
    {
        public RepositoryWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"course-planner-persistence-fuzz-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "course-planner.sqlite");

        public void WriteRawState(string json)
        {
            var repository = new SqliteAppRepository(Path);
            repository.Initialize();
            using var connection = OpenConnection(repository.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_state (id, json, updated_at)
                VALUES ('default', $json, 'fuzz')
                ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$json", json);
            command.ExecuteNonQuery();
            connection.Close();
            SqliteConnection.ClearAllPools();
        }

        public string ReadRawState()
        {
            using var connection = OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM app_state WHERE id = 'default'";
            return Assert.IsType<string>(command.ExecuteScalar());
        }

        public void CreateDatabase(string sql)
        {
            using var connection = OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
            connection.Close();
            SqliteConnection.ClearAllPools();
        }

        public string[] ReadUserTableNames()
        {
            using var connection = OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name
                """;
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names.ToArray();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static SqliteConnection OpenConnection(string databasePath)
        {
            var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            return connection;
        }
    }
}

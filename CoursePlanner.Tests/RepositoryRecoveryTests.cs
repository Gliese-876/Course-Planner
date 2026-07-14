using System.Text.Json;
using System.Text.Json.Nodes;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class RepositoryRecoveryTests
{
    [Fact]
    public void StartupFailureDetailsExposeDataAndRecoveryPathsWithoutPromisingAReset()
    {
        var dataDirectory = Path.Combine("C:\\", "Users", "test", "CoursePlannerData");
        var recoveryDirectory = Path.Combine(dataDirectory, "recovery-artifacts");
        var exception = new RepositoryRecoveryException(
            "Recovery artifact could not be written.",
            recoveryDirectory,
            new IOException("Disk is read-only."));

        var details = StartupFailureDetails.Create(exception, dataDirectory);

        Assert.Contains(dataDirectory, details.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains(recoveryDirectory, details.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains("stored application state unchanged", details.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(exception.Message, details.TechnicalDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("seed", details.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppLaunchCatchesConstructionFailuresAndShowsAStandaloneFailureWindow()
    {
        var appSource = File.ReadAllText(RepositoryPaths.FromRoot(
            "CoursePlanner", "App.xaml.cs"));

        Assert.Contains("catch (Exception exception)", appSource, StringComparison.Ordinal);
        Assert.Contains("ShowStartupFailure(exception);", appSource, StringComparison.Ordinal);
        Assert.Contains("StartupFailureDetails.Create(exception, dataDirectory)", appSource, StringComparison.Ordinal);
        Assert.Contains("_window = new Window", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SeedData.Create", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidCurrentStateLoadsWithoutSeedingArchivingOrRewritingJson()
    {
        using var workspace = new RepositoryWorkspace();
        var document = SeedData.Create("有效状态", "有效方案");
        var originalJson = "\r\n" + JsonSerializer.Serialize(document, JsonDefaults.Options) + "\r\n";
        workspace.WriteRawState(originalJson);
        var seedCalls = 0;
        var repository = workspace.CreateRepository(() =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var loaded = repository.LoadOrCreate();

        Assert.Equal("有效状态", loaded.Semesters[0].SemesterName);
        Assert.Equal(0, seedCalls);
        Assert.Null(repository.LastRecoveryArtifactPath);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Fact]
    public void ExistingEmptyStateIsArchivedBeforeRecoverySeedIsWritten()
    {
        using var workspace = new RepositoryWorkspace();
        workspace.WriteRawState(string.Empty);
        var repository = workspace.CreateRepository(() => SeedData.Create("空状态恢复", "空状态恢复"));

        var recovered = repository.LoadOrCreate();

        Assert.Equal("空状态恢复", recovered.Semesters[0].SemesterName);
        AssertRecoveryArtifact(repository, string.Empty);
        Assert.Equal(PlannerSchemas.Current, ReadSchemaVersion(workspace.ReadRawState()));
    }

    [Fact]
    public void VersionOneStateIsRejectedWithoutMigrationSeedingOrReplacingStoredState()
    {
        using var workspace = new RepositoryWorkspace();
        var legacyDocument = CreateLegacyDocument();
        var originalJson = ToVersionOneJson(legacyDocument);
        workspace.WriteRawState(originalJson);
        var seedCalls = 0;
        var repository = workspace.CreateRepository(() =>
        {
            seedCalls++;
            return SeedData.Create("unexpected seed", "unexpected seed");
        });

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());

        Assert.Contains("Document.SchemaVersion.Unsupported", exception.IssueCodes);
        Assert.Equal(0, seedCalls);
        Assert.Null(repository.LastRecoveryArtifactPath);
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Fact]
    public void UnknownNewerSchemaIsRejectedWithoutSeedingOrReplacingStoredState()
    {
        using var workspace = new RepositoryWorkspace();
        const string rawJson = "  { \"schemaVersion\": \"99.0.0\", \"futureProperty\": [1, 2, 3] }  ";
        workspace.WriteRawState(rawJson);
        var seedCalls = 0;
        var repository = workspace.CreateRepository(() =>
        {
            seedCalls++;
            return SeedData.Create("不得使用的种子", "不得使用的种子");
        });

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.LoadOrCreate());

        Assert.Contains("Document.SchemaVersion.Unsupported", exception.IssueCodes);
        Assert.Equal(0, seedCalls);
        Assert.Null(repository.LastRecoveryArtifactPath);
        Assert.False(Directory.Exists(repository.RecoveryDirectory));
        Assert.Equal(rawJson, workspace.ReadRawState());
    }

    [Fact]
    public void MalformedCurrentStateIsArchivedThenDocumentSessionStartsWithoutJsonException()
    {
        using var workspace = new RepositoryWorkspace();
        const string rawJson = "{\r\n  \"schemaVersion\": \"2.0.0\",\r\n  \"semesters\": [ definitely-not-json\r\n";
        workspace.WriteRawState(rawJson);
        var repository = workspace.CreateRepository(() => SeedData.Create("malformed recovery", "recovered plan"));

        var exception = Record.Exception(() => new DocumentSession(repository));

        Assert.Null(exception);
        AssertRecoveryArtifact(repository, rawJson);
        Assert.Equal(PlannerSchemas.Current, ReadSchemaVersion(workspace.ReadRawState()));
        Assert.Contains(
            repository.ReadEventSummaries(),
            line => line.Contains("state-recovery", StringComparison.Ordinal) &&
                    line.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecoveryArtifactFailureLeavesAppStateByteForByteUnchangedAndDoesNotSeed()
    {
        using var workspace = new RepositoryWorkspace();
        const string rawJson = "{ \"schemaVersion\": \"2.0.0\", \"semesters\": [ definitely-not-json";
        workspace.WriteRawState(rawJson);
        var recoveryPathThatIsAFile = Path.Combine(workspace.Path, "recovery-blocker");
        File.WriteAllText(recoveryPathThatIsAFile, "not a directory");
        var seedCalls = 0;
        var repository = new SqliteAppRepository(
            workspace.Path,
            () =>
            {
                seedCalls++;
                return SeedData.Create();
            },
            recoveryDirectory: recoveryPathThatIsAFile);

        var exception = Assert.Throws<RepositoryRecoveryException>(() => repository.LoadOrCreate());

        Assert.Equal(0, seedCalls);
        Assert.Null(repository.LastRecoveryArtifactPath);
        Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rawJson, workspace.ReadRawState());
    }

    [Fact]
    public void OversizedStateIsNotMaterializedForParsingAndIsStreamedToARecoveryArtifact()
    {
        using var workspace = new RepositoryWorkspace();
        const int maximumJsonBytes = 16 * 1024;
        var rawJson = "{ \"schemaVersion\": \"2.0.0\", \"padding\": \"" + new string('x', maximumJsonBytes) + "\" }";
        workspace.WriteRawState(rawJson);
        var repository = new SqliteAppRepository(
            workspace.Path,
            () => SeedData.Create("oversize recovery", "oversize recovery"),
            maximumStateJsonBytes: maximumJsonBytes);

        var recovered = repository.LoadOrCreate();

        Assert.Equal("oversize recovery", recovered.Semesters[0].SemesterName);
        var artifactPath = Assert.IsType<string>(repository.LastRecoveryArtifactPath);
        Assert.Equal(rawJson, File.ReadAllText(artifactPath));
        Assert.Equal(PlannerSchemas.Current, ReadSchemaVersion(workspace.ReadRawState()));
        Assert.Contains(
            repository.ReadEventSummaries(),
            line => line.Contains("size limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecoveryArtifactsAreUniqueAndAtomicTemporaryFilesAreRemoved()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        const string first = "{ \"schemaVersion\": \"2.0.0\", \"semesters\": [ invalid-a";
        workspace.WriteRawState(first);
        repository.LoadOrCreate();
        var firstPath = AssertRecoveryArtifact(repository, first);

        const string second = "{ \"schemaVersion\": \"2.0.0\", \"semesters\": [ invalid-b";
        workspace.WriteRawState(second);
        repository.LoadOrCreate();
        var secondPath = AssertRecoveryArtifact(repository, second);

        Assert.NotEqual(firstPath, secondPath);
        var recoveryDirectory = Assert.IsType<string>(Path.GetDirectoryName(firstPath));
        Assert.Equal(2, Directory.EnumerateFiles(recoveryDirectory, "*.json").Count());
        Assert.Empty(Directory.EnumerateFiles(recoveryDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("null-course-library", "Document.CourseLibrary.Null")]
    [InlineData("null-semester-element", "Document.Semesters.NullElement")]
    [InlineData("null-semester-id", "Semester.Id.Null")]
    [InlineData("duplicate-semester-id", "Semester.Id.Duplicate")]
    [InlineData("duplicate-plan-id", "Plan.Id.Duplicate")]
    [InlineData("duplicate-snapshot-id", "Snapshot.Id.Duplicate")]
    [InlineData("duplicate-offering-id", "Course.Id.Duplicate")]
    [InlineData("duplicate-course-identity", "Course.Identity.Duplicate")]
    [InlineData("missing-course-semester", "Course.Semester.Missing")]
    [InlineData("missing-plan-semester", "Plan.Semester.Missing")]
    [InlineData("missing-snapshot-course", "Snapshot.Course.Missing")]
    [InlineData("cross-semester-plan-course", "Snapshot.Course.CrossSemester")]
    [InlineData("duplicate-plan-course", "Snapshot.Course.Duplicate")]
    [InlineData("invalid-week-start", "Semester.InvalidWeekStartDay")]
    [InlineData("invalid-label-kind", "Label.Kind.Invalid")]
    [InlineData("invalid-week-parity", "Course.InvalidWeekParity")]
    [InlineData("invalid-settings-language", "Settings.Language.Invalid")]
    [InlineData("invalid-period-table", "Semester.PeriodNumberSequence")]
    [InlineData("too-many-meetings", "Course.MeetingTimes.TooMany")]
    [InlineData("oversized-course-notes", "Course.Text.TooLong")]
    [InlineData("oversized-open-plan-id", "Settings.Id.TooLong")]
    public void SemanticallyMalformedCurrentStateIsArchivedInsteadOfBeingSilentlyRepairedOrLoaded(
        string corruption,
        string expectedIssueCode)
    {
        using var workspace = new RepositoryWorkspace();
        var node = CreateValidCurrentStateNode();
        ApplySemanticCorruption(node, corruption);
        var rawJson = "\r\n" + node.ToJsonString(JsonDefaults.Options) + "\r\n";
        workspace.WriteRawState(rawJson);
        var repository = workspace.CreateRepository(() => SeedData.Create("语义恢复", "语义恢复"));

        var recovered = repository.LoadOrCreate();

        Assert.Equal("语义恢复", recovered.Semesters[0].SemesterName);
        AssertRecoveryArtifact(repository, rawJson);
        Assert.Contains(
            repository.ReadEventSummaries(),
            line => line.Contains(nameof(RepositoryStateValidationException), StringComparison.Ordinal) &&
                    line.Contains(expectedIssueCode, StringComparison.Ordinal));
    }

    [Fact]
    public void SavingSemanticallyInvalidDocumentThrowsTypedValidationWithoutReplacingStoredState()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var document = repository.LoadOrCreate();
        var originalJson = workspace.ReadRawState();
        var duplicateSemester = JsonDefaults.Clone(document.Semesters[0]);
        duplicateSemester.SemesterName = "重复关键 ID";
        document.Semesters.Add(duplicateSemester);

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Contains("Semester.Id.Duplicate", exception.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Fact]
    public void SavingUnsupportedSchemaVersionIsRejectedWithoutPoisoningTheNextStartup()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var document = repository.LoadOrCreate();
        var originalJson = workspace.ReadRawState();
        document.SchemaVersion = "future-version";

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Contains("Document.SchemaVersion.Unsupported", exception.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
        Assert.Equal(PlannerSchemas.Current, repository.LoadOrCreate().SchemaVersion);
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Fact]
    public void SavingOversizedTextThrowsTypedValidationWithoutReplacingStoredState()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var document = repository.LoadOrCreate();
        var originalJson = workspace.ReadRawState();
        document.Plans[0].PlanName = new string('x', 2049);

        var exception = Assert.Throws<RepositoryStateValidationException>(() => repository.Save(document));

        Assert.Contains("Plan.Text.TooLong", exception.IssueCodes);
        Assert.Equal(originalJson, workspace.ReadRawState());
    }

    [Fact]
    public void SavingStateLargerThanConfiguredReadBoundCannotCreateAnUnreadableRow()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = new SqliteAppRepository(
            workspace.Path,
            maximumStateJsonBytes: 1_024);

        var exception = Assert.Throws<RepositoryStateValidationException>(
            () => repository.Save(SeedData.Create()));

        Assert.Contains("Document.SerializedSize.TooLarge", exception.IssueCodes);
        Assert.False(File.Exists(repository.DatabasePath));
    }

    [Fact]
    public void RejectedSaveDoesNotCreateARepositoryAtANonexistentPath()
    {
        using var workspace = new RepositoryWorkspace();
        var dataDirectory = Path.Combine(workspace.Path, "must-remain-missing", "nested");
        var repository = new SqliteAppRepository(dataDirectory);
        var invalid = SeedData.Create();
        invalid.SchemaVersion = "unsupported";

        Assert.Throws<RepositoryStateValidationException>(() => repository.Save(invalid));

        Assert.False(Directory.Exists(dataDirectory));
        Assert.False(File.Exists(repository.DatabasePath));
    }

    [Fact]
    public void RejectedSaveDoesNotPruneExistingEventHistory()
    {
        using var workspace = new RepositoryWorkspace();
        var repository = workspace.CreateRepository();
        var invalid = repository.LoadOrCreate();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = repository.DatabasePath
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM app_events;
                WITH RECURSIVE sequence(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM sequence WHERE value < 1005
                )
                INSERT INTO app_events (occurred_at, level, event_name, message)
                SELECT '2026-07-13T00:00:00Z', 'Info', 'retained', CAST(value AS TEXT)
                FROM sequence;
                """;
            command.ExecuteNonQuery();
        }
        invalid.SchemaVersion = "unsupported";

        Assert.Throws<RepositoryStateValidationException>(() => repository.Save(invalid));

        using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = repository.DatabasePath
        }.ToString());
        verification.Open();
        using var count = verification.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM app_events";
        Assert.Equal(1005L, Convert.ToInt64(count.ExecuteScalar()));
    }

    private static string AssertRecoveryArtifact(SqliteAppRepository repository, string expectedJson)
    {
        var artifactPath = Assert.IsType<string>(repository.LastRecoveryArtifactPath);
        Assert.True(File.Exists(artifactPath));
        Assert.Equal(expectedJson, File.ReadAllText(artifactPath));
        return artifactPath;
    }

    private static JsonObject CreateValidCurrentStateNode()
    {
        var document = SeedData.Create("第一学期", "第一方案");
        var firstSemester = document.Semesters[0];
        var secondSemester = JsonDefaults.Clone(firstSemester);
        secondSemester.SemesterId = "semester-two";
        secondSemester.SemesterName = "第二学期";
        secondSemester.DisplayOrder = 1;
        document.Semesters.Add(secondSemester);

        var firstCourse = new CourseOffering
        {
            SemesterId = firstSemester.SemesterId,
            CourseName = "第一课程",
            Teacher = "教师甲",
            Location = "教室甲",
            Credits = 3m,
            Color = "#123456",
            MeetingTimes =
            [
                new MeetingTime
                {
                    Weekday = 1,
                    StartPeriod = 1,
                    EndPeriod = 2,
                    Weeks = "1-18",
                    WeekParity = WeekParity.All
                }
            ]
        };
        var secondCourse = new CourseOffering
        {
            SemesterId = secondSemester.SemesterId,
            CourseName = "第二课程",
            Teacher = "教师乙",
            Location = "教室乙",
            Credits = 2m,
            Color = "#654321",
            MeetingTimes =
            [
                new MeetingTime
                {
                    Weekday = 2,
                    StartPeriod = 3,
                    EndPeriod = 4,
                    Weeks = "1-18",
                    WeekParity = WeekParity.All
                }
            ]
        };
        CourseIdentityService.AssignOfferingId(firstCourse);
        CourseIdentityService.AssignOfferingId(secondCourse);
        document.CourseLibrary.AddRange([firstCourse, secondCourse]);

        var firstPlan = document.Plans[0];
        firstPlan.Snapshots.Add(new PlanCourseSnapshot
        {
            SnapshotId = "snapshot-one",
            CourseOfferingId = firstCourse.OfferingId,
            RegistrationOrder = 0
        });
        var secondPlan = new SelectionPlan
        {
            PlanId = "plan-two",
            SemesterId = secondSemester.SemesterId,
            PlanName = "第二方案",
            DisplayOrder = 1,
            Snapshots =
            [
                new PlanCourseSnapshot
                {
                    SnapshotId = "snapshot-two",
                    CourseOfferingId = secondCourse.OfferingId,
                    RegistrationOrder = 0
                }
            ]
        };
        document.Plans.Add(secondPlan);
        document.Settings.OpenPlanIds.Add(secondPlan.PlanId);
        DocumentConsistencyService.Ensure(document);
        return Assert.IsType<JsonObject>(JsonNode.Parse(JsonSerializer.Serialize(document, JsonDefaults.Options)));
    }

    private static void ApplySemanticCorruption(JsonObject root, string corruption)
    {
        var semesters = Assert.IsType<JsonArray>(root["semesters"]);
        var labels = Assert.IsType<JsonArray>(root["labels"]);
        var courses = Assert.IsType<JsonArray>(root["courseLibrary"]);
        var plans = Assert.IsType<JsonArray>(root["plans"]);
        var settings = Assert.IsType<JsonObject>(root["settings"]);
        var firstSemester = Assert.IsType<JsonObject>(semesters[0]);
        var secondSemester = Assert.IsType<JsonObject>(semesters[1]);
        var firstCourse = Assert.IsType<JsonObject>(courses[0]);
        var secondCourse = Assert.IsType<JsonObject>(courses[1]);
        var firstPlan = Assert.IsType<JsonObject>(plans[0]);
        var secondPlan = Assert.IsType<JsonObject>(plans[1]);
        var firstSnapshots = Assert.IsType<JsonArray>(firstPlan["snapshots"]);
        var secondSnapshots = Assert.IsType<JsonArray>(secondPlan["snapshots"]);
        var firstSnapshot = Assert.IsType<JsonObject>(firstSnapshots[0]);
        var secondSnapshot = Assert.IsType<JsonObject>(secondSnapshots[0]);

        switch (corruption)
        {
            case "null-course-library":
                root["courseLibrary"] = null;
                break;
            case "null-semester-element":
                semesters[0] = null;
                break;
            case "null-semester-id":
                firstSemester["semesterId"] = null;
                break;
            case "duplicate-semester-id":
                secondSemester["semesterId"] = firstSemester["semesterId"]!.DeepClone();
                break;
            case "duplicate-plan-id":
                secondPlan["planId"] = firstPlan["planId"]!.DeepClone();
                break;
            case "duplicate-snapshot-id":
                secondSnapshot["snapshotId"] = firstSnapshot["snapshotId"]!.DeepClone();
                break;
            case "duplicate-offering-id":
                secondCourse["offeringId"] = firstCourse["offeringId"]!.DeepClone();
                break;
            case "duplicate-course-identity":
                foreach (var propertyName in new[]
                         {
                             "semesterId", "courseName", "teacher", "location", "meetingTimes"
                         })
                {
                    secondCourse[propertyName] = firstCourse[propertyName]!.DeepClone();
                }
                break;
            case "missing-course-semester":
                firstCourse["semesterId"] = "missing-semester";
                break;
            case "missing-plan-semester":
                firstPlan["semesterId"] = "missing-semester";
                break;
            case "missing-snapshot-course":
                firstSnapshot["courseOfferingId"] = "missing-course";
                break;
            case "cross-semester-plan-course":
                firstSnapshot["courseOfferingId"] = secondCourse["offeringId"]!.DeepClone();
                break;
            case "duplicate-plan-course":
                var duplicateSnapshot = Assert.IsType<JsonObject>(firstSnapshot.DeepClone());
                duplicateSnapshot["snapshotId"] = "snapshot-duplicate-course";
                firstSnapshots.Add(duplicateSnapshot);
                break;
            case "invalid-week-start":
                firstSemester["weekStartDay"] = 999;
                break;
            case "invalid-label-kind":
                Assert.IsType<JsonObject>(labels[0])["kind"] = 999;
                break;
            case "invalid-week-parity":
                var meetings = Assert.IsType<JsonArray>(firstCourse["meetingTimes"]);
                Assert.IsType<JsonObject>(meetings[0])["weekParity"] = 999;
                break;
            case "invalid-settings-language":
                settings["language"] = 999;
                break;
            case "invalid-period-table":
                var periods = Assert.IsType<JsonArray>(firstSemester["periodSchedule"]);
                Assert.IsType<JsonObject>(periods[1])["period"] = 1;
                break;
            case "too-many-meetings":
                var tooManyMeetings = Assert.IsType<JsonArray>(firstCourse["meetingTimes"]);
                var meetingTemplate = tooManyMeetings[0]!.DeepClone();
                while (tooManyMeetings.Count <= 32)
                    tooManyMeetings.Add(meetingTemplate.DeepClone());
                break;
            case "oversized-course-notes":
                firstCourse["notes"] = new string('大', 2_000_000);
                break;
            case "oversized-open-plan-id":
                var openPlanIds = Assert.IsType<JsonArray>(settings["openPlanIds"]);
                openPlanIds[0] = new string('p', 2049);
                break;
            case "oversized-schema-version":
                root["schemaVersion"] = new string('9', 2_000_000);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static string ReadSchemaVersion(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return Assert.IsType<string>(parsed.RootElement.GetProperty("schemaVersion").GetString());
    }

    private static PlannerDocument CreateLegacyDocument()
    {
        var document = SeedData.Create("用户保存的学期", "用户保存的方案");
        document.Labels.Add(new CourseLabel { Name = "普通标签", Kind = LabelKind.Ordinary, DisplayOrder = 7 });
        document.CourseLibrary.AddRange(
        [
            new CourseOffering
            {
                OfferingId = "legacy-course-a",
                SemesterId = document.Semesters[0].SemesterId,
                CourseName = "用户保存的课程",
                Teacher = "教师甲",
                Location = "教室 101",
                Credits = 3.5m,
                Labels = ["普通标签"],
                Notes = "必须原样保留的备注",
                Color = "#123456",
                MeetingTimes =
                [
                    new MeetingTime
                    {
                        Weekday = 2,
                        StartPeriod = 3,
                        EndPeriod = 4,
                        Weeks = "1-18"
                    }
                ]
            },
            new CourseOffering
            {
                OfferingId = "legacy-course-b",
                SemesterId = document.Semesters[0].SemesterId,
                CourseName = "用户保存的第二门课",
                Credits = 2m,
                Color = "#654321"
            }
        ]);
        var plan = document.Plans[0];
        plan.Snapshots =
        [
            new PlanCourseSnapshot { SnapshotId = "snapshot-a", CourseOfferingId = "legacy-course-a" },
            new PlanCourseSnapshot { SnapshotId = "snapshot-b", CourseOfferingId = "legacy-course-b" }
        ];
        return document;
    }

    private static string ToVersionOneJson(PlannerDocument document)
    {
        var node = Assert.IsType<JsonObject>(JsonNode.Parse(JsonSerializer.Serialize(document, JsonDefaults.Options)));
        node["schemaVersion"] = "1.0.0";
        foreach (var courseNode in Assert.IsType<JsonArray>(node["courseLibrary"]))
        {
            foreach (var meetingNode in Assert.IsType<JsonArray>(courseNode!["meetingTimes"]))
                Assert.IsType<JsonObject>(meetingNode).Remove("weekParity");
        }

        foreach (var planNode in Assert.IsType<JsonArray>(node["plans"]))
        {
            foreach (var snapshotNode in Assert.IsType<JsonArray>(planNode!["snapshots"]))
                Assert.IsType<JsonObject>(snapshotNode).Remove("registrationOrder");
        }

        return "\r\n" + node.ToJsonString(JsonDefaults.Options) + "\r\n";
    }

    private sealed class RepositoryWorkspace : IDisposable
    {
        public RepositoryWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"course-planner-repository-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public SqliteAppRepository CreateRepository(Func<PlannerDocument>? seedFactory = null) =>
            new(Path, seedFactory);

        public void WriteRawState(string json)
        {
            var repository = CreateRepository();
            repository.Initialize();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_state (id, json, updated_at)
                VALUES ('default', $json, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        public string ReadRawState()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM app_state WHERE id = 'default'";
            return Assert.IsType<string>(command.ExecuteScalar());
        }

        public bool HasState()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM app_state WHERE id = 'default')";
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(Path, "course-planner.sqlite")
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}

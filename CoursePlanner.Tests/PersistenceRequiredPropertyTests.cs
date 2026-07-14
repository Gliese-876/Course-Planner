using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class PersistenceRequiredPropertyTests
{
    public static TheoryData<string> RequiredManifestProperties =>
    [
        "kind",
        "schemaVersion",
        "createdAt",
        "databaseFile"
    ];

    public static TheoryData<string, string> RequiredStateProperties => new()
    {
        { "document", "schemaVersion" },
        { "document", "semesters" },
        { "document", "labels" },
        { "document", "courseLibrary" },
        { "document", "plans" },
        { "document", "settings" },

        { "semester", "semesterId" },
        { "semester", "semesterName" },
        { "semester", "startDate" },
        { "semester", "endDate" },
        { "semester", "weekCount" },
        { "semester", "weekStartDay" },
        { "semester", "displayOrder" },
        { "semester", "periodSchedule" },

        { "period", "period" },
        { "period", "start" },
        { "period", "end" },

        { "label", "name" },
        { "label", "kind" },
        { "label", "displayOrder" },

        { "course", "offeringId" },
        { "course", "semesterId" },
        { "course", "courseName" },
        { "course", "teacher" },
        { "course", "location" },
        { "course", "credits" },
        { "course", "labels" },
        { "course", "meetingTimes" },
        { "course", "notes" },
        { "course", "color" },
        { "course", "modifiedAt" },

        { "meeting", "weekday" },
        { "meeting", "startPeriod" },
        { "meeting", "endPeriod" },
        { "meeting", "weeks" },
        { "meeting", "weekParity" },

        { "plan", "planId" },
        { "plan", "semesterId" },
        { "plan", "planName" },
        { "plan", "displayOrder" },
        { "plan", "createdAt" },
        { "plan", "modifiedAt" },
        { "plan", "snapshots" },

        { "snapshot", "snapshotId" },
        { "snapshot", "courseOfferingId" },
        { "snapshot", "registrationOrder" },
        { "snapshot", "snapshotAt" },

        { "settings", "language" },
        { "settings", "theme" },
        { "settings", "currentSemesterId" },
        { "settings", "openPlanIds" }
    };

    public static TheoryData<string, string> RequiredReferenceStateProperties => new()
    {
        { "document", "semesters" },
        { "document", "labels" },
        { "document", "courseLibrary" },
        { "document", "plans" },
        { "document", "settings" },
        { "semester", "semesterId" },
        { "semester", "semesterName" },
        { "semester", "periodSchedule" },
        { "label", "name" },
        { "course", "offeringId" },
        { "course", "semesterId" },
        { "course", "courseName" },
        { "course", "teacher" },
        { "course", "location" },
        { "course", "labels" },
        { "course", "meetingTimes" },
        { "course", "notes" },
        { "course", "color" },
        { "meeting", "weeks" },
        { "plan", "planId" },
        { "plan", "semesterId" },
        { "plan", "planName" },
        { "plan", "snapshots" },
        { "snapshot", "snapshotId" },
        { "snapshot", "courseOfferingId" },
        { "settings", "openPlanIds" }
    };

    [Theory]
    [MemberData(nameof(RequiredStateProperties))]
    public void MissingCurrentSchemaPropertyIsArchivedAndRecoveredAsMalformedJson(
        string objectPath,
        string propertyName)
    {
        using var workspace = new TestDirectory();
        var writer = new SqliteAppRepository(workspace.Path);
        writer.Save(TestDocumentFactory.CreatePopulated(), "valid-current-state");
        var root = JsonNode.Parse(ReadRawState(writer.DatabasePath))!.AsObject();
        var owner = ResolveObject(root, objectPath);
        Assert.True(owner.Remove(propertyName));
        var malformedJson = root.ToJsonString(JsonDefaults.CompactOptions);

        AssertCurrentStateRecovery(
            workspace.Path,
            writer.DatabasePath,
            malformedJson,
            nameof(JsonException));
    }

    [Theory]
    [MemberData(nameof(RequiredReferenceStateProperties))]
    public void ExplicitNullRequiredReferenceStillUsesTheShapeValidator(
        string objectPath,
        string propertyName)
    {
        using var workspace = new TestDirectory();
        var writer = new SqliteAppRepository(workspace.Path);
        writer.Save(TestDocumentFactory.CreatePopulated(), "valid-current-state");
        var root = JsonNode.Parse(ReadRawState(writer.DatabasePath))!.AsObject();
        ResolveObject(root, objectPath)[propertyName] = null;
        var malformedJson = root.ToJsonString(JsonDefaults.CompactOptions);

        AssertCurrentStateRecovery(
            workspace.Path,
            writer.DatabasePath,
            malformedJson,
            nameof(RepositoryStateValidationException));
    }

    [Fact]
    public void NullableOmittedPropertiesRemainOptionalAcrossTheCurrentFormatRoundTrip()
    {
        using var workspace = new TestDirectory();
        var document = SeedData.Create("Optional fields", "Closed plan");
        document.Settings.OpenPlanIds.Clear();
        document.Settings.CurrentPlanId = null;
        var course = new CourseOffering
        {
            SemesterId = document.Semesters[0].SemesterId,
            CourseName = "Unscheduled optional course",
            Teacher = "",
            Location = "",
            Credits = 1,
            CourseGroupType = null,
            StudyType = null,
            EnrolledCount = null,
            Capacity = null,
            Labels = [],
            MeetingTimes = [],
            Notes = "",
            Color = "#123456"
        };
        CourseIdentityService.AssignOfferingId(course);
        document.CourseLibrary.Add(course);
        var repository = new SqliteAppRepository(workspace.Path);

        repository.Save(document, "optional-fields");
        var firstJson = ReadRawState(repository.DatabasePath);
        var firstRoot = JsonNode.Parse(firstJson)!.AsObject();
        var storedCourse = firstRoot["courseLibrary"]![0]!.AsObject();
        Assert.False(storedCourse.ContainsKey("courseGroupType"));
        Assert.False(storedCourse.ContainsKey("studyType"));
        Assert.False(storedCourse.ContainsKey("enrolledCount"));
        Assert.False(storedCourse.ContainsKey("capacity"));
        Assert.False(firstRoot["settings"]!.AsObject().ContainsKey("currentPlanId"));

        var loaded = repository.LoadOrCreate();
        var loadedCourse = Assert.Single(loaded.CourseLibrary);
        Assert.Null(loadedCourse.CourseGroupType);
        Assert.Null(loadedCourse.StudyType);
        Assert.Null(loadedCourse.EnrolledCount);
        Assert.Null(loadedCourse.Capacity);
        Assert.Null(loaded.Settings.CurrentPlanId);

        repository.Save(loaded, "optional-fields-roundtrip");
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(firstJson),
            JsonNode.Parse(ReadRawState(repository.DatabasePath))));
    }

    [Fact]
    public void UnknownIncrementalStatePropertiesAreIgnoredWithoutRewritingCurrentJson()
    {
        using var workspace = new TestDirectory();
        var writer = new SqliteAppRepository(workspace.Path);
        writer.Save(TestDocumentFactory.CreatePopulated(), "future-fields");
        var root = JsonNode.Parse(ReadRawState(writer.DatabasePath))!.AsObject();
        root["futureDocumentField"] = new JsonObject { ["enabled"] = true };
        foreach (var objectPath in new[]
                 {
                     "semester", "period", "label", "course", "meeting",
                     "plan", "snapshot", "settings"
                 })
        {
            ResolveObject(root, objectPath)["futureNestedField"] = 42;
        }
        var extendedJson = root.ToJsonString(JsonDefaults.CompactOptions);
        WriteRawState(writer.DatabasePath, extendedJson);
        var seedCalls = 0;
        var reader = new SqliteAppRepository(workspace.Path, () =>
        {
            seedCalls++;
            return SeedData.Create();
        });

        var loaded = reader.LoadOrCreate();

        Assert.Equal(0, seedCalls);
        Assert.NotEmpty(loaded.CourseLibrary);
        Assert.Null(reader.LastRecoveryArtifactPath);
        Assert.Equal(extendedJson, ReadRawState(reader.DatabasePath));
    }

    [Fact]
    public void CompleteCurrentFormatDocumentRoundTripsEveryNestedShape()
    {
        using var workspace = new TestDirectory();
        var repository = new SqliteAppRepository(workspace.Path);
        var document = TestDocumentFactory.CreatePopulated();
        var expected = JsonSerializer.Serialize(document, JsonDefaults.CompactOptions);

        repository.Save(document, "complete-current-format");
        var loaded = repository.LoadOrCreate();

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(expected),
            JsonNode.Parse(JsonSerializer.Serialize(loaded, JsonDefaults.CompactOptions))));
        repository.Save(loaded, "complete-current-format-roundtrip");
        Assert.Null(repository.LastRecoveryArtifactPath);
    }

    [Theory]
    [MemberData(nameof(RequiredManifestProperties))]
    public void MissingManifestPropertyIsRejectedBeforeTheTargetDatabaseIsReplaced(
        string propertyName)
    {
        using var workspace = new TestDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var automaticDirectory = System.IO.Path.Combine(workspace.Path, "automatic");
        var source = new SqliteAppRepository(sourceDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Candidate must not be installed";
        source.Save(candidate, "candidate");
        var backupPath = System.IO.Path.Combine(workspace.Path, "missing-manifest-property.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        RemoveManifestProperty(backupPath, propertyName);

        var target = new SqliteAppRepository(targetDirectory);
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original must remain";
        target.Save(original, "original");
        SqliteConnection.ClearAllPools();
        var originalBytes = File.ReadAllBytes(target.DatabasePath);

        BackupRestoreTransaction? transaction = null;
        Exception? exception;
        byte[] bytesImmediatelyAfterAttempt;
        try
        {
            exception = Record.Exception(() =>
            {
                transaction = BackupService.BeginRestoreWithPreBackup(
                    target.DatabasePath,
                    backupPath,
                    automaticDirectory);
            });
            bytesImmediatelyAfterAttempt = File.ReadAllBytes(target.DatabasePath);
        }
        finally
        {
            transaction?.Rollback();
        }

        Assert.IsType<InvalidDataException>(exception);
        Assert.Null(transaction);
        Assert.Equal(originalBytes, bytesImmediatelyAfterAttempt);
        Assert.False(Directory.Exists(automaticDirectory));
    }

    [Fact]
    public void MissingRequiredStatePropertyInBackupIsRejectedBeforeTargetReplacement()
    {
        using var workspace = new TestDirectory();
        var source = new SqliteAppRepository(workspace.CreateSubdirectory("missing-state-source"));
        source.Save(TestDocumentFactory.CreatePopulated(), "source");
        var sourceRoot = JsonNode.Parse(ReadRawState(source.DatabasePath))!.AsObject();
        Assert.True(ResolveObject(sourceRoot, "plan").Remove("createdAt"));
        WriteRawState(
            source.DatabasePath,
            sourceRoot.ToJsonString(JsonDefaults.CompactOptions));
        var backupPath = System.IO.Path.Combine(workspace.Path, "missing-state.zip");
        CreateUncheckedArchive(source.DatabasePath, backupPath);

        var target = new SqliteAppRepository(workspace.CreateSubdirectory("missing-state-target"));
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Missing-state original";
        target.Save(original, "target");
        SqliteConnection.ClearAllPools();
        var originalBytes = File.ReadAllBytes(target.DatabasePath);
        var automaticDirectory = System.IO.Path.Combine(workspace.Path, "missing-state-automatic");

        Assert.Throws<InvalidDataException>(() =>
            BackupService.BeginRestoreWithPreBackup(
                target.DatabasePath,
                backupPath,
                automaticDirectory));

        Assert.Equal(originalBytes, File.ReadAllBytes(target.DatabasePath));
        Assert.False(Directory.Exists(automaticDirectory));
    }

    [Fact]
    public void UnknownIncrementalManifestPropertiesRemainTolerated()
    {
        using var workspace = new TestDirectory();
        var source = new SqliteAppRepository(workspace.CreateSubdirectory("future-manifest-source"));
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Future manifest candidate";
        source.Save(candidate, "candidate");
        var backupPath = System.IO.Path.Combine(workspace.Path, "future-manifest.zip");
        BackupService.CreateBackup(source.DatabasePath, backupPath);
        RewriteManifest(
            backupPath,
            manifest => manifest["futureManifestField"] = new JsonObject
            {
                ["revision"] = 3
            });

        var target = new SqliteAppRepository(workspace.CreateSubdirectory("future-manifest-target"));
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Future manifest original";
        target.Save(original, "original");
        var transaction = BackupService.BeginRestoreWithPreBackup(
            target.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("future-manifest-automatic"));
        try
        {
            Assert.Equal(
                "Future manifest candidate",
                new SqliteAppRepository(target.DataDirectory).LoadOrCreate().Plans[0].PlanName);
        }
        finally
        {
            transaction.Rollback();
        }

        Assert.Equal("Future manifest original", target.LoadOrCreate().Plans[0].PlanName);
    }

    private static JsonObject ResolveObject(JsonObject root, string objectPath) => objectPath switch
    {
        "document" => root,
        "semester" => root["semesters"]![0]!.AsObject(),
        "period" => root["semesters"]![0]!["periodSchedule"]![0]!.AsObject(),
        "label" => root["labels"]![0]!.AsObject(),
        "course" => root["courseLibrary"]![0]!.AsObject(),
        "meeting" => root["courseLibrary"]![0]!["meetingTimes"]![0]!.AsObject(),
        "plan" => root["plans"]![0]!.AsObject(),
        "snapshot" => root["plans"]![0]!["snapshots"]![0]!.AsObject(),
        "settings" => root["settings"]!.AsObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(objectPath), objectPath, null)
    };

    private static void RemoveManifestProperty(string backupPath, string propertyName)
    {
        RewriteManifest(backupPath, manifest => Assert.True(manifest.Remove(propertyName)));
    }

    private static void RewriteManifest(string backupPath, Action<JsonObject> rewrite)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        var entry = Assert.Single(archive.Entries, item => item.FullName == "manifest.json");
        JsonObject manifest;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        rewrite(manifest);
        entry.Delete();
        var replacement = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(manifest.ToJsonString(JsonDefaults.CompactOptions));
    }

    private static void CreateUncheckedArchive(string databasePath, string backupPath)
    {
        var snapshotPath = backupPath + ".sqlite";
        try
        {
            using (var source = Open(databasePath))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);
            var databaseEntry = archive.CreateEntry("course-planner.sqlite", CompressionLevel.Optimal);
            using (var source = File.OpenRead(snapshotPath))
            using (var destination = databaseEntry.Open())
                source.CopyTo(destination);

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(new BackupManifest(), JsonDefaults.CompactOptions));
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    private static void AssertCurrentStateRecovery(
        string dataDirectory,
        string databasePath,
        string malformedJson,
        string expectedExceptionName)
    {
        WriteRawState(databasePath, malformedJson);
        var seedCalls = 0;
        var reader = new SqliteAppRepository(dataDirectory, () =>
        {
            seedCalls++;
            return SeedData.Create("Required-property recovery", "Recovered plan");
        });

        var recovered = reader.LoadOrCreate();

        Assert.Equal(1, seedCalls);
        Assert.Equal("Required-property recovery", recovered.Semesters[0].SemesterName);
        var artifactPath = Assert.IsType<string>(reader.LastRecoveryArtifactPath);
        Assert.Equal(malformedJson, File.ReadAllText(artifactPath));
        Assert.Contains(
            reader.ReadEventSummaries(),
            line => line.Contains("state-recovery", StringComparison.Ordinal) &&
                    line.Contains(expectedExceptionName, StringComparison.Ordinal));
    }

    private static string ReadRawState(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_state WHERE id = 'default'";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void WriteRawState(string databasePath, string json)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_state SET json = $json WHERE id = 'default'";
        command.Parameters.AddWithValue("$json", json);
        Assert.Equal(1, command.ExecuteNonQuery());
        SqliteConnection.ClearPool(connection);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"course-planner-required-json-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateSubdirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

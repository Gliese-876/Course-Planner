using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

[Collection(SqliteGlobalPoolTestCollection.Name)]
public sealed class BackupRestoreWorkflowTests
{
    [Fact]
    public void BackupCreatedFromHighlyCompressibleDatabaseCanBeRestored()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var repository = new SqliteAppRepository(sourceDirectory);
        var document = TestDocumentFactory.CreatePopulated();
        document.Plans[0].PlanName = "Highly compressible backup";
        repository.Save(document, "source");
        // Simulate a legacy database created before event payloads were bounded.
        // The backup reader must accept a high compression ratio without treating it as a zip bomb.
        using (var connection = Open(repository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO app_events (occurred_at, level, event_name, message)
                VALUES ('2026-07-13T00:00:00Z', 'Info', 'compressible', $message)
                """;
            command.Parameters.AddWithValue("$message", new string('A', 2 * 1024 * 1024));
            command.ExecuteNonQuery();
        }

        var backupPath = Path.Combine(workspace.Path, "compressible.zip");
        BackupService.CreateBackup(repository.DatabasePath, backupPath);
        using (var archive = ZipFile.OpenRead(backupPath))
        {
            var databaseEntry = Assert.Single(archive.Entries, entry => entry.FullName == "course-planner.sqlite");
            Assert.True(databaseEntry.Length / (double)databaseEntry.CompressedLength > 100);
        }

        BackupService.RestoreWithPreBackup(
            Path.Combine(targetDirectory, "course-planner.sqlite"),
            backupPath,
            workspace.CreateSubdirectory("automatic"));

        var restored = LoadExisting(targetDirectory);
        Assert.Equal("Highly compressible backup", restored.Plans[0].PlanName);
    }

    [Fact]
    public void MaximumLengthBackupNameDoesNotMakeTheTemporaryNameInvalid()
    {
        using var workspace = new TemporaryDirectory();
        var repository = new SqliteAppRepository(workspace.CreateSubdirectory("maximum-backup-name-source"));
        repository.Save(TestDocumentFactory.CreatePopulated(), "source");
        var fileName = new string('a', WindowsFileNameRules.MaxComponentLength - ".zip".Length) + ".zip";
        var backupPath = Path.Combine(workspace.Path, fileName);

        BackupService.CreateBackup(repository.DatabasePath, backupPath);

        Assert.True(File.Exists(backupPath));
        Assert.Equal([backupPath], Directory.EnumerateFiles(workspace.Path));
    }

    [Fact]
    public void ValidBackupReplacesCorruptCurrentDatabaseAndPreservesItsOriginalBytes()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var automaticDirectory = workspace.CreateSubdirectory("automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var sourceDocument = TestDocumentFactory.CreatePopulated();
        sourceDocument.Plans[0].PlanName = "Recovered from valid backup";
        sourceRepository.Save(sourceDocument, "source");
        var backupPath = Path.Combine(workspace.Path, "valid.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        var corruptOriginal = Encoding.UTF8.GetBytes("not a sqlite database; preserve these exact bytes");
        File.WriteAllBytes(targetDatabasePath, corruptOriginal);
        File.WriteAllText(targetDatabasePath + "-wal", "stale corrupt wal");
        File.WriteAllText(targetDatabasePath + "-shm", "stale corrupt shm");
        File.WriteAllText(targetDatabasePath + "-journal", "stale corrupt journal");

        var preservedPath = BackupService.RestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            automaticDirectory);

        Assert.Equal(".sqlite", Path.GetExtension(preservedPath));
        Assert.Equal(corruptOriginal, File.ReadAllBytes(preservedPath));
        Assert.Equal("stale corrupt wal", File.ReadAllText(preservedPath + "-wal"));
        Assert.Equal("stale corrupt shm", File.ReadAllText(preservedPath + "-shm"));
        Assert.Equal("stale corrupt journal", File.ReadAllText(preservedPath + "-journal"));
        Assert.Equal(preservedPath, Assert.Single(Directory.EnumerateFiles(automaticDirectory, "*.sqlite")));
        Assert.Equal(4, Directory.EnumerateFiles(automaticDirectory).Count());
        Assert.False(File.Exists(targetDatabasePath + "-wal"));
        Assert.False(File.Exists(targetDatabasePath + "-shm"));
        Assert.False(File.Exists(targetDatabasePath + "-journal"));
        Assert.Equal("Recovered from valid backup", LoadExisting(targetDirectory).Plans[0].PlanName);
    }

    [Fact]
    public void RestoringIntoAMissingDatabaseRemovesOrphanedSqliteSidecars()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var sourceDocument = TestDocumentFactory.CreatePopulated();
        sourceDocument.Plans[0].PlanName = "Restored without stale sidecars";
        sourceRepository.Save(sourceDocument, "source");
        var backupPath = Path.Combine(workspace.Path, "valid.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        File.WriteAllBytes(targetDatabasePath + "-wal", "orphaned wal"u8.ToArray());
        File.WriteAllBytes(targetDatabasePath + "-shm", "orphaned shm"u8.ToArray());
        File.WriteAllBytes(targetDatabasePath + "-journal", "orphaned journal"u8.ToArray());

        BackupService.RestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic"));

        Assert.False(File.Exists(targetDatabasePath + "-wal"));
        Assert.False(File.Exists(targetDatabasePath + "-shm"));
        Assert.False(File.Exists(targetDatabasePath + "-journal"));
        Assert.Equal("Restored without stale sidecars", LoadExisting(targetDirectory).Plans[0].PlanName);
    }

    [Fact]
    public void FailedRestoreIntoAMissingDatabaseRestoresOrphanedSidecars()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        var backupPath = Path.Combine(workspace.Path, "valid.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        Directory.CreateDirectory(targetDatabasePath);
        var walBytes = "orphaned wal survives failure"u8.ToArray();
        var shmBytes = "orphaned shm survives failure"u8.ToArray();
        var journalBytes = "orphaned journal survives failure"u8.ToArray();
        File.WriteAllBytes(targetDatabasePath + "-wal", walBytes);
        File.WriteAllBytes(targetDatabasePath + "-shm", shmBytes);
        File.WriteAllBytes(targetDatabasePath + "-journal", journalBytes);

        Assert.ThrowsAny<IOException>(() => BackupService.RestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic")));

        Assert.Equal(walBytes, File.ReadAllBytes(targetDatabasePath + "-wal"));
        Assert.Equal(shmBytes, File.ReadAllBytes(targetDatabasePath + "-shm"));
        Assert.Equal(journalBytes, File.ReadAllBytes(targetDatabasePath + "-journal"));
    }

    [Fact]
    public void FailedReplacementRestoresCorruptCurrentDatabaseAndItsSidecarsWithoutMutation()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var automaticDirectory = workspace.CreateSubdirectory("automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        var backupPath = Path.Combine(workspace.Path, "valid.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        var databaseBytes = "corrupt current database"u8.ToArray();
        var walBytes = "unrecoverable wal bytes"u8.ToArray();
        var shmBytes = "unrecoverable shm bytes"u8.ToArray();
        var journalBytes = "unrecoverable journal bytes"u8.ToArray();
        File.WriteAllBytes(targetDatabasePath, databaseBytes);
        File.WriteAllBytes(targetDatabasePath + "-wal", walBytes);
        File.WriteAllBytes(targetDatabasePath + "-shm", shmBytes);
        File.WriteAllBytes(targetDatabasePath + "-journal", journalBytes);

        using (var replacementBlocker = new FileStream(
                   targetDatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() =>
                BackupService.RestoreWithPreBackup(
                    targetDatabasePath,
                    backupPath,
                    automaticDirectory));
        }

        Assert.Equal(databaseBytes, File.ReadAllBytes(targetDatabasePath));
        Assert.Equal(walBytes, File.ReadAllBytes(targetDatabasePath + "-wal"));
        Assert.Equal(shmBytes, File.ReadAllBytes(targetDatabasePath + "-shm"));
        Assert.Equal(journalBytes, File.ReadAllBytes(targetDatabasePath + "-journal"));
        var preservedPath = Assert.Single(Directory.EnumerateFiles(automaticDirectory, "*.sqlite"));
        Assert.Equal(databaseBytes, File.ReadAllBytes(preservedPath));
    }

    [Fact]
    public void BackupCapturesLatestCommittedStateWhileWalHasUncheckpointedChanges()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var restoredDirectory = workspace.CreateSubdirectory("restored");
        var repository = new SqliteAppRepository(sourceDirectory);
        var document = TestDocumentFactory.CreatePopulated();
        repository.Save(document, "initial");

        using var readerConnection = Open(repository.DatabasePath);
        using (var journalMode = readerConnection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL";
            Assert.Equal("wal", Convert.ToString(journalMode.ExecuteScalar())?.ToLowerInvariant());
        }

        using var readerTransaction = readerConnection.BeginTransaction(deferred: true);
        using (var establishSnapshot = readerConnection.CreateCommand())
        {
            establishSnapshot.Transaction = readerTransaction;
            establishSnapshot.CommandText = "SELECT COUNT(*) FROM app_state";
            Assert.Equal(1L, Convert.ToInt64(establishSnapshot.ExecuteScalar()));
        }

        document.Plans[0].PlanName = "Committed after WAL snapshot";
        repository.Save(document, "wal.commit");
        Assert.True(new FileInfo(repository.DatabasePath + "-wal").Length > 0);

        var backupPath = Path.Combine(workspace.Path, "wal-backup.zip");
        BackupService.CreateBackup(repository.DatabasePath, backupPath);
        ZipFile.ExtractToDirectory(backupPath, restoredDirectory);

        var restored = LoadExisting(restoredDirectory);
        Assert.Equal("Committed after WAL snapshot", restored.Plans[0].PlanName);
    }

    [Fact]
    public void RestoreRejectsInvalidApplicationStateWithoutReplacingCurrentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var targetDirectory = workspace.CreateSubdirectory("target");
        var invalidDirectory = workspace.CreateSubdirectory("invalid");
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var currentDocument = TestDocumentFactory.CreatePopulated();
        currentDocument.Plans[0].PlanName = "Keep current document";
        targetRepository.Save(currentDocument, "current");

        var invalidRepository = new SqliteAppRepository(invalidDirectory);
        invalidRepository.Initialize();
        using (var connection = Open(invalidRepository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO app_state (id, json, updated_at)
                VALUES ('default', 'not valid json', '2026-07-13T00:00:00Z')
                """;
            command.ExecuteNonQuery();
        }

        var invalidBackup = Path.Combine(workspace.Path, "invalid-state.zip");
        CreateArchiveFromDatabase(invalidRepository.DatabasePath, invalidBackup);
        var automaticDirectory = workspace.CreateSubdirectory("automatic");

        Assert.Throws<InvalidDataException>(() =>
            BackupService.RestoreWithPreBackup(
                targetRepository.DatabasePath,
                invalidBackup,
                automaticDirectory));

        var reloaded = LoadExisting(targetDirectory);
        Assert.Equal("Keep current document", reloaded.Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Fact]
    public void RestoreRejectsSemanticallyInvalidApplicationStateWithoutReplacingCurrentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var targetDirectory = workspace.CreateSubdirectory("target-semantic");
        var invalidDirectory = workspace.CreateSubdirectory("invalid-semantic");
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var currentDocument = TestDocumentFactory.CreatePopulated();
        currentDocument.Plans[0].PlanName = "Keep current after semantic rejection";
        targetRepository.Save(currentDocument, "current");

        var invalidRepository = new SqliteAppRepository(invalidDirectory);
        var invalidDocument = TestDocumentFactory.CreatePopulated();
        invalidDocument.Settings.Language = (LanguageMode)int.MaxValue;
        invalidRepository.Initialize();
        using (var connection = Open(invalidRepository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO app_state (id, json, updated_at)
                VALUES ('default', $json, '2026-07-13T00:00:00Z')
                """;
            command.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(invalidDocument, JsonDefaults.Options));
            command.ExecuteNonQuery();
        }

        var invalidBackup = Path.Combine(workspace.Path, "invalid-semantic-state.zip");
        CreateArchiveFromDatabase(invalidRepository.DatabasePath, invalidBackup);
        var automaticDirectory = workspace.CreateSubdirectory("automatic-semantic");

        Assert.Throws<InvalidDataException>(() =>
            BackupService.RestoreWithPreBackup(
                targetRepository.DatabasePath,
                invalidBackup,
                automaticDirectory));

        var reloaded = LoadExisting(targetDirectory);
        Assert.Equal("Keep current after semantic rejection", reloaded.Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Fact]
    public void RestoreRejectsCorruptSqliteContentWithoutReplacingCurrentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var targetDirectory = workspace.CreateSubdirectory("target");
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var currentDocument = TestDocumentFactory.CreatePopulated();
        currentDocument.Plans[0].PlanName = "Keep current after corrupt restore";
        targetRepository.Save(currentDocument, "current");

        var corruptDatabase = Path.Combine(workspace.Path, "corrupt.sqlite");
        var content = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(content);
        File.WriteAllBytes(corruptDatabase, content);
        var corruptBackup = Path.Combine(workspace.Path, "corrupt.zip");
        CreateArchiveFromDatabase(corruptDatabase, corruptBackup);
        var automaticDirectory = workspace.CreateSubdirectory("automatic");

        Assert.Throws<InvalidDataException>(() =>
            BackupService.RestoreWithPreBackup(
                targetRepository.DatabasePath,
                corruptBackup,
                automaticDirectory));

        var reloaded = LoadExisting(targetDirectory);
        Assert.Equal("Keep current after corrupt restore", reloaded.Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Theory]
    [InlineData("CREATE TABLE unexpected_data (value TEXT)")]
    [InlineData("CREATE VIEW unexpected_view AS SELECT id FROM app_state")]
    [InlineData("CREATE INDEX unexpected_index ON app_events(event_name)")]
    [InlineData("CREATE TRIGGER sabotage AFTER UPDATE ON app_state BEGIN DELETE FROM app_state WHERE id = 'default'; END")]
    [InlineData("INSERT INTO app_state (id, json, updated_at) SELECT 'unexpected', json, updated_at FROM app_state WHERE id = 'default'")]
    [InlineData("ALTER TABLE app_state RENAME TO old_state; CREATE TABLE app_state (id TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL, CHECK (id = 'default')); INSERT INTO app_state SELECT * FROM old_state; DROP TABLE old_state;")]
    [InlineData("ALTER TABLE app_state RENAME TO old_state; CREATE TABLE app_state (id TEXT PRIMARY KEY NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL) WITHOUT ROWID; INSERT INTO app_state SELECT * FROM old_state; DROP TABLE old_state;")]
    [InlineData("ALTER TABLE app_state RENAME TO old_state; CREATE TABLE app_state (id TEXT COLLATE NOCASE PRIMARY KEY NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL); INSERT INTO app_state SELECT * FROM old_state; DROP TABLE old_state;")]
    [InlineData("ALTER TABLE app_events RENAME TO old_events; CREATE TABLE app_events (id INTEGER PRIMARY KEY, occurred_at TEXT NOT NULL, level TEXT NOT NULL, event_name TEXT NOT NULL, message TEXT NOT NULL); INSERT INTO app_events SELECT * FROM old_events; DROP TABLE old_events;")]
    public void RestoreRejectsCurrentSchemaDatabaseExtensionsBeforeTheyCanAffectLaterSaves(string schemaMutation)
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("extended-schema-source");
        var targetDirectory = workspace.CreateSubdirectory("extended-schema-target");
        var automaticDirectory = workspace.CreateSubdirectory("extended-schema-automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        using (var connection = Open(sourceRepository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = schemaMutation;
            command.ExecuteNonQuery();
        }

        var archivePath = Path.Combine(workspace.Path, "extended-schema.zip");
        CreateArchiveFromDatabase(sourceRepository.DatabasePath, archivePath);

        var targetRepository = new SqliteAppRepository(targetDirectory);
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original exact schema remains";
        targetRepository.Save(original, "target");
        SqliteConnection.ClearAllPools();
        var originalBytes = ReadAllBytesShared(targetRepository.DatabasePath);

        Assert.Throws<InvalidDataException>(() => BackupService.RestoreWithPreBackup(
            targetRepository.DatabasePath,
            archivePath,
            automaticDirectory));

        Assert.Equal(originalBytes, ReadAllBytesShared(targetRepository.DatabasePath));
        Assert.Equal("Original exact schema remains", LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Fact]
    public void RestoreRejectsOversizedDuplicateAndMalformedManifestWithoutTouchingCurrentDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("manifest-source");
        var targetDirectory = workspace.CreateSubdirectory("manifest-target");
        var automaticDirectory = workspace.CreateSubdirectory("manifest-automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var current = TestDocumentFactory.CreatePopulated();
        current.Plans[0].PlanName = "Keep current after manifest rejection";
        targetRepository.Save(current, "current");

        var duplicateManifest = Encoding.UTF8.GetBytes($$"""
            {"kind":"{{PlannerSchemas.BackupKind}}","kind":"{{PlannerSchemas.BackupKind}}","schemaVersion":"{{PlannerSchemas.Current}}","databaseFile":"course-planner.sqlite"}
            """);
        var malformedPrefix = Encoding.UTF8.GetBytes($$"""
            {"kind":"{{PlannerSchemas.BackupKind}}","schemaVersion":"{{PlannerSchemas.Current}}","databaseFile":"course-planner.sqlite","future":"
            """);
        var malformedManifest = malformedPrefix
            .Concat(new byte[] { 0xC3, 0x28 })
            .Concat("\"}"u8.ToArray())
            .ToArray();
        var oversizedManifest = new byte[(64 * 1024) + 1];

        foreach (var (name, manifest) in new[]
                 {
                     ("duplicate", duplicateManifest),
                     ("malformed", malformedManifest),
                     ("oversized", oversizedManifest)
                 })
        {
            var archivePath = Path.Combine(workspace.Path, $"{name}-manifest.zip");
            CreateArchiveFromDatabaseWithManifest(
                sourceRepository.DatabasePath,
                archivePath,
                manifest);

            Assert.Throws<InvalidDataException>(() => BackupService.RestoreWithPreBackup(
                targetRepository.DatabasePath,
                archivePath,
                automaticDirectory));
            Assert.Equal(
                "Keep current after manifest rejection",
                LoadExisting(targetDirectory).Plans[0].PlanName);
            Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
        }
    }

    [Fact]
    public void RestoreRejectsDuplicatePropertiesInsideBackedUpApplicationState()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("duplicate-state-source");
        var targetDirectory = workspace.CreateSubdirectory("duplicate-state-target");
        var automaticDirectory = workspace.CreateSubdirectory("duplicate-state-automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        using (var connection = Open(sourceRepository.DatabasePath))
        {
            string json;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT json FROM app_state WHERE id = 'default'";
                json = Assert.IsType<string>(read.ExecuteScalar());
            }

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE app_state SET json = $json WHERE id = 'default'";
            update.Parameters.AddWithValue(
                "$json",
                $"{{\"schemaVersion\":\"{PlannerSchemas.Current}\"," + json[1..]);
            update.ExecuteNonQuery();
        }

        var invalidBackup = Path.Combine(workspace.Path, "duplicate-state.zip");
        CreateArchiveFromDatabase(sourceRepository.DatabasePath, invalidBackup);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var current = TestDocumentFactory.CreatePopulated();
        current.Plans[0].PlanName = "Keep current after duplicate state rejection";
        targetRepository.Save(current, "current");

        Assert.Throws<InvalidDataException>(() => BackupService.RestoreWithPreBackup(
            targetRepository.DatabasePath,
            invalidBackup,
            automaticDirectory));

        Assert.Equal(
            "Keep current after duplicate state rejection",
            LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Fact]
    public void RestoreRejectsAnOversizedArchiveBeforeZipParsingAndLeavesNoRestoreArtifacts()
    {
        using var workspace = new TemporaryDirectory();
        var targetDirectory = workspace.CreateSubdirectory("oversized-archive-target");
        var automaticDirectory = workspace.CreateSubdirectory("oversized-archive-automatic");
        var archivePath = Path.Combine(workspace.Path, "oversized.zip");
        using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength((112L * 1024 * 1024) + 1);

        Assert.Throws<InvalidDataException>(() => BackupService.RestoreWithPreBackup(
            Path.Combine(targetDirectory, "course-planner.sqlite"),
            archivePath,
            automaticDirectory));

        Assert.False(File.Exists(Path.Combine(targetDirectory, "course-planner.sqlite")));
        Assert.Empty(Directory.EnumerateDirectories(targetDirectory, ".restore-*"));
        Assert.Empty(Directory.EnumerateFiles(automaticDirectory));
    }

    [Fact]
    public void FailedBackupDoesNotOverwriteAnExistingBackupFile()
    {
        using var workspace = new TemporaryDirectory();
        var invalidDatabase = Path.Combine(workspace.Path, "invalid.sqlite");
        File.WriteAllText(invalidDatabase, "not a database");
        var backupPath = Path.Combine(workspace.Path, "existing.zip");
        var existingContent = "existing backup content"u8.ToArray();
        File.WriteAllBytes(backupPath, existingContent);

        Assert.Throws<InvalidDataException>(() =>
            BackupService.CreateBackup(invalidDatabase, backupPath));

        Assert.Equal(existingContent, File.ReadAllBytes(backupPath));
        Assert.Empty(Directory.EnumerateDirectories(workspace.Path, ".backup-*"));
        Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".existing.zip.*.tmp"));
    }

    [Fact]
    public void BackupDestinationCannotOverwriteTheSourceDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var repository = new SqliteAppRepository(workspace.Path);
        repository.Save(TestDocumentFactory.CreatePopulated(), "source");
        SqliteConnection.ClearAllPools();
        var originalBytes = File.ReadAllBytes(repository.DatabasePath);

        Assert.Throws<ArgumentException>(() =>
            BackupService.CreateBackup(repository.DatabasePath, repository.DatabasePath));

        Assert.Equal(originalBytes, File.ReadAllBytes(repository.DatabasePath));
        Assert.Equal("SQLite format 3\0"u8.ToArray(), File.ReadAllBytes(repository.DatabasePath)[..16]);
    }

    [Fact]
    public void BackupDestinationCannotUseAReservedSqliteSidecarPath()
    {
        using var workspace = new TemporaryDirectory();
        var repository = new SqliteAppRepository(workspace.Path);
        var document = TestDocumentFactory.CreatePopulated();
        document.Plans[0].PlanName = "Source remains usable";
        repository.Save(document, "source");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = repository.DatabasePath + suffix;
            Assert.Throws<ArgumentException>(() =>
                BackupService.CreateBackup(repository.DatabasePath, sidecarPath));
            Assert.False(File.Exists(sidecarPath));
        }
        Assert.Equal("Source remains usable", LoadExisting(workspace.Path).Plans[0].PlanName);
    }

    [Fact]
    public void RestoreRoundTripIncludesBusinessDataAndCreatesRestorablePreBackup()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var automaticDirectory = workspace.CreateSubdirectory("automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var backupDocument = TestDocumentFactory.CreatePopulated();
        backupDocument.Plans[0].PlanName = "Document from backup";
        sourceRepository.Save(backupDocument, "backup.source");
        var currentDocument = TestDocumentFactory.CreatePopulated();
        currentDocument.Plans[0].PlanName = "Document before restore";
        targetRepository.Save(currentDocument, "restore.target");

        var backupPath = Path.Combine(workspace.Path, "complete.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var preRestoreBackup = BackupService.RestoreWithPreBackup(
            targetRepository.DatabasePath,
            backupPath,
            automaticDirectory);

        Assert.True(File.Exists(preRestoreBackup));
        using (var archive = ZipFile.OpenRead(preRestoreBackup))
        {
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("course-planner.sqlite"));
        }

        var restored = LoadExisting(targetDirectory);
        Assert.Equal("Document from backup", restored.Plans[0].PlanName);
        Assert.Equal(backupDocument.CourseLibrary.Count, restored.CourseLibrary.Count);
        Assert.Equal(backupDocument.Semesters.Count, restored.Semesters.Count);
        Assert.Equal(backupDocument.Settings.OpenPlanIds, restored.Settings.OpenPlanIds);

        BackupService.RestoreWithPreBackup(
            targetRepository.DatabasePath,
            preRestoreBackup,
            automaticDirectory);
        var rolledBack = LoadExisting(targetDirectory);
        Assert.Equal("Document before restore", rolledBack.Plans[0].PlanName);
    }

    [Fact]
    public void ReplacementFailureLeavesCurrentDatabaseIntactAndKeepsPreRestoreBackup()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var automaticDirectory = workspace.CreateSubdirectory("automatic");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var sourceDocument = TestDocumentFactory.CreatePopulated();
        sourceDocument.Plans[0].PlanName = "Replacement candidate";
        sourceRepository.Save(sourceDocument, "source");
        var currentDocument = TestDocumentFactory.CreatePopulated();
        currentDocument.Plans[0].PlanName = "Current document survives";
        targetRepository.Save(currentDocument, "current");
        var backupPath = Path.Combine(workspace.Path, "candidate.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        SqliteConnection.ClearAllPools();
        using (var replacementBlocker = new FileStream(
                   targetRepository.DatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() =>
                BackupService.RestoreWithPreBackup(
                    targetRepository.DatabasePath,
                    backupPath,
                    automaticDirectory));
        }

        var reloaded = LoadExisting(targetDirectory);
        Assert.Equal("Current document survives", reloaded.Plans[0].PlanName);
        Assert.Single(Directory.EnumerateFiles(automaticDirectory, "before-restore-*.zip"));
    }

    [Fact]
    public void ReplacingDocumentAfterRestoreClearsUndoAndRedoHistory()
    {
        using var workspace = new TemporaryDirectory();
        var session = new DocumentSession(new SqliteAppRepository(workspace.Path));
        session.CaptureUndo();
        Assert.True(session.UndoRedo.CanUndo);

        session.ReplaceDocument(TestDocumentFactory.CreatePopulated(), "backup.restore");

        Assert.False(session.UndoRedo.CanUndo);
        Assert.False(session.UndoRedo.CanRedo);
    }

    [Fact]
    public void ReloadingAfterRestoreRefreshesTheSessionWithoutRewritingTheBackup()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source");
        var targetDirectory = workspace.CreateSubdirectory("target");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var sourceDocument = TestDocumentFactory.CreatePopulated();
        sourceDocument.Plans[0].PlanName = "Reloaded document";
        sourceRepository.Save(sourceDocument, "source.saved");
        var backupPath = Path.Combine(workspace.Path, "reload.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var session = new DocumentSession(new SqliteAppRepository(targetDirectory));
        session.CaptureUndo();
        BackupService.RestoreWithPreBackup(
            session.Repository.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic"));
        var eventCountBeforeReload = session.Repository.ReadEventSummaries().Count;

        session.ReloadFromRepository();

        Assert.Equal("Reloaded document", session.Document.Plans[0].PlanName);
        Assert.False(session.UndoRedo.CanUndo);
        Assert.False(session.UndoRedo.CanRedo);
        Assert.Equal(eventCountBeforeReload, session.Repository.ReadEventSummaries().Count);
    }

    [Fact]
    public void RestoreRollsBackDatabaseAndSessionWhenReloadFails()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-reload-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-reload-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var restoredDocument = TestDocumentFactory.CreatePopulated();
        restoredDocument.Plans[0].PlanName = "Candidate that must be rolled back";
        sourceRepository.Save(restoredDocument, "source");
        var originalDocument = TestDocumentFactory.CreatePopulated();
        originalDocument.Plans[0].PlanName = "Original remains authoritative";
        targetRepository.Save(originalDocument, "target");
        var backupPath = Path.Combine(workspace.Path, "reload-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        var loadCount = 0;
        var session = new DocumentSession(
            targetRepository,
            loadDocument: () =>
            {
                if (Interlocked.Increment(ref loadCount) == 2)
                    throw new IOException("Injected reload failure.");
                return targetRepository.LoadOrCreate();
            });
        var expectedRollbackToken = session.AcceptedStateToken;
        var acceptedCount = 0;
        var changedCount = 0;
        var refreshCount = 0;
        var rollbacks = new List<DocumentRolledBackEventArgs>();
        session.StateAccepted += (_, _) => acceptedCount++;
        session.Changed += (_, _) => changedCount++;
        session.RolledBack += (_, args) => rollbacks.Add(args);

        var exception = Assert.Throws<IOException>(() =>
            session.RestoreFromBackup(
                backupPath,
                workspace.CreateSubdirectory("automatic-reload-failure"),
                refreshRestoredState: () => refreshCount++));

        Assert.Equal("Injected reload failure.", exception.Message);
        var rollback = Assert.Single(rollbacks);
        Assert.Equal(DocumentRollbackTargetKind.PersistedBaseline, rollback.TargetKind);
        Assert.Equal(expectedRollbackToken, rollback.TargetStateToken);
        Assert.True(rollback.DurableOutcomeKnown);
        Assert.Equal(0, acceptedCount);
        Assert.Equal(0, changedCount);
        Assert.Equal(0, refreshCount);
        Assert.Equal("Original remains authoritative", session.Document.Plans[0].PlanName);
        Assert.Equal("Original remains authoritative", LoadExisting(targetDirectory).Plans[0].PlanName);
    }

    [Fact]
    public void PendingRestoreCanRollBackToAMissingDatabaseAndExactOrphanedSidecars()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-missing-rollback");
        var targetDirectory = workspace.CreateSubdirectory("target-missing-rollback");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var restoredDocument = TestDocumentFactory.CreatePopulated();
        restoredDocument.Plans[0].PlanName = "Temporary restored state";
        sourceRepository.Save(restoredDocument, "source");
        var backupPath = Path.Combine(workspace.Path, "missing-rollback.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        var walBytes = "orphaned wal before restore"u8.ToArray();
        var shmBytes = "orphaned shm before restore"u8.ToArray();
        var journalBytes = "orphaned journal before restore"u8.ToArray();
        File.WriteAllBytes(targetDatabasePath + "-wal", walBytes);
        File.WriteAllBytes(targetDatabasePath + "-shm", shmBytes);
        File.WriteAllBytes(targetDatabasePath + "-journal", journalBytes);

        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-missing-rollback"));

        Assert.Null(transaction.PreRestoreBackupPath);
        Assert.Equal("Temporary restored state", LoadExisting(targetDirectory).Plans[0].PlanName);
        transaction.Rollback();

        Assert.False(File.Exists(targetDatabasePath));
        Assert.Equal(walBytes, File.ReadAllBytes(targetDatabasePath + "-wal"));
        Assert.Equal(shmBytes, File.ReadAllBytes(targetDatabasePath + "-shm"));
        Assert.Equal(journalBytes, File.ReadAllBytes(targetDatabasePath + "-journal"));
        Assert.False(Directory.Exists(transaction.RecoveryDirectory));
    }

    [Fact]
    public void PendingRestoreCanRollBackToExactCorruptDatabaseAndRawSidecars()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-corrupt-rollback");
        var targetDirectory = workspace.CreateSubdirectory("target-corrupt-rollback");
        var automaticDirectory = workspace.CreateSubdirectory("automatic-corrupt-rollback");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(TestDocumentFactory.CreatePopulated(), "source");
        var backupPath = Path.Combine(workspace.Path, "corrupt-rollback.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        var databaseBytes = "corrupt database bytes before pending restore"u8.ToArray();
        var walBytes = "corrupt wal bytes before pending restore"u8.ToArray();
        var shmBytes = "corrupt shm bytes before pending restore"u8.ToArray();
        var journalBytes = "corrupt journal bytes before pending restore"u8.ToArray();
        File.WriteAllBytes(targetDatabasePath, databaseBytes);
        File.WriteAllBytes(targetDatabasePath + "-wal", walBytes);
        File.WriteAllBytes(targetDatabasePath + "-shm", shmBytes);
        File.WriteAllBytes(targetDatabasePath + "-journal", journalBytes);

        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            automaticDirectory);
        var rawArtifact = Assert.IsType<string>(transaction.PreRestoreBackupPath);
        Assert.Equal(".sqlite", Path.GetExtension(rawArtifact));

        transaction.Rollback();

        Assert.Equal(databaseBytes, File.ReadAllBytes(targetDatabasePath));
        Assert.Equal(walBytes, File.ReadAllBytes(targetDatabasePath + "-wal"));
        Assert.Equal(shmBytes, File.ReadAllBytes(targetDatabasePath + "-shm"));
        Assert.Equal(journalBytes, File.ReadAllBytes(targetDatabasePath + "-journal"));
        Assert.Equal(databaseBytes, File.ReadAllBytes(rawArtifact));
        Assert.Equal(walBytes, File.ReadAllBytes(rawArtifact + "-wal"));
        Assert.Equal(shmBytes, File.ReadAllBytes(rawArtifact + "-shm"));
        Assert.Equal(journalBytes, File.ReadAllBytes(rawArtifact + "-journal"));
        Assert.False(Directory.Exists(transaction.RecoveryDirectory));
    }

    [Fact]
    public void CommitCleanupFailureReportsCompletedRestoreWithoutMakingRollbackReusable()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-commit-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-commit-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Committed despite cleanup failure";
        sourceRepository.Save(candidate, "source");
        var backupPath = Path.Combine(workspace.Path, "commit-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var targetDatabasePath = Path.Combine(targetDirectory, "course-planner.sqlite");
        File.WriteAllText(targetDatabasePath + "-wal", "orphaned wal held during cleanup");
        File.WriteAllText(targetDatabasePath + "-shm", "orphaned shm held during cleanup");
        File.WriteAllText(targetDatabasePath + "-journal", "orphaned journal held during cleanup");
        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetDatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-commit-cleanup-failure"));
        var lockedRecoveryFile = Path.Combine(
            transaction.RecoveryDirectory,
            "database-sidecar-wal");
        Assert.True(File.Exists(lockedRecoveryFile));

        BackupRestoreCleanupException exception;
        using (new FileStream(
                   lockedRecoveryFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            exception = Assert.Throws<BackupRestoreCleanupException>(transaction.Commit);

            Assert.True(transaction.IsCompleted);
            Assert.Equal(Path.GetFullPath(transaction.RecoveryDirectory), exception.RecoveryDirectory);
            Assert.IsAssignableFrom<IOException>(exception.InnerException);
            Assert.True(Directory.Exists(transaction.RecoveryDirectory));
            Assert.Throws<ObjectDisposedException>(transaction.Rollback);
            transaction.Dispose();
        }

        Assert.Equal("Committed despite cleanup failure", LoadExisting(targetDirectory).Plans[0].PlanName);
        Directory.Delete(transaction.RecoveryDirectory, recursive: true);
    }

    [Fact]
    public void RollbackCleanupFailureReportsCompletedRollbackWithoutReapplyingItOnDispose()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-rollback-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-rollback-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Temporary rollback cleanup candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original restored before cleanup failure";
        targetRepository.Save(original, "target");
        var backupPath = Path.Combine(workspace.Path, "rollback-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-rollback-cleanup-failure"));
        var lockedCleanupProbe = Path.Combine(transaction.RecoveryDirectory, "locked-cleanup-probe.bin");
        File.WriteAllText(lockedCleanupProbe, "prevent terminal cleanup");

        BackupRestoreCleanupException exception;
        using (new FileStream(
                   lockedCleanupProbe,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            exception = Assert.Throws<BackupRestoreCleanupException>(transaction.Rollback);

            Assert.True(transaction.IsCompleted);
            Assert.Equal(Path.GetFullPath(transaction.RecoveryDirectory), exception.RecoveryDirectory);
            Assert.IsAssignableFrom<IOException>(exception.InnerException);
            Assert.True(Directory.Exists(transaction.RecoveryDirectory));
            Assert.Throws<ObjectDisposedException>(transaction.Commit);
            transaction.Dispose();
        }

        Assert.Equal("Original restored before cleanup failure", LoadExisting(targetDirectory).Plans[0].PlanName);
        Directory.Delete(transaction.RecoveryDirectory, recursive: true);
    }

    [Fact]
    public void SessionCommitCleanupFailureKeepsTheCommittedDocumentAndPersistenceBaseline()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-session-commit-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-session-commit-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Session committed cleanup candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Session commit cleanup original";
        targetRepository.Save(original, "target");
        var session = new DocumentSession(targetRepository);
        var backupPath = Path.Combine(workspace.Path, "session-commit-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var transaction = session.BeginBackupRestore(
            backupPath,
            workspace.CreateSubdirectory("automatic-session-commit-cleanup-failure"));
        var lockedRecoveryFile = Path.Combine(
            transaction.RecoveryDirectory,
            "database-before-replacement.sqlite");
        var changedCount = 0;
        var rolledBackCount = 0;
        var refreshCount = 0;
        var accepted = new List<DocumentStateAcceptedEventArgs>();
        var notifications = new List<string>();
        session.StateAccepted += (_, args) =>
        {
            Assert.True(transaction.IsCompleted);
            accepted.Add(args);
            notifications.Add("accepted");
        };
        session.Changed += (_, _) =>
        {
            Assert.True(transaction.IsCompleted);
            changedCount++;
            notifications.Add("changed");
        };
        session.RolledBack += (_, _) => rolledBackCount++;

        using (new FileStream(
                   lockedRecoveryFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var exception = Assert.Throws<BackupRestoreCleanupException>(() =>
                session.ApplyBackupRestore(
                    transaction,
                    refreshRestoredState: () =>
                    {
                        Assert.True(transaction.IsCompleted);
                        refreshCount++;
                        notifications.Add("refresh");
                    }));

            Assert.Equal(Path.GetFullPath(transaction.RecoveryDirectory), exception.RecoveryDirectory);
            Assert.True(transaction.IsCompleted);
            Assert.Equal(1, changedCount);
            Assert.Equal(1, refreshCount);
            Assert.Equal(0, rolledBackCount);
            var acceptance = Assert.Single(accepted);
            Assert.Equal(DocumentStateAcceptanceKind.Restore, acceptance.Kind);
            Assert.Null(acceptance.EventName);
            Assert.Equal(session.AcceptedStateToken, acceptance.AcceptedStateToken);
            Assert.Equal(["accepted", "changed", "refresh"], notifications);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.Equal("Session committed cleanup candidate", session.Document.Plans[0].PlanName);
            transaction.Dispose();
        }

        Assert.Equal("Session committed cleanup candidate", LoadExisting(targetDirectory).Plans[0].PlanName);
        var eventCount = targetRepository.ReadEventSummaries().Count;
        session.Save("session.commit-cleanup.no-op");
        Assert.Equal(eventCount, targetRepository.ReadEventSummaries().Count);
        Directory.Delete(transaction.RecoveryDirectory, recursive: true);
    }

    [Fact]
    public void SessionCommitCleanupAndRefreshFailuresAreAggregatedAfterPublishingExactlyOnce()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-session-combined-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-session-combined-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Committed combined failure candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Combined failure original";
        targetRepository.Save(original, "target");
        var session = new DocumentSession(targetRepository);
        var backupPath = Path.Combine(workspace.Path, "session-combined-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var transaction = session.BeginBackupRestore(
            backupPath,
            workspace.CreateSubdirectory("automatic-session-combined-cleanup-failure"));
        var lockedRecoveryFile = Path.Combine(
            transaction.RecoveryDirectory,
            "database-before-replacement.sqlite");
        var changedCount = 0;
        var rolledBackCount = 0;
        var refreshCount = 0;
        session.Changed += (_, _) =>
        {
            Assert.True(transaction.IsCompleted);
            changedCount++;
        };
        session.RolledBack += (_, _) => rolledBackCount++;

        using (new FileStream(
                   lockedRecoveryFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var exception = Assert.Throws<DocumentRestorePostCommitException>(() =>
                session.ApplyBackupRestore(
                    transaction,
                    refreshRestoredState: () =>
                    {
                        Assert.True(transaction.IsCompleted);
                        refreshCount++;
                        throw new IOException("Injected refresh failure after cleanup failure.");
                    }));

            var cleanupException = Assert.IsType<BackupRestoreCleanupException>(
                exception.CleanupException);
            Assert.Equal(
                Path.GetFullPath(transaction.RecoveryDirectory),
                cleanupException.RecoveryDirectory);
            Assert.IsType<IOException>(exception.RefreshException);
            Assert.True(transaction.IsCompleted);
            Assert.Equal(1, changedCount);
            Assert.Equal(1, refreshCount);
            Assert.Equal(0, rolledBackCount);
            Assert.Equal("Committed combined failure candidate", session.Document.Plans[0].PlanName);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.False(session.IsSessionConsistencyUnknown);
            transaction.Dispose();
        }

        Assert.Equal("Committed combined failure candidate", LoadExisting(targetDirectory).Plans[0].PlanName);
        Directory.Delete(transaction.RecoveryDirectory, recursive: true);
    }

    [Fact]
    public void SessionRollbackCleanupFailureKeepsTheOriginalDocumentAndPersistenceBaseline()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-session-rollback-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-session-rollback-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Session rollback cleanup candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Session rollback cleanup original";
        targetRepository.Save(original, "target");
        var loadCount = 0;
        var session = new DocumentSession(
            targetRepository,
            loadDocument: () =>
            {
                if (Interlocked.Increment(ref loadCount) == 2)
                    throw new IOException("Injected restored-state load failure before rollback cleanup.");
                return targetRepository.LoadOrCreate();
            });
        var backupPath = Path.Combine(workspace.Path, "session-rollback-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var transaction = session.BeginBackupRestore(
            backupPath,
            workspace.CreateSubdirectory("automatic-session-rollback-cleanup-failure"));
        var lockedCleanupProbe = Path.Combine(transaction.RecoveryDirectory, "locked-session-cleanup-probe.bin");
        File.WriteAllText(lockedCleanupProbe, "prevent session rollback cleanup");

        using (new FileStream(
                   lockedCleanupProbe,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var exception = Assert.Throws<BackupRestoreCleanupException>(() =>
                session.ApplyBackupRestore(transaction));

            Assert.Equal(Path.GetFullPath(transaction.RecoveryDirectory), exception.RecoveryDirectory);
            Assert.True(transaction.IsCompleted);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.Equal("Session rollback cleanup original", session.Document.Plans[0].PlanName);
            transaction.Dispose();
        }

        Assert.Equal("Session rollback cleanup original", LoadExisting(targetDirectory).Plans[0].PlanName);
        var eventCount = targetRepository.ReadEventSummaries().Count;
        session.Save("session.rollback-cleanup.no-op");
        Assert.Equal(eventCount, targetRepository.ReadEventSummaries().Count);
        Directory.Delete(transaction.RecoveryDirectory, recursive: true);
    }

    [Fact]
    public void UnboundSameRepositoryRestoreIsRejectedWithoutTakingOwnershipOrMutatingSessionState()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-rejected-cleanup-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-rejected-cleanup-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Unbound restore candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original session state";
        targetRepository.Save(original, "target");
        var session = new DocumentSession(targetRepository);
        var backupPath = Path.Combine(workspace.Path, "rejected-cleanup-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-rejected-cleanup-failure"));

        Assert.Throws<InvalidOperationException>(() =>
            session.ApplyBackupRestore(transaction));

        Assert.False(transaction.IsCompleted);
        Assert.True(Directory.Exists(transaction.RecoveryDirectory));
        Assert.False(session.IsStorageConsistencyUnknown);
        Assert.False(session.IsSessionConsistencyUnknown);
        Assert.Equal("Original session state", session.Document.Plans[0].PlanName);
        Assert.Equal("Unbound restore candidate", LoadExisting(targetDirectory).Plans[0].PlanName);

        transaction.Rollback();

        Assert.True(transaction.IsCompleted);
        Assert.Equal("Original session state", LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.False(Directory.Exists(transaction.RecoveryDirectory));
    }

    [Fact]
    public void PendingRestoreRollbackPreservesLatestCommittedStateFromAValidWalDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-valid-wal-rollback");
        var targetDirectory = workspace.CreateSubdirectory("target-valid-wal-rollback");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Restore candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original before WAL commit";
        targetRepository.Save(original, "target.initial");
        var backupPath = Path.Combine(workspace.Path, "valid-wal-rollback.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        byte[] databaseBytes;
        byte[] walBytes;
        byte[] shmBytes;
        using (var keeper = Open(targetRepository.DatabasePath))
        {
            using (var command = keeper.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
            }
            original.Plans[0].PlanName = "Latest committed state in WAL";
            targetRepository.Save(original, "target.wal");
            Assert.True(new FileInfo(targetRepository.DatabasePath + "-wal").Length > 0);
            databaseBytes = ReadAllBytesShared(targetRepository.DatabasePath);
            walBytes = ReadAllBytesShared(targetRepository.DatabasePath + "-wal");
            shmBytes = ReadAllBytesShared(targetRepository.DatabasePath + "-shm");
        }

        // Closing the final SQLite connection normally checkpoints and removes
        // WAL sidecars. Recreate the captured, internally consistent on-disk
        // state to exercise startup/restore recovery with no open file handles.
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(targetRepository.DatabasePath, databaseBytes);
        File.WriteAllBytes(targetRepository.DatabasePath + "-wal", walBytes);
        File.WriteAllBytes(targetRepository.DatabasePath + "-shm", shmBytes);
        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-valid-wal-rollback"));
        transaction.Rollback();

        Assert.Equal(
            "Latest committed state in WAL",
            LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.False(Directory.Exists(transaction.RecoveryDirectory));
    }

    [Fact]
    public void RestorePublishesAWalModeBackupAsDeleteJournalWithoutLosingCommittedState()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-wal-candidate-normalization");
        var targetDirectory = workspace.CreateSubdirectory("target-wal-candidate-normalization");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Initial WAL candidate";
        sourceRepository.Save(candidate, "source.initial");
        targetRepository.Save(TestDocumentFactory.CreatePopulated(), "target.initial");
        var backupPath = Path.Combine(workspace.Path, "wal-mode-candidate.zip");

        using (var keeper = Open(sourceRepository.DatabasePath))
        {
            using (var command = keeper.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
            }
            using (var command = keeper.CreateCommand())
            {
                command.CommandText = """
                    UPDATE app_state
                    SET json = replace(json, $oldName, $newName),
                        updated_at = $updatedAt
                    WHERE id = 'default'
                    """;
                command.Parameters.AddWithValue("$oldName", "Initial WAL candidate");
                command.Parameters.AddWithValue("$newName", "Latest committed WAL candidate");
                command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            Assert.True(new FileInfo(sourceRepository.DatabasePath + "-wal").Length > 0);
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        }

        var transaction = BackupService.BeginRestoreWithPreBackup(
            targetRepository.DatabasePath,
            backupPath,
            workspace.CreateSubdirectory("automatic-wal-candidate-normalization"));

        using (var connection = Open(targetRepository.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode";
            Assert.Equal(
                "delete",
                Assert.IsType<string>(command.ExecuteScalar()),
                ignoreCase: true);
        }
        Assert.False(File.Exists(targetRepository.DatabasePath + "-wal"));
        Assert.False(File.Exists(targetRepository.DatabasePath + "-shm"));
        Assert.False(File.Exists(targetRepository.DatabasePath + "-journal"));
        Assert.Equal(
            "Latest committed WAL candidate",
            LoadExisting(targetDirectory).Plans[0].PlanName);

        transaction.Commit();

        Assert.True(transaction.IsCompleted);
        Assert.Equal(
            "Latest committed WAL candidate",
            LoadExisting(targetDirectory).Plans[0].PlanName);
    }

    [Fact]
    public void RestoreClearsOnlyTheTargetDatabasePool()
    {
        using var workspace = new TemporaryDirectory();
        var sourceRepository = new SqliteAppRepository(workspace.CreateSubdirectory("pool-source"));
        var targetRepository = new SqliteAppRepository(workspace.CreateSubdirectory("pool-target"));
        var unrelatedRepository = new SqliteAppRepository(workspace.CreateSubdirectory("pool-unrelated"));
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Pool restore candidate";
        sourceRepository.Save(candidate, "source");
        targetRepository.Save(TestDocumentFactory.CreatePopulated(), "target");
        var unrelated = TestDocumentFactory.CreatePopulated();
        unrelatedRepository.Save(unrelated, "unrelated.initial");
        var backupPath = Path.Combine(workspace.Path, "pool-target.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        using (var connection = Open(unrelatedRepository.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
            command.ExecuteNonQuery();
            unrelated.Plans[0].PlanName = "Unrelated committed WAL state";
            unrelatedRepository.Save(unrelated, "unrelated.wal");
        }

        var unrelatedWalPath = unrelatedRepository.DatabasePath + "-wal";
        Assert.True(File.Exists(unrelatedWalPath));
        Assert.True(new FileInfo(unrelatedWalPath).Length > 0);

        using (var transaction = BackupService.BeginRestoreWithPreBackup(
                   targetRepository.DatabasePath,
                   backupPath,
                   workspace.CreateSubdirectory("pool-automatic")))
        {
            Assert.True(File.Exists(unrelatedWalPath));
            Assert.True(new FileInfo(unrelatedWalPath).Length > 0);
            transaction.Rollback();
        }

        Assert.Equal(
            "Unrelated committed WAL state",
            LoadExisting(Path.GetDirectoryName(unrelatedRepository.DatabasePath)!).Plans[0].PlanName);
    }

    [Fact]
    public void MissingPlanCandidatePublishesWindowCloseOnlyAfterCommitWhenRefreshFails()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-refresh-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-refresh-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Candidate shown during refresh";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original shown after compensation";
        var trackedPlanId = original.Plans[0].PlanId;
        Assert.DoesNotContain(candidate.Plans, plan => plan.PlanId == trackedPlanId);
        targetRepository.Save(original, "target");
        var backupPath = Path.Combine(workspace.Path, "refresh-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var session = new DocumentSession(targetRepository);
        using var transaction = session.BeginBackupRestore(
            backupPath,
            workspace.CreateSubdirectory("automatic-refresh-failure"));
        var notifications = new List<string>();
        var durableWindowCloseCount = 0;
        session.Changed += (_, _) =>
        {
            Assert.True(transaction.IsCompleted);
            notifications.Add("changed");
            if (!session.Document.Plans.Any(plan => plan.PlanId == trackedPlanId))
                durableWindowCloseCount++;
        };

        var exception = Assert.Throws<DocumentRestorePostCommitException>(() =>
            session.ApplyBackupRestore(
                transaction,
                refreshRestoredState: () =>
                {
                    Assert.True(transaction.IsCompleted);
                    notifications.Add("refresh");
                    throw new IOException("Injected refresh failure.");
                }));

        Assert.True(transaction.IsCompleted);
        Assert.Equal(["changed", "refresh"], notifications);
        Assert.Equal(1, durableWindowCloseCount);
        Assert.IsType<IOException>(exception.RefreshException);
        Assert.Null(exception.CleanupException);
        Assert.Equal("Candidate shown during refresh", session.Document.Plans[0].PlanName);
        Assert.Equal("Candidate shown during refresh", LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.False(session.IsStorageConsistencyUnknown);
        Assert.False(session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void ReloadRetriesTheLockedRollbackIntentAndReleasesTheSessionGateAfterSuccess()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-rollback-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-rollback-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Candidate left after blocked rollback";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original recovered after retry";
        targetRepository.Save(original, "target");
        var backupPath = Path.Combine(workspace.Path, "rollback-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var loadCount = 0;
        var session = new DocumentSession(
            targetRepository,
            loadDocument: () =>
            {
                if (Interlocked.Increment(ref loadCount) == 2)
                    throw new IOException("Injected restored-state load failure.");
                return targetRepository.LoadOrCreate();
            });
        var transaction = session.BeginBackupRestore(
            backupPath,
            workspace.CreateSubdirectory("automatic-rollback-failure"));

        DocumentRestoreConsistencyException exception;
        using (var blocker = new FileStream(
                   targetRepository.DatabasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            exception = Assert.Throws<DocumentRestoreConsistencyException>(() =>
                session.ApplyBackupRestore(transaction));
            Assert.True(session.IsStorageConsistencyUnknown);
            Assert.False(transaction.IsCompleted);
            Assert.True(Directory.Exists(exception.RecoveryDirectory));
            Assert.Same(transaction.PreRestoreBackupPath, exception.PreRestoreBackupPath);
            Assert.Equal(
                "Injected restored-state load failure.",
                exception.RestoreException.Message);
            Assert.IsAssignableFrom<IOException>(exception.RollbackException);
            var saveException = Assert.Throws<InvalidOperationException>(() => session.Save("must-not-write"));
            Assert.Contains("Saving is disabled", saveException.Message, StringComparison.Ordinal);
        }

        session.ReloadFromRepository();

        Assert.False(session.IsStorageConsistencyUnknown);
        Assert.True(transaction.IsCompleted);
        Assert.False(Directory.Exists(exception.RecoveryDirectory));
        Assert.Equal("Original recovered after retry", session.Document.Plans[0].PlanName);
        Assert.Equal("Original recovered after retry", LoadExisting(targetDirectory).Plans[0].PlanName);

        session.Document.Plans[0].PlanName = "Writable after automatic terminal replay";
        session.Save("after-replayed-rollback");
        var postRecoveryBackup = Path.Combine(workspace.Path, "after-replayed-rollback.zip");
        BackupService.CreateBackup(targetRepository.DatabasePath, postRecoveryBackup);
        Assert.True(File.Exists(postRecoveryBackup));
    }

    [Fact]
    public void PostCommitSubscriberAndRefreshFailuresAreBothReportedWithoutRollback()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-compensation-failure");
        var targetDirectory = workspace.CreateSubdirectory("target-compensation-failure");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Candidate compensation state";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Original compensation state";
        targetRepository.Save(original, "target");
        var backupPath = Path.Combine(workspace.Path, "compensation-failure.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var session = new DocumentSession(targetRepository);
        var laterSubscriberCalls = 0;
        var refreshCalls = 0;
        EventHandler failingSubscriber = (_, _) =>
            throw new IOException("Projection subscriber failed.");
        session.Changed += failingSubscriber;
        session.Changed += (_, _) => laterSubscriberCalls++;

        var exception = Assert.Throws<DocumentRestorePostCommitException>(() =>
            session.RestoreFromBackup(
                backupPath,
                workspace.CreateSubdirectory("automatic-compensation-failure"),
                refreshRestoredState: () =>
                {
                    refreshCalls++;
                    throw new InvalidOperationException("Settings refresh remains unavailable.");
                }));

        Assert.IsType<IOException>(exception.NotificationException);
        Assert.IsType<InvalidOperationException>(exception.RefreshException);
        Assert.Equal(1, laterSubscriberCalls);
        Assert.Equal(1, refreshCalls);
        Assert.False(session.IsStorageConsistencyUnknown);
        Assert.False(session.IsSessionConsistencyUnknown);
        Assert.Equal("Candidate compensation state", session.Document.Plans[0].PlanName);
        Assert.Equal("Candidate compensation state", LoadExisting(targetDirectory).Plans[0].PlanName);
        session.Changed -= failingSubscriber;
        session.Document.Plans[0].PlanName = "Core remains writable after callback failures";
        session.Save("after-postcommit-callback-failures");
        Assert.Equal(
            "Core remains writable after callback failures",
            LoadExisting(targetDirectory).Plans[0].PlanName);
    }

    [Fact]
    public void SuccessfulSessionRestoreCommitsDatabaseDocumentHistoryAndRefreshTogether()
    {
        using var workspace = new TemporaryDirectory();
        var sourceDirectory = workspace.CreateSubdirectory("source-session-commit");
        var targetDirectory = workspace.CreateSubdirectory("target-session-commit");
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var targetRepository = new SqliteAppRepository(targetDirectory);
        var candidate = TestDocumentFactory.CreatePopulated();
        candidate.Plans[0].PlanName = "Committed restore candidate";
        sourceRepository.Save(candidate, "source");
        var original = TestDocumentFactory.CreatePopulated();
        original.Plans[0].PlanName = "Replaced original";
        targetRepository.Save(original, "target");
        var backupPath = Path.Combine(workspace.Path, "session-commit.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        var automaticDirectory = workspace.CreateSubdirectory("automatic-session-commit");
        var session = new DocumentSession(targetRepository);
        session.CaptureUndo();
        var changedCount = 0;
        var refreshCount = 0;
        var notifications = new List<string>();
        DocumentStateAcceptedEventArgs? accepted = null;
        session.StateAccepted += (_, args) =>
        {
            accepted = args;
            notifications.Add("accepted");
        };
        session.Changed += (_, _) =>
        {
            changedCount++;
            notifications.Add("changed");
        };

        var preRestoreBackup = session.RestoreFromBackup(
            backupPath,
            automaticDirectory,
            refreshRestoredState: () =>
            {
                refreshCount++;
                notifications.Add("refresh");
                Assert.Equal("Committed restore candidate", session.Document.Plans[0].PlanName);
            });

        Assert.NotNull(preRestoreBackup);
        Assert.True(File.Exists(preRestoreBackup));
        Assert.Equal(1, changedCount);
        Assert.Equal(1, refreshCount);
        Assert.NotNull(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Restore, accepted.Kind);
        Assert.Null(accepted.EventName);
        Assert.Equal(session.AcceptedStateToken, accepted.AcceptedStateToken);
        Assert.Equal(["accepted", "changed", "refresh"], notifications);
        Assert.False(session.IsStorageConsistencyUnknown);
        Assert.False(session.UndoRedo.CanUndo);
        Assert.False(session.UndoRedo.CanRedo);
        Assert.Equal("Committed restore candidate", session.Document.Plans[0].PlanName);
        Assert.Equal("Committed restore candidate", LoadExisting(targetDirectory).Plans[0].PlanName);
        Assert.Empty(Directory.EnumerateDirectories(targetDirectory, ".restore-*"));
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void CreateArchiveFromDatabase(string databasePath, string archivePath)
    {
        SqliteConnection.ClearAllPools();
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var databaseEntry = archive.CreateEntry("course-planner.sqlite");
        using (var input = File.OpenRead(databasePath))
        using (var output = databaseEntry.Open())
        {
            input.CopyTo(output);
        }

        var manifestEntry = archive.CreateEntry("manifest.json");
        using var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8);
        writer.Write(JsonSerializer.Serialize(new BackupManifest(), JsonDefaults.Options));
    }

    private static void CreateArchiveFromDatabaseWithManifest(
        string databasePath,
        string archivePath,
        byte[] manifestBytes)
    {
        SqliteConnection.ClearAllPools();
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var databaseEntry = archive.CreateEntry("course-planner.sqlite");
        using (var input = File.OpenRead(databasePath))
        using (var output = databaseEntry.Open())
        {
            input.CopyTo(output);
        }

        var manifestEntry = archive.CreateEntry("manifest.json");
        using var manifest = manifestEntry.Open();
        manifest.Write(manifestBytes);
    }

    private static PlannerDocument LoadExisting(string dataDirectory) =>
        new SqliteAppRepository(
            dataDirectory,
            () => throw new InvalidOperationException("The restored database did not contain a loadable document."))
        .LoadOrCreate();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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

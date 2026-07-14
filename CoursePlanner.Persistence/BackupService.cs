using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoursePlanner.Core;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Persistence;

public sealed class BackupManifest
{
    [JsonRequired]
    public string Kind { get; set; } = PlannerSchemas.BackupKind;
    [JsonRequired]
    public string SchemaVersion { get; set; } = PlannerSchemas.Current;
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public string DatabaseFile { get; set; } = "course-planner.sqlite";
}

public sealed class BackupRestoreCleanupException : IOException
{
    public BackupRestoreCleanupException(string recoveryDirectory, Exception innerException)
        : base(
            $"The database restore completed, but its recovery directory '{Path.GetFullPath(recoveryDirectory)}' could not be removed.",
            innerException)
    {
        RecoveryDirectory = Path.GetFullPath(recoveryDirectory);
    }

    public string RecoveryDirectory { get; }
}

public sealed class BackupRestoreCandidateChangedException : IOException
{
    internal BackupRestoreCandidateChangedException(
        string databasePath,
        string recoveryDirectory,
        DatabaseFileFingerprint expected,
        DatabaseFileFingerprint observed)
        : base(
            "The published restore candidate changed before the restore transaction reached its " +
            "requested terminal state. The transaction remains pending and its recovery material " +
            $"has been retained at '{Path.GetFullPath(recoveryDirectory)}'. " +
            $"Expected={expected}; observed={observed}.")
    {
        DatabasePath = Path.GetFullPath(databasePath);
        RecoveryDirectory = Path.GetFullPath(recoveryDirectory);
        ExpectedExists = expected.Exists;
        ExpectedLength = expected.Length;
        ExpectedSha256 = expected.Sha256;
        ObservedExists = observed.Exists;
        ObservedLength = observed.Length;
        ObservedSha256 = observed.Sha256;
    }

    public string DatabasePath { get; }
    public string RecoveryDirectory { get; }
    public bool ExpectedExists { get; }
    public long ExpectedLength { get; }
    public string? ExpectedSha256 { get; }
    public bool ObservedExists { get; }
    public long ObservedLength { get; }
    public string? ObservedSha256 { get; }
}

internal sealed record DatabaseFileFingerprint(bool Exists, long Length, string? Sha256)
{
    public static DatabaseFileFingerprint Capture(string databasePath)
    {
        try
        {
            using var stream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var sha256 = Convert.ToHexString(SHA256.HashData(stream));
            return new DatabaseFileFingerprint(true, length, sha256);
        }
        catch (FileNotFoundException)
        {
            return new DatabaseFileFingerprint(false, 0, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new DatabaseFileFingerprint(false, 0, null);
        }
    }

    public override string ToString() => Exists
        ? $"exists,length={Length},sha256={Sha256}"
        : "missing";
}

/// <summary>
/// Represents a database restore whose file replacement has completed but whose
/// application-level state has not yet been accepted. The caller must either
/// commit after loading the restored state or roll back to the exact prior
/// database state.
/// </summary>
public sealed class BackupRestoreTransaction : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IDisposable> _acquirePathLease;
    private readonly Action _cleanup;
    private readonly DatabaseFileFingerprint _expectedCandidate;
    private Action? _rollback;
    private Action? _commit;
    private IDisposable? _pathLease;
    private TerminalIntent? _terminalIntent;

    internal BackupRestoreTransaction(
        string databasePath,
        string? preRestoreBackupPath,
        string recoveryDirectory,
        Action rollback,
        Action commit,
        Action cleanup,
        IDisposable pathLease,
        Func<IDisposable> acquirePathLease,
        DatabaseFileFingerprint expectedCandidate)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        PreRestoreBackupPath = preRestoreBackupPath;
        RecoveryDirectory = recoveryDirectory;
        _rollback = rollback;
        _commit = commit;
        _cleanup = cleanup;
        _pathLease = pathLease;
        _acquirePathLease = acquirePathLease;
        _expectedCandidate = expectedCandidate;
    }

    public string DatabasePath { get; }

    public string? PreRestoreBackupPath { get; }

    /// <summary>
    /// Retained when rollback itself or terminal cleanup fails so recovery or
    /// manual cleanup still has the remaining database material.
    /// </summary>
    public string RecoveryDirectory { get; }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _rollback is null;
        }
    }

    public void Commit()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_rollback is null, this);
            ExecuteTerminalAction(_commit!, TerminalIntent.Commit);
        }
    }

    public void Rollback()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_rollback is null, this);
            ExecuteTerminalAction(_rollback, TerminalIntent.Rollback);
        }
    }

    /// <summary>
    /// Replays the terminal action selected by an earlier failed
    /// <see cref="Commit"/> or <see cref="Rollback"/> attempt. Callers cannot
    /// use this method to select or switch the transaction's terminal intent.
    /// </summary>
    public void RetryTerminalAction()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_rollback is null, this);
            var intent = _terminalIntent ?? throw new InvalidOperationException(
                "No terminal action has been attempted for this restore transaction.");
            ExecuteTerminalAction(
                intent == TerminalIntent.Commit ? _commit! : _rollback!,
                intent);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_rollback is null)
                return;
            // A failed explicit terminal action intentionally retains both the
            // path lease and its recovery material. Disposal must not switch
            // intent or hide that failure by retrying implicitly.
            if (_terminalIntent is not null)
                return;
            ExecuteTerminalAction(_rollback, TerminalIntent.Rollback);
        }
    }

    private void ExecuteTerminalAction(Action action, TerminalIntent intent)
    {
        if (_terminalIntent is { } existingIntent && existingIntent != intent)
        {
            throw new InvalidOperationException(
                $"This restore transaction is already locked to {existingIntent} and cannot switch to {intent}.");
        }
        _terminalIntent ??= intent;

        var pathLease = _pathLease ??= _acquirePathLease();
        var terminalStateReached = false;
        try
        {
            var observedCandidate = DatabaseFileFingerprint.Capture(DatabasePath);
            if (observedCandidate != _expectedCandidate)
            {
                throw new BackupRestoreCandidateChangedException(
                    DatabasePath,
                    RecoveryDirectory,
                    _expectedCandidate,
                    observedCandidate);
            }

            action();
            _rollback = null;
            _commit = null;
            terminalStateReached = true;
            try
            {
                _cleanup();
            }
            catch (BackupRestoreCleanupException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BackupRestoreCleanupException(RecoveryDirectory, exception);
            }
        }
        finally
        {
            // A failed candidate check or database transition is still a live
            // transaction. Keep the lease so no newer restore can commit and
            // then be overwritten by a retry of this older transaction.
            if (terminalStateReached)
            {
                _pathLease = null;
                pathLease.Dispose();
            }
        }
    }

    private enum TerminalIntent
    {
        Commit,
        Rollback
    }
}

public static class BackupService
{
    private const string DatabaseEntryName = "course-planner.sqlite";
    private const string ManifestEntryName = "manifest.json";
    private const long MaxBackupEntryBytes = 100 * 1024 * 1024;
    private const long MaxManifestEntryBytes = 64 * 1024;
    private const long MaxBackupArchiveBytes = 112 * 1024 * 1024;
    private const int MaxBackupArchiveEntries = 64;
    private static readonly TimeSpan RestorePathLeaseTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] DatabaseSidecarSuffixes = ["-wal", "-shm", "-journal"];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly object RestorePathLeasesGate = new();
    private static readonly Dictionary<string, RestorePathLeaseEntry> RestorePathLeases =
        new(StringComparer.OrdinalIgnoreCase);

    private static IDisposable AcquireRestorePathLease(string databasePath)
    {
        var normalizedPath = Path.GetFullPath(databasePath);
        RestorePathLeaseEntry entry;
        lock (RestorePathLeasesGate)
        {
            if (!RestorePathLeases.TryGetValue(normalizedPath, out entry!))
            {
                entry = new RestorePathLeaseEntry();
                RestorePathLeases.Add(normalizedPath, entry);
            }
            entry.ReferenceCount++;
        }

        try
        {
            if (!entry.Semaphore.Wait(RestorePathLeaseTimeout))
            {
                throw new IOException(
                    $"Timed out waiting for another backup or restore operation for '{normalizedPath}' to finish.");
            }
            return new RestorePathLease(normalizedPath, entry);
        }
        catch
        {
            RemoveRestorePathLeaseReference(normalizedPath, entry);
            throw;
        }
    }

    private static void ReleaseRestorePathLease(string normalizedPath, RestorePathLeaseEntry entry)
    {
        entry.Semaphore.Release();
        RemoveRestorePathLeaseReference(normalizedPath, entry);
    }

    private static void RemoveRestorePathLeaseReference(
        string normalizedPath,
        RestorePathLeaseEntry entry)
    {
        var disposeSemaphore = false;
        lock (RestorePathLeasesGate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                RestorePathLeases.TryGetValue(normalizedPath, out var registered) &&
                ReferenceEquals(registered, entry))
            {
                RestorePathLeases.Remove(normalizedPath);
                disposeSemaphore = true;
            }
        }

        if (disposeSemaphore)
            entry.Semaphore.Dispose();
    }

    private sealed class RestorePathLeaseEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class RestorePathLease(
        string normalizedPath,
        RestorePathLeaseEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ReleaseRestorePathLease(normalizedPath, entry);
        }
    }

    public static void CreateBackup(string databasePath, string zipPath)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        using var pathLease = AcquireRestorePathLease(fullDatabasePath);
        CreateBackupCore(fullDatabasePath, zipPath);
    }

    // Restore already owns the per-database lease while producing its automatic
    // pre-restore artifact. Keeping the non-locking core private prevents both
    // an accidental re-entrant wait and an observable snapshot of an uncommitted
    // restore transaction.
    private static void CreateBackupCore(string fullDatabasePath, string zipPath)
    {
        var fullZipPath = Path.GetFullPath(zipPath);
        var reservedDatabasePaths = DatabaseSidecarSuffixes
            .Select(suffix => fullDatabasePath + suffix)
            .Prepend(fullDatabasePath);
        if (reservedDatabasePaths.Contains(fullZipPath, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The backup destination cannot overwrite a SQLite database file.", nameof(zipPath));
        if (!File.Exists(fullDatabasePath))
            throw new FileNotFoundException("Database file was not found.", fullDatabasePath);

        EnsureParentDirectory(fullZipPath);
        var outputDirectory = Path.GetDirectoryName(fullZipPath) ?? Path.GetTempPath();
        var stagingDirectory = Path.Combine(outputDirectory, $".backup-{Guid.NewGuid():N}");
        var snapshotPath = Path.Combine(stagingDirectory, DatabaseEntryName);
        var temporaryZipPath = Path.Combine(
            outputDirectory,
            $".course-planner-backup-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            CreateDatabaseSnapshot(fullDatabasePath, snapshotPath);
            ValidateSqliteDatabase(snapshotPath);

            using (var archive = ZipFile.Open(temporaryZipPath, ZipArchiveMode.Create))
            {
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.Optimal);
                using (var source = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = dbEntry.Open())
                {
                    source.CopyTo(destination);
                }

                var manifest = JsonSerializer.Serialize(new BackupManifest(), JsonDefaults.Options);
                var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(manifest);
            }

            var validationSnapshotPath = Path.Combine(stagingDirectory, "validated-course-planner.sqlite");
            ExtractAndValidateBackup(temporaryZipPath, validationSnapshotPath);
            ValidateSqliteDatabase(validationSnapshotPath);

            File.Move(temporaryZipPath, fullZipPath, overwrite: true);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The database could not be read for backup.", exception);
        }
        finally
        {
            TryDeleteFile(temporaryZipPath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public static string RestoreWithPreBackup(string databasePath, string backupZipPath, string automaticBackupDirectory)
    {
        using var transaction = BeginRestoreWithPreBackup(
            databasePath,
            backupZipPath,
            automaticBackupDirectory);
        transaction.Commit();
        return transaction.PreRestoreBackupPath ?? string.Empty;
    }

    public static BackupRestoreTransaction BeginRestoreWithPreBackup(
        string databasePath,
        string backupZipPath,
        string automaticBackupDirectory)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        IDisposable? pathLease = AcquireRestorePathLease(fullDatabasePath);
        string? restoreDirectory = null;
        try
        {
            var databaseDirectory = Path.GetDirectoryName(fullDatabasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
                Directory.CreateDirectory(databaseDirectory);
            restoreDirectory = Path.Combine(
                string.IsNullOrWhiteSpace(databaseDirectory) ? Path.GetTempPath() : databaseDirectory,
                $".restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(restoreDirectory);
            var restoredDatabase = Path.Combine(restoreDirectory, "course-planner.sqlite");
            ExtractAndValidateBackup(backupZipPath, restoredDatabase);
            ValidateSqliteDatabase(restoredDatabase);
            NormalizeRestoreCandidateJournalMode(restoredDatabase);

            Directory.CreateDirectory(automaticBackupDirectory);
            var artifactStem = $"before-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            string? preBackup = null;
            var currentDatabaseIsValid = true;
            if (File.Exists(fullDatabasePath))
            {
                (preBackup, currentDatabaseIsValid) = CreatePreRestoreArtifact(
                    fullDatabasePath,
                    automaticBackupDirectory,
                    artifactStem,
                    restoreDirectory);
            }

            var expectedCandidate = DatabaseFileFingerprint.Capture(restoredDatabase);
            if (!expectedCandidate.Exists)
                throw new IOException("The validated restore candidate disappeared before publication.");

            var replacement = ReplaceDatabase(
                restoredDatabase,
                fullDatabasePath,
                restoreDirectory,
                currentDatabaseIsValid);
            var transaction = new BackupRestoreTransaction(
                fullDatabasePath,
                preBackup,
                restoreDirectory,
                rollback: () =>
                    RollbackDatabaseReplacement(replacement),
                commit: static () => { },
                cleanup: () => DeleteDirectory(restoreDirectory),
                pathLease: pathLease,
                acquirePathLease: () => AcquireRestorePathLease(fullDatabasePath),
                expectedCandidate: expectedCandidate);
            pathLease = null;
            return transaction;
        }
        catch (SqliteException exception)
        {
            if (restoreDirectory is not null)
                TryDeleteDirectory(restoreDirectory);
            throw new InvalidDataException("The backup database could not be restored.", exception);
        }
        catch
        {
            if (restoreDirectory is not null)
                TryDeleteDirectory(restoreDirectory);
            throw;
        }
        finally
        {
            pathLease?.Dispose();
        }
    }

    private static void CreateDatabaseSnapshot(string databasePath, string snapshotPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static (string ArtifactPath, bool CurrentDatabaseIsValid) CreatePreRestoreArtifact(
        string databasePath,
        string automaticBackupDirectory,
        string artifactStem,
        string restoreDirectory)
    {
        ClearPoolForDatabase(databasePath);
        var originalDatabaseCopy = Path.Combine(restoreDirectory, "original-current-database.sqlite");
        CopyFileAtomically(databasePath, originalDatabaseCopy);
        var originalSidecars = PreserveDatabaseSidecars(databasePath, restoreDirectory);
        var zipPath = Path.Combine(automaticBackupDirectory, $"{artifactStem}.zip");
        var rawPath = Path.Combine(automaticBackupDirectory, $"{artifactStem}.sqlite");

        if (!HasSqliteHeader(originalDatabaseCopy))
        {
            CreateRawPreRestoreArtifact(originalDatabaseCopy, originalSidecars, rawPath);
            return (rawPath, false);
        }

        try
        {
            CreateBackupCore(databasePath, zipPath);
            return (zipPath, true);
        }
        catch (InvalidDataException)
        {
            ClearPoolForDatabase(databasePath);
            RestorePreservedDatabaseSidecars(databasePath, originalSidecars);
            CreateRawPreRestoreArtifact(originalDatabaseCopy, originalSidecars, rawPath);
            return (rawPath, false);
        }
        catch
        {
            ClearPoolForDatabase(databasePath);
            RestorePreservedDatabaseSidecars(databasePath, originalSidecars);
            throw;
        }
    }

    private static void ExtractAndValidateBackup(string backupZipPath, string restoredDatabase)
    {
        var archiveLength = new FileInfo(backupZipPath).Length;
        if (archiveLength <= 0 || archiveLength > MaxBackupArchiveBytes)
            throw new InvalidDataException("Backup archive size is invalid.");

        using var archive = ZipFile.OpenRead(backupZipPath);
        if (archive.Entries.Count > MaxBackupArchiveEntries)
            throw new InvalidDataException("Backup archive contains too many entries.");

        var manifestEntries = archive.Entries.Where(entry => entry.FullName == ManifestEntryName).ToList();
        if (manifestEntries.Count != 1)
            throw new InvalidDataException("Backup manifest is missing or duplicated.");

        var manifestEntry = manifestEntries[0];
        ValidateEntrySize(manifestEntry, MaxManifestEntryBytes);
        using (var manifestDocument = ReadManifestDocument(manifestEntry))
        {
            BackupManifest manifest;
            try
            {
                manifest = manifestDocument.RootElement.Deserialize<BackupManifest>(JsonDefaults.Options)
                    ?? throw new InvalidDataException("Backup manifest is invalid.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Backup manifest is invalid.", exception);
            }

            if (manifest.Kind != PlannerSchemas.BackupKind ||
                manifest.SchemaVersion != PlannerSchemas.Current ||
                manifest.DatabaseFile != DatabaseEntryName)
            {
                throw new InvalidDataException("Backup manifest is invalid.");
            }
        }

        var databaseEntries = archive.Entries.Where(entry => entry.FullName == DatabaseEntryName).ToList();
        if (databaseEntries.Count != 1)
            throw new InvalidDataException("Backup database is missing or duplicated.");
        ValidateEntrySize(databaseEntries[0], MaxBackupEntryBytes);
        databaseEntries[0].ExtractToFile(restoredDatabase, overwrite: false);
    }

    private static JsonDocument ReadManifestDocument(ZipArchiveEntry entry)
    {
        var bytes = new byte[checked((int)entry.Length)];
        using (var stream = entry.Open())
            stream.ReadExactly(bytes);

        var payload = bytes.AsSpan();
        if (payload.StartsWith(Utf8Bom))
            payload = payload[Utf8Bom.Length..];

        try
        {
            return JsonInputGuard.ParseDocument(StrictUtf8.GetString(payload));
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException)
        {
            throw new InvalidDataException("Backup manifest is invalid.", exception);
        }
    }

    private static DatabaseReplacementState ReplaceDatabase(
        string restoredDatabase,
        string databasePath,
        string restoreDirectory,
        bool currentDatabaseIsValid)
    {
        ClearPoolForDatabase(databasePath);
        if (!File.Exists(databasePath))
        {
            var orphanedSidecars = QuarantineDatabaseSidecars(databasePath, restoreDirectory);
            try
            {
                File.Move(restoredDatabase, databasePath);
            }
            catch
            {
                RestoreQuarantinedSidecars(orphanedSidecars);
                throw;
            }
            return new DatabaseReplacementState(
                databasePath,
                restoreDirectory,
                OriginalDatabaseExisted: false,
                OriginalDatabaseWasValid: false,
                RollbackDatabasePath: null,
                orphanedSidecars);
        }

        if (currentDatabaseIsValid)
            CheckpointDatabase(databasePath);
        ClearPoolForDatabase(databasePath);

        var quarantinedSidecars = QuarantineDatabaseSidecars(databasePath, restoreDirectory);
        var rollbackPath = Path.Combine(restoreDirectory, "database-before-replacement.sqlite");
        try
        {
            File.Replace(restoredDatabase, databasePath, rollbackPath, ignoreMetadataErrors: true);
        }
        catch
        {
            RestoreQuarantinedSidecars(quarantinedSidecars);
            throw;
        }
        return new DatabaseReplacementState(
            databasePath,
            restoreDirectory,
            OriginalDatabaseExisted: true,
            currentDatabaseIsValid,
            rollbackPath,
            quarantinedSidecars);
    }

    private static void RollbackDatabaseReplacement(DatabaseReplacementState state)
    {
        ClearPoolForDatabase(state.DatabasePath);
        if (state.OriginalDatabaseExisted)
        {
            var expectedRollbackPath = state.RollbackDatabasePath
                ?? throw new IOException("The original database rollback file is missing.");
            if (!File.Exists(expectedRollbackPath))
                throw new IOException("The original database rollback file no longer exists.");
            if (!File.Exists(state.DatabasePath))
                throw new IOException("The restored database disappeared before rollback.");
        }

        var restoredSidecars = QuarantineDatabaseSidecars(
            state.DatabasePath,
            state.RestoreDirectory,
            "restored-database-sidecar");

        if (!state.OriginalDatabaseExisted)
        {
            RollbackToMissingDatabase(state, restoredSidecars);
            return;
        }

        var rollbackDatabasePath = state.RollbackDatabasePath!;

        var failedRestoredPath = Path.Combine(state.RestoreDirectory, "failed-restored-database.sqlite");
        var databaseWasRolledBack = false;
        try
        {
            File.Replace(
                rollbackDatabasePath,
                state.DatabasePath,
                failedRestoredPath,
                ignoreMetadataErrors: true);
            databaseWasRolledBack = true;
            if (!state.OriginalDatabaseWasValid)
                RestoreSidecarsFromCopies(state.OriginalSidecars);
        }
        catch (Exception rollbackException)
        {
            if (!databaseWasRolledBack)
            {
                RestoreQuarantinedSidecars(restoredSidecars);
                throw;
            }

            try
            {
                DeleteDatabaseSidecars(state.DatabasePath);
                File.Replace(
                    failedRestoredPath,
                    state.DatabasePath,
                    rollbackDatabasePath,
                    ignoreMetadataErrors: true);
                RestoreQuarantinedSidecars(restoredSidecars);
            }
            catch (Exception reverseException)
            {
                throw new AggregateException(
                    "Database rollback failed and the restored state could not be re-established.",
                    rollbackException,
                    reverseException);
            }

            throw;
        }
    }

    private static void RollbackToMissingDatabase(
        DatabaseReplacementState state,
        IReadOnlyList<(string OriginalPath, string QuarantinedPath)> restoredSidecars)
    {
        var failedRestoredPath = Path.Combine(state.RestoreDirectory, "failed-restored-database.sqlite");
        var databaseWasRemoved = false;
        try
        {
            if (File.Exists(state.DatabasePath))
            {
                File.Move(state.DatabasePath, failedRestoredPath);
                databaseWasRemoved = true;
            }
            RestoreSidecarsFromCopies(state.OriginalSidecars);
        }
        catch (Exception rollbackException)
        {
            try
            {
                DeleteDatabaseSidecars(state.DatabasePath);
                if (databaseWasRemoved && File.Exists(failedRestoredPath))
                    File.Move(failedRestoredPath, state.DatabasePath);
                RestoreQuarantinedSidecars(restoredSidecars);
            }
            catch (Exception reverseException)
            {
                throw new AggregateException(
                    "Rollback to a missing database failed and the restored state could not be re-established.",
                    rollbackException,
                    reverseException);
            }

            throw;
        }
    }

    private static void CheckpointDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var result = command.ExecuteReader();
        if (result.Read() && result.GetInt32(0) != 0)
            throw new IOException("The current database is busy and could not be prepared for restore.");
    }

    private static void NormalizeRestoreCandidateJournalMode(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE";
            var journalMode = command.ExecuteScalar() as string;
            if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The restore candidate could not be normalized to DELETE journal mode (reported '{journalMode ?? "null"}').");
            }
        }

        // A backup can persist WAL mode in the main database header. Closing
        // the normalization connection first makes these candidate sidecars
        // disposable; none may accompany the atomically published main file.
        DeleteDatabaseSidecars(databasePath);
    }

    private static void ClearPoolForDatabase(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        };
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static List<(string OriginalPath, string QuarantinedPath)> QuarantineDatabaseSidecars(
        string databasePath,
        string restoreDirectory,
        string quarantineStem = "database-sidecar")
    {
        var quarantined = new List<(string OriginalPath, string QuarantinedPath)>();
        try
        {
            foreach (var suffix in DatabaseSidecarSuffixes)
            {
                var originalPath = databasePath + suffix;
                if (!File.Exists(originalPath))
                    continue;

                var quarantinedPath = Path.Combine(restoreDirectory, $"{quarantineStem}{suffix}");
                File.Move(originalPath, quarantinedPath);
                quarantined.Add((originalPath, quarantinedPath));
            }

            return quarantined;
        }
        catch
        {
            RestoreQuarantinedSidecars(quarantined);
            throw;
        }
    }

    private static Dictionary<string, string> PreserveDatabaseSidecars(
        string databasePath,
        string restoreDirectory)
    {
        var preserved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var originalPath = databasePath + suffix;
            if (!File.Exists(originalPath))
                continue;

            var preservedPath = Path.Combine(restoreDirectory, $"original-current-database.sqlite{suffix}");
            CopyFileAtomically(originalPath, preservedPath);
            preserved.Add(suffix, preservedPath);
        }

        return preserved;
    }

    private static void CreateRawPreRestoreArtifact(
        string originalDatabaseCopy,
        IReadOnlyDictionary<string, string> preservedSidecars,
        string rawDatabasePath)
    {
        try
        {
            CopyFileAtomically(originalDatabaseCopy, rawDatabasePath);
            foreach (var (suffix, preservedPath) in preservedSidecars)
                CopyFileAtomically(preservedPath, rawDatabasePath + suffix);
        }
        catch
        {
            TryDeleteFile(rawDatabasePath);
            foreach (var suffix in DatabaseSidecarSuffixes)
                TryDeleteFile(rawDatabasePath + suffix);
            throw;
        }
    }

    private static void RestorePreservedDatabaseSidecars(
        string databasePath,
        IReadOnlyDictionary<string, string> preservedSidecars)
    {
        foreach (var suffix in DatabaseSidecarSuffixes)
        {
            var originalPath = databasePath + suffix;
            if (preservedSidecars.TryGetValue(suffix, out var preservedPath))
                CopyFileAtomically(preservedPath, originalPath, overwrite: true);
            else
                File.Delete(originalPath);
        }
    }

    private static void RestoreQuarantinedSidecars(
        IEnumerable<(string OriginalPath, string QuarantinedPath)> quarantinedSidecars)
    {
        foreach (var (originalPath, quarantinedPath) in quarantinedSidecars.Reverse())
        {
            if (File.Exists(quarantinedPath))
                File.Move(quarantinedPath, originalPath);
        }
    }

    private static void RestoreSidecarsFromCopies(
        IEnumerable<(string OriginalPath, string QuarantinedPath)> quarantinedSidecars)
    {
        foreach (var (originalPath, quarantinedPath) in quarantinedSidecars)
        {
            if (!File.Exists(quarantinedPath))
                throw new IOException($"The original SQLite sidecar '{quarantinedPath}' is missing.");
            CopyFileAtomically(quarantinedPath, originalPath, overwrite: true);
        }
    }

    private static void DeleteDatabaseSidecars(string databasePath)
    {
        foreach (var suffix in DatabaseSidecarSuffixes)
            File.Delete(databasePath + suffix);
    }

    private static void ValidateEntrySize(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length <= 0 || entry.Length > maximumBytes)
            throw new InvalidDataException("Backup entry size is invalid.");
    }

    private static void ValidateSqliteDatabase(string databasePath)
    {
        if (!HasSqliteHeader(databasePath))
            throw new InvalidDataException("Backup database is not a SQLite database.");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA quick_check";
            var result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup database integrity check failed.");
        }

        SqliteStorageSchemaValidator.Validate(connection, requireDefaultState: true);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CASE
                    WHEN length(CAST(json AS BLOB)) <= $maximumBytes THEN json
                    ELSE NULL
                END
                FROM app_state
                WHERE id = 'default'
                """;
            command.Parameters.AddWithValue(
                "$maximumBytes",
                PlannerDataLimits.MaxPersistedStateJsonBytes);
            var json = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("Backup application state is missing or exceeds the safe size limit.");

            try
            {
                using var guardedJson = JsonInputGuard.ParseDocument(json);
                var dto = guardedJson.RootElement.Deserialize<PlannerDocumentDto>(JsonDefaults.Options);
                if (dto is null || !string.Equals(dto.SchemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
                    throw new InvalidDataException("Backup application state schema is invalid.");
                _ = PlannerDocumentPersistenceValidator.ToValidatedDomain(dto);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or
                                               RepositoryStateValidationException)
            {
                throw new InvalidDataException("Backup application state is invalid.", exception);
            }
        }
    }

    private static bool HasSqliteHeader(string databasePath)
    {
        using var stream = File.OpenRead(databasePath);
        Span<byte> header = stackalloc byte[16];
        return stream.Read(header) == header.Length &&
               header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("The destination path has no parent directory.", nameof(destinationPath));
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".course-planner-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed record DatabaseReplacementState(
        string DatabasePath,
        string RestoreDirectory,
        bool OriginalDatabaseExisted,
        bool OriginalDatabaseWasValid,
        string? RollbackDatabasePath,
        IReadOnlyList<(string OriginalPath, string QuarantinedPath)> OriginalSidecars);
}

public static class LogFileService
{
    public static string WriteTextLog(string logsDirectory, IReadOnlyList<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(lines);
        Directory.CreateDirectory(logsDirectory);
        var path = Path.Combine(
            logsDirectory,
            $"course-planner-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return path;
    }
}

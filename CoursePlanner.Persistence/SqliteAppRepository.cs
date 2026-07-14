using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoursePlanner.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CoursePlanner.Persistence;

public sealed class SqliteAppRepository
{
    private const string StateId = "default";
    private const string InitialDatabaseTemporaryPrefix = ".course-planner-initialize-";
    private const string InitialDatabaseTemporaryExtension = ".sqlite";
    private const string InitialDatabaseLockFileName = ".course-planner-initialize.lock";
    private static readonly TimeSpan InitialDatabaseLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialDatabaseLockRetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly string[] SqliteSidecarSuffixes = ["-wal", "-shm", "-journal"];
    public const long DefaultMaximumStateJsonBytes = PlannerDataLimits.MaxPersistedStateJsonBytes;
    public const int MaximumEventRows = 1_000;
    public const int MaximumEventLevelLength = 64;
    public const int MaximumEventNameLength = 256;
    public const int MaximumEventMessageLength = 16_384;
    private const int MaximumSchemaProbeCharacters = 64 * 1024;

    private readonly Func<PlannerDocument> _seedFactory;
    private readonly string _recoveryDirectory;
    private readonly long _maximumStateJsonBytes;

    public SqliteAppRepository(
        string dataDirectory,
        Func<PlannerDocument>? seedFactory = null,
        string? recoveryDirectory = null,
        long maximumStateJsonBytes = DefaultMaximumStateJsonBytes)
    {
        if (maximumStateJsonBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumStateJsonBytes));

        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(dataDirectory, "course-planner.sqlite");
        LogsDirectory = Path.Combine(dataDirectory, "logs");
        _recoveryDirectory = recoveryDirectory ?? Path.Combine(dataDirectory, "recovery");
        _maximumStateJsonBytes = maximumStateJsonBytes;
        _seedFactory = seedFactory ?? SeedData.Create;
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string RecoveryDirectory => _recoveryDirectory;
    public long MaximumStateJsonBytes => _maximumStateJsonBytes;
    public string? LastRecoveryArtifactPath { get; private set; }

    public void Initialize()
    {
        ValidateStoredSchemaBeforeMutableInitialization();
        Batteries_V2.Init();
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(DatabasePath))
            CreateInitialDatabaseAtomically();
        DeleteInterruptedInitializationArtifactsAfterPublication();
        Directory.CreateDirectory(LogsDirectory);
    }

    private void CreateInitialDatabaseAtomically()
    {
        using var initializationLock = AcquireInitialDatabaseLock(
            stopWaitingWhenDatabaseIsPublished: true);
        if (initializationLock is null)
        {
            ValidateStoredSchemaBeforeMutableInitialization();
            return;
        }

        if (File.Exists(DatabasePath))
        {
            ValidateStoredSchemaBeforeMutableInitialization();
            return;
        }

        DeleteInterruptedInitializationArtifacts();
        var temporaryDatabasePath = Path.Combine(
            DataDirectory,
            $"{InitialDatabaseTemporaryPrefix}{Guid.NewGuid():N}{InitialDatabaseTemporaryExtension}");
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                SqliteStorageSchemaValidator.EnsureCreated(connection);
                SqliteStorageSchemaValidator.Validate(connection, requireDefaultState: false);
            }

            try
            {
                File.Move(temporaryDatabasePath, DatabasePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(DatabasePath))
            {
                // Another initializer won the atomic publication race. Never
                // assume its file is usable merely because it exists.
                ValidateStoredSchemaBeforeMutableInitialization();
            }
        }
        finally
        {
            DeleteInitializationDatabaseArtifacts(temporaryDatabasePath, ignoreFailures: true);
        }
    }

    private FileStream? AcquireInitialDatabaseLock(bool stopWaitingWhenDatabaseIsPublished)
    {
        var lockPath = Path.Combine(DataDirectory, InitialDatabaseLockFileName);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (stopWaitingWhenDatabaseIsPublished && File.Exists(DatabasePath))
                return null;

            try
            {
                return new FileStream(
                    lockPath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.DeleteOnClose
                    });
            }
            catch (IOException) when (elapsed.Elapsed < InitialDatabaseLockTimeout)
            {
                Thread.Sleep(InitialDatabaseLockRetryDelay);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    $"Timed out waiting to coordinate SQLite database initialization for '{DatabasePath}'.",
                    exception);
            }
        }
    }

    private void DeleteInterruptedInitializationArtifactsAfterPublication()
    {
        if (!HasInterruptedInitializationArtifacts())
            return;

        using var cleanupLock = AcquireInitialDatabaseLock(
            stopWaitingWhenDatabaseIsPublished: false)
            ?? throw new InvalidOperationException("The initialization cleanup lock was not acquired.");
        DeleteInterruptedInitializationArtifacts();
    }

    private bool HasInterruptedInitializationArtifacts()
    {
        foreach (var artifactPath in Directory.EnumerateFiles(
                     DataDirectory,
                     $"{InitialDatabaseTemporaryPrefix}*"))
        {
            if (TryGetInitializationDatabasePath(artifactPath, out _))
                return true;
        }

        return false;
    }

    private void DeleteInterruptedInitializationArtifacts()
    {
        var temporaryDatabasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifactPath in Directory.EnumerateFiles(
                     DataDirectory,
                     $"{InitialDatabaseTemporaryPrefix}*"))
        {
            if (TryGetInitializationDatabasePath(artifactPath, out var temporaryDatabasePath))
                temporaryDatabasePaths.Add(temporaryDatabasePath);
        }

        foreach (var temporaryDatabasePath in temporaryDatabasePaths)
            DeleteInitializationDatabaseArtifacts(temporaryDatabasePath, ignoreFailures: false);
    }

    private static bool TryGetInitializationDatabasePath(
        string artifactPath,
        out string temporaryDatabasePath)
    {
        temporaryDatabasePath = "";
        var fileName = Path.GetFileName(artifactPath);
        if (!fileName.StartsWith(InitialDatabaseTemporaryPrefix, StringComparison.Ordinal))
            return false;

        var remainder = fileName[InitialDatabaseTemporaryPrefix.Length..];
        if (remainder.Length < 32 ||
            !Guid.TryParseExact(remainder[..32], "N", out var identifier))
        {
            return false;
        }

        var suffix = remainder[32..];
        if (!string.Equals(suffix, InitialDatabaseTemporaryExtension, StringComparison.Ordinal) &&
            !SqliteSidecarSuffixes.Any(sidecarSuffix => string.Equals(
                suffix,
                InitialDatabaseTemporaryExtension + sidecarSuffix,
                StringComparison.Ordinal)))
        {
            return false;
        }

        temporaryDatabasePath = Path.Combine(
            Path.GetDirectoryName(artifactPath)!,
            $"{InitialDatabaseTemporaryPrefix}{identifier:N}{InitialDatabaseTemporaryExtension}");
        return true;
    }

    private static void DeleteInitializationDatabaseArtifacts(
        string temporaryDatabasePath,
        bool ignoreFailures)
    {
        foreach (var path in SqliteSidecarSuffixes
                     .Select(suffix => temporaryDatabasePath + suffix)
                     .Prepend(temporaryDatabasePath))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (ignoreFailures &&
                                               exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public PlannerDocument LoadOrCreate()
    {
        LastRecoveryArtifactPath = null;
        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var state = ReadState(connection, transaction);
        if (!state.Exists)
        {
            var seeded = CreateSeed();
            PersistDocument(
                seeded,
                connection,
                transaction,
                "Info",
                "seed",
                "Application state created from seed data.");
            transaction.Commit();
            return seeded;
        }

        if (state.Json is null)
        {
            return RecoverFromUnreadableState(
                connection,
                transaction,
                $"State JSON exceeds the configured size limit of {_maximumStateJsonBytes} bytes " +
                $"(stored size: {state.ByteLength} bytes). ");
        }

        string schemaVersion;
        try
        {
            schemaVersion = ReadSchemaVersion(state.Json);
        }
        catch (DuplicateJsonPropertyException)
        {
            CreateRecoveryArtifact(connection, transaction);
            throw new RepositoryStateValidationException(
                ["Document.Json.DuplicateProperty"],
                wasTruncated: false);
        }
        catch (Exception exception) when (IsStateContentException(exception))
        {
            return RecoverFromUnreadableState(
                connection,
                transaction,
                $"Malformed state JSON: {exception.GetType().Name}: {exception.Message}");
        }

        if (!string.Equals(schemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
            throw UnsupportedSchemaException();

        try
        {
            var document = DeserializeDocument(state.Json);
            transaction.Commit();
            return document;
        }
        catch (Exception exception) when (IsStateContentException(exception))
        {
            return RecoverFromUnreadableState(
                connection,
                transaction,
                $"Malformed current-schema state: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads the currently published application state without initializing,
    /// repairing, recovering, or otherwise mutating the repository. This is
    /// intentionally stricter than <see cref="LoadOrCreate"/> so callers can
    /// use it as evidence when a write reports an ambiguous outcome.
    /// </summary>
    public PlannerDocument LoadExistingForVerification()
    {
        Batteries_V2.Init();
        if (!File.Exists(DatabasePath))
        {
            throw new FileNotFoundException(
                "The planner database does not exist.",
                DatabasePath);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        SqliteStorageSchemaValidator.Validate(connection, requireDefaultState: true);

        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction);
        if (!state.Exists)
        {
            throw new RepositoryStateValidationException(
                ["Document.State.Missing"],
                wasTruncated: false);
        }
        if (state.Json is null)
        {
            throw new RepositoryStateValidationException(
                ["Document.SerializedSize.TooLarge"],
                wasTruncated: false);
        }

        string schemaVersion;
        try
        {
            schemaVersion = ReadSchemaVersion(state.Json);
        }
        catch (DuplicateJsonPropertyException)
        {
            throw new RepositoryStateValidationException(
                ["Document.Json.DuplicateProperty"],
                wasTruncated: false);
        }

        if (!string.Equals(schemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
            throw UnsupportedSchemaException();

        return DeserializeDocument(state.Json);
    }

    public void Save(PlannerDocument document, string eventName = "save")
    {
        var json = SerializeValidatedDocument(document);
        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        PersistSerializedDocument(
            json,
            connection,
            transaction,
            "Info",
            eventName,
            "Application state saved.");
        transaction.Commit();
    }

    private void PersistDocument(
        PlannerDocument document,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string level,
        string eventName,
        string message)
    {
        var json = SerializeValidatedDocument(document);
        PersistSerializedDocument(json, connection, transaction, level, eventName, message);
    }

    private string SerializeValidatedDocument(PlannerDocument document)
    {
        PlannerDocumentPersistenceValidator.ValidateForPersistence(document);
        var json = JsonSerializer.Serialize(PersistenceDocumentMapper.ToDto(document), JsonDefaults.CompactOptions);
        if (json.Length > _maximumStateJsonBytes ||
            Encoding.UTF8.GetByteCount(json) > _maximumStateJsonBytes)
        {
            throw new RepositoryStateValidationException(
                ["Document.SerializedSize.TooLarge"],
                wasTruncated: false);
        }
        return json;
    }

    private static void PersistSerializedDocument(
        string json,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string level,
        string eventName,
        string message)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_state (id, json, updated_at)
                VALUES ($id, $json, $updatedAt)
                ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", StateId);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        AppendEvent(connection, transaction, level, eventName, message);
        PruneEvents(connection, transaction);
    }

    public void Log(string level, string eventName, string message)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(message);
        Initialize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        AppendEvent(connection, transaction, level, eventName, message);
        PruneEvents(connection, transaction);
        transaction.Commit();
    }

    public IReadOnlyList<string> ReadEventSummaries(int limit = 200)
    {
        if (limit < 0 || limit > MaximumEventRows)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (limit == 0)
            return Array.Empty<string>();

        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                occurred_at,
                substr(level, 1, $maximumLevelLength),
                substr(event_name, 1, $maximumEventNameLength),
                substr(message, 1, $maximumMessageLength)
            FROM app_events
            ORDER BY id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$maximumLevelLength", MaximumEventLevelLength);
        command.Parameters.AddWithValue("$maximumEventNameLength", MaximumEventNameLength);
        command.Parameters.AddWithValue("$maximumMessageLength", MaximumEventMessageLength);
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
            lines.Add($"{reader.GetString(0)} [{reader.GetString(1)}] {reader.GetString(2)} - {reader.GetString(3)}");
        return lines;
    }

    public void ClearLogs()
    {
        Initialize();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_events";
        command.ExecuteNonQuery();
    }

    public string CopyDatabaseBackup(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        Initialize();
        Directory.CreateDirectory(targetDirectory);
        var backupPath = Path.Combine(
            targetDirectory,
            $"course-planner-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.sqlite");
        var temporaryPath = Path.Combine(targetDirectory, $".database-backup-{Guid.NewGuid():N}.tmp");
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        try
        {
            using (var source = OpenConnection())
            using (var destination = new SqliteConnection(destinationBuilder.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            File.Move(temporaryPath, backupPath);
            return backupPath;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AppendEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string level,
        string eventName,
        string message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_events (occurred_at, level, event_name, message)
            VALUES ($at, $level, $event, $message)
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue(
            "$level",
            TextRules.TruncateUtf16(level, MaximumEventLevelLength));
        command.Parameters.AddWithValue(
            "$event",
            TextRules.TruncateUtf16(eventName, MaximumEventNameLength));
        command.Parameters.AddWithValue(
            "$message",
            TextRules.TruncateUtf16(message, MaximumEventMessageLength));
        command.ExecuteNonQuery();
    }

    private static void PruneEvents(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM app_events
            WHERE id <= (
                SELECT id
                FROM app_events
                ORDER BY id DESC
                LIMIT 1 OFFSET $maximumRows
            )
            """;
        command.Parameters.AddWithValue("$maximumRows", MaximumEventRows);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = DatabasePath };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private void ValidateStoredSchemaBeforeMutableInitialization()
    {
        if (!File.Exists(DatabasePath))
            return;

        try
        {
            ValidateStoredSchemaBeforeMutableInitializationCore();
        }
        catch (RepositoryStateValidationException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw UnsupportedStorageSchemaException();
        }
        catch (SqliteException)
        {
            throw UnsupportedStorageSchemaException();
        }
    }

    private void ValidateStoredSchemaBeforeMutableInitializationCore()
    {
        Batteries_V2.Init();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        SqliteStorageSchemaValidator.Validate(connection, requireDefaultState: false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                length(CAST(json AS BLOB)),
                CASE
                    WHEN length(CAST(json AS BLOB)) <= $maximumBytes THEN json
                    ELSE substr(json, 1, $probeCharacters)
                END
            FROM app_state
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$maximumBytes", _maximumStateJsonBytes);
        command.Parameters.AddWithValue("$probeCharacters", MaximumSchemaProbeCharacters);
        command.Parameters.AddWithValue("$id", StateId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
            return;

        var byteLength = reader.GetInt64(0);
        var jsonOrPrefix = reader.GetString(1);
        if (byteLength <= _maximumStateJsonBytes)
        {
            try
            {
                var schemaVersion = ReadSchemaVersion(jsonOrPrefix);
                if (!string.Equals(schemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
                    throw UnsupportedSchemaException();
            }
            catch (DuplicateJsonPropertyException)
            {
                throw new RepositoryStateValidationException(
                    ["Document.Json.DuplicateProperty"],
                    wasTruncated: false);
            }
            catch (RepositoryStateValidationException)
            {
                throw;
            }
            catch (JsonException)
            {
                // Malformed current-schema data follows the existing diagnostic
                // recovery path. A well-formed different schema is rejected above
                // before Initialize can prune logs or otherwise mutate the database.
            }
            return;
        }

        if (ProbeSchemaVersion(jsonOrPrefix) != SchemaProbeResult.Current)
            throw UnsupportedSchemaException();
    }

    private static SchemaProbeResult ProbeSchemaVersion(string jsonPrefix)
    {
        try
        {
            var utf8 = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetBytes(jsonPrefix);
            var reader = new Utf8JsonReader(
                utf8,
                isFinalBlock: false,
                new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 }));
            string? schemaVersion = null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    reader.CurrentDepth != 1 ||
                    !reader.ValueTextEquals("schemaVersion"u8))
                {
                    continue;
                }

                if (schemaVersion is not null)
                    return SchemaProbeResult.Duplicate;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    return SchemaProbeResult.Unknown;
                schemaVersion = reader.GetString();
            }

            if (schemaVersion is null)
                return SchemaProbeResult.Unknown;
            return string.Equals(schemaVersion, PlannerSchemas.Current, StringComparison.Ordinal)
                ? SchemaProbeResult.Current
                : SchemaProbeResult.Unsupported;
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException)
        {
            return SchemaProbeResult.Unknown;
        }
    }

    private static RepositoryStateValidationException UnsupportedSchemaException() =>
        new(["Document.SchemaVersion.Unsupported"], wasTruncated: false);

    private static RepositoryStateValidationException UnsupportedStorageSchemaException() =>
        new(["Document.StorageSchema.Unsupported"], wasTruncated: false);

    private StateReadResult ReadState(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                length(CAST(json AS BLOB)),
                CASE
                    WHEN length(CAST(json AS BLOB)) <= $maximumBytes THEN json
                    ELSE NULL
                END
            FROM app_state
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$maximumBytes", _maximumStateJsonBytes);
        command.Parameters.AddWithValue("$id", StateId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new StateReadResult(false, 0, null);

        var byteLength = reader.GetInt64(0);
        var json = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new StateReadResult(true, byteLength, json);
    }

    private PlannerDocument RecoverFromUnreadableState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string reason,
        string? existingArtifactPath = null)
    {
        var artifactPath = existingArtifactPath ?? CreateRecoveryArtifact(connection, transaction);
        var seeded = CreateSeed();
        PersistDocument(
            seeded,
            connection,
            transaction,
            "Error",
            "state-recovery",
            $"Application state was not loaded. {reason} " +
            $"Original JSON recovery artifact: {artifactPath}. A new seed state was created.");
        transaction.Commit();
        return seeded;
    }

    private string CreateRecoveryArtifact(SqliteConnection connection, SqliteTransaction transaction)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_recoveryDirectory);
            var uniqueSuffix = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
            var finalPath = Path.Combine(_recoveryDirectory, $"app-state-{uniqueSuffix}.json");
            temporaryPath = Path.Combine(_recoveryDirectory, $".app-state-{uniqueSuffix}.tmp");

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT CAST(json AS BLOB) FROM app_state WHERE id = $id";
                command.Parameters.AddWithValue("$id", StateId);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                    throw new IOException("The application state disappeared before it could be archived.");

                using var source = reader.GetStream(0);
                using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.WriteThrough);
                source.CopyTo(destination, 64 * 1024);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath);
            temporaryPath = null;
            LastRecoveryArtifactPath = finalPath;
            return finalPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            LastRecoveryArtifactPath = null;
            throw new RepositoryRecoveryException(
                $"Unable to create an atomic application-state recovery artifact in '{_recoveryDirectory}'. " +
                "The stored application state was left unchanged.",
                _recoveryDirectory,
                exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // The original exception is more useful and the final artifact was never published.
                }
            }
        }
    }

    private PlannerDocument CreateSeed()
    {
        var seeded = _seedFactory();
        seeded.SchemaVersion = PlannerSchemas.Current;
        return seeded;
    }

    private static PlannerDocument DeserializeDocument(string json)
    {
        var dto = JsonSerializer.Deserialize<PlannerDocumentDto>(json, JsonDefaults.Options)
            ?? throw new JsonException("The application state document is null.");
        if (!string.Equals(dto.SchemaVersion, PlannerSchemas.Current, StringComparison.Ordinal))
            throw new JsonException("The deserialized schema version does not match the current schema.");
        return PlannerDocumentPersistenceValidator.ToValidatedDomain(dto);
    }

    private static string ReadSchemaVersion(string json)
    {
        using var parsed = JsonInputGuard.ParseDocument(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            !parsed.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("The state JSON does not contain a string schemaVersion property.");
        }

        var schemaVersion = schemaElement.GetString();
        if (string.IsNullOrWhiteSpace(schemaVersion))
            throw new JsonException("The state JSON contains an empty schemaVersion property.");
        if (schemaVersion.Length > PlannerDataLimits.MaxSchemaVersionLength)
        {
            throw new RepositoryStateValidationException(
                ["Document.SchemaVersion.TooLong"],
                wasTruncated: false);
        }
        return schemaVersion;
    }

    private static bool IsStateContentException(Exception exception) => exception is
        RepositoryStateValidationException or
        JsonException or
        FormatException or
        OverflowException;

    private readonly record struct StateReadResult(bool Exists, long ByteLength, string? Json);

    private enum SchemaProbeResult
    {
        Unknown,
        Current,
        Unsupported,
        Duplicate
    }
}

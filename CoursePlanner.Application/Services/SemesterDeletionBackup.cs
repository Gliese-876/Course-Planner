using CoursePlanner.Persistence;

namespace CoursePlanner.Services;

public interface ISemesterDeletionBackup
{
    string Create(string databasePath, string dataDirectory);
}

public sealed class SemesterDeletionBackupException : IOException
{
    public SemesterDeletionBackupException(Exception innerException)
        : base("The automatic backup required before semester deletion failed.", innerException)
    {
    }
}

public sealed class SemesterDeletionBackup : ISemesterDeletionBackup
{
    public string Create(string databasePath, string dataDirectory)
    {
        var path = AutomaticBackupPathFactory.BeforeSemesterDeletion(
            dataDirectory,
            DateTimeOffset.UtcNow,
            Guid.NewGuid());
        BackupService.CreateBackup(databasePath, path);
        return path;
    }
}

public static class AutomaticBackupPathFactory
{
    public static string BeforeSemesterDeletion(
        string dataDirectory,
        DateTimeOffset timestamp,
        Guid nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (nonce == Guid.Empty)
            throw new ArgumentException("A non-empty nonce is required.", nameof(nonce));

        var automaticBackupDirectory = Path.Combine(dataDirectory, "automatic-backups");
        var utc = timestamp.ToUniversalTime();
        var fileName = $"before-delete-semester-{utc:yyyyMMdd-HHmmss-fff}-{nonce:N}.zip";
        return Path.Combine(automaticBackupDirectory, fileName);
    }
}

public static class SemesterDeletionBackupFailure
{
    public static bool IsExpected(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        InvalidDataException;
}

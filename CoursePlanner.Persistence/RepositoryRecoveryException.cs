namespace CoursePlanner.Persistence;

public sealed class RepositoryRecoveryException : Exception
{
    public RepositoryRecoveryException(string message, string recoveryDirectory, Exception innerException)
        : base(message, innerException)
    {
        RecoveryDirectory = recoveryDirectory;
    }

    public string RecoveryDirectory { get; }
}

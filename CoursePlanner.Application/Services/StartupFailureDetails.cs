using CoursePlanner.Persistence;

namespace CoursePlanner.Services;

public sealed record StartupFailureDetails(string Title, string Summary, string TechnicalDetails)
{
    private const int MaxErrorMessageLength = 4_096;

    public static StartupFailureDetails Create(Exception exception, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var safeDataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? "(unavailable)"
            : dataDirectory;
        var recoveryException = FindException<RepositoryRecoveryException>(exception);
        var recoveryDirectory = recoveryException?.RecoveryDirectory ??
                                Path.Combine(safeDataDirectory, "recovery");
        var safetyText = recoveryException is null
            ? "Startup stopped. The error presentation did not attempt a reset or another seed write."
            : "The repository left the stored application state unchanged because a safe recovery artifact could not be created.";
        var errorText = $"{exception.GetType().FullName}: {exception.Message}";
        if (errorText.Length > MaxErrorMessageLength)
            errorText = errorText[..MaxErrorMessageLength] + "…";

        return new StartupFailureDetails(
            "Course Planner could not start",
            "The application could not open its data safely. Your existing files were not silently reset.",
            $"Data folder: {safeDataDirectory}{Environment.NewLine}" +
            $"Recovery folder: {recoveryDirectory}{Environment.NewLine}" +
            $"Data safety: {safetyText}{Environment.NewLine}" +
            $"Error: {errorText}");
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Exception? current = exception;
        while (current is not null && visited.Add(current))
        {
            if (current is TException typed)
                return typed;
            current = current.InnerException;
        }
        return null;
    }
}

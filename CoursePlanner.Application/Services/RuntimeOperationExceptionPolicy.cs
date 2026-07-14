using System.Data.Common;
using System.Runtime.InteropServices;
using CoursePlanner.Persistence;

namespace CoursePlanner.Services;

/// <summary>
/// Defines the narrow set of operational failures for which the WinUI shell
/// can remain usable after an exception crosses an async-void event boundary.
/// Programming errors and process-corrupting failures must keep their default
/// unhandled behavior.
/// </summary>
public static class RuntimeOperationExceptionPolicy
{
    private static readonly HashSet<int> RecoverableStorageHResults =
    [
        unchecked((int)0x80070002), // ERROR_FILE_NOT_FOUND
        unchecked((int)0x80070003), // ERROR_PATH_NOT_FOUND
        unchecked((int)0x80070005), // ERROR_ACCESS_DENIED
        unchecked((int)0x80070020), // ERROR_SHARING_VIOLATION
        unchecked((int)0x80070070)  // ERROR_DISK_FULL
    ];

    public static bool IsRecoverable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RepositoryStateValidationException or RepositoryRecoveryException or InvalidDataException)
            return true;

        return exception is IOException or UnauthorizedAccessException or DbException ||
               exception is COMException comException &&
               RecoverableStorageHResults.Contains(comException.HResult);
    }

    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsDirectFatal(exception))
            return true;
        if (exception is not AggregateException && exception.InnerException is null)
            return false;

        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            if (IsDirectFatal(current))
                return true;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }

    private static bool IsDirectFatal(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException or
        BadImageFormatException or
        SEHException;
}

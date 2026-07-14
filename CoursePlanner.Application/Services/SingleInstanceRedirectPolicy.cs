using System.Runtime.InteropServices;

namespace CoursePlanner.Services;

public static class SingleInstanceRedirectPolicy
{
    // Long enough for a busy primary process, but finite so a broken lifecycle
    // broker can never strand the redirecting process indefinitely.
    public const uint TimeoutMilliseconds = 15_000;
    private const int RpcSCallPending = unchecked((int)0x80010115);

    public static bool IsTimeout(int waitResult) => waitResult == RpcSCallPending;

    public static bool IsOperationalFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is COMException or IOException or UnauthorizedAccessException;
    }

    public static bool IsForegroundFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // GetProcessById reports a process that has already disappeared with
        // ArgumentException. MainWindowHandle reports the same race after the
        // Process object was created with InvalidOperationException.
        return exception is ArgumentException or InvalidOperationException;
    }
}

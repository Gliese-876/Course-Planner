namespace CoursePlanner.Services;

/// <summary>
/// Owns the replaceable lifetime of a transient notification. A newer
/// notification cancels the old countdown so stale work can never dismiss the
/// current message.
/// </summary>
public sealed class TransientNotificationLifetime : IDisposable
{
    public static TimeSpan DefaultDisplayDuration { get; } = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly TimeSpan _displayDuration;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _current;
    private bool _disposed;

    public TransientNotificationLifetime(
        TimeSpan? displayDuration = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _displayDuration = displayDuration ?? DefaultDisplayDuration;
        if (_displayDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(displayDuration), "The display duration must be positive.");

        _delay = delay ?? Task.Delay;
    }

    public CancellationToken Restart()
    {
        CancellationTokenSource? previous;
        CancellationToken token;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = new CancellationTokenSource();
            token = _current.Token;
        }

        CancelAndDispose(previous);
        return token;
    }

    public async Task<bool> WaitForExpiryAsync(CancellationToken token)
    {
        try
        {
            await _delay(_displayDuration, token).ConfigureAwait(false);
            return IsCurrent(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    public bool IsCurrent(CancellationToken token)
    {
        lock (_sync)
        {
            return !_disposed &&
                   _current is not null &&
                   _current.Token == token &&
                   !token.IsCancellationRequested;
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }

        CancelAndDispose(current);
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            current = _current;
            _current = null;
        }

        CancelAndDispose(current);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

namespace CoursePlanner.Services;

public sealed class LatestRequestTracker : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _currentCancellation;
    private long _version;
    private bool _disposed;

    public LatestRequest Begin()
    {
        CancellationTokenSource? previous;
        LatestRequest request;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var version = checked(_version + 1);
            previous = _currentCancellation;
            var cancellation = new CancellationTokenSource();
            _currentCancellation = cancellation;
            _version = version;
            request = new LatestRequest(this, version, cancellation);
        }

        CancelAndDispose(previous);
        return request;
    }

    public bool TryExecuteIfCurrent(LatestRequest request, Action action)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            if (_disposed ||
                !ReferenceEquals(request.Owner, this) ||
                request.Version != _version ||
                !ReferenceEquals(request.Cancellation, _currentCancellation) ||
                request.Token.IsCancellationRequested)
            {
                return false;
            }

            action();
            return true;
        }
    }

    internal void Release(LatestRequest request)
    {
        CancellationTokenSource? released = null;
        lock (_sync)
        {
            if (ReferenceEquals(request.Cancellation, _currentCancellation))
            {
                _currentCancellation = null;
                released = request.Cancellation;
            }
        }
        released?.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            current = _currentCancellation;
            _currentCancellation = null;
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

public sealed class LatestRequest : IDisposable
{
    private int _disposed;

    internal LatestRequest(
        LatestRequestTracker owner,
        long version,
        CancellationTokenSource cancellation)
    {
        Owner = owner;
        Version = version;
        Cancellation = cancellation;
        Token = cancellation.Token;
    }

    internal LatestRequestTracker Owner { get; }
    internal CancellationTokenSource Cancellation { get; }
    public long Version { get; }
    public CancellationToken Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Owner.Release(this);
    }
}

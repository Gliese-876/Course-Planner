namespace CoursePlanner.Services;

public sealed class RegistrationOrderWindowLifetime
{
    private readonly object _gate = new();
    private LifetimeState _state;

    public bool IsClosing
    {
        get
        {
            lock (_gate)
                return _state == LifetimeState.Closing;
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_gate)
                return _state == LifetimeState.Closed;
        }
    }

    public bool AcceptsInteraction
    {
        get
        {
            lock (_gate)
                return _state == LifetimeState.Open;
        }
    }

    public bool TryBeginClose()
    {
        lock (_gate)
        {
            if (_state != LifetimeState.Open)
                return false;

            _state = LifetimeState.Closing;
            return true;
        }
    }

    public void CancelClose()
    {
        lock (_gate)
        {
            if (_state == LifetimeState.Closing)
                _state = LifetimeState.Open;
        }
    }

    public void CompleteClose()
    {
        lock (_gate)
            _state = LifetimeState.Closed;
    }

    public bool TryRunDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            if (_state != LifetimeState.Open)
                return false;

            action();
            return true;
        }
    }

    private enum LifetimeState
    {
        Open,
        Closing,
        Closed
    }
}

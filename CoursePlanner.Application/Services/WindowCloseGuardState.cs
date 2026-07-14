namespace CoursePlanner.Services;

public enum WindowCloseGuardPhase
{
    Idle,
    Resolving,
    Approved,
    Released
}

public readonly record struct WindowCloseInterception(bool Cancel, bool StartResolution);

/// <summary>
/// Coordinates the synchronous native close interception with one asynchronous
/// save/discard decision. The state is thread-safe so repeated system close
/// signals cannot open duplicate dialogs or release more than one close path.
/// </summary>
public sealed class WindowCloseGuardState
{
    private readonly object _gate = new();
    private WindowCloseGuardPhase _phase;

    public WindowCloseGuardPhase Phase
    {
        get
        {
            lock (_gate)
                return _phase;
        }
    }

    public WindowCloseInterception InterceptClose()
    {
        lock (_gate)
        {
            if (_phase is WindowCloseGuardPhase.Approved or WindowCloseGuardPhase.Released)
            {
                _phase = WindowCloseGuardPhase.Released;
                return new WindowCloseInterception(Cancel: false, StartResolution: false);
            }

            if (_phase == WindowCloseGuardPhase.Resolving)
                return new WindowCloseInterception(Cancel: true, StartResolution: false);

            _phase = WindowCloseGuardPhase.Resolving;
            return new WindowCloseInterception(Cancel: true, StartResolution: true);
        }
    }

    public bool ApproveResolution()
    {
        lock (_gate)
        {
            if (_phase != WindowCloseGuardPhase.Resolving)
                return false;

            _phase = WindowCloseGuardPhase.Approved;
            return true;
        }
    }

    public void RejectResolution()
    {
        lock (_gate)
        {
            if (_phase is WindowCloseGuardPhase.Resolving or WindowCloseGuardPhase.Approved)
                _phase = WindowCloseGuardPhase.Idle;
        }
    }
}

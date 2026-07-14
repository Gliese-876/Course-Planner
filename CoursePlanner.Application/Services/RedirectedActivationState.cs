namespace CoursePlanner.Services;

/// <summary>
/// Coalesces redirected activation signals that can arrive after the process
/// has registered its single-instance key but before WinUI creates the window.
/// </summary>
public sealed class RedirectedActivationState
{
    private readonly object _gate = new();
    private bool _windowReady;
    private bool _activationPending;

    /// <returns>
    /// <see langword="true"/> when the window is already ready and the caller
    /// should dispatch activation immediately; otherwise the request is queued.
    /// </returns>
    public bool RequestActivation()
    {
        lock (_gate)
        {
            if (_windowReady)
                return true;

            _activationPending = true;
            return false;
        }
    }

    /// <returns>
    /// <see langword="true"/> exactly when at least one early activation was
    /// queued and must now be dispatched to the newly created window.
    /// </returns>
    public bool MarkWindowReady()
    {
        lock (_gate)
        {
            if (_windowReady)
                return false;

            _windowReady = true;
            var dispatchPending = _activationPending;
            _activationPending = false;
            return dispatchPending;
        }
    }
}

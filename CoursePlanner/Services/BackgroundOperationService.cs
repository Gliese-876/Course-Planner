namespace CoursePlanner.Services;

public sealed class BackgroundOperationService
{
    private int _isBusy;
    private string _message = "";

    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;
    public string Message => Volatile.Read(ref _message);

    public event EventHandler? Changed;

    public async Task<bool> RunAsync(string message, Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
            return false;

        Volatile.Write(ref _message, message ?? "");
        try
        {
            NotifyChanged();
            await operation();
            return true;
        }
        finally
        {
            Volatile.Write(ref _message, "");
            Interlocked.Exchange(ref _isBusy, 0);
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        var subscribers = Changed;
        if (subscribers is null)
            return;

        // Busy-state subscribers are presentation projections. One broken
        // projection must neither control the operation nor starve the other
        // windows of the state transition.
        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
            }
        }
    }
}

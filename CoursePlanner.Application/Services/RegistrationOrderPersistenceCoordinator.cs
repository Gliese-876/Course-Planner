namespace CoursePlanner.Services;

public readonly record struct RegistrationOrderPersistenceTicket(long Value);

/// <summary>
/// Coordinates a single low-priority registration-order save without depending on
/// a UI dispatcher. A reference-identity generation prevents a delayed callback
/// from writing an order into a replaced or deleted plan.
/// </summary>
public sealed class RegistrationOrderPersistenceCoordinator
{
    private PendingSave? _pending;
    private RegistrationOrderPersistenceTicket? _scheduledTicket;
    private long _nextTicket;
    private bool _isClosed;

    public bool HasPending => _pending is not null;
    public bool IsClosed => _isClosed;

    public RegistrationOrderPersistenceTicket? Queue(
        object planGeneration,
        IReadOnlyList<string> orderedSnapshotIds)
    {
        ArgumentNullException.ThrowIfNull(planGeneration);
        ArgumentNullException.ThrowIfNull(orderedSnapshotIds);

        if (_isClosed)
            return null;

        _pending = new PendingSave(planGeneration, Snapshot(orderedSnapshotIds));
        if (_scheduledTicket is not null)
            return null;

        var ticket = new RegistrationOrderPersistenceTicket(checked(++_nextTicket));
        _scheduledTicket = ticket;
        return ticket;
    }

    public bool CanRetainPending(
        object currentPlanGeneration,
        IReadOnlyList<string> currentSnapshotIds)
    {
        ArgumentNullException.ThrowIfNull(currentPlanGeneration);
        ArgumentNullException.ThrowIfNull(currentSnapshotIds);

        var pending = _pending;
        if (_isClosed ||
            pending is null ||
            !ReferenceEquals(pending.PlanGeneration, currentPlanGeneration) ||
            pending.OrderedSnapshotIds.Count != currentSnapshotIds.Count)
        {
            return false;
        }

        var pendingIds = pending.OrderedSnapshotIds.ToHashSet(StringComparer.Ordinal);
        var currentIds = currentSnapshotIds.ToHashSet(StringComparer.Ordinal);
        return pendingIds.Count == pending.OrderedSnapshotIds.Count &&
               currentIds.Count == currentSnapshotIds.Count &&
               pendingIds.SetEquals(currentIds);
    }

    public void DiscardPending()
    {
        _pending = null;
        _scheduledTicket = null;
    }

    public bool ExecuteScheduled(
        RegistrationOrderPersistenceTicket ticket,
        object? currentPlanGeneration,
        Action<IReadOnlyList<string>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);

        if (_isClosed || _scheduledTicket != ticket)
            return false;

        var pending = TakePending();
        if (pending is null || !ReferenceEquals(pending.PlanGeneration, currentPlanGeneration))
            return true;

        persist(pending.OrderedSnapshotIds);
        return true;
    }

    public void Flush(
        object? currentPlanGeneration,
        Action<IReadOnlyList<string>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);

        if (_isClosed)
            return;

        var pending = TakePending();
        if (pending is null || !ReferenceEquals(pending.PlanGeneration, currentPlanGeneration))
            return;

        persist(pending.OrderedSnapshotIds);
    }

    public void PersistImmediately(
        object planGeneration,
        IReadOnlyList<string> orderedSnapshotIds,
        Action<IReadOnlyList<string>> persist)
    {
        ArgumentNullException.ThrowIfNull(planGeneration);
        ArgumentNullException.ThrowIfNull(orderedSnapshotIds);
        ArgumentNullException.ThrowIfNull(persist);

        if (_isClosed)
            return;

        var immutableOrder = Snapshot(orderedSnapshotIds);
        DiscardPending();
        persist(immutableOrder);
    }

    public void Close(
        object? currentPlanGeneration,
        Action<IReadOnlyList<string>> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);

        if (_isClosed)
            return;

        try
        {
            Flush(currentPlanGeneration, persist);
        }
        finally
        {
            DiscardPending();
            _isClosed = true;
        }
    }

    private PendingSave? TakePending()
    {
        var pending = _pending;
        _pending = null;
        _scheduledTicket = null;
        return pending;
    }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> orderedSnapshotIds) =>
        Array.AsReadOnly(orderedSnapshotIds.ToArray());

    private sealed record PendingSave(
        object PlanGeneration,
        IReadOnlyList<string> OrderedSnapshotIds);
}

using System.Text.Json;

namespace CoursePlanner.Core;

/// <summary>
/// An opaque snapshot owned by an undo/redo state participant. Its membership
/// and topology must be immutable. A snapshot may reference shared monotonic
/// facts (for example, a false-to-true "was modified" latch) when restoring an
/// older snapshot must not roll those facts back.
/// </summary>
public abstract class PlannerUndoRedoState
{
}

/// <summary>
/// Captures and restores session-only state whose lifetime must follow the
/// document history without becoming part of the persisted document schema.
/// Captured state membership must be immutable and may be shared by checkpoints.
/// CaptureState and AreEquivalent must not change observable state.
/// AreEquivalent must be an equivalence relation for every state returned by
/// this participant; equivalent snapshots need not share object identity.
/// </summary>
public interface IPlannerUndoRedoStateParticipant<TState>
    where TState : PlannerUndoRedoState
{
    TState CaptureState(PlannerDocument document);
    bool AreEquivalent(TState left, TState right);
    void RestoreState(TState state);
    void ClearState();
}

public sealed class PlannerUndoRedo
{
    internal interface IStateParticipantAdapter
    {
        PlannerUndoRedoState CaptureState(PlannerDocument document);
        bool AreEquivalent(PlannerUndoRedoState left, PlannerUndoRedoState right);
        void RestoreState(PlannerUndoRedoState state);
        void ClearState();
    }

    private sealed class StateParticipantAdapter<TState>(
        IPlannerUndoRedoStateParticipant<TState> participant) : IStateParticipantAdapter
        where TState : PlannerUndoRedoState
    {
        public PlannerUndoRedoState CaptureState(PlannerDocument document) =>
            participant.CaptureState(document);

        public bool AreEquivalent(PlannerUndoRedoState left, PlannerUndoRedoState right)
        {
            if (left is not TState typedLeft || right is not TState typedRight)
                throw new InvalidOperationException("Undo/redo auxiliary state has an unexpected type.");
            return participant.AreEquivalent(typedLeft, typedRight);
        }

        public void RestoreState(PlannerUndoRedoState state)
        {
            if (state is not TState typedState)
                throw new InvalidOperationException("Undo/redo auxiliary state has an unexpected type.");
            participant.RestoreState(typedState);
        }

        public void ClearState() => participant.ClearState();
    }

    internal sealed record CapturedAuxiliaryState(
        IStateParticipantAdapter Owner,
        PlannerUndoRedoState State);

    internal sealed class HistoryEntry
    {
        private readonly byte[] _json;

        internal HistoryEntry(byte[] json, CapturedAuxiliaryState? auxiliaryState)
        {
            _json = json;
            AuxiliaryState = auxiliaryState;
        }

        internal int Length => _json.Length;
        internal ReadOnlySpan<byte> Bytes => _json;
        private CapturedAuxiliaryState? AuxiliaryState { get; }

        internal bool Matches(HistoryEntry other) =>
            Bytes.SequenceEqual(other.Bytes) &&
            AuxiliaryStatesMatch(AuxiliaryState, other.AuxiliaryState);

        internal CapturedAuxiliaryState? CapturedState => AuxiliaryState;

    }

    public sealed class Checkpoint
    {
        private readonly PlannerUndoRedo _owner;
        private readonly HistoryEntry[] _undoEntries;
        private readonly HistoryEntry[] _redoEntries;
        private readonly CapturedAuxiliaryState? _auxiliaryState;

        internal Checkpoint(
            PlannerUndoRedo owner,
            HistoryEntry[] undoEntries,
            HistoryEntry[] redoEntries,
            CapturedAuxiliaryState? auxiliaryState)
        {
            _owner = owner;
            _undoEntries = undoEntries;
            _redoEntries = redoEntries;
            _auxiliaryState = auxiliaryState;
        }

        internal void RestoreInto(PlannerUndoRedo target)
        {
            target.RestoreAuxiliaryState(_auxiliaryState);
            target.ClearHistories();
            foreach (var entry in _undoEntries)
                target.AddLast(target._undo, entry);
            foreach (var entry in _redoEntries)
                target.AddLast(target._redo, entry);
        }

        internal bool BelongsTo(PlannerUndoRedo owner) => ReferenceEquals(_owner, owner);

        internal CapturedAuxiliaryState? AuxiliaryState => _auxiliaryState;
    }

    public const int MaxHistoryEntries = 50;
    public const int MaxHistoryBytes = 16 * 1024 * 1024;

    private readonly LinkedList<HistoryEntry> _undo = new();
    private readonly LinkedList<HistoryEntry> _redo = new();
    private IStateParticipantAdapter? _stateParticipant;
    private CapturedAuxiliaryState? _initialAuxiliaryState;
    private long _historyBytes;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public long HistoryBytes => _historyBytes;

    /// <summary>
    /// Registers session-only state that must travel with document history and
    /// checkpoints. One participant owns the state type and its restore logic;
    /// the history only retains opaque immutable snapshots.
    /// </summary>
    public void RegisterStateParticipant<TState>(
        IPlannerUndoRedoStateParticipant<TState> participant,
        PlannerDocument currentDocument)
        where TState : PlannerUndoRedoState
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(currentDocument);
        if (_stateParticipant is not null)
            throw new InvalidOperationException("An undo/redo state participant is already registered.");
        if (_undo.Count != 0 || _redo.Count != 0)
            throw new InvalidOperationException("Undo/redo state participants must be registered before history is captured.");

        var adapter = new StateParticipantAdapter<TState>(participant);
        var initialState = adapter.CaptureState(currentDocument) ??
                           throw new InvalidOperationException(
                               "The undo/redo state participant returned null.");
        _stateParticipant = adapter;
        _initialAuxiliaryState = new CapturedAuxiliaryState(adapter, initialState);
    }

    public bool Capture(PlannerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entry = Snapshot(document);
        if (entry.Length > MaxHistoryBytes)
        {
            ClearHistory(_redo);
            ClearHistory(_undo);
            return false;
        }

        var isDuplicate = _undo.Last?.Value is { } previous && previous.Matches(entry);
        ClearHistory(_redo);
        if (isDuplicate)
            return false;

        AddLast(_undo, entry);
        TrimHistory();
        return true;
    }

    public PlannerDocument? Undo(PlannerDocument current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_undo.Count == 0)
            return null;

        var currentEntry = Snapshot(current);
        var duplicateCount = CountDuplicateTop(_undo, currentEntry);
        var target = EntryBelowTop(_undo, duplicateCount);
        if (target is null)
        {
            RemoveDuplicateTop(_undo, duplicateCount);
            return null;
        }

        var restored = Restore(target);

        RemoveTop(_undo, duplicateCount + 1);
        if (currentEntry.Length <= MaxHistoryBytes)
            AddLast(_redo, currentEntry);
        else
            ClearHistory(_redo);
        TrimHistory();
        return restored;
    }

    public PlannerDocument? Redo(PlannerDocument current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_redo.Count == 0)
            return null;

        var currentEntry = Snapshot(current);
        var duplicateCount = CountDuplicateTop(_redo, currentEntry);
        var target = EntryBelowTop(_redo, duplicateCount);
        if (target is null)
        {
            RemoveDuplicateTop(_redo, duplicateCount);
            return null;
        }

        var restored = Restore(target);

        RemoveTop(_redo, duplicateCount + 1);
        if (currentEntry.Length <= MaxHistoryBytes)
            AddLast(_undo, currentEntry);
        else
            ClearHistory(_undo);
        TrimHistory();
        return restored;
    }

    public void Clear()
    {
        _stateParticipant?.ClearState();
        ClearHistories();
    }

    public Checkpoint CreateCheckpoint()
    {
        if (_stateParticipant is not null)
        {
            throw new InvalidOperationException(
                "A document is required when checkpointing registered undo/redo state.");
        }

        return CreateCheckpointCore(auxiliaryState: null);
    }

    public Checkpoint CreateCheckpoint(PlannerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CreateCheckpointCore(CaptureAuxiliaryState(document));
    }

    private Checkpoint CreateCheckpointCore(CapturedAuxiliaryState? auxiliaryState) =>
        new(
            this,
            _undo.ToArray(),
            _redo.ToArray(),
            auxiliaryState);

    public void RestoreCheckpoint(Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!checkpoint.BelongsTo(this))
            throw new ArgumentException("The checkpoint belongs to a different undo/redo instance.", nameof(checkpoint));
        checkpoint.RestoreInto(this);
    }

    /// <summary>
    /// Applies only the checkpoint's current participant state, leaving both
    /// history stacks untouched. This lets a transaction align its live state
    /// to the exact precomputed checkpoint before crossing a commit boundary.
    /// </summary>
    public void ApplyCheckpointCurrentState(Checkpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!checkpoint.BelongsTo(this))
            throw new ArgumentException("The checkpoint belongs to a different undo/redo instance.", nameof(checkpoint));
        RestoreAuxiliaryState(checkpoint.AuxiliaryState);
    }

    private HistoryEntry Snapshot(PlannerDocument document) =>
        new(
            JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.CompactOptions),
            CaptureAuxiliaryState(document));

    private PlannerDocument Restore(HistoryEntry entry)
    {
        var document = JsonSerializer.Deserialize<PlannerDocument>(entry.Bytes, JsonDefaults.CompactOptions)
                       ?? throw new InvalidOperationException("Unable to restore undo history.");
        DocumentConsistencyService.Ensure(document);
        RestoreAuxiliaryState(entry.CapturedState);
        return document;
    }

    private CapturedAuxiliaryState? CaptureAuxiliaryState(PlannerDocument document)
    {
        var participant = _stateParticipant;
        return participant is null
            ? null
            : new CapturedAuxiliaryState(
                participant,
                participant.CaptureState(document) ??
                throw new InvalidOperationException("The undo/redo state participant returned null."));
    }

    private void RestoreAuxiliaryState(CapturedAuxiliaryState? captured)
    {
        var participant = _stateParticipant;
        if (participant is null)
            return;

        captured ??= _initialAuxiliaryState
                     ?? throw new InvalidOperationException("The undo/redo state participant has no initial state.");
        if (!ReferenceEquals(captured.Owner, participant))
            throw new InvalidOperationException("Undo/redo auxiliary state belongs to a different participant.");
        participant.RestoreState(captured.State);
    }

    private static bool AuxiliaryStatesMatch(
        CapturedAuxiliaryState? left,
        CapturedAuxiliaryState? right) =>
        left is null
            ? right is null
            : right is not null &&
              ReferenceEquals(left.Owner, right.Owner) &&
              left.Owner.AreEquivalent(left.State, right.State);

    private void AddLast(LinkedList<HistoryEntry> history, HistoryEntry entry)
    {
        history.AddLast(entry);
        _historyBytes += entry.Length;
    }

    private HistoryEntry RemoveLast(LinkedList<HistoryEntry> history)
    {
        var entry = history.Last?.Value
                    ?? throw new InvalidOperationException("History is empty.");
        history.RemoveLast();
        _historyBytes -= entry.Length;
        return entry;
    }

    private static int CountDuplicateTop(LinkedList<HistoryEntry> history, HistoryEntry current)
    {
        var count = 0;
        for (var node = history.Last; node is not null && node.Value.Matches(current); node = node.Previous)
            count++;
        return count;
    }

    private static HistoryEntry? EntryBelowTop(
        LinkedList<HistoryEntry> history,
        int entriesToSkip)
    {
        var node = history.Last;
        for (var index = 0; index < entriesToSkip && node is not null; index++)
            node = node.Previous;
        return node?.Value;
    }

    private void RemoveDuplicateTop(LinkedList<HistoryEntry> history, int duplicateCount)
        => RemoveTop(history, duplicateCount);

    private void RemoveTop(LinkedList<HistoryEntry> history, int count)
    {
        for (var index = 0; index < count; index++)
            RemoveLast(history);
    }

    private void ClearHistory(LinkedList<HistoryEntry> history)
    {
        foreach (var entry in history)
            _historyBytes -= entry.Length;
        history.Clear();
    }

    private void ClearHistories()
    {
        ClearHistory(_undo);
        ClearHistory(_redo);
    }

    private void TrimHistory()
    {
        while (_undo.Count + _redo.Count > MaxHistoryEntries || _historyBytes > MaxHistoryBytes)
        {
            var history = _undo.Count > 0 ? _undo : _redo;
            var entry = history.First?.Value
                        ?? throw new InvalidOperationException("History accounting is inconsistent.");
            history.RemoveFirst();
            _historyBytes -= entry.Length;
        }
    }
}

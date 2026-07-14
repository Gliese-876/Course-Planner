using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;

namespace CoursePlanner.Services;

public sealed class DocumentRestoreConsistencyException : IOException
{
    public DocumentRestoreConsistencyException(
        string recoveryDirectory,
        string? preRestoreBackupPath,
        Exception restoreException,
        Exception rollbackException)
        : base(
            "The restored database could not be rolled back after the application failed to load it. " +
            $"Saving is disabled until the database is reloaded successfully. Recovery files: '{recoveryDirectory}'.",
            new AggregateException(restoreException, rollbackException))
    {
        RecoveryDirectory = recoveryDirectory;
        PreRestoreBackupPath = preRestoreBackupPath;
        RestoreException = restoreException;
        RollbackException = rollbackException;
    }

    public string RecoveryDirectory { get; }
    public string? PreRestoreBackupPath { get; }
    public Exception RestoreException { get; }
    public Exception RollbackException { get; }
}

public sealed class DocumentRestoreCompensationException : Exception
{
    public DocumentRestoreCompensationException(
        Exception restoreException,
        Exception compensationException)
        : base(
            "The database and document were rolled back, but rebinding the rolled-back application state failed.",
            new AggregateException(restoreException, compensationException))
    {
        RestoreException = restoreException;
        CompensationException = compensationException;
    }

    public Exception RestoreException { get; }
    public Exception CompensationException { get; }
}

/// <summary>
/// Reports failures that occur only after a restore transaction has accepted
/// its candidate as the durable database state. The restore must not be rolled
/// back; callers should reload the accepted repository to repair projections.
/// </summary>
public sealed class DocumentRestorePostCommitException : IOException
{
    public DocumentRestorePostCommitException(
        string? preRestoreBackupPath,
        Exception? cleanupException,
        Exception? notificationException,
        Exception? refreshException,
        Exception? invariantException)
        : base(
            invariantException is null
                ? "The database restore was committed, but refreshing the accepted application state failed."
                : "The database restore was committed, but a post-commit callback changed the accepted " +
                  "in-memory document. Saving is disabled until the repository is reloaded successfully.",
            CreateInnerException(
                cleanupException,
                notificationException,
                refreshException,
                invariantException))
    {
        PreRestoreBackupPath = preRestoreBackupPath;
        CleanupException = cleanupException;
        NotificationException = notificationException;
        RefreshException = refreshException;
        InvariantException = invariantException;
    }

    public string? PreRestoreBackupPath { get; }
    public Exception? CleanupException { get; }
    public Exception? NotificationException { get; }
    public Exception? RefreshException { get; }
    public Exception? InvariantException { get; }

    private static Exception CreateInnerException(params Exception?[] exceptions)
    {
        var failures = exceptions.Where(exception => exception is not null).Cast<Exception>().ToList();
        return failures.Count switch
        {
            0 => new InvalidOperationException("No post-commit failure was supplied."),
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }
}

public sealed class DocumentSessionRollbackException : Exception
{
    public DocumentSessionRollbackException(
        Exception operationException,
        Exception rollbackException)
        : base(
            "The document operation failed and restoring the last persisted application state also reported an error.",
            new AggregateException(operationException, rollbackException))
    {
        OperationException = operationException;
        RollbackException = rollbackException;
    }

    public Exception OperationException { get; }
    public Exception RollbackException { get; }
}

public sealed class DocumentSessionConsistencyException : Exception
{
    public DocumentSessionConsistencyException(
        Exception operationException,
        Exception compensationException)
        : base(
            "The document operation failed and the exact in-memory session state could not be restored. " +
            "Saving is disabled until the repository is reloaded successfully.",
            new AggregateException(operationException, compensationException))
    {
        OperationException = operationException;
        CompensationException = compensationException;
    }

    public Exception OperationException { get; }
    public Exception CompensationException { get; }
}

public sealed class DocumentSessionCommitAmbiguityException : IOException
{
    public DocumentSessionCommitAmbiguityException(
        string eventName,
        Exception saveException,
        Exception? verificationException,
        Exception? compensationException,
        string priorStateHash,
        string attemptedStateHash,
        string? observedStateHash)
        : base(
            "The save reported a failure and durable storage could not be proven to contain either the prior " +
            "or attempted document. Saving is disabled until the repository is reloaded successfully. " +
            $"Event='{eventName}', prior={priorStateHash}, attempted={attemptedStateHash}, " +
            $"observed={observedStateHash ?? "unavailable"}.",
            CreateInnerException(saveException, verificationException, compensationException))
    {
        EventName = eventName;
        SaveException = saveException;
        VerificationException = verificationException;
        CompensationException = compensationException;
        PriorStateHash = priorStateHash;
        AttemptedStateHash = attemptedStateHash;
        ObservedStateHash = observedStateHash;
    }

    public string EventName { get; }
    public Exception SaveException { get; }
    public Exception? VerificationException { get; }
    public Exception? CompensationException { get; }
    public string PriorStateHash { get; }
    public string AttemptedStateHash { get; }
    public string? ObservedStateHash { get; }

    private static Exception CreateInnerException(
        Exception saveException,
        Exception? verificationException,
        Exception? compensationException)
    {
        var failures = new List<Exception> { saveException };
        if (verificationException is not null)
            failures.Add(verificationException);
        if (compensationException is not null)
            failures.Add(compensationException);
        return failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }
}

public sealed class DocumentSessionReloadConsistencyException : IOException
{
    public DocumentSessionReloadConsistencyException(
        Exception operationException,
        Exception? compensationException,
        string priorStateHash,
        string loadedStateHash)
        : base(
            "The repository was read successfully, but the newly loaded state could not be installed atomically. " +
            "Storage differs from the active session, so saving is disabled until reload succeeds. " +
            $"Prior={priorStateHash}, loaded={loadedStateHash}.",
            compensationException is null
                ? operationException
                : new AggregateException(operationException, compensationException))
    {
        OperationException = operationException;
        CompensationException = compensationException;
        PriorStateHash = priorStateHash;
        LoadedStateHash = loadedStateHash;
    }

    public Exception OperationException { get; }
    public Exception? CompensationException { get; }
    public string PriorStateHash { get; }
    public string LoadedStateHash { get; }
}

public enum DocumentStateAcceptanceKind
{
    Save,
    Undo,
    Redo,
    Replace,
    Reload,
    Restore
}

public sealed class DocumentStateAcceptedEventArgs(
    DocumentStateAcceptanceKind kind,
    string acceptedStateToken,
    string? eventName = null) : EventArgs
{
    public DocumentStateAcceptanceKind Kind { get; } = kind;
    public string AcceptedStateToken { get; } = acceptedStateToken;
    public string? EventName { get; } = eventName;
}

public enum DocumentRollbackTargetKind
{
    PersistedBaseline,
    OperationStart
}

public sealed class DocumentRolledBackEventArgs(
    DocumentRollbackTargetKind targetKind,
    string targetStateToken,
    bool durableOutcomeKnown) : EventArgs
{
    public DocumentRollbackTargetKind TargetKind { get; } = targetKind;
    public string TargetStateToken { get; } = targetStateToken;
    public bool DurableOutcomeKnown { get; } = durableOutcomeKnown;

    /// <summary>
    /// Compatibility projection for subscribers that have not yet moved to
    /// <see cref="TargetKind"/>. New code should consume the explicit target.
    /// </summary>
    public bool RestoredPersistedState => TargetKind == DocumentRollbackTargetKind.PersistedBaseline;
}

public sealed class DocumentSession
{
    private const int RestoreIdle = 0;
    private const int RestoreReserved = 1;
    private const int RestoreApplying = 2;
    private const int RestoreAwaitingTerminalReplay = 3;

    private readonly Func<PlannerDocument> _loadDocument;
    private readonly Action<PlannerDocument, string> _saveDocument;
    private readonly Func<PlannerDocument>? _verifyPersistedDocument;
    private readonly object _sessionOperationGate = new();
    private byte[] _persistedDocumentJson;
    private PlannerUndoRedo.Checkpoint _persistedHistory;
    private BackupRestoreTransaction? _activeRestoreTransaction;
    private int _restoreInProgress;
    private int _rollbackNotificationInProgress;
    private int _changedNotificationInProgress;
    private bool _storageConsistencyUnknown;
    private bool _sessionConsistencyUnknown;

    public DocumentSession(
        SqliteAppRepository repository,
        Func<PlannerDocument>? loadDocument = null,
        Action<PlannerDocument, string>? saveDocument = null,
        Func<PlannerDocument>? verifyPersistedDocument = null)
    {
        Repository = repository;
        _loadDocument = loadDocument ?? Repository.LoadOrCreate;
        _saveDocument = saveDocument ?? Repository.Save;
        _verifyPersistedDocument = verifyPersistedDocument ??
                                   (saveDocument is null
                                       ? Repository.LoadExistingForVerification
                                       : null);
        Document = _loadDocument();
        UndoRedo = new PlannerUndoRedo();
        _persistedDocumentJson = Snapshot(Document);
        _persistedHistory = UndoRedo.CreateCheckpoint(Document);
    }

    public SqliteAppRepository Repository { get; }
    public PlannerDocument Document { get; private set; }
    public PlannerUndoRedo UndoRedo { get; }
    public bool IsStorageConsistencyUnknown => _storageConsistencyUnknown;
    public bool IsSessionConsistencyUnknown => _sessionConsistencyUnknown;
    public string AcceptedStateToken
    {
        get
        {
            lock (_sessionOperationGate)
                return Hash(_persistedDocumentJson);
        }
    }

    public event EventHandler? Changed;
    public event EventHandler<DocumentRolledBackEventArgs>? RolledBack;
    public event EventHandler<DocumentStateAcceptedEventArgs>? StateAccepted;

    /// <summary>
    /// Raised whenever a Save accepts the current document as its durable state,
    /// including no-op and notify:false saves and a commit verified after the
    /// writer threw. Handlers share the Changed post-commit exception and
    /// invariant boundary.
    /// </summary>
    public event EventHandler? SaveAccepted;

    /// <summary>
    /// Must be called before a public operation mutates document or auxiliary
    /// state. It closes the window where a later Save rejection would otherwise
    /// leave speculative state behind.
    /// </summary>
    public void EnsureMutationAllowed()
    {
        lock (_sessionOperationGate)
            EnsureSavingIsSafe();
    }

    public void CaptureUndo()
    {
        lock (_sessionOperationGate)
        {
            EnsureSavingIsSafe();
            UndoRedo.Capture(Document);
        }
    }

    public void Save(string eventName = "save", bool notify = true)
    {
        lock (_sessionOperationGate)
            SaveCore(eventName, notify);
    }

    private bool SaveCore(
        string eventName,
        bool notify,
        SessionRollbackState? rollbackState = null,
        bool acceptNoOpSessionTransition = false,
        DocumentStateAcceptanceKind acceptanceKind = DocumentStateAcceptanceKind.Save)
    {
        EnsureSaveCanStartOrCompensate();
        rollbackState ??= CapturePersistedRollbackState();

        byte[] documentJson;
        try
        {
            DocumentConsistencyService.Ensure(Document);
            documentJson = Snapshot(Document);
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }

        if (documentJson.AsSpan().SequenceEqual(_persistedDocumentJson))
        {
            if (acceptNoOpSessionTransition)
            {
                PlannerUndoRedo.Checkpoint transitionHistory;
                try
                {
                    transitionHistory = UndoRedo.CreateCheckpoint(Document);
                    UndoRedo.ApplyCheckpointCurrentState(transitionHistory);
                }
                catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
                {
                    CompensateAndRethrow(operationException, rollbackState);
                    throw;
                }

                _persistedDocumentJson = documentJson;
                _persistedHistory = transitionHistory;
                InvokeChangedSubscribersAndVerify(
                    documentJson,
                    notify,
                    includeSaveAcceptedSubscribers: true,
                    CreateAcceptedEventArgs(acceptanceKind, documentJson, eventName));
                return false;
            }

            try
            {
                UndoRedo.RestoreCheckpoint(_persistedHistory);
            }
            catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
            {
                CompensateAndRethrow(operationException, rollbackState);
                throw;
            }
            InvokeChangedSubscribersAndVerify(
                documentJson,
                notify: false,
                includeSaveAcceptedSubscribers: true,
                CreateAcceptedEventArgs(acceptanceKind, documentJson, eventName));
            return false;
        }

        PlannerUndoRedo.Checkpoint attemptedHistory;
        try
        {
            // This is the only post-transition participant capture. It is
            // computed before the durable write and reused for every accepted
            // outcome, including a verified commit-then-throw.
            attemptedHistory = UndoRedo.CreateCheckpoint(Document);
            // A participant may canonicalize its captured state. Install that
            // exact state before the durable write so a restore failure remains
            // compensatable and the live session cannot diverge from the
            // checkpoint accepted for the commit.
            UndoRedo.ApplyCheckpointCurrentState(attemptedHistory);
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }

        try
        {
            _saveDocument(Document, eventName);
        }
        catch (Exception saveException) when (!RuntimeOperationExceptionPolicy.IsFatal(saveException))
        {
            var verification = VerifyFailedSave(
                documentJson,
                rollbackState.PersistedDocumentJson);
            switch (verification.Outcome)
            {
                case SaveVerificationOutcome.Attempted:
                    AcceptCommittedState(
                        documentJson,
                        attemptedHistory,
                        notify,
                        acceptanceKind,
                        eventName);
                    return true;
                case SaveVerificationOutcome.Prior:
                    CompensateAndRethrow(saveException, rollbackState);
                    throw;
                case SaveVerificationOutcome.Unknown:
                    ThrowCommitAmbiguity(
                        eventName,
                        saveException,
                        verification.VerificationException,
                        verification.ObservedDocumentJson,
                        documentJson,
                        rollbackState);
                    throw;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        AcceptCommittedState(
            documentJson,
            attemptedHistory,
            notify,
            acceptanceKind,
            eventName);
        return true;
    }

    public bool Undo()
    {
        lock (_sessionOperationGate)
            return UndoCore();
    }

    private bool UndoCore()
    {
        EnsureSavingIsSafe();
        var rollbackState = CaptureLiveRollbackState();
        PlannerDocument? restored;
        try
        {
            restored = UndoRedo.Undo(Document);
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }
        if (restored is null)
            return false;
        Document = restored;
        SaveCore(
            "undo",
            notify: true,
            rollbackState,
            acceptNoOpSessionTransition: true,
            acceptanceKind: DocumentStateAcceptanceKind.Undo);
        return true;
    }

    public bool Redo()
    {
        lock (_sessionOperationGate)
            return RedoCore();
    }

    private bool RedoCore()
    {
        EnsureSavingIsSafe();
        var rollbackState = CaptureLiveRollbackState();
        PlannerDocument? restored;
        try
        {
            restored = UndoRedo.Redo(Document);
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }
        if (restored is null)
            return false;
        Document = restored;
        SaveCore(
            "redo",
            notify: true,
            rollbackState,
            acceptNoOpSessionTransition: true,
            acceptanceKind: DocumentStateAcceptanceKind.Redo);
        return true;
    }

    public void ReplaceDocument(PlannerDocument document, string eventName)
    {
        lock (_sessionOperationGate)
            ReplaceDocumentCore(document, eventName);
    }

    private void ReplaceDocumentCore(PlannerDocument document, string eventName)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureSavingIsSafe();
        var rollbackState = CaptureLiveRollbackState();
        try
        {
            Document = document;
            UndoRedo.Clear();
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }

        SaveCore(
            eventName,
            notify: true,
            rollbackState,
            acceptNoOpSessionTransition: true,
            acceptanceKind: DocumentStateAcceptanceKind.Replace);
    }

    public void ReloadFromRepository()
    {
        lock (_sessionOperationGate)
            ReloadFromRepositoryCore();
    }

    private void ReloadFromRepositoryCore()
    {
        EnsureReloadIsSafe();
        SessionRollbackState rollbackState;
        try
        {
            rollbackState = CaptureLiveRollbackState();
        }
        catch (Exception captureException) when (
            !RuntimeOperationExceptionPolicy.IsFatal(captureException) &&
            (_storageConsistencyUnknown || _sessionConsistencyUnknown))
        {
            // Reload is the sole recovery boundary for an unknown session. A
            // broken live participant must not make that recovery path
            // permanently unreachable; installation below starts from the
            // last accepted checkpoint and only clears the flags on success.
            rollbackState = CapturePersistedRollbackState();
        }
        PlannerDocument loaded;
        try
        {
            loaded = _loadDocument();
        }
        catch (Exception loadException) when (!RuntimeOperationExceptionPolicy.IsFatal(loadException))
        {
            // A reload is the operation that re-establishes storage evidence.
            // If it cannot read storage, continuing to write would overwrite an
            // unobserved external state.
            _storageConsistencyUnknown = true;
            ExceptionDispatchInfo.Capture(loadException).Throw();
            throw;
        }

        byte[] loadedJson;
        try
        {
            DocumentConsistencyService.Ensure(loaded);
            loadedJson = Snapshot(loaded);
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            byte[]? observedLoadedJson = null;
            Exception reportedOperationException = operationException;
            try
            {
                observedLoadedJson = Snapshot(loaded);
            }
            catch (Exception snapshotException) when (!RuntimeOperationExceptionPolicy.IsFatal(snapshotException))
            {
                reportedOperationException = new AggregateException(operationException, snapshotException);
            }

            if (observedLoadedJson is not null &&
                observedLoadedJson.AsSpan().SequenceEqual(rollbackState.PersistedDocumentJson))
            {
                CompensateAndRethrow(reportedOperationException, rollbackState);
                throw;
            }

            ThrowReloadConsistency(
                reportedOperationException,
                observedLoadedJson,
                rollbackState);
            throw;
        }

        try
        {
            Document = loaded;
            UndoRedo.Clear();
            var loadedHistory = UndoRedo.CreateCheckpoint(Document);
            UndoRedo.ApplyCheckpointCurrentState(loadedHistory);
            _persistedDocumentJson = loadedJson;
            _persistedHistory = loadedHistory;
            _storageConsistencyUnknown = false;
            _sessionConsistencyUnknown = false;
        }
        catch (Exception operationException) when (!RuntimeOperationExceptionPolicy.IsFatal(operationException))
        {
            if (!loadedJson.AsSpan().SequenceEqual(rollbackState.PersistedDocumentJson))
            {
                ThrowReloadConsistency(
                    operationException,
                    loadedJson,
                    rollbackState);
                throw;
            }

            CompensateAndRethrow(operationException, rollbackState);
            throw;
        }

        // Once metadata accepts the newly read state, subscriber failures do
        // not roll it back. A later reload remains a safe retry boundary.
        InvokeChangedSubscribersAndVerify(
            loadedJson,
            acceptedState: CreateAcceptedEventArgs(
                DocumentStateAcceptanceKind.Reload,
                loadedJson,
                eventName: null));
    }

    public string? RestoreFromBackup(
        string backupZipPath,
        string automaticBackupDirectory,
        Action? refreshRestoredState = null)
    {
        var transaction = BeginBackupRestore(
            backupZipPath,
            automaticBackupDirectory);
        return ApplyBackupRestore(transaction, refreshRestoredState);
    }

    /// <summary>
    /// Reserves the session before the database candidate is published. The
    /// returned transaction must be passed to <see cref="ApplyBackupRestore"/>;
    /// until it reaches a terminal state, all ordinary session mutation and
    /// reload entry points remain closed.
    /// </summary>
    public BackupRestoreTransaction BeginBackupRestore(
        string backupZipPath,
        string automaticBackupDirectory)
    {
        lock (_sessionOperationGate)
        {
            EnsureRestoreCanStartBeforeDatabaseMutation();
            Volatile.Write(ref _activeRestoreTransaction, null);
            Volatile.Write(ref _restoreInProgress, RestoreReserved);
        }

        try
        {
            var transaction = BackupService.BeginRestoreWithPreBackup(
                Repository.DatabasePath,
                backupZipPath,
                automaticBackupDirectory);
            lock (_sessionOperationGate)
            {
                if (Volatile.Read(ref _restoreInProgress) != RestoreReserved ||
                    Volatile.Read(ref _activeRestoreTransaction) is not null)
                {
                    throw new InvalidOperationException(
                        "The document restore reservation changed before its database transaction was bound.");
                }

                Volatile.Write(ref _activeRestoreTransaction, transaction);
            }
            return transaction;
        }
        catch
        {
            lock (_sessionOperationGate)
            {
                if (Volatile.Read(ref _restoreInProgress) == RestoreReserved &&
                    Volatile.Read(ref _activeRestoreTransaction) is null)
                {
                    Volatile.Write(ref _restoreInProgress, RestoreIdle);
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Applies only the pending transaction returned by this session's most
    /// recent <see cref="BeginBackupRestore"/> call. Unbound transactions stay
    /// entirely owned by their creator and are never completed here.
    /// </summary>
    public string? ApplyBackupRestore(
        BackupRestoreTransaction transaction,
        Action? refreshRestoredState = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.IsCompleted)
            throw new ObjectDisposedException(nameof(transaction), "The restore transaction is already completed.");
        lock (_sessionOperationGate)
        {
            var restoreState = Volatile.Read(ref _restoreInProgress);
            var activeTransaction = Volatile.Read(ref _activeRestoreTransaction);
            if (restoreState == RestoreReserved &&
                ReferenceEquals(activeTransaction, transaction))
            {
                if (!string.Equals(
                        Path.GetFullPath(Repository.DatabasePath),
                        transaction.DatabasePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The bound restore transaction no longer targets this document repository.");
                }

                Volatile.Write(ref _restoreInProgress, RestoreApplying);
            }
            else if (ReferenceEquals(activeTransaction, transaction))
            {
                throw new InvalidOperationException(
                    "The active document restore transaction cannot be applied recursively.");
            }
            else
            {
                throw new InvalidOperationException(
                    "The restore transaction was not created and bound by this document session.");
            }
        }

        try
        {
            var loaded = _loadDocument();
            DocumentConsistencyService.Ensure(loaded);
            var loadedJson = Snapshot(loaded);
            Document = loaded;
            UndoRedo.Clear();
            var loadedHistory = UndoRedo.CreateCheckpoint(Document);
            UndoRedo.ApplyCheckpointCurrentState(loadedHistory);
            if (!MatchesSnapshot(Document, loadedJson))
            {
                throw new InvalidOperationException(
                    "Restore checkpoint preparation changed the candidate document before commit.");
            }
            BackupRestoreCleanupException? cleanupException = null;
            try
            {
                transaction.Commit();
            }
            catch (BackupRestoreCleanupException exception) when (transaction.IsCompleted)
            {
                cleanupException = exception;
            }

            _persistedDocumentJson = loadedJson;
            _persistedHistory = loadedHistory;
            _storageConsistencyUnknown = false;
            _sessionConsistencyUnknown = false;

            var postCommitException = PublishAcceptedRestore(
                loadedJson,
                transaction.PreRestoreBackupPath,
                refreshRestoredState,
                cleanupException);
            if (postCommitException is not null)
                throw postCommitException;
            if (cleanupException is not null)
                ExceptionDispatchInfo.Capture(cleanupException).Throw();
            return transaction.PreRestoreBackupPath;
        }
        catch (Exception restoreException) when (
            !transaction.IsCompleted &&
            !RuntimeOperationExceptionPolicy.IsFatal(restoreException))
        {
            try
            {
                transaction.Rollback();
            }
            catch (BackupRestoreCleanupException cleanupException) when (transaction.IsCompleted)
            {
                try
                {
                    RestorePersistedStateAndNotifyRollback();
                }
                catch (Exception compensationException) when (!RuntimeOperationExceptionPolicy.IsFatal(compensationException))
                {
                    throw new DocumentRestoreCompensationException(
                        cleanupException,
                        compensationException);
                }

                _storageConsistencyUnknown = false;
                ExceptionDispatchInfo.Capture(cleanupException).Throw();
                throw;
            }
            catch (Exception rollbackException) when (!RuntimeOperationExceptionPolicy.IsFatal(rollbackException))
            {
                _storageConsistencyUnknown = true;
                throw new DocumentRestoreConsistencyException(
                    transaction.RecoveryDirectory,
                    transaction.PreRestoreBackupPath,
                    restoreException,
                    rollbackException);
            }

            try
            {
                RestorePersistedStateAndNotifyRollback();
            }
            catch (Exception compensationException) when (!RuntimeOperationExceptionPolicy.IsFatal(compensationException))
            {
                throw new DocumentRestoreCompensationException(
                    restoreException,
                    compensationException);
            }

            ExceptionDispatchInfo.Capture(restoreException).Throw();
            throw;
        }
        finally
        {
            if (transaction.IsCompleted)
                ReleaseCompletedRestoreReservation(transaction);
            else
                MarkRestoreAwaitingTerminalReplay(transaction);
        }
    }

    private DocumentRestorePostCommitException? PublishAcceptedRestore(
        byte[] acceptedDocumentJson,
        string? preRestoreBackupPath,
        Action? refreshRestoredState,
        BackupRestoreCleanupException? cleanupException)
    {
        var acceptedState = CreateAcceptedEventArgs(
            DocumentStateAcceptanceKind.Restore,
            acceptedDocumentJson,
            eventName: null);
        var acceptedSubscribers = StateAccepted;
        var changedSubscribers = Changed;
        List<Exception>? notificationFailures = null;
        Exception? refreshException = null;
        Exception? invariantException = null;
        if (acceptedSubscribers is not null ||
            changedSubscribers is not null ||
            refreshRestoredState is not null)
        {
            try
            {
                InvokeChangedSubscribers(() =>
                {
                    CollectSubscriberFailures(
                        acceptedSubscribers,
                        acceptedState,
                        ref notificationFailures);
                    CollectSubscriberFailures(changedSubscribers, ref notificationFailures);
                    try
                    {
                        refreshRestoredState?.Invoke();
                    }
                    catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
                    {
                        refreshException = exception;
                    }
                });
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                (notificationFailures ??= []).Add(exception);
            }
        }

        var notificationException = CreateSubscriberException(notificationFailures);

        try
        {
            if (!MatchesSnapshot(Document, acceptedDocumentJson))
            {
                throw new InvalidOperationException(
                    "Post-commit restore callbacks changed the accepted document state.");
            }
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            _sessionConsistencyUnknown = true;
            invariantException = exception;
        }

        if (notificationException is null && refreshException is null && invariantException is null)
            return null;

        return new DocumentRestorePostCommitException(
            preRestoreBackupPath,
            cleanupException,
            notificationException,
            refreshException,
            invariantException);
    }

    private SessionRollbackState CaptureLiveRollbackState()
    {
        // Both operations are observational by contract. If participant capture
        // fails, no session or storage mutation has started.
        var documentJson = Snapshot(Document);
        var history = UndoRedo.CreateCheckpoint(Document);
        return new SessionRollbackState(
            DocumentRollbackTargetKind.OperationStart,
            Document,
            documentJson,
            history,
            _persistedDocumentJson,
            _persistedHistory,
            _storageConsistencyUnknown,
            _sessionConsistencyUnknown);
    }

    private SessionRollbackState CapturePersistedRollbackState() =>
        new(
            TargetKind: DocumentRollbackTargetKind.PersistedBaseline,
            DocumentReference: null,
            DocumentJson: _persistedDocumentJson,
            History: _persistedHistory,
            PersistedDocumentJson: _persistedDocumentJson,
            PersistedHistory: _persistedHistory,
            StorageConsistencyUnknown: _storageConsistencyUnknown,
            SessionConsistencyUnknown: _sessionConsistencyUnknown);

    private void AcceptCommittedState(
        byte[] documentJson,
        PlannerUndoRedo.Checkpoint history,
        bool notify,
        DocumentStateAcceptanceKind acceptanceKind,
        string? eventName)
    {
        _persistedDocumentJson = documentJson;
        _persistedHistory = history;
        _storageConsistencyUnknown = false;
        _sessionConsistencyUnknown = false;
        InvokeChangedSubscribersAndVerify(
            documentJson,
            notify,
            includeSaveAcceptedSubscribers: true,
            CreateAcceptedEventArgs(acceptanceKind, documentJson, eventName));
    }

    private static DocumentStateAcceptedEventArgs CreateAcceptedEventArgs(
        DocumentStateAcceptanceKind kind,
        byte[] acceptedDocumentJson,
        string? eventName) =>
        new(kind, Hash(acceptedDocumentJson), eventName);

    private SaveVerification VerifyFailedSave(
        byte[] attemptedDocumentJson,
        byte[] priorDocumentJson)
    {
        // Custom save delegates retain the historical contract unless their
        // caller explicitly supplies durable verification. Product code uses
        // the repository's strict read-only verifier by default.
        if (_verifyPersistedDocument is null)
            return new SaveVerification(SaveVerificationOutcome.Prior, null, null);

        try
        {
            var observedDocument = _verifyPersistedDocument();
            if (ReferenceEquals(observedDocument, Document))
            {
                throw new InvalidOperationException(
                    "The durable-state verifier returned the live document graph and therefore supplied no storage evidence.");
            }

            var observedDocumentJson = Snapshot(observedDocument);
            if (observedDocumentJson.AsSpan().SequenceEqual(attemptedDocumentJson))
            {
                return new SaveVerification(
                    SaveVerificationOutcome.Attempted,
                    observedDocumentJson,
                    null);
            }
            if (observedDocumentJson.AsSpan().SequenceEqual(priorDocumentJson))
            {
                return new SaveVerification(
                    SaveVerificationOutcome.Prior,
                    observedDocumentJson,
                    null);
            }

            return new SaveVerification(
                SaveVerificationOutcome.Unknown,
                observedDocumentJson,
                null);
        }
        catch (Exception verificationException) when (!RuntimeOperationExceptionPolicy.IsFatal(verificationException))
        {
            return new SaveVerification(
                SaveVerificationOutcome.Unknown,
                null,
                verificationException);
        }
    }

    private void CompensateAndRethrow(
        Exception operationException,
        SessionRollbackState rollbackState)
    {
        try
        {
            RestoreRollbackStateCore(rollbackState, preserveUnknownState: false);
        }
        catch (Exception compensationException) when (!RuntimeOperationExceptionPolicy.IsFatal(compensationException))
        {
            _sessionConsistencyUnknown = true;
            throw new DocumentSessionConsistencyException(
                operationException,
                compensationException);
        }

        Exception? notificationException = null;
        try
        {
            InvokeRollbackSubscribers(rollbackState, durableOutcomeKnown: true);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            notificationException = exception;
        }

        try
        {
            EnsureDocumentMatchesRollbackState(rollbackState);
        }
        catch (Exception compensationException) when (!RuntimeOperationExceptionPolicy.IsFatal(compensationException))
        {
            _sessionConsistencyUnknown = true;
            throw new DocumentSessionConsistencyException(
                operationException,
                notificationException is null
                    ? compensationException
                    : new AggregateException(notificationException, compensationException));
        }

        if (notificationException is not null)
        {
            throw new DocumentSessionRollbackException(
                operationException,
                notificationException);
        }

        ExceptionDispatchInfo.Capture(operationException).Throw();
        throw new InvalidOperationException("The compensated operation exception could not be propagated.");
    }

    private void ThrowCommitAmbiguity(
        string eventName,
        Exception saveException,
        Exception? verificationException,
        byte[]? observedDocumentJson,
        byte[] attemptedDocumentJson,
        SessionRollbackState rollbackState)
    {
        _storageConsistencyUnknown = true;
        Exception? compensationException = null;
        try
        {
            RestoreRollbackStateCore(rollbackState, preserveUnknownState: true);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            _sessionConsistencyUnknown = true;
            compensationException = exception;
        }

        if (compensationException is null)
        {
            try
            {
                // The unknown flag is already visible, so a reentrant subscriber
                // cannot overwrite the unverified durable state.
                InvokeRollbackSubscribers(rollbackState, durableOutcomeKnown: false);
                EnsureDocumentMatchesRollbackState(rollbackState);
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                compensationException = exception;
            }
        }

        throw new DocumentSessionCommitAmbiguityException(
            eventName,
            saveException,
            verificationException,
            compensationException,
            Hash(_persistedDocumentJson),
            Hash(attemptedDocumentJson),
            observedDocumentJson is null ? null : Hash(observedDocumentJson));
    }

    private void ThrowReloadConsistency(
        Exception operationException,
        byte[]? loadedDocumentJson,
        SessionRollbackState rollbackState)
    {
        _storageConsistencyUnknown = true;
        Exception? compensationException = null;
        try
        {
            RestoreRollbackStateCore(rollbackState, preserveUnknownState: true);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            _sessionConsistencyUnknown = true;
            compensationException = exception;
        }

        if (compensationException is null)
        {
            try
            {
                InvokeRollbackSubscribers(rollbackState, durableOutcomeKnown: false);
                EnsureDocumentMatchesRollbackState(rollbackState);
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                compensationException = exception;
            }
        }

        throw new DocumentSessionReloadConsistencyException(
            operationException,
            compensationException,
            Hash(rollbackState.PersistedDocumentJson),
            loadedDocumentJson is null ? "unavailable" : Hash(loadedDocumentJson));
    }

    private void RestoreRollbackStateCore(
        SessionRollbackState rollbackState,
        bool preserveUnknownState)
    {
        var restoredDocument = rollbackState.DocumentReference;
        if (restoredDocument is null ||
            !Snapshot(restoredDocument).AsSpan().SequenceEqual(rollbackState.DocumentJson))
        {
            restoredDocument = RestoreSnapshot(rollbackState.DocumentJson);
        }

        Document = restoredDocument;
        UndoRedo.RestoreCheckpoint(rollbackState.History);
        _persistedDocumentJson = rollbackState.PersistedDocumentJson;
        _persistedHistory = rollbackState.PersistedHistory;
        if (preserveUnknownState)
        {
            _storageConsistencyUnknown = true;
        }
        else
        {
            _storageConsistencyUnknown = rollbackState.StorageConsistencyUnknown;
            _sessionConsistencyUnknown = rollbackState.SessionConsistencyUnknown;
        }
    }

    private void EnsureDocumentMatchesRollbackState(SessionRollbackState rollbackState)
    {
        var documentJson = Snapshot(Document);
        if (!documentJson.AsSpan().SequenceEqual(rollbackState.DocumentJson))
        {
            throw new InvalidOperationException(
                "Rollback subscribers changed the compensated document state.");
        }
    }

    private void RestorePersistedState()
    {
        try
        {
            Document = RestoreSnapshot(_persistedDocumentJson);
            UndoRedo.RestoreCheckpoint(_persistedHistory);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            _sessionConsistencyUnknown = true;
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private void RestorePersistedStateAndNotifyRollback()
    {
        RestorePersistedState();
        List<Exception>? callbackFailures = null;
        var invariantFailed = false;
        try
        {
            InvokePersistedRollbackSubscribers(durableOutcomeKnown: true);
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            (callbackFailures ??= []).Add(exception);
        }

        try
        {
            var compensatedJson = Snapshot(Document);
            if (!compensatedJson.AsSpan().SequenceEqual(_persistedDocumentJson))
            {
                throw new InvalidOperationException(
                    "Restore compensation callbacks changed the document after the database was rolled back.");
            }
        }
        catch (Exception invariantException) when (!RuntimeOperationExceptionPolicy.IsFatal(invariantException))
        {
            invariantFailed = true;
            _sessionConsistencyUnknown = true;
            (callbackFailures ??= []).Add(invariantException);
        }

        _storageConsistencyUnknown = false;
        _sessionConsistencyUnknown = invariantFailed;

        if (callbackFailures is null)
            return;
        if (callbackFailures.Count == 1)
            ExceptionDispatchInfo.Capture(callbackFailures[0]).Throw();
        throw new AggregateException(
            "One or more restore compensation callbacks failed.",
            callbackFailures);
    }

    private void InvokeRollbackSubscribers(
        SessionRollbackState rollbackState,
        bool durableOutcomeKnown) =>
        InvokeRollbackSubscribers(new DocumentRolledBackEventArgs(
            rollbackState.TargetKind,
            Hash(rollbackState.DocumentJson),
            durableOutcomeKnown));

    private void InvokePersistedRollbackSubscribers(bool durableOutcomeKnown) =>
        InvokeRollbackSubscribers(new DocumentRolledBackEventArgs(
            DocumentRollbackTargetKind.PersistedBaseline,
            Hash(_persistedDocumentJson),
            durableOutcomeKnown));

    private void InvokeRollbackSubscribers(DocumentRolledBackEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _rollbackNotificationInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A document rollback notification is already in progress.");
        }

        try
        {
            InvokeSubscribers(RolledBack, args);
        }
        finally
        {
            Volatile.Write(ref _rollbackNotificationInProgress, 0);
        }
    }

    private void InvokeChangedSubscribers()
        => InvokeChangedSubscribers(Changed);

    private void InvokeChangedSubscribers(EventHandler? subscribers)
    {
        if (Interlocked.CompareExchange(ref _changedNotificationInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A document changed notification is already in progress.");
        }

        try
        {
            InvokeSubscribers(subscribers);
        }
        finally
        {
            Volatile.Write(ref _changedNotificationInProgress, 0);
        }
    }

    private void InvokeChangedSubscribersAndVerify(
        byte[] acceptedDocumentJson,
        bool notify = true,
        bool includeSaveAcceptedSubscribers = false,
        DocumentStateAcceptedEventArgs? acceptedState = null)
    {
        // Capture every delegate before invoking any callback. A StateAccepted
        // handler may rewire later event groups, but that must only affect the
        // next accepted-state publication rather than the current batch.
        var acceptedSubscribers = acceptedState is null ? null : StateAccepted;
        var legacySaveAcceptedSubscribers = includeSaveAcceptedSubscribers ? SaveAccepted : null;
        var changedSubscribers = notify ? Changed : null;
        List<Exception>? notificationFailures = null;
        if (acceptedSubscribers is not null ||
            legacySaveAcceptedSubscribers is not null ||
            changedSubscribers is not null)
        {
            try
            {
                InvokeChangedSubscribers(() =>
                {
                    if (acceptedState is not null)
                    {
                        CollectSubscriberFailures(
                            acceptedSubscribers,
                            acceptedState,
                            ref notificationFailures);
                    }
                    CollectSubscriberFailures(
                        legacySaveAcceptedSubscribers,
                        ref notificationFailures);
                    CollectSubscriberFailures(changedSubscribers, ref notificationFailures);
                });
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                (notificationFailures ??= []).Add(exception);
            }
        }

        var notificationException = CreateSubscriberException(notificationFailures);

        Exception? invariantException = null;
        try
        {
            if (!MatchesSnapshot(Document, acceptedDocumentJson))
            {
                throw new InvalidOperationException(
                    "The accepted document state changed after it was serialized for persistence.");
            }
        }
        catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
        {
            _sessionConsistencyUnknown = true;
            invariantException = exception;
        }

        if (invariantException is not null)
        {
            throw new DocumentSessionConsistencyException(
                notificationException ?? new InvalidOperationException(
                    "The accepted document changed after persistence."),
                invariantException);
        }

        if (notificationException is not null)
            ExceptionDispatchInfo.Capture(notificationException).Throw();
    }

    private void InvokeChangedSubscribers(Action callback)
    {
        if (Interlocked.CompareExchange(ref _changedNotificationInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A document changed notification is already in progress.");
        }

        try
        {
            callback();
        }
        finally
        {
            Volatile.Write(ref _changedNotificationInProgress, 0);
        }
    }

    private void InvokeSubscribers(EventHandler? subscribers)
    {
        List<Exception>? failures = null;
        CollectSubscriberFailures(subscribers, ref failures);
        ThrowSubscriberFailures(failures);
    }

    private void InvokeSubscribers<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        List<Exception>? failures = null;
        CollectSubscriberFailures(subscribers, eventArgs, ref failures);
        ThrowSubscriberFailures(failures);
    }

    private void CollectSubscriberFailures(
        EventHandler? subscribers,
        ref List<Exception>? failures)
    {
        if (subscribers is null)
            return;

        foreach (EventHandler subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                (failures ??= []).Add(exception);
            }
        }
    }

    private void CollectSubscriberFailures<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs eventArgs,
        ref List<Exception>? failures)
        where TEventArgs : EventArgs
    {
        if (subscribers is null)
            return;

        foreach (EventHandler<TEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch (Exception exception) when (!RuntimeOperationExceptionPolicy.IsFatal(exception))
            {
                (failures ??= []).Add(exception);
            }
        }
    }

    private static Exception? CreateSubscriberException(List<Exception>? failures) =>
        failures switch
        {
            null or { Count: 0 } => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException("One or more document-session subscribers failed.", failures)
        };

    private static void ThrowSubscriberFailures(List<Exception>? failures)
    {
        var exception = CreateSubscriberException(failures);
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private void ReleaseCompletedRestoreReservation(BackupRestoreTransaction transaction)
    {
        if (!transaction.IsCompleted)
            return;

        lock (_sessionOperationGate)
        {
            if (ReferenceEquals(Volatile.Read(ref _activeRestoreTransaction), transaction))
            {
                Volatile.Write(ref _activeRestoreTransaction, null);
                Volatile.Write(ref _restoreInProgress, RestoreIdle);
            }
        }
    }

    private void MarkRestoreAwaitingTerminalReplay(BackupRestoreTransaction transaction)
    {
        lock (_sessionOperationGate)
        {
            if (ReferenceEquals(Volatile.Read(ref _activeRestoreTransaction), transaction) &&
                Volatile.Read(ref _restoreInProgress) == RestoreApplying)
            {
                Volatile.Write(
                    ref _restoreInProgress,
                    RestoreAwaitingTerminalReplay);
            }
        }
    }

    private void ReleaseCompletedRestoreReservationForReload()
    {
        var activeTransaction = Volatile.Read(ref _activeRestoreTransaction);
        if (activeTransaction is not null &&
            Volatile.Read(ref _restoreInProgress) != RestoreApplying &&
            activeTransaction.IsCompleted)
        {
            Volatile.Write(ref _activeRestoreTransaction, null);
            Volatile.Write(ref _restoreInProgress, RestoreIdle);
        }
    }

    private void ReplayActiveRestoreTerminalActionForReload()
    {
        var activeTransaction = Volatile.Read(ref _activeRestoreTransaction);
        if (activeTransaction is null)
            return;
        if (activeTransaction.IsCompleted)
        {
            if (Volatile.Read(ref _restoreInProgress) != RestoreApplying)
                ReleaseCompletedRestoreReservation(activeTransaction);
            return;
        }
        if (Volatile.Read(ref _restoreInProgress) != RestoreAwaitingTerminalReplay)
            return;
        if (!_storageConsistencyUnknown && !_sessionConsistencyUnknown)
            return;

        try
        {
            activeTransaction.RetryTerminalAction();
        }
        catch
        {
            // A cleanup exception can be reported after the database already
            // reached its terminal state. Release the session reservation but
            // never execute that terminal action a second time.
            if (activeTransaction.IsCompleted)
                ReleaseCompletedRestoreReservation(activeTransaction);
            throw;
        }

        if (!activeTransaction.IsCompleted)
        {
            throw new InvalidOperationException(
                "The active restore terminal action returned without completing its transaction.");
        }

        ReleaseCompletedRestoreReservation(activeTransaction);
    }

    private void EnsureSaveCanStartOrCompensate()
    {
        if (Volatile.Read(ref _restoreInProgress) != RestoreIdle)
        {
            // The outer restore transaction owns compensation. Restoring the
            // old persisted snapshot here would replace its candidate graph.
            throw new InvalidOperationException(
                "Saving is disabled while a document restore is in progress.");
        }
        if (Volatile.Read(ref _rollbackNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Saving is disabled while a document rollback notification is in progress.");
        }
        if (Volatile.Read(ref _changedNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Saving is disabled while a document changed notification is in progress.");
        }

        if (!_storageConsistencyUnknown && !_sessionConsistencyUnknown)
            return;

        var gateException = new InvalidOperationException(
            "Saving is disabled because document-session consistency is unknown. Reload the repository first.");
        var rollbackState = CapturePersistedRollbackState();
        try
        {
            RestoreRollbackStateCore(rollbackState, preserveUnknownState: true);
            InvokeRollbackSubscribers(rollbackState, durableOutcomeKnown: false);
        }
        catch (Exception compensationException) when (!RuntimeOperationExceptionPolicy.IsFatal(compensationException))
        {
            _sessionConsistencyUnknown = true;
            throw new DocumentSessionConsistencyException(
                gateException,
                compensationException);
        }

        throw gateException;
    }

    private void EnsureSavingIsSafe()
    {
        if (_storageConsistencyUnknown || _sessionConsistencyUnknown)
        {
            throw new InvalidOperationException(
                "Saving is disabled because document-session consistency is unknown. Reload the repository first.");
        }
        if (Volatile.Read(ref _restoreInProgress) != RestoreIdle)
            throw new InvalidOperationException("Saving is disabled while a document restore is in progress.");
        if (Volatile.Read(ref _rollbackNotificationInProgress) != 0)
            throw new InvalidOperationException("Mutation is disabled while a document rollback notification is in progress.");
        if (Volatile.Read(ref _changedNotificationInProgress) != 0)
            throw new InvalidOperationException("Mutation is disabled while a document changed notification is in progress.");
    }

    private void EnsureReloadIsSafe()
    {
        ReplayActiveRestoreTerminalActionForReload();
        ReleaseCompletedRestoreReservationForReload();
        if (Volatile.Read(ref _restoreInProgress) != RestoreIdle)
        {
            throw new InvalidOperationException(
                "Reloading is disabled while a document restore is in progress.");
        }
        if (Volatile.Read(ref _rollbackNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Reloading is disabled while a document rollback notification is in progress.");
        }
        if (Volatile.Read(ref _changedNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Reloading is disabled while a document changed notification is in progress.");
        }
    }

    private void EnsureRestoreCanStartBeforeDatabaseMutation()
    {
        if (_storageConsistencyUnknown || _sessionConsistencyUnknown)
        {
            throw new InvalidOperationException(
                "Restoring is disabled because document-session consistency is unknown. Reload the repository first.");
        }
        if (Volatile.Read(ref _restoreInProgress) != RestoreIdle)
        {
            throw new InvalidOperationException(
                "Another document restore is already in progress.");
        }
        if (Volatile.Read(ref _rollbackNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Restoring is disabled while a document rollback notification is in progress.");
        }
        if (Volatile.Read(ref _changedNotificationInProgress) != 0)
        {
            throw new InvalidOperationException(
                "Restoring is disabled while a document changed notification is in progress.");
        }
    }

    private static string Hash(byte[] documentJson) =>
        Convert.ToHexString(SHA256.HashData(documentJson));

    private static bool MatchesSnapshot(PlannerDocument document, byte[] expectedJson)
    {
        using var comparisonStream = new JsonComparisonWriteStream(expectedJson);
        JsonSerializer.Serialize(comparisonStream, document, JsonDefaults.CompactOptions);
        return comparisonStream.Matches;
    }

    private static byte[] Snapshot(PlannerDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.CompactOptions);

    private static PlannerDocument RestoreSnapshot(byte[] json) =>
        JsonSerializer.Deserialize<PlannerDocument>(json, JsonDefaults.CompactOptions)
        ?? throw new InvalidOperationException("Unable to restore the last persisted document state.");

    private enum SaveVerificationOutcome
    {
        Attempted,
        Prior,
        Unknown
    }

    private sealed record SaveVerification(
        SaveVerificationOutcome Outcome,
        byte[]? ObservedDocumentJson,
        Exception? VerificationException);

    private sealed class JsonComparisonWriteStream(byte[] expectedJson) : Stream
    {
        private long _writtenLength;
        private bool _matches = true;

        public bool Matches => _matches && _writtenLength == expectedJson.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_matches)
            {
                if (_writtenLength > expectedJson.Length ||
                    buffer.Length > expectedJson.Length - (int)_writtenLength ||
                    !buffer.SequenceEqual(expectedJson.AsSpan((int)_writtenLength, buffer.Length)))
                {
                    _matches = false;
                }
            }

            _writtenLength = checked(_writtenLength + buffer.Length);
        }

        public override void WriteByte(byte value)
        {
            Span<byte> buffer = stackalloc byte[1];
            buffer[0] = value;
            Write(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }

    private sealed record SessionRollbackState(
        DocumentRollbackTargetKind TargetKind,
        PlannerDocument? DocumentReference,
        byte[] DocumentJson,
        PlannerUndoRedo.Checkpoint History,
        byte[] PersistedDocumentJson,
        PlannerUndoRedo.Checkpoint PersistedHistory,
        bool StorageConsistencyUnknown,
        bool SessionConsistencyUnknown);
}

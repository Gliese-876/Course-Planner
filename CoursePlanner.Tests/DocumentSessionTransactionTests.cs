using System.Security.Cryptography;
using System.Text.Json;
using CoursePlanner.Core;
using CoursePlanner.Persistence;
using CoursePlanner.Services;
using CoursePlanner.ViewModels;
using Microsoft.Data.Sqlite;

namespace CoursePlanner.Tests;

public sealed class DocumentSessionTransactionTests
{
    [Fact]
    public void FailedSaveRestoresDocumentUndoRedoEventsAndNotifications()
    {
        using var fixture = Fixture.Create();
        var before = fixture.CaptureState();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "must roll back";
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.Save("failing.save"));

        fixture.AssertState(before);
    }

    [Fact]
    public void FailedSavePublishesKnownPersistedBaselineRollbackWithoutAcceptance()
    {
        using var fixture = Fixture.Create();
        var expectedTargetToken = fixture.Session.AcceptedStateToken;
        var rollbacks = new List<DocumentRolledBackEventArgs>();
        var accepted = 0;
        fixture.Session.RolledBack += (_, args) => rollbacks.Add(args);
        fixture.Session.StateAccepted += (_, _) => accepted++;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "known failed save";
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.Save("known.failed.save"));

        var rollback = Assert.Single(rollbacks);
        Assert.Equal(DocumentRollbackTargetKind.PersistedBaseline, rollback.TargetKind);
        Assert.Equal(expectedTargetToken, rollback.TargetStateToken);
        Assert.Equal(fixture.Session.AcceptedStateToken, rollback.TargetStateToken);
        Assert.True(rollback.DurableOutcomeKnown);
        Assert.True(rollback.RestoredPersistedState);
        Assert.Equal(0, accepted);
    }

    [Fact]
    public void FailedReplacePublishesKnownOperationStartRollbackWithItsExactToken()
    {
        using var fixture = Fixture.Create();
        fixture.Session.Document.Plans[0].PlanName = "dirty operation-start state";
        var acceptedBaselineToken = fixture.Session.AcceptedStateToken;
        var expectedTargetToken = StateToken(fixture.Session.Document);
        Assert.NotEqual(acceptedBaselineToken, expectedTargetToken);
        var rollbacks = new List<DocumentRolledBackEventArgs>();
        fixture.Session.RolledBack += (_, args) => rollbacks.Add(args);
        var replacement = JsonDefaults.Clone(fixture.Session.Document);
        replacement.Plans[0].PlanName = "replacement that must fail";
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() =>
            fixture.Session.ReplaceDocument(replacement, "known.failed.replace"));

        var rollback = Assert.Single(rollbacks);
        Assert.Equal(DocumentRollbackTargetKind.OperationStart, rollback.TargetKind);
        Assert.Equal(expectedTargetToken, rollback.TargetStateToken);
        Assert.True(rollback.DurableOutcomeKnown);
        Assert.False(rollback.RestoredPersistedState);
        Assert.Equal("dirty operation-start state", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(acceptedBaselineToken, fixture.Session.AcceptedStateToken);
    }

    [Fact]
    public void RollbackNotificationFailureDoesNotMaskStorageFailureOrSkipLaterSubscribers()
    {
        using var fixture = Fixture.Create();
        var before = fixture.CaptureState();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "must roll back after subscriber failure";
        fixture.Storage.FailSave = true;
        var laterSubscriberCalls = 0;
        fixture.Session.RolledBack += (_, _) =>
            throw new InvalidOperationException("simulated rollback subscriber failure");
        fixture.Session.RolledBack += (_, _) => laterSubscriberCalls++;

        var thrown = Record.Exception(() => fixture.Session.Save("failing.save.with-bad-subscriber"));

        var rollbackFailure = Assert.IsType<DocumentSessionRollbackException>(thrown);
        Assert.IsType<IOException>(rollbackFailure.OperationException);
        Assert.IsType<InvalidOperationException>(rollbackFailure.RollbackException);
        Assert.Contains(
            EnumerateExceptions(rollbackFailure),
            exception => exception is IOException &&
                         exception.Message.Contains("simulated save failure", StringComparison.Ordinal));
        Assert.Contains(
            EnumerateExceptions(rollbackFailure),
            exception => exception is InvalidOperationException &&
                         exception.Message.Contains("simulated rollback subscriber failure", StringComparison.Ordinal));
        Assert.Equal(1, laterSubscriberCalls);
        fixture.AssertState(before);
    }

    [Fact]
    public void FatalStorageFailureIsNeverConvertedIntoARecoverableRollbackFailure()
    {
        var document = TestDocumentFactory.CreatePopulated();
        var fatal = new OutOfMemoryException("simulated fatal storage failure");
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => document,
            saveDocument: (_, _) => throw fatal);
        var rollbackCalls = 0;
        session.RolledBack += (_, _) => rollbackCalls++;
        session.CaptureUndo();
        session.Document.Plans[0].PlanName = "fatal mutation";

        var thrown = Record.Exception(() => session.Save("fatal.save"));

        Assert.Same(fatal, thrown);
        Assert.Equal(0, rollbackCalls);
    }

    [Fact]
    public void FatalRollbackSubscriberFailureIsNeverWrappedAsAnOperationalException()
    {
        using var fixture = Fixture.Create();
        var fatal = new OutOfMemoryException("simulated fatal rollback subscriber failure");
        fixture.Session.RolledBack += (_, _) => throw fatal;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "must restore before fatal notification";
        fixture.Storage.FailSave = true;

        var thrown = Record.Exception(() => fixture.Session.Save("failing.save.with-fatal-subscriber"));

        Assert.Same(fatal, thrown);
        Assert.False(thrown is DocumentSessionRollbackException);
    }

    [Fact]
    public void SuccessfulSaveNotifiesLaterSubscribersEvenWhenAnEarlierSubscriberFails()
    {
        using var fixture = Fixture.Create();
        var beforeEventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        var laterSubscriberCalls = 0;
        fixture.Session.Changed += (_, _) =>
            throw new InvalidOperationException("simulated changed subscriber failure");
        fixture.Session.Changed += (_, _) => laterSubscriberCalls++;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "committed despite notification failure";

        var thrown = Record.Exception(() => fixture.Session.Save("save.with-bad-changed-subscriber"));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal(1, laterSubscriberCalls);
        Assert.Equal(
            "committed despite notification failure",
            fixture.Session.Repository.LoadOrCreate().Plans[0].PlanName);
        Assert.Equal(beforeEventCount + 1, fixture.Session.Repository.ReadEventSummaries().Count);

        // Persisted-state capture happens before notification, so a retry is a true no-op.
        fixture.Session.Save("must-not-duplicate-committed-save");
        Assert.Equal(beforeEventCount + 1, fixture.Session.Repository.ReadEventSummaries().Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulSaveRaisesSaveAcceptedIndependentlyOfChangedNotification(bool notify)
    {
        using var fixture = Fixture.Create();
        var accepted = 0;
        var changed = 0;
        fixture.Session.SaveAccepted += (_, _) => accepted++;
        fixture.Session.Changed += (_, _) => changed++;
        fixture.Session.Document.Plans[0].PlanName = $"accepted notify {notify}";

        fixture.Session.Save("save.accepted.notification-policy", notify);

        Assert.Equal(1, accepted);
        Assert.Equal(notify ? 1 : 0, changed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SuccessfulSavePublishesAcceptedStateBeforeOptionalChangedNotification(bool notify)
    {
        using var fixture = Fixture.Create();
        var notifications = new List<string>();
        DocumentStateAcceptedEventArgs? accepted = null;
        fixture.Session.StateAccepted += (_, args) =>
        {
            accepted = args;
            notifications.Add("accepted");
            Assert.Equal(fixture.Session.AcceptedStateToken, args.AcceptedStateToken);
        };
        fixture.Session.Changed += (_, _) => notifications.Add("changed");
        fixture.Session.Document.Plans[0].PlanName = $"state accepted notify {notify}";

        fixture.Session.Save("save.state-accepted.notification-policy", notify);

        Assert.NotNull(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Save, accepted.Kind);
        Assert.Equal("save.state-accepted.notification-policy", accepted.EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, accepted.AcceptedStateToken);
        Assert.Equal(notify ? ["accepted", "changed"] : ["accepted"], notifications);
    }

    [Fact]
    public void StateAcceptedSubscriberFailureCannotSkipLaterAcceptedOrChangedSubscribers()
    {
        using var fixture = Fixture.Create();
        var laterAcceptedCalls = 0;
        var changedCalls = 0;
        fixture.Session.StateAccepted += (_, _) =>
            throw new InvalidOperationException("simulated state-accepted subscriber failure");
        fixture.Session.StateAccepted += (_, _) => laterAcceptedCalls++;
        fixture.Session.Changed += (_, _) => changedCalls++;
        fixture.Session.Document.Plans[0].PlanName = "durable despite state-accepted failure";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.Save("save.state-accepted.subscriber.failure"));

        Assert.Contains("state-accepted subscriber failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, laterAcceptedCalls);
        Assert.Equal(1, changedCalls);
        Assert.Equal(
            "durable despite state-accepted failure",
            fixture.Session.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void StateAcceptedSubscriberMutationIsRejectedByThePostCommitInvariant()
    {
        using var fixture = Fixture.Create();
        fixture.Session.StateAccepted += (_, _) =>
            fixture.Session.Document.Plans[0].PlanName = "state-accepted subscriber live state C";
        fixture.Session.Document.Plans[0].PlanName = "state-accepted durable state B";

        Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("state-accepted.subscriber.mutation", notify: false));

        Assert.Equal(
            "state-accepted subscriber live state C",
            fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            "state-accepted durable state B",
            fixture.Session.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.True(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
    }

    [Fact]
    public void AcceptedNotificationBatchUsesOneAudienceSnapshotAcrossAllEventKinds()
    {
        using var fixture = Fixture.Create();
        var oldLegacyCalls = 0;
        var newLegacyCalls = 0;
        var oldChangedCalls = 0;
        var newChangedCalls = 0;
        EventHandler oldLegacy = (_, _) => oldLegacyCalls++;
        EventHandler newLegacy = (_, _) => newLegacyCalls++;
        EventHandler oldChanged = (_, _) => oldChangedCalls++;
        EventHandler newChanged = (_, _) => newChangedCalls++;
        fixture.Session.SaveAccepted += oldLegacy;
        fixture.Session.Changed += oldChanged;
        var rewired = false;
        fixture.Session.StateAccepted += (_, _) =>
        {
            if (rewired)
                return;
            rewired = true;
            fixture.Session.SaveAccepted -= oldLegacy;
            fixture.Session.SaveAccepted += newLegacy;
            fixture.Session.Changed -= oldChanged;
            fixture.Session.Changed += newChanged;
        };
        fixture.Session.Document.Plans[0].PlanName = "audience snapshot first";

        fixture.Session.Save("save.audience-snapshot.first");

        Assert.Equal(1, oldLegacyCalls);
        Assert.Equal(0, newLegacyCalls);
        Assert.Equal(1, oldChangedCalls);
        Assert.Equal(0, newChangedCalls);

        fixture.Session.Document.Plans[0].PlanName = "audience snapshot second";
        fixture.Session.Save("save.audience-snapshot.second");

        Assert.Equal(1, oldLegacyCalls);
        Assert.Equal(1, newLegacyCalls);
        Assert.Equal(1, oldChangedCalls);
        Assert.Equal(1, newChangedCalls);
    }

    [Fact]
    public void ReplacePublishesItsOwnAcceptedStateKindAndEventName()
    {
        using var fixture = Fixture.Create();
        DocumentStateAcceptedEventArgs? accepted = null;
        fixture.Session.StateAccepted += (_, args) => accepted = args;
        var replacement = JsonDefaults.Clone(fixture.Session.Document);
        replacement.Plans[0].PlanName = "replacement accepted kind";

        fixture.Session.ReplaceDocument(replacement, "replace.state-accepted.kind");

        Assert.NotNull(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Replace, accepted.Kind);
        Assert.Equal("replace.state-accepted.kind", accepted.EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, accepted.AcceptedStateToken);
    }

    [Fact]
    public void UndoAndRedoPublishDistinctAcceptedStateKindsBeforeChanged()
    {
        using var fixture = Fixture.Create();
        fixture.CommitPlanName("state accepted undo-redo change");
        var notifications = new List<string>();
        var accepted = new List<DocumentStateAcceptedEventArgs>();
        fixture.Session.StateAccepted += (_, args) =>
        {
            accepted.Add(args);
            notifications.Add($"accepted:{args.Kind}");
            Assert.Equal(fixture.Session.AcceptedStateToken, args.AcceptedStateToken);
        };
        fixture.Session.Changed += (_, _) => notifications.Add("changed");

        Assert.True(fixture.Session.Undo());
        Assert.True(fixture.Session.Redo());

        Assert.Equal(
            [DocumentStateAcceptanceKind.Undo, DocumentStateAcceptanceKind.Redo],
            accepted.Select(args => args.Kind));
        Assert.Equal(["undo", "redo"], accepted.Select(args => args.EventName));
        Assert.Equal(
            ["accepted:Undo", "changed", "accepted:Redo", "changed"],
            notifications);
    }

    [Fact]
    public void ReloadPublishesAcceptedStateBeforeChangedEvenWhenContentIsUnchanged()
    {
        using var fixture = Fixture.Create();
        var notifications = new List<string>();
        DocumentStateAcceptedEventArgs? accepted = null;
        fixture.Session.StateAccepted += (_, args) =>
        {
            accepted = args;
            notifications.Add("accepted");
        };
        fixture.Session.Changed += (_, _) => notifications.Add("changed");

        fixture.Session.ReloadFromRepository();

        Assert.NotNull(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Reload, accepted.Kind);
        Assert.Null(accepted.EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, accepted.AcceptedStateToken);
        Assert.Equal(["accepted", "changed"], notifications);
    }

    [Fact]
    public void OrdinaryNoOpSavePublishesOneAcceptanceWithoutWriteOrChanged()
    {
        using var fixture = Fixture.Create();
        var beforeEventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        var beforeToken = fixture.Session.AcceptedStateToken;
        var accepted = new List<DocumentStateAcceptedEventArgs>();
        var changed = 0;
        fixture.Session.StateAccepted += (_, args) => accepted.Add(args);
        fixture.Session.Changed += (_, _) => changed++;

        fixture.Session.Save("save.no-op.state-accepted");

        var acceptance = Assert.Single(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Save, acceptance.Kind);
        Assert.Equal("save.no-op.state-accepted", acceptance.EventName);
        Assert.Equal(beforeToken, acceptance.AcceptedStateToken);
        Assert.Equal(beforeToken, fixture.Session.AcceptedStateToken);
        Assert.Equal(0, changed);
        Assert.Equal(beforeEventCount, fixture.Session.Repository.ReadEventSummaries().Count);
    }

    [Fact]
    public void SaveAcceptedSubscriberFailureCannotSkipChangedOrUndoTheDurableCommit()
    {
        using var fixture = Fixture.Create();
        var changed = 0;
        fixture.Session.SaveAccepted += (_, _) =>
            throw new InvalidOperationException("simulated save-accepted subscriber failure");
        fixture.Session.Changed += (_, _) => changed++;
        fixture.Session.Document.Plans[0].PlanName = "durable despite save-accepted failure";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.Save("save.accepted.subscriber.failure"));

        Assert.Contains("save-accepted subscriber failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, changed);
        Assert.Equal(
            "durable despite save-accepted failure",
            fixture.Session.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void SaveAcceptedSubscriberMutationIsRejectedByThePostCommitInvariant()
    {
        using var fixture = Fixture.Create();
        fixture.Session.SaveAccepted += (_, _) =>
            fixture.Session.Document.Plans[0].PlanName = "save-accepted subscriber live state C";
        fixture.Session.Document.Plans[0].PlanName = "save-accepted durable state B";

        Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("save.accepted.subscriber.mutation", notify: false));

        Assert.Equal(
            "save-accepted subscriber live state C",
            fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            "save-accepted durable state B",
            fixture.Session.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.True(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
    }

    [Fact]
    public void ChangedSubscriberMutationMarksSessionUnknownUntilReloadRestoresDurableState()
    {
        using var fixture = Fixture.Create();
        EventHandler mutatingSubscriber = (_, _) =>
            fixture.Session.Document.Plans[0].PlanName = "subscriber-only live state C";
        fixture.Session.Changed += mutatingSubscriber;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "durable accepted state B";

        Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("changed.mutates.accepted.state"));

        Assert.Equal("subscriber-only live state C", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            "durable accepted state B",
            fixture.Session.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.True(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);

        fixture.Session.Changed -= mutatingSubscriber;
        fixture.Session.ReloadFromRepository();

        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Equal("durable accepted state B", fixture.Session.Document.Plans[0].PlanName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveDelegateMutationWithoutChangedSubscribersMarksSessionUnknownUntilReload(bool notify)
    {
        const string durableName = "save-delegate durable state B";
        const string mutatedLiveName = "save-delegate durable state C";
        var stored = TestDocumentFactory.CreatePopulated();
        var repository = new SqliteAppRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var session = new DocumentSession(
            repository,
            loadDocument: () => JsonDefaults.Clone(stored),
            saveDocument: (document, _) =>
            {
                stored = JsonDefaults.Clone(document);
                document.Plans[0].PlanName = mutatedLiveName;
            });
        session.Document.Plans[0].PlanName = durableName;
        var acceptedSerializedLength = JsonSerializer.SerializeToUtf8Bytes(
            session.Document,
            JsonDefaults.CompactOptions).Length;

        Assert.Throws<DocumentSessionConsistencyException>(() =>
            session.Save("save-delegate.mutates.accepted.state", notify));

        var durableSerializedLength = JsonSerializer.SerializeToUtf8Bytes(
            stored,
            JsonDefaults.CompactOptions).Length;
        var mutatedSerializedLength = JsonSerializer.SerializeToUtf8Bytes(
            session.Document,
            JsonDefaults.CompactOptions).Length;
        Assert.Equal(acceptedSerializedLength, durableSerializedLength);
        Assert.Equal(
            durableSerializedLength,
            mutatedSerializedLength);
        Assert.Equal(mutatedLiveName, session.Document.Plans[0].PlanName);
        Assert.Equal(durableName, stored.Plans[0].PlanName);
        Assert.True(session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(session.EnsureMutationAllowed);

        session.ReloadFromRepository();

        Assert.False(session.IsSessionConsistencyUnknown);
        Assert.Equal(durableName, session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void RollbackSubscriberMutationAndFailureCannotLeaveTheSessionClaimedKnown()
    {
        using var fixture = Fixture.Create();
        fixture.Session.RolledBack += (_, _) =>
        {
            fixture.Session.Document.Plans[0].PlanName = "rollback subscriber mutation C";
            throw new InvalidOperationException("rollback subscriber also failed");
        };
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "failed attempted state B";
        fixture.Storage.FailSave = true;

        var exception = Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("rollback.subscriber.mutates.and.throws"));

        Assert.Contains(
            EnumerateExceptions(exception),
            failure => failure.Message.Contains("rollback subscriber also failed", StringComparison.Ordinal));
        Assert.True(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
    }

    [Fact]
    public void VerifiedCommitThenThrowIsAcceptedAndNotifiedExactlyOnce()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.CommitAttemptedThenThrow);
        var beforeEventCount = fixture.Repository.ReadEventSummaries().Count;
        var changed = 0;
        var accepted = 0;
        var stateAccepted = new List<DocumentStateAcceptedEventArgs>();
        var rolledBack = 0;
        fixture.Session.Changed += (_, _) => changed++;
        fixture.Session.SaveAccepted += (_, _) => accepted++;
        fixture.Session.StateAccepted += (_, args) => stateAccepted.Add(args);
        fixture.Session.RolledBack += (_, _) => rolledBack++;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "durably committed despite reported failure";

        var exception = Record.Exception(() => fixture.Session.Save("commit.then.throw"));

        Assert.Null(exception);
        Assert.Equal(1, changed);
        Assert.Equal(1, accepted);
        var committedAcceptance = Assert.Single(stateAccepted);
        Assert.Equal(DocumentStateAcceptanceKind.Save, committedAcceptance.Kind);
        Assert.Equal("commit.then.throw", committedAcceptance.EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, committedAcceptance.AcceptedStateToken);
        Assert.Equal(0, rolledBack);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(
            "durably committed despite reported failure",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.Equal(beforeEventCount + 1, fixture.Repository.ReadEventSummaries().Count);

        fixture.Session.Save("accepted.retry.is-no-op");
        Assert.Equal(beforeEventCount + 1, fixture.Repository.ReadEventSummaries().Count);
        Assert.Equal(2, stateAccepted.Count);
        Assert.Equal("accepted.retry.is-no-op", stateAccepted[1].EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, stateAccepted[1].AcceptedStateToken);
    }

    [Fact]
    public void VerifiedCommitThenThrowAllowsNotifyFalseRuntimePostCommitWork()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.CommitAttemptedThenThrow);
        var localization = new LocalizationService(fixture.Session);
        var languageNotifications = 0;
        var changed = 0;
        var accepted = 0;
        localization.LanguageChanged += (_, _) => languageNotifications++;
        fixture.Session.Changed += (_, _) => changed++;
        fixture.Session.SaveAccepted += (_, _) => accepted++;

        localization.ApplyLanguage(LanguageMode.SimplifiedChinese);

        Assert.Equal(0, changed);
        Assert.Equal(1, accepted);
        Assert.Equal(1, languageNotifications);
        Assert.Equal(LanguageMode.SimplifiedChinese, fixture.Session.Document.Settings.Language);
        Assert.Equal(LanguageMode.SimplifiedChinese, localization.Localizer.ResolvedLanguage);
        Assert.Equal(
            LanguageMode.SimplifiedChinese,
            fixture.Repository.LoadExistingForVerification().Settings.Language);
    }

    [Fact]
    public void VerifiedPreCommitFailureRestoresPriorStateAndRethrows()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.ThrowBeforeCommit);
        var before = Serialize(fixture.Session.Document);
        var beforeEventCount = fixture.Repository.ReadEventSummaries().Count;
        var rolledBack = 0;
        fixture.Session.RolledBack += (_, _) => rolledBack++;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "must not commit";

        var exception = Assert.Throws<IOException>(() => fixture.Session.Save("precommit.throw"));

        Assert.Contains("injected save failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, Serialize(fixture.Session.Document));
        Assert.Equal(before, Serialize(fixture.Repository.LoadExistingForVerification()));
        Assert.Equal(beforeEventCount, fixture.Repository.ReadEventSummaries().Count);
        Assert.Equal(1, rolledBack);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
    }

    [Fact]
    public void ThirdDurableStateAfterSaveFailureDisablesWritesAndRestoresPriorMemory()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.CommitThirdStateThenThrow);
        var localization = new LocalizationService(fixture.Session);
        var planner = new PlannerViewModel(fixture.Session, localization);
        var settings = new SettingsViewModel(
            fixture.Session,
            localization,
            new TestThemeService());
        var before = Serialize(fixture.Session.Document);
        var beforeUndoCount = fixture.Session.UndoRedo.UndoCount;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "attempted state";

        var exception = Assert.Throws<DocumentSessionCommitAmbiguityException>(() =>
            fixture.Session.Save("third-state.throw"));

        Assert.NotNull(exception.ObservedStateHash);
        Assert.NotEqual(exception.PriorStateHash, exception.AttemptedStateHash);
        Assert.Equal(before, Serialize(fixture.Session.Document));
        Assert.Equal("injected third durable state", fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);

        var beforeRejectedMutation = Serialize(fixture.Session.Document);
        var beforeRejectedUndoCount = fixture.Session.UndoRedo.UndoCount;
        Assert.Throws<InvalidOperationException>(() =>
            planner.RenamePlan(planner.CurrentPlan!, "must remain unchanged while unknown"));
        Assert.Equal(beforeRejectedMutation, Serialize(fixture.Session.Document));
        Assert.Equal(beforeRejectedUndoCount, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal(beforeUndoCount, fixture.Session.UndoRedo.UndoCount);

        var periodBefore = Serialize(fixture.Session.Document);
        var periodUndoCount = fixture.Session.UndoRedo.UndoCount;
        var firstPeriod = settings.SelectedSemester!.PeriodSchedule[0];
        Assert.Throws<InvalidOperationException>(() => settings.UpdatePeriodTime(
            firstPeriod.Period,
            firstPeriod.Start.AddMinutes(-1),
            firstPeriod.End.AddMinutes(-1)));
        Assert.Equal(periodBefore, Serialize(fixture.Session.Document));
        Assert.Equal(periodUndoCount, fixture.Session.UndoRedo.UndoCount);

        var languageBefore = Serialize(fixture.Session.Document);
        var resolvedLanguageBefore = localization.Localizer.ResolvedLanguage;
        Assert.Throws<InvalidOperationException>(() =>
            localization.ApplyLanguage(LanguageMode.SimplifiedChinese));
        Assert.Equal(languageBefore, Serialize(fixture.Session.Document));
        Assert.Equal(resolvedLanguageBefore, localization.Localizer.ResolvedLanguage);

        var semesterBefore = Serialize(fixture.Session.Document);
        var currentSemesterId = planner.CurrentSemester!.SemesterId;
        var otherSemester = planner.Semesters.Single(semester => semester.SemesterId != currentSemesterId);
        Assert.Throws<InvalidOperationException>(() => planner.CurrentSemester = otherSemester);
        Assert.Equal(semesterBefore, Serialize(fixture.Session.Document));
        Assert.Equal(currentSemesterId, planner.CurrentSemester!.SemesterId);
    }

    [Fact]
    public void VerificationReadFailureDisablesWritesWithoutClaimingRollbackDurability()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.VerificationFails);
        var before = Serialize(fixture.Session.Document);
        var expectedTargetToken = fixture.Session.AcceptedStateToken;
        var rollbacks = new List<DocumentRolledBackEventArgs>();
        var accepted = 0;
        fixture.Session.RolledBack += (_, args) => rollbacks.Add(args);
        fixture.Session.StateAccepted += (_, _) => accepted++;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "unverified attempt";

        var exception = Assert.Throws<DocumentSessionCommitAmbiguityException>(() =>
            fixture.Session.Save("verification.failure"));

        Assert.IsType<IOException>(exception.VerificationException);
        Assert.Null(exception.ObservedStateHash);
        Assert.Equal(before, Serialize(fixture.Session.Document));
        var rollback = Assert.Single(rollbacks);
        Assert.Equal(DocumentRollbackTargetKind.PersistedBaseline, rollback.TargetKind);
        Assert.Equal(expectedTargetToken, rollback.TargetStateToken);
        Assert.False(rollback.DurableOutcomeKnown);
        Assert.Equal(0, accepted);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
    }

    [Fact]
    public void UnknownStateRollbackNotificationCannotRecursivelyStartAnotherCompensation()
    {
        using var fixture = AmbiguousSaveFixture.Create(SaveFailureMode.VerificationFails);
        var rollbackNotifications = 0;
        fixture.Session.RolledBack += (_, _) =>
        {
            rollbackNotifications++;
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Session.Save("reentrant.rollback.save"));
            Assert.Throws<InvalidOperationException>(fixture.Session.ReloadFromRepository);
        };
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "unverified attempt with reentrant subscriber";

        var exception = Assert.Throws<DocumentSessionCommitAmbiguityException>(() =>
            fixture.Session.Save("verification.failure.reentrant"));

        Assert.Equal(1, rollbackNotifications);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.NotEqual(exception.PriorStateHash, exception.AttemptedStateHash);
        Assert.Equal(
            fixture.Session.Document.Plans[0].PlanName,
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
    }

    [Fact]
    public void StrictVerificationOfMissingDatabaseDoesNotInitializeAnyArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new SqliteAppRepository(directory);
            var before = Directory.EnumerateFileSystemEntries(directory).Order().ToArray();

            Assert.Throws<FileNotFoundException>(repository.LoadExistingForVerification);

            Assert.Equal(before, Directory.EnumerateFileSystemEntries(directory).Order().ToArray());
            Assert.False(File.Exists(repository.DatabasePath));
            Assert.False(Directory.Exists(repository.LogsDirectory));
            Assert.False(Directory.Exists(repository.RecoveryDirectory));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void StrictVerificationOfMalformedStateDoesNotRecoverSeedOrCreateArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteAppRepository(directory);
            repository.Save(TestDocumentFactory.CreatePopulated(), "strict.seed");
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = repository.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE app_state SET json = '{' WHERE id = 'default'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var before = Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Order()
                .ToArray();

            Assert.ThrowsAny<JsonException>(repository.LoadExistingForVerification);

            Assert.Equal(
                before,
                Directory.EnumerateFileSystemEntries(directory)
                    .Select(Path.GetFileName)
                    .Order()
                    .ToArray());
            Assert.False(Directory.Exists(repository.RecoveryDirectory));
            Assert.Null(repository.LastRecoveryArtifactPath);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void StrictVerificationRoundTripsCanonicalDomainStateWithoutWritingEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteAppRepository(directory);
            var document = TestDocumentFactory.CreatePopulated();
            document.Plans[0].PlanName = "strict canonical round trip";
            repository.Save(document, "strict.roundtrip.seed");
            var eventCount = repository.ReadEventSummaries().Count;

            var verified = repository.LoadExistingForVerification();

            Assert.Equal(Serialize(document), Serialize(verified));
            Assert.Equal(eventCount, repository.ReadEventSummaries().Count);
            Assert.Null(repository.LastRecoveryArtifactPath);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void FailedUndoRestoresDocumentAndBothHistoryStacks()
    {
        using var fixture = Fixture.Create();
        var originalPlanName = fixture.Session.Document.Plans[0].PlanName;
        fixture.CommitPlanName("committed change");
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.Undo());

        fixture.AssertState(before);
        fixture.Storage.FailSave = false;
        Assert.True(fixture.Session.Undo());
        Assert.Equal(originalPlanName, fixture.Session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void FailedRedoRestoresDocumentAndBothHistoryStacks()
    {
        using var fixture = Fixture.Create();
        fixture.CommitPlanName("committed change");
        Assert.True(fixture.Session.Undo());
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.Redo());

        fixture.AssertState(before);
        fixture.Storage.FailSave = false;
        Assert.True(fixture.Session.Redo());
        Assert.Equal("committed change", fixture.Session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void UndoAndRedoCheckpointCaptureFailuresRestoreExactStateAndRemainRetryable()
    {
        using var fixture = ParticipantFixture.Create();
        var originalName = fixture.Session.Document.Plans[0].PlanName;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "participant committed change";
        fixture.Participant.Value = 1;
        fixture.Session.Save("participant.seed-change");

        var beforeUndo = fixture.CaptureState();
        var beforeUndoDocument = fixture.Session.Document;
        fixture.Participant.FailCaptureAfter(3);

        Assert.Throws<InvalidOperationException>(() => fixture.Session.Undo());

        fixture.AssertState(beforeUndo);
        Assert.Same(beforeUndoDocument, fixture.Session.Document);
        Assert.Equal(1, fixture.Participant.Value);
        fixture.Participant.DisableCaptureFailure();
        Assert.True(fixture.Session.Undo());
        Assert.Equal(originalName, fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(0, fixture.Participant.Value);

        var beforeRedo = fixture.CaptureState();
        var beforeRedoDocument = fixture.Session.Document;
        fixture.Participant.FailCaptureAfter(3);

        Assert.Throws<InvalidOperationException>(() => fixture.Session.Redo());

        fixture.AssertState(beforeRedo);
        Assert.Same(beforeRedoDocument, fixture.Session.Document);
        Assert.Equal(0, fixture.Participant.Value);
        fixture.Participant.DisableCaptureFailure();
        Assert.True(fixture.Session.Redo());
        Assert.Equal("participant committed change", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(1, fixture.Participant.Value);
    }

    [Fact]
    public void FailedCompensationIsReportedAndDisablesFurtherMutation()
    {
        var failSave = false;
        FaultingParticipant? participant = null;
        using var fixture = ParticipantFixture.Create((repository, document, eventName) =>
        {
            if (failSave)
            {
                participant!.FailRestore = true;
                throw new IOException("injected precommit failure requiring compensation");
            }
            repository.Save(document, eventName);
        });
        participant = fixture.Participant;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "first committed participant state";
        fixture.Participant.Value = 1;
        fixture.Session.Save("participant.first-commit");
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "failed participant state";
        fixture.Participant.Value = 2;
        failSave = true;

        var exception = Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("participant.compensation-failure"));

        Assert.IsType<IOException>(exception.OperationException);
        Assert.IsType<InvalidOperationException>(exception.CompensationException);
        Assert.True(fixture.Session.IsSessionConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
    }

    [Fact]
    public void AuxiliaryAlignmentFailureBeforeSaveDoesNotCallSaveDelegateAndIsRetryable()
    {
        var saveCalls = 0;
        using var fixture = ParticipantFixture.Create((repository, document, eventName) =>
        {
            saveCalls++;
            repository.Save(document, eventName);
        });
        var before = fixture.CaptureState();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "rejected alignment state";
        fixture.Participant.Value = 1;
        fixture.Participant.FailRestoreAfter(1);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.Save("participant.alignment-failure"));

        Assert.Equal(0, saveCalls);
        fixture.AssertState(before);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);

        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "retried aligned state";
        fixture.Participant.Value = 2;
        fixture.Session.Save("participant.alignment-retry");

        Assert.Equal(1, saveCalls);
        Assert.Equal("retried aligned state", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(2, fixture.Participant.Value);
    }

    [Fact]
    public void EquivalentReplaceAlignmentFailureRestoresOriginalGraphAndIsRetryable()
    {
        var saveCalls = 0;
        using var fixture = ParticipantFixture.Create((repository, document, eventName) =>
        {
            saveCalls++;
            repository.Save(document, eventName);
        });
        var originalGraph = fixture.Session.Document;
        var before = fixture.CaptureState();
        var replacement = JsonDefaults.Clone(originalGraph);
        fixture.Participant.FailRestoreAfter(1);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.ReplaceDocument(replacement, "participant.equivalent-alignment-failure"));

        Assert.Equal(0, saveCalls);
        Assert.Same(originalGraph, fixture.Session.Document);
        fixture.AssertState(before);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);

        var retry = JsonDefaults.Clone(originalGraph);
        fixture.Session.ReplaceDocument(retry, "participant.equivalent-alignment-retry");

        Assert.Equal(0, saveCalls);
        Assert.Same(retry, fixture.Session.Document);
    }

    [Fact]
    public void ExternalReloadAlignmentFailureKeepsOldMemoryUnknownUntilRetry()
    {
        using var fixture = ParticipantFixture.Create();
        var priorGraph = fixture.Session.Document;
        var prior = fixture.CaptureState();
        var external = JsonDefaults.Clone(priorGraph);
        external.Plans[0].PlanName = "external state after alignment failure";
        fixture.Repository.Save(external, "participant.external-state");
        fixture.Participant.FailRestoreAfter(1);

        var exception = Assert.Throws<DocumentSessionReloadConsistencyException>(
            fixture.Session.ReloadFromRepository);

        Assert.IsType<InvalidOperationException>(exception.OperationException);
        Assert.Same(priorGraph, fixture.Session.Document);
        fixture.AssertInMemoryState(prior);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(
            "external state after alignment failure",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);

        fixture.Session.ReloadFromRepository();

        Assert.Equal("external state after alignment failure", fixture.Session.Document.Plans[0].PlanName);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void ExternalReloadClearFailureKeepsOldMemoryUnknownUntilRetry()
    {
        using var fixture = ParticipantFixture.Create();
        var priorGraph = fixture.Session.Document;
        var prior = fixture.CaptureState();
        var external = JsonDefaults.Clone(priorGraph);
        external.Plans[0].PlanName = "external state after clear failure";
        fixture.Repository.Save(external, "participant.external-clear-state");
        fixture.Participant.FailClear = true;

        var exception = Assert.Throws<DocumentSessionReloadConsistencyException>(
            fixture.Session.ReloadFromRepository);

        Assert.IsType<InvalidOperationException>(exception.OperationException);
        Assert.Same(priorGraph, fixture.Session.Document);
        fixture.AssertInMemoryState(prior);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(
            "external state after clear failure",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);

        fixture.Participant.FailClear = false;
        fixture.Session.ReloadFromRepository();

        Assert.Equal("external state after clear failure", fixture.Session.Document.Plans[0].PlanName);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);
    }

    [Fact]
    public void RestoreAlignmentFailureRollsBackDatabaseAndSessionAndIsRetryable()
    {
        using var fixture = ParticipantFixture.Create();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "restore alignment original state";
        fixture.Participant.Value = 7;
        fixture.Session.Save("participant.restore-alignment-original");
        var originalName = fixture.Session.Document.Plans[0].PlanName;
        var prior = fixture.CaptureState();
        var sourceDirectory = Path.Combine(fixture.DirectoryPath, "alignment-source");
        var automaticDirectory = Path.Combine(fixture.DirectoryPath, "alignment-automatic");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(automaticDirectory);
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var candidate = JsonDefaults.Clone(fixture.Session.Document);
        candidate.Plans[0].PlanName = "restore candidate after alignment failure";
        sourceRepository.Save(candidate, "participant.restore-alignment-candidate");
        var backupPath = Path.Combine(fixture.DirectoryPath, "alignment-candidate.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        using var transaction = fixture.Session.BeginBackupRestore(backupPath, automaticDirectory);
        fixture.Participant.FailRestoreAfter(1);
        var changed = 0;
        var rolledBack = 0;
        var refresh = 0;
        fixture.Session.Changed += (_, _) => changed++;
        fixture.Session.RolledBack += (_, _) => rolledBack++;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.ApplyBackupRestore(transaction, () => refresh++));

        Assert.Contains("participant restore failure", exception.Message, StringComparison.Ordinal);
        Assert.True(transaction.IsCompleted);
        Assert.Equal(0, changed);
        Assert.Equal(1, rolledBack);
        Assert.Equal(0, refresh);
        fixture.AssertInMemoryState(prior);
        Assert.Equal(originalName, fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            originalName,
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);

        using var retry = fixture.Session.BeginBackupRestore(backupPath, automaticDirectory);
        fixture.Session.ApplyBackupRestore(retry);

        Assert.True(retry.IsCompleted);
        Assert.Equal("restore candidate after alignment failure", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            "restore candidate after alignment failure",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
    }

    [Fact]
    public void RestoreClearFailureRollsBackDatabaseAndSessionAndIsRetryable()
    {
        using var fixture = ParticipantFixture.Create();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "restore clear original state";
        fixture.Participant.Value = 11;
        fixture.Session.Save("participant.restore-clear-original");
        var originalName = fixture.Session.Document.Plans[0].PlanName;
        var prior = fixture.CaptureState();
        var sourceDirectory = Path.Combine(fixture.DirectoryPath, "clear-source");
        var automaticDirectory = Path.Combine(fixture.DirectoryPath, "clear-automatic");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(automaticDirectory);
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        var candidate = JsonDefaults.Clone(fixture.Session.Document);
        candidate.Plans[0].PlanName = "restore candidate after clear failure";
        sourceRepository.Save(candidate, "participant.restore-clear-candidate");
        var backupPath = Path.Combine(fixture.DirectoryPath, "clear-candidate.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
        using var transaction = fixture.Session.BeginBackupRestore(backupPath, automaticDirectory);
        fixture.Participant.FailClearAfterReset = true;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.ApplyBackupRestore(transaction));

        Assert.Contains("participant clear failure", exception.Message, StringComparison.Ordinal);
        Assert.True(transaction.IsCompleted);
        fixture.AssertInMemoryState(prior);
        Assert.Equal(originalName, fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            originalName,
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.False(fixture.Session.IsSessionConsistencyUnknown);

        using var retry = fixture.Session.BeginBackupRestore(backupPath, automaticDirectory);
        fixture.Session.ApplyBackupRestore(retry);

        Assert.True(retry.IsCompleted);
        Assert.Equal("restore candidate after clear failure", fixture.Session.Document.Plans[0].PlanName);
        Assert.Equal(
            "restore candidate after clear failure",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);
    }

    [Fact]
    public void FailedReplaceRestoresPriorDocumentAndHistory()
    {
        using var fixture = Fixture.Create();
        fixture.CommitPlanName("committed change");
        var before = fixture.CaptureState();
        var replacement = TestDocumentFactory.CreatePopulated();
        replacement.Plans[0].PlanName = "replacement";
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.ReplaceDocument(replacement, "failing.replace"));

        fixture.AssertState(before);
        fixture.Storage.FailSave = false;
        Assert.True(fixture.Session.Undo());
        Assert.Equal("Balanced Plan", fixture.Session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void ReplaceClearFailureRestoresOriginalGraphHistoryAndAuxiliaryState()
    {
        using var fixture = ParticipantFixture.Create();
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "replace baseline";
        fixture.Participant.Value = 7;
        fixture.Session.Save("replace.baseline");
        var before = fixture.CaptureState();
        var beforeDocument = fixture.Session.Document;
        var replacement = JsonDefaults.Clone(fixture.Session.Document);
        replacement.Plans[0].PlanName = "replacement must fail during clear";
        fixture.Participant.FailClear = true;

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.ReplaceDocument(replacement, "replace.clear-failure"));

        fixture.AssertState(before);
        Assert.Same(beforeDocument, fixture.Session.Document);
        Assert.Equal(7, fixture.Participant.Value);
        fixture.Participant.FailClear = false;
        fixture.Session.ReplaceDocument(replacement, "replace.clear-retry");
        Assert.Equal("replacement must fail during clear", fixture.Session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void EquivalentReplaceCheckpointFailureRestoresOriginalGraphAndIsRetryable()
    {
        using var fixture = ParticipantFixture.Create();
        var before = fixture.CaptureState();
        var beforeDocument = fixture.Session.Document;
        var replacement = JsonDefaults.Clone(fixture.Session.Document);
        fixture.Participant.FailCaptureAfter(2);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Session.ReplaceDocument(replacement, "replace.capture-failure"));

        fixture.AssertState(before);
        Assert.Same(beforeDocument, fixture.Session.Document);
        fixture.Participant.DisableCaptureFailure();
        fixture.Session.ReplaceDocument(replacement, "replace.capture-retry");
        Assert.Same(replacement, fixture.Session.Document);
    }

    [Fact]
    public void ContentIdenticalReplacementRebindsProjectionsAndStartsNewHistory()
    {
        using var fixture = Fixture.Create();
        fixture.CommitPlanName("committed before equivalent replacement");
        var replacement = JsonDefaults.Clone(fixture.Session.Document);
        var beforeEventCount = fixture.Session.Repository.ReadEventSummaries().Count;
        var accepted = new List<DocumentStateAcceptedEventArgs>();
        fixture.Session.StateAccepted += (_, args) => accepted.Add(args);

        fixture.Session.ReplaceDocument(replacement, "equivalent.replace");

        var acceptance = Assert.Single(accepted);
        Assert.Equal(DocumentStateAcceptanceKind.Replace, acceptance.Kind);
        Assert.Equal("equivalent.replace", acceptance.EventName);
        Assert.Equal(fixture.Session.AcceptedStateToken, acceptance.AcceptedStateToken);
        Assert.Same(replacement, fixture.Session.Document);
        Assert.Equal(beforeEventCount, fixture.Session.Repository.ReadEventSummaries().Count);
        Assert.Equal(0, fixture.Session.UndoRedo.UndoCount);
        Assert.Equal(0, fixture.Session.UndoRedo.RedoCount);
        Assert.Equal(0, fixture.Session.UndoRedo.HistoryBytes);
        Assert.Equal(1, fixture.ChangedCount);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
        fixture.AssertSettingsProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedReloadLeavesCurrentDocumentAndHistoryUntouched()
    {
        using var fixture = Fixture.Create();
        fixture.CommitPlanName("committed change");
        var before = fixture.CaptureState();
        fixture.Storage.FailLoad = true;

        Assert.Throws<IOException>(() => fixture.Session.ReloadFromRepository());

        fixture.AssertState(before);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
    }

    [Fact]
    public void ExternalReloadCheckpointFailureKeepsOldMemoryButDisablesWritesUntilRetry()
    {
        using var fixture = ParticipantFixture.Create();
        var before = fixture.CaptureState();
        var beforeDocument = fixture.Session.Document;
        var expectedTargetToken = StateToken(beforeDocument);
        var rollbacks = new List<DocumentRolledBackEventArgs>();
        fixture.Session.RolledBack += (_, args) => rollbacks.Add(args);
        var external = JsonDefaults.Clone(fixture.Session.Document);
        external.Plans[0].PlanName = "externally replaced repository state";
        fixture.Repository.Save(external, "external.replace");
        fixture.Participant.FailCaptureAfter(2);

        var exception = Assert.Throws<DocumentSessionReloadConsistencyException>(
            fixture.Session.ReloadFromRepository);

        Assert.NotEqual(exception.PriorStateHash, exception.LoadedStateHash);
        var rollback = Assert.Single(rollbacks);
        Assert.Equal(DocumentRollbackTargetKind.OperationStart, rollback.TargetKind);
        Assert.Equal(expectedTargetToken, rollback.TargetStateToken);
        Assert.False(rollback.DurableOutcomeKnown);
        fixture.AssertInMemoryState(before);
        Assert.Same(beforeDocument, fixture.Session.Document);
        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Throws<InvalidOperationException>(fixture.Session.EnsureMutationAllowed);
        Assert.Equal(
            "externally replaced repository state",
            fixture.Repository.LoadExistingForVerification().Plans[0].PlanName);

        // The next live capture also fails. Unknown-state reload must fall back
        // to the last accepted checkpoint instead of self-locking before it can
        // read and install the repository state.
        fixture.Participant.FailCaptureAfter(1);
        fixture.Session.ReloadFromRepository();
        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal("externally replaced repository state", fixture.Session.Document.Plans[0].PlanName);
    }

    [Fact]
    public void SessionRestoreReservationBlocksMutationAndReloadBeforeApply()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "reservation original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "reservation candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            using var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);

            session.Document.Plans[0].PlanName = "speculative C must not be saved";
            Assert.Throws<InvalidOperationException>(() => session.Save("blocked.pending.restore"));
            Assert.Throws<InvalidOperationException>(session.ReloadFromRepository);
            Assert.Throws<InvalidOperationException>(session.CaptureUndo);
            Assert.Equal(
                "reservation candidate B",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);

            // Restore the in-memory baseline before applying so the candidate
            // load, rather than the speculative graph, is what gets accepted.
            session.Document.Plans[0].PlanName = "reservation original A";
            session.ApplyBackupRestore(transaction);

            Assert.True(transaction.IsCompleted);
            Assert.Equal("reservation candidate B", session.Document.Plans[0].PlanName);
            Assert.Equal(
                "reservation candidate B",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task RestoreReservationWaitsForAnInFlightSessionSaveBeforePublishingCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "operation gate original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "operation gate candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            using var saveEntered = new ManualResetEventSlim();
            using var allowSave = new ManualResetEventSlim();
            var session = new DocumentSession(
                targetRepository,
                saveDocument: (document, eventName) =>
                {
                    saveEntered.Set();
                    Assert.True(allowSave.Wait(TimeSpan.FromSeconds(10)));
                    targetRepository.Save(document, eventName);
                });
            session.Document.Plans[0].PlanName = "in-flight committed state";
            var saveTask = Task.Run(() => session.Save("in-flight.before.restore"));
            Assert.True(saveEntered.Wait(TimeSpan.FromSeconds(10)));

            var beginTask = Task.Run(() => session.BeginBackupRestore(
                backupPath,
                automaticDirectory));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(beginTask.IsCompleted);

            allowSave.Set();
            await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            using var transaction = await beginTask.WaitAsync(TimeSpan.FromSeconds(10));
            session.ApplyBackupRestore(transaction);

            Assert.True(transaction.IsCompleted);
            Assert.Equal("operation gate candidate B", session.Document.Plans[0].PlanName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void SessionPendingRestoreCannotAcceptAThirdDatabaseStateAsItsCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "external pending original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "external pending candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);
            var exactCandidateBytes = File.ReadAllBytes(targetRepository.DatabasePath);
            var third = TestDocumentFactory.CreatePopulated();
            third.Plans[0].PlanName = "external pending third state C";
            targetRepository.Save(third, "external.third");

            var exception = Assert.Throws<DocumentRestoreConsistencyException>(() =>
                session.ApplyBackupRestore(transaction));

            Assert.IsType<BackupRestoreCandidateChangedException>(exception.RestoreException);
            Assert.IsType<InvalidOperationException>(exception.RollbackException);
            Assert.False(transaction.IsCompleted);
            Assert.True(session.IsStorageConsistencyUnknown);
            Assert.Equal(
                "external pending third state C",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
            Assert.Throws<BackupRestoreCandidateChangedException>(session.ReloadFromRepository);
            Assert.False(transaction.IsCompleted);
            Assert.True(session.IsStorageConsistencyUnknown);
            Assert.Equal(
                "external pending third state C",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);

            var poolBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = targetRepository.DatabasePath
            };
            using (var poolConnection = new SqliteConnection(poolBuilder.ToString()))
                SqliteConnection.ClearPool(poolConnection);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                File.Delete(targetRepository.DatabasePath + suffix);
            File.WriteAllBytes(targetRepository.DatabasePath, exactCandidateBytes);
            session.ReloadFromRepository();

            Assert.True(transaction.IsCompleted);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.Equal("external pending candidate B", session.Document.Plans[0].PlanName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void ReloadDuringPostCommitRefreshIsRejectedWithoutRollingBackTheAcceptedCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "restore reentrancy original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "restore reentrancy candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            using var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);
            var refreshCalls = 0;

            var exception = Assert.Throws<DocumentRestorePostCommitException>(() =>
                session.ApplyBackupRestore(
                    transaction,
                    refreshRestoredState: () =>
                    {
                        refreshCalls++;
                        session.ReloadFromRepository();
                    }));

            Assert.Equal(1, refreshCalls);
            Assert.True(transaction.IsCompleted);
            Assert.IsType<InvalidOperationException>(exception.RefreshException);
            Assert.Equal("restore reentrancy candidate B", session.Document.Plans[0].PlanName);
            Assert.Equal(
                "restore reentrancy candidate B",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.False(session.IsSessionConsistencyUnknown);

            session.ReloadFromRepository();

            Assert.Equal("restore reentrancy candidate B", session.Document.Plans[0].PlanName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void PostCommitRestoreCallbackMutationMarksSessionUnknownUntilCandidateIsReloaded()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "restore callback original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "restore callback candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            using var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);
            var refreshCalls = 0;

            var exception = Assert.Throws<DocumentRestorePostCommitException>(() =>
                session.ApplyBackupRestore(
                    transaction,
                    refreshRestoredState: () =>
                    {
                        refreshCalls++;
                        session.Document.Plans[0].PlanName = "caught callback mutation C";
                        Assert.Throws<InvalidOperationException>(() => session.Save("blocked.restore.save"));
                        Assert.Throws<InvalidOperationException>(session.ReloadFromRepository);
                        Assert.Throws<InvalidOperationException>(session.CaptureUndo);
                        Assert.Throws<InvalidOperationException>(() =>
                            session.RestoreFromBackup(backupPath, automaticDirectory));
                    }));

            Assert.Equal(1, refreshCalls);
            Assert.NotNull(exception.InvariantException);
            Assert.Null(exception.RefreshException);
            Assert.Equal("caught callback mutation C", session.Document.Plans[0].PlanName);
            Assert.Equal(
                "restore callback candidate B",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.True(session.IsSessionConsistencyUnknown);
            Assert.Throws<InvalidOperationException>(session.EnsureMutationAllowed);

            session.ReloadFromRepository();

            Assert.Equal("restore callback candidate B", session.Document.Plans[0].PlanName);
            Assert.False(session.IsStorageConsistencyUnknown);
            Assert.False(session.IsSessionConsistencyUnknown);
            Assert.False(session.IsStorageConsistencyUnknown);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void RecursiveApplyAfterCommitIsRejectedWithoutRollingBackTheOuterTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            targetRepository.Save(TestDocumentFactory.CreatePopulated(), "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "outer restore remains committable";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var session = new DocumentSession(targetRepository);
            using var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);
            var recursiveAttempts = 0;

            session.ApplyBackupRestore(
                transaction,
                refreshRestoredState: () =>
                {
                    recursiveAttempts++;
                    Assert.Throws<ObjectDisposedException>(() =>
                        session.ApplyBackupRestore(transaction));
                });

            Assert.Equal(1, recursiveAttempts);
            Assert.True(transaction.IsCompleted);
            Assert.Equal("outer restore remains committable", session.Document.Plans[0].PlanName);
            Assert.Equal(
                "outer restore remains committable",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void RestoreRollbackCallbackMutationMarksSessionUnknownInsteadOfClaimingCompensation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var targetDirectory = Path.Combine(root, "target");
            var sourceDirectory = Path.Combine(root, "source");
            var automaticDirectory = Path.Combine(root, "automatic");
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(automaticDirectory);
            var targetRepository = new SqliteAppRepository(targetDirectory);
            var sourceRepository = new SqliteAppRepository(sourceDirectory);
            var original = TestDocumentFactory.CreatePopulated();
            original.Plans[0].PlanName = "compensation original A";
            targetRepository.Save(original, "target.seed");
            var candidate = TestDocumentFactory.CreatePopulated();
            candidate.Plans[0].PlanName = "compensation candidate B";
            sourceRepository.Save(candidate, "source.seed");
            var backupPath = Path.Combine(root, "candidate.zip");
            BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);
            var loadCount = 0;
            var session = new DocumentSession(
                targetRepository,
                loadDocument: () =>
                {
                    if (Interlocked.Increment(ref loadCount) == 2)
                        throw new IOException("force candidate rollback before commit");
                    return targetRepository.LoadOrCreate();
                });
            session.RolledBack += (_, _) =>
            {
                session.Document.Plans[0].PlanName = "compensation callback mutation C";
                Assert.Throws<InvalidOperationException>(session.CaptureUndo);
            };
            using var transaction = session.BeginBackupRestore(
                backupPath,
                automaticDirectory);
            var exception = Assert.Throws<DocumentRestoreCompensationException>(() =>
                session.ApplyBackupRestore(transaction));

            Assert.IsType<IOException>(exception.RestoreException);
            Assert.Contains(
                EnumerateExceptions(exception),
                failure => failure is InvalidOperationException &&
                           failure.Message.Contains("callbacks changed", StringComparison.Ordinal));
            Assert.True(transaction.IsCompleted);
            Assert.Equal("compensation callback mutation C", session.Document.Plans[0].PlanName);
            Assert.Equal(
                "compensation original A",
                targetRepository.LoadExistingForVerification().Plans[0].PlanName);
            Assert.True(session.IsSessionConsistencyUnknown);
            Assert.Throws<InvalidOperationException>(session.EnsureMutationAllowed);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void FailedNotifyFalsePlanSwitchRestoresViewModelProjectionAndPreservesActiveEdit()
    {
        using var fixture = Fixture.Create();
        var originalPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var otherPlan = fixture.ViewModel.OpenPlans.Single(plan => plan.PlanId != originalPlanId);
        var edit = fixture.BeginChangedCourseEdit("draft survives plan switch failure");
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CurrentPlan = otherPlan);

        fixture.AssertState(before);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Same(
            fixture.Session.Document.Plans.Single(plan => plan.PlanId == originalPlanId),
            fixture.ViewModel.CurrentPlan);
        Assert.Same(edit, fixture.ViewModel.ActiveEdit);
        Assert.Equal("draft survives plan switch failure", fixture.ViewModel.ActiveEdit?.Course.CourseName);
        Assert.True(fixture.ViewModel.HasUnsavedCourseEdit);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedCrossSemesterPlanSwitchRestoresTheUsersWeek()
    {
        using var fixture = Fixture.Create();
        var originalPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var originalSemesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var shortSemester = new Semester
        {
            SemesterId = "failed-plan-switch-short-semester",
            SemesterName = "Short semester",
            StartDate = new DateOnly(2026, 9, 7),
            EndDate = new DateOnly(2026, 10, 4),
            DisplayOrder = fixture.Session.Document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        var shortSemesterPlan = new SelectionPlan
        {
            PlanId = "failed-plan-switch-short-plan",
            SemesterId = shortSemester.SemesterId,
            PlanName = "Short semester plan",
            DisplayOrder = fixture.Session.Document.Plans.Count
        };
        fixture.Session.Document.Semesters.Add(shortSemester);
        fixture.Session.Document.Plans.Add(shortSemesterPlan);
        fixture.Session.Save("test.add-short-semester-plan");
        fixture.ResetChangedCount();
        fixture.ViewModel.CurrentWeek = 12;
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CurrentPlan = shortSemesterPlan);

        fixture.AssertState(before);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(originalSemesterId, fixture.ViewModel.CurrentSemester?.SemesterId);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedCurrentSemesterSwitchRestoresTheUsersWeek()
    {
        using var fixture = Fixture.Create();
        var originalPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var originalSemesterId = fixture.ViewModel.CurrentSemester!.SemesterId;
        var shortSemester = new Semester
        {
            SemesterId = "failed-semester-switch-short-semester",
            SemesterName = "Short semester",
            StartDate = new DateOnly(2026, 9, 7),
            EndDate = new DateOnly(2026, 10, 4),
            DisplayOrder = fixture.Session.Document.Semesters.Count,
            PeriodSchedule = PeriodScheduleFactory.CreateDefault12()
        };
        fixture.Session.Document.Semesters.Add(shortSemester);
        fixture.Session.Save("test.add-short-semester");
        fixture.ResetChangedCount();
        fixture.ViewModel.CurrentWeek = 12;
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CurrentSemester = shortSemester);

        fixture.AssertState(before);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(originalPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(originalSemesterId, fixture.ViewModel.CurrentSemester?.SemesterId);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedComparisonSwapRestoresTheCompleteComparisonContext()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var basePlan = plans[0];
        var currentPlan = plans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);
        var selectedPlanIds = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        Assert.Same(basePlan, fixture.ViewModel.BaseComparePlan);
        Assert.Same(currentPlan, fixture.ViewModel.CurrentPlan);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(fixture.ViewModel.SwapComparison);

        fixture.AssertState(before);
        Assert.Equal(basePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(currentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(selectedPlanIds, fixture.ViewModel.SelectedComparisonPlanIds);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void RejectedComparisonSwapDoesNotMutateContextBeforeTheMutationGate()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.OpenComparison(plans[0], plans[1]);
        var expectedBasePlanId = fixture.ViewModel.BaseComparePlan!.PlanId;
        var expectedCurrentPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        EventHandler corruptingAcceptedState = (_, _) =>
            fixture.Session.Document.Plans[0].PlanName = "subscriber-only unknown state";
        fixture.Session.SaveAccepted += corruptingAcceptedState;
        fixture.Session.Document.CourseLibrary[0].Notes = "force accepted state invariant check";
        Assert.Throws<DocumentSessionConsistencyException>(() =>
            fixture.Session.Save("test.make-session-unknown", notify: false));

        Assert.Throws<InvalidOperationException>(fixture.ViewModel.SwapComparison);

        Assert.Equal(expectedBasePlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(expectedCurrentPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
    }

    [Fact]
    public void ValidationRejectedComparisonSwapLeavesNoStaleRollbackSnapshot()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.OpenComparison(plans[0], plans[1]);
        var expectedBasePlanId = fixture.ViewModel.BaseComparePlan!.PlanId;
        var expectedCurrentPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        var course = fixture.Session.Document.CourseLibrary[0];
        var originalNotes = course.Notes;
        var overflowLength = checked((int)(
            PlannerDataLimits.MaxAggregateTextCharacters -
            PlannerDocumentTextCapacity.Count(fixture.Session.Document) +
            1));
        course.Notes += new string('x', overflowLength);

        fixture.ViewModel.SwapComparison();

        Assert.Equal(expectedBasePlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(expectedCurrentPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        course.Notes = originalNotes;
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[0]);
        var latestSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CreatePlan("later failure after rejected swap"));

        Assert.Equal(latestSelection, fixture.ViewModel.SelectedComparisonPlanIds);
    }

    [Fact]
    public void FailedActiveCourseSaveRestoresProjectionAndDoesNotDiscardInput()
    {
        using var fixture = Fixture.Create();
        var edit = fixture.BeginChangedCourseEdit("draft survives course save failure");
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.SaveActiveCourseEdit());

        fixture.AssertState(before);
        Assert.Same(edit, fixture.ViewModel.ActiveEdit);
        Assert.Equal("draft survives course save failure", fixture.ViewModel.ActiveEdit?.Course.CourseName);
        Assert.True(fixture.ViewModel.HasUnsavedCourseEdit);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedCourseDeletionDoesNotDiscardTheActiveEditForThatCourse()
    {
        using var fixture = Fixture.Create();
        var course = fixture.Session.Document.CourseLibrary[0];
        fixture.ViewModel.BeginEditLibraryCourse(course);
        fixture.ViewModel.UpdateActiveCourseEdit(edited => edited.Notes = "unsaved deletion-resistant notes");
        var edit = fixture.ViewModel.ActiveEdit;
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.DeleteLibraryCourse(course));

        fixture.AssertState(before);
        Assert.Same(edit, fixture.ViewModel.ActiveEdit);
        Assert.Equal("unsaved deletion-resistant notes", fixture.ViewModel.ActiveEdit?.Course.Notes);
        Assert.True(fixture.ViewModel.HasUnsavedCourseEdit);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedSettingsMutationRestoresBothSettingsAndPlannerProjections()
    {
        using var fixture = Fixture.Create();
        var semesterIds = fixture.Session.Document.Semesters.Select(semester => semester.SemesterId).ToArray();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Settings.AddSemester());

        fixture.AssertState(before);
        Assert.Equal(semesterIds, fixture.Settings.Semesters.Select(semester => semester.SemesterId));
        Assert.Equal(semesterIds, fixture.ViewModel.Semesters.Select(semester => semester.SemesterId));
        Assert.Contains(
            fixture.Session.Document.Semesters,
            semester => ReferenceEquals(semester, fixture.Settings.SelectedSemester));
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedLanguageSaveDoesNotApplyUnpersistedRuntimeLanguage()
    {
        using var fixture = Fixture.Create();
        Assert.Equal(LanguageMode.English, fixture.Localization.Localizer.ResolvedLanguage);
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Localization.ApplyLanguage(LanguageMode.SimplifiedChinese));

        fixture.AssertState(before);
        Assert.Equal(LanguageMode.English, fixture.Session.Document.Settings.Language);
        Assert.Equal(LanguageMode.English, fixture.Localization.Localizer.ResolvedLanguage);
    }

    [Theory]
    [InlineData("planner.create-plan")]
    [InlineData("planner.copy-plan")]
    [InlineData("planner.rename-plan")]
    [InlineData("planner.close-tab")]
    [InlineData("planner.reorder-tabs")]
    [InlineData("planner.delete-plan")]
    [InlineData("planner.clear-plan")]
    [InlineData("planner.reorder-registration")]
    [InlineData("planner.add-course")]
    [InlineData("planner.bulk-add-course")]
    [InlineData("planner.remove-course")]
    [InlineData("settings.add-semester")]
    [InlineData("settings.save-semester")]
    [InlineData("settings.clear-semester-courses")]
    [InlineData("settings.add-period")]
    [InlineData("settings.update-period")]
    [InlineData("settings.delete-period")]
    [InlineData("settings.reset-periods")]
    [InlineData("settings.upsert-label")]
    [InlineData("settings.delete-label")]
    [InlineData("settings.move-label")]
    public void FailedViewModelMutationsAreAtomicAcrossMutationFamilies(string operation)
    {
        using var fixture = Fixture.Create();
        var mutation = PrepareMutation(fixture, operation);
        var before = fixture.CaptureState();
        fixture.ResetChangedCount();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(mutation);

        fixture.AssertState(before);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
        fixture.AssertSettingsProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedCloseOfNewTabPreservesItsBaselineForTheNextCloseAttempt()
    {
        using var fixture = Fixture.Create();
        var newPlan = fixture.ViewModel.CreatePlanFromTab();
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.ClosePlanTab(newPlan));

        fixture.AssertState(before);
        fixture.Storage.FailSave = false;
        var restoredPlan = fixture.ViewModel.AllPlans.Single(plan => plan.PlanId == newPlan.PlanId);
        fixture.ViewModel.ClosePlanTab(restoredPlan);
        Assert.DoesNotContain(fixture.Session.Document.Plans, plan => plan.PlanId == newPlan.PlanId);
    }

    [Fact]
    public void FailedTabCloseRestoresTransientComparisonSelection()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[0]);
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[1]);
        var selectedIds = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.ClosePlanTab(plans[0]));

        fixture.AssertState(before);
        Assert.Equal(selectedIds, fixture.ViewModel.SelectedComparisonPlanIds);
    }

    [Fact]
    public void FailedDelayedTabCloseRestoresTheCompleteTransientContext()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var basePlan = plans[0];
        var currentPlan = plans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        var selectedPlanIds = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();

        fixture.ViewModel.ClosePlanTab(basePlan, persist: false);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        fixture.Storage.FailSave = true;
        Assert.Throws<IOException>(fixture.ViewModel.PersistPlanTabState);

        fixture.AssertState(before);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(basePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(currentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(selectedPlanIds, fixture.ViewModel.SelectedComparisonPlanIds);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void FailedMultiCloseBatchRestoresTheFirstTransientSnapshot()
    {
        using var fixture = Fixture.Create();
        fixture.ViewModel.CreatePlan("Third open plan");
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var basePlan = plans[0];
        var currentPlan = plans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        var expectedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();

        fixture.ViewModel.ClosePlanTab(basePlan, persist: false);
        fixture.ViewModel.ClosePlanTab(currentPlan, persist: false);
        Assert.Single(fixture.ViewModel.OpenPlans);
        fixture.Storage.FailSave = true;
        Assert.Throws<IOException>(fixture.ViewModel.PersistPlanTabState);

        fixture.AssertState(before);
        Assert.Equal(3, fixture.ViewModel.OpenPlans.Count);
        Assert.Equal(basePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(currentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
    }

    [Fact]
    public void ReloadToPendingSnapshotBaselineRestoresTheCompleteTransientContext()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var basePlan = plans[0];
        var currentPlan = plans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        fixture.ViewModel.SelectedCourse = fixture.ViewModel.LibraryCourses[0];
        fixture.ViewModel.IsDetailOpen = true;
        var expectedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        var expectedCourseId = fixture.ViewModel.SelectedCourse!.OfferingId;

        fixture.ViewModel.ClosePlanTab(basePlan, persist: false);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);

        fixture.Session.ReloadFromRepository();

        Assert.Equal(basePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(currentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.Equal(expectedCourseId, fixture.ViewModel.SelectedCourse?.OfferingId);
        Assert.True(fixture.ViewModel.IsDetailOpen);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void RestoreContainingTheSameDeletedPlanIdNeverRevivesAnOlderTransientContext()
    {
        using var fixture = Fixture.Create();
        var createdPlan = fixture.ViewModel.CreatePlanFromTab();
        var candidateCurrentPlan = fixture.ViewModel.OpenPlans.First(plan =>
            !string.Equals(plan.PlanId, createdPlan.PlanId, StringComparison.Ordinal));
        fixture.ViewModel.OpenComparison(candidateCurrentPlan, createdPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(candidateCurrentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(createdPlan);
        fixture.ViewModel.CurrentWeek = 12;

        var candidate = JsonDefaults.Clone(fixture.Session.Document);
        candidate.Settings.OpenPlanIds = [candidateCurrentPlan.PlanId];
        candidate.Settings.CurrentPlanId = candidateCurrentPlan.PlanId;
        candidate.Settings.CurrentSemesterId = candidateCurrentPlan.SemesterId;
        DocumentConsistencyService.Ensure(candidate);
        var sourceDirectory = Path.Combine(fixture.DirectoryPath, "same-id-restore-source");
        var automaticDirectory = Path.Combine(fixture.DirectoryPath, "same-id-restore-automatic");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(automaticDirectory);
        var sourceRepository = new SqliteAppRepository(sourceDirectory);
        sourceRepository.Save(candidate, "same-id.restore.source");
        var backupPath = Path.Combine(fixture.DirectoryPath, "same-id-restore.zip");
        BackupService.CreateBackup(sourceRepository.DatabasePath, backupPath);

        fixture.ViewModel.ClosePlanTab(createdPlan, persist: false);
        Assert.DoesNotContain(
            fixture.Session.Document.Plans,
            plan => string.Equals(plan.PlanId, createdPlan.PlanId, StringComparison.Ordinal));

        fixture.Session.RestoreFromBackup(backupPath, automaticDirectory);

        Assert.Contains(
            fixture.Session.Document.Plans,
            plan => string.Equals(plan.PlanId, createdPlan.PlanId, StringComparison.Ordinal));
        Assert.Equal(candidateCurrentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.DoesNotContain(createdPlan.PlanId, fixture.ViewModel.SelectedComparisonPlanIds);
        fixture.AssertPlannerProjectionReferencesCurrentDocument();
    }

    [Fact]
    public void VerifiedCommitThenNewFailedCloseRestoresOnlyTheNewGenerationSnapshot()
    {
        using var fixture = ControlledPlannerFixture.Create();
        fixture.ViewModel.CreatePlan("Third plan for snapshot generations");
        var firstGenerationClosedPlan = fixture.ViewModel.OpenPlans[0];
        fixture.ViewModel.CurrentWeek = 3;
        fixture.ViewModel.ClosePlanTab(firstGenerationClosedPlan, persist: false);
        fixture.SaveMode = ControlledSaveMode.CommitThenThrowVerified;

        fixture.ViewModel.PersistPlanTabState();

        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        fixture.SaveMode = ControlledSaveMode.Normal;
        var secondGenerationPlans = fixture.ViewModel.OpenPlans.ToArray();
        var expectedBasePlan = secondGenerationPlans[0];
        var expectedCurrentPlan = secondGenerationPlans[1];
        fixture.ViewModel.OpenComparison(expectedBasePlan, expectedCurrentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(expectedBasePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(expectedCurrentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        fixture.ViewModel.SelectedCourse = fixture.ViewModel.LibraryCourses[0];
        fixture.ViewModel.IsDetailOpen = true;
        var expectedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        var expectedCourseId = fixture.ViewModel.SelectedCourse!.OfferingId;

        fixture.ViewModel.ClosePlanTab(expectedBasePlan, persist: false);
        fixture.SaveMode = ControlledSaveMode.ThrowBeforeCommitVerified;
        Assert.Throws<IOException>(fixture.ViewModel.PersistPlanTabState);

        Assert.Equal(expectedBasePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(expectedCurrentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.Equal(expectedCourseId, fixture.ViewModel.SelectedCourse?.OfferingId);
        Assert.True(fixture.ViewModel.IsDetailOpen);
        Assert.NotEqual(firstGenerationClosedPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
    }

    [Fact]
    public void AmbiguousPendingCloseReloadedToBaselineRestoresThenConsumesItsSnapshot()
    {
        using var fixture = ControlledPlannerFixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var expectedBasePlan = plans[0];
        var expectedCurrentPlan = plans[1];
        fixture.ViewModel.OpenComparison(expectedBasePlan, expectedCurrentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(expectedBasePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(expectedCurrentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        var expectedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        fixture.ViewModel.ClosePlanTab(expectedBasePlan, persist: false);
        fixture.SaveMode = ControlledSaveMode.ThrowBeforeCommitVerificationFails;

        Assert.Throws<DocumentSessionCommitAmbiguityException>(
            fixture.ViewModel.PersistPlanTabState);

        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(expectedBasePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(expectedCurrentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);

        fixture.SaveMode = ControlledSaveMode.Normal;
        fixture.Session.ReloadFromRepository();

        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(expectedBasePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        Assert.Equal(expectedCurrentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(12, fixture.ViewModel.CurrentWeek);
        Assert.Equal(PlannerViewMode.Comparison, fixture.ViewModel.ViewMode);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);

        fixture.ViewModel.CurrentWeek = 6;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "later known failure after baseline reload";
        fixture.SaveMode = ControlledSaveMode.ThrowBeforeCommitVerified;
        Assert.Throws<IOException>(() => fixture.Session.Save("later.known.baseline.failure"));
        Assert.Equal(6, fixture.ViewModel.CurrentWeek);
    }

    [Fact]
    public void AmbiguousPendingCloseReloadedToAttemptedStateDiscardsItsSnapshot()
    {
        using var fixture = ControlledPlannerFixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var oldBasePlan = plans[0];
        var expectedCurrentPlan = plans[1];
        fixture.ViewModel.OpenComparison(oldBasePlan, expectedCurrentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(oldBasePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(expectedCurrentPlan);
        fixture.ViewModel.CurrentWeek = 12;
        fixture.ViewModel.ClosePlanTab(oldBasePlan, persist: false);
        fixture.SaveMode = ControlledSaveMode.CommitThenThrowVerificationFails;

        Assert.Throws<DocumentSessionCommitAmbiguityException>(
            fixture.ViewModel.PersistPlanTabState);

        Assert.True(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(oldBasePlan.PlanId, fixture.ViewModel.BaseComparePlan?.PlanId);
        fixture.SaveMode = ControlledSaveMode.Normal;
        fixture.Session.ReloadFromRepository();

        Assert.False(fixture.Session.IsStorageConsistencyUnknown);
        Assert.Equal(expectedCurrentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.DoesNotContain(oldBasePlan.PlanId, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.DoesNotContain(oldBasePlan.PlanId, fixture.Session.Document.Settings.OpenPlanIds);

        fixture.ViewModel.CurrentWeek = 6;
        fixture.Session.CaptureUndo();
        fixture.Session.Document.Plans[0].PlanName = "later known failure after attempted reload";
        fixture.SaveMode = ControlledSaveMode.ThrowBeforeCommitVerified;
        Assert.Throws<IOException>(() => fixture.Session.Save("later.known.attempted.failure"));
        Assert.Equal(6, fixture.ViewModel.CurrentWeek);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
    }

    [Fact]
    public void FailedUndoBackToALiveRollbackTargetDoesNotConsumeThePendingCloseSnapshot()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.RenamePlan(plans[0], "Undo baseline rename");
        fixture.ViewModel.OpenComparison(plans[0], plans[1]);
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[0]);
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[1]);
        fixture.ViewModel.ClosePlanTab(plans[1], persist: false);
        var expectedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        var expectedCurrentPlanId = fixture.ViewModel.CurrentPlan!.PlanId;
        Assert.Equal([plans[0].PlanId], expectedSelection);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.Session.Undo());

        Assert.DoesNotContain(plans[1].PlanId, fixture.Session.Document.Settings.OpenPlanIds);
        Assert.Equal(expectedCurrentPlanId, fixture.ViewModel.CurrentPlan?.PlanId);
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);

        fixture.Storage.FailSave = false;
        fixture.ViewModel.PersistPlanTabState();
        fixture.Storage.FailSave = true;
        Assert.Throws<IOException>(() => fixture.ViewModel.CreatePlan("later rejected after persisted live rollback"));
        Assert.Equal(expectedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.DoesNotContain(plans[1].PlanId, fixture.Session.Document.Settings.OpenPlanIds);
    }

    [Fact]
    public void LaterFailureCannotRestoreTabSelectionFromAnAlreadyPersistedCloseBatch()
    {
        using var fixture = Fixture.Create();
        var oldPlans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.ToggleComparisonPlanSelection(oldPlans[0]);
        fixture.ViewModel.ToggleComparisonPlanSelection(oldPlans[1]);
        foreach (var plan in oldPlans)
            fixture.ViewModel.ClosePlanTab(plan, persist: false);
        fixture.ViewModel.CreatePlanFromTab();
        Assert.Empty(fixture.ViewModel.SelectedComparisonPlanIds);
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CreatePlan("later failure"));

        fixture.AssertState(before);
        Assert.Empty(fixture.ViewModel.SelectedComparisonPlanIds);
    }

    [Fact]
    public void NotifyFalseSaveCommitsAPendingCloseBeforeALaterFailure()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[0]);
        fixture.ViewModel.ToggleComparisonPlanSelection(plans[1]);
        fixture.ViewModel.ClosePlanTab(plans[0], persist: false);
        var persistedSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        Assert.Equal([plans[1].PlanId], persistedSelection);

        fixture.Localization.ApplyLanguage(LanguageMode.SimplifiedChinese);
        Assert.DoesNotContain(
            plans[0].PlanId,
            fixture.Session.Document.Settings.OpenPlanIds);
        fixture.ResetChangedCount();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CreatePlan("later rejected plan"));

        Assert.Equal(persistedSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.DoesNotContain(
            plans[0].PlanId,
            fixture.Session.Document.Settings.OpenPlanIds);
    }

    [Fact]
    public void NoOpSaveCommitsTheLatestTransientContextBeforeALaterFailure()
    {
        using var fixture = Fixture.Create();
        var plans = fixture.ViewModel.OpenPlans.ToArray();
        var basePlan = plans[0];
        var currentPlan = plans[1];
        fixture.ViewModel.OpenComparison(basePlan, currentPlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(basePlan);
        fixture.ViewModel.ToggleComparisonPlanSelection(currentPlan);

        fixture.ViewModel.ClosePlanTab(currentPlan, persist: false);
        fixture.ViewModel.CurrentPlan = currentPlan;
        var latestSelection = fixture.ViewModel.SelectedComparisonPlanIds.ToArray();
        Assert.Equal([basePlan.PlanId], latestSelection);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.Equal(currentPlan.PlanId, fixture.Session.Document.Settings.CurrentPlanId);
        Assert.Equal([basePlan.PlanId, currentPlan.PlanId], fixture.Session.Document.Settings.OpenPlanIds);
        fixture.ResetChangedCount();
        fixture.Storage.FailSave = true;

        Assert.Throws<IOException>(() => fixture.ViewModel.CreatePlan("later rejected after no-op"));

        Assert.Equal(latestSelection, fixture.ViewModel.SelectedComparisonPlanIds);
        Assert.Null(fixture.ViewModel.BaseComparePlan);
        Assert.Equal(PlannerViewMode.Week, fixture.ViewModel.ViewMode);
        Assert.Equal(currentPlan.PlanId, fixture.ViewModel.CurrentPlan?.PlanId);
    }

    [Fact]
    public void SavingAnUnchangedDocumentRestoresSpeculativeHistoryAndDoesNotWriteOrNotify()
    {
        using var fixture = Fixture.Create();
        var before = fixture.CaptureState();
        fixture.Session.CaptureUndo();

        fixture.Session.Save("no-op.must-not-be-logged");

        fixture.AssertState(before);
    }

    [Fact]
    public void NoOpSaveDoesNotTouchAStorageThatWouldFailIfCalled()
    {
        using var fixture = Fixture.Create();
        var before = fixture.CaptureState();
        fixture.Session.CaptureUndo();
        fixture.Storage.FailSave = true;

        fixture.Session.Save("no-op.must-not-call-storage");

        fixture.AssertState(before);
    }

    [Fact]
    public void StaleCourseObjectCannotDeleteTheCurrentCourseOrItsPlanReferences()
    {
        using var fixture = Fixture.Create();
        var staleCourse = fixture.Session.Document.CourseLibrary[0];
        Assert.Contains(
            fixture.Session.Document.Plans.SelectMany(plan => plan.Snapshots),
            snapshot => snapshot.CourseOfferingId == staleCourse.OfferingId);
        fixture.Session.ReloadFromRepository();
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();

        fixture.ViewModel.DeleteLibraryCourse(staleCourse);

        fixture.AssertState(before);
        Assert.Contains(
            fixture.Session.Document.Plans.SelectMany(plan => plan.Snapshots),
            snapshot => snapshot.CourseOfferingId == staleCourse.OfferingId);
    }

    [Fact]
    public void RepeatingCourseDeletionIsANoOpWithoutHistoryOrEventPollution()
    {
        using var fixture = Fixture.Create();
        var course = fixture.Session.Document.CourseLibrary[0];
        fixture.ViewModel.DeleteLibraryCourse(course);
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();

        fixture.ViewModel.DeleteLibraryCourse(course);

        fixture.AssertState(before);
    }

    [Fact]
    public void RemovingMissingPlanCourseIsANoOpWithoutHistoryOrEventPollution()
    {
        using var fixture = Fixture.Create();
        var before = fixture.CaptureState();

        fixture.ViewModel.RemoveCourseFromCurrentPlan("missing-offering-id");

        fixture.AssertState(before);
    }

    [Fact]
    public void RenamingPlanToItsExistingNameIsANoOpWithoutTouchingModifiedTime()
    {
        using var fixture = Fixture.Create();
        var plan = fixture.ViewModel.CurrentPlan!;
        var modifiedAt = plan.ModifiedAt;
        var before = fixture.CaptureState();

        var validation = fixture.ViewModel.RenamePlan(plan, $"  {plan.PlanName}  ");

        Assert.True(validation.IsValid);
        fixture.AssertState(before);
        Assert.Equal(modifiedAt, plan.ModifiedAt);
    }

    [Fact]
    public void SavingAnUnchangedExistingCourseClosesTheEditWithoutPersistencePollution()
    {
        using var fixture = Fixture.Create();
        var course = fixture.Session.Document.CourseLibrary[0];
        fixture.ViewModel.BeginEditLibraryCourse(course);
        Assert.False(fixture.ViewModel.HasUnsavedCourseEdit);
        var modifiedAt = course.ModifiedAt;
        var before = fixture.CaptureState();

        var validation = fixture.ViewModel.SaveActiveCourseEdit();

        Assert.True(validation.IsValid);
        Assert.Null(fixture.ViewModel.ActiveEdit);
        fixture.AssertState(before);
        Assert.Equal(modifiedAt, fixture.Session.Document.CourseLibrary[0].ModifiedAt);
    }

    [Fact]
    public void AddCourseToPlanRejectsStalePlanInstanceEvenWhenItsIdMatches()
    {
        using var fixture = Fixture.Create();
        var stalePlan = JsonDefaults.Clone(fixture.ViewModel.CurrentPlan!);
        var staleSnapshotCount = stalePlan.Snapshots.Count;
        var course = CreateUnpersistedCourse(fixture);
        var before = fixture.CaptureState();

        var result = fixture.ViewModel.AddCourseToPlan(
            stalePlan,
            course,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.True(result.Cancelled);
        Assert.False(result.Added);
        Assert.Equal(staleSnapshotCount, stalePlan.Snapshots.Count);
        fixture.AssertState(before);
    }

    [Fact]
    public void BulkAddRejectsForgedPlanWithoutAddingItsCourseToTheLibrary()
    {
        using var fixture = Fixture.Create();
        var forgedPlan = JsonDefaults.Clone(fixture.ViewModel.CurrentPlan!);
        forgedPlan.PlanId = "forged-plan-id";
        var course = CreateUnpersistedCourse(fixture);
        var before = fixture.CaptureState();

        var result = fixture.ViewModel.AddCourseToPlans(
            [forgedPlan],
            course,
            DuplicateResolution.SkipExisting,
            ConflictResolution.KeepConflict);

        Assert.Equal(1, result.TargetCount);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(0, result.Added);
        fixture.AssertState(before);
    }

    [Fact]
    public void StaleCloneCannotCloseOrDeleteALiveNewPlanTab()
    {
        using var fixture = Fixture.Create();
        var livePlan = fixture.ViewModel.CreatePlanFromTab();
        var staleClone = JsonDefaults.Clone(livePlan);
        fixture.ResetChangedCount();
        var before = fixture.CaptureState();

        fixture.ViewModel.ClosePlanTab(staleClone);

        fixture.AssertState(before);
        Assert.Contains(fixture.ViewModel.OpenPlans, plan => ReferenceEquals(plan, livePlan));
        Assert.Contains(fixture.Session.Document.Plans, plan => ReferenceEquals(plan, livePlan));
    }

    [Fact]
    public void StaleSelectedSemesterCannotMutateLiveSemesterCoursesOrPeriods()
    {
        using var fixture = Fixture.Create();
        var liveSemester = fixture.Settings.SelectedSemester!;
        var staleSemester = JsonDefaults.Clone(liveSemester);
        fixture.Settings.AddSemester();
        fixture.ResetChangedCount();

        fixture.Settings.SelectedSemester = staleSemester;

        Assert.Null(fixture.Settings.SelectedSemester);
        Assert.Empty(fixture.Settings.Periods);
        var before = fixture.CaptureState();
        var validation = fixture.Settings.SaveSelectedSemester(
            "stale rename",
            staleSemester.StartDate,
            staleSemester.EndDate,
            staleSemester.WeekStartDay);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, issue => issue.Code == "SemesterEditSourceMissing");
        Assert.False(fixture.Settings.DeleteSelectedSemester());
        Assert.False(fixture.Settings.ClearCurrentSemesterCourses());
        Assert.Null(fixture.Settings.AddPeriodAfter(1));
        fixture.Settings.UpdatePeriodTime(1, new TimeOnly(8, 1), new TimeOnly(8, 46));
        fixture.Settings.DeletePeriod(1);
        fixture.Settings.ResetDefaultPeriods();

        fixture.AssertState(before);
        Assert.Contains(
            fixture.Session.Document.Semesters,
            semester => ReferenceEquals(semester, liveSemester));
    }

    [Fact]
    public void StaleSelectedLabelCannotRenameDeleteOrReorderLiveLabelReferences()
    {
        using var fixture = Fixture.Create();
        var liveLabel = fixture.Settings.SelectedLabel!;
        var staleLabel = JsonDefaults.Clone(liveLabel);

        fixture.Settings.SelectedLabel = staleLabel;

        Assert.Null(fixture.Settings.SelectedLabel);
        var before = fixture.CaptureState();
        var validation = fixture.Settings.UpsertLabel("stale rename", staleLabel.Kind);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, issue => issue.Code == "LabelEditSourceMissing");
        fixture.Settings.DeleteSelectedLabel();
        fixture.Settings.MoveSelectedLabel(1);

        fixture.AssertState(before);
        Assert.Contains(
            fixture.Session.Document.Labels,
            label => ReferenceEquals(label, liveLabel));
    }

    [Fact]
    public void NewLabelFlowClearsRejectedStaleSelectionAndSelectsTheInsertedLiveInstance()
    {
        using var fixture = Fixture.Create();
        fixture.Settings.SelectedLabel = JsonDefaults.Clone(fixture.Settings.SelectedLabel!);
        Assert.Null(fixture.Settings.SelectedLabel);

        fixture.Settings.NewLabelTemplate();
        var validation = fixture.Settings.UpsertLabel("new live label", LabelKind.Ordinary);

        Assert.True(validation.IsValid);
        Assert.NotNull(fixture.Settings.SelectedLabel);
        Assert.Contains(
            fixture.Session.Document.Labels,
            label => ReferenceEquals(label, fixture.Settings.SelectedLabel));
    }

    private static CourseOffering CreateUnpersistedCourse(Fixture fixture)
    {
        var course = JsonDefaults.Clone(fixture.Session.Document.CourseLibrary[^1]);
        course.CourseName = "unpersisted stale-plan course";
        CourseIdentityService.AssignOfferingId(course);
        Assert.DoesNotContain(
            fixture.Session.Document.CourseLibrary,
            existing => existing.OfferingId == course.OfferingId);
        return course;
    }

    private static string Serialize(PlannerDocument document) =>
        JsonSerializer.Serialize(document, JsonDefaults.Options);

    private static string StateToken(PlannerDocument document) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.CompactOptions)));

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(EnumerateExceptions))
                yield return inner;
            yield break;
        }

        if (exception.InnerException is not null)
        {
            foreach (var inner in EnumerateExceptions(exception.InnerException))
                yield return inner;
        }
    }

    private static Action PrepareMutation(Fixture fixture, string operation)
    {
        var currentPlan = fixture.ViewModel.CurrentPlan!;
        var currentSemester = fixture.Settings.SelectedSemester!;
        var firstCourse = fixture.Session.Document.CourseLibrary[0];
        var addableCourse = fixture.Session.Document.CourseLibrary.Single(course =>
            course.CourseName == "Physical Education");

        return operation switch
        {
            "planner.create-plan" => () => fixture.ViewModel.CreatePlan("must roll back"),
            "planner.copy-plan" => () => fixture.ViewModel.CopyCurrentPlan(),
            "planner.rename-plan" => () => fixture.ViewModel.RenamePlan(currentPlan, "must roll back"),
            "planner.close-tab" => () => fixture.ViewModel.ClosePlanTab(fixture.ViewModel.OpenPlans[^1]),
            "planner.reorder-tabs" => () => fixture.ViewModel.PersistOpenPlanOrder(
                fixture.ViewModel.OpenPlans.Select(plan => plan.PlanId).Reverse().ToArray()),
            "planner.delete-plan" => fixture.ViewModel.DeleteCurrentPlan,
            "planner.clear-plan" => fixture.ViewModel.ClearCurrentPlan,
            "planner.reorder-registration" => () => fixture.ViewModel.PersistRegistrationOrder(
                currentPlan.Snapshots.Select(snapshot => snapshot.SnapshotId).Reverse().ToArray()),
            "planner.add-course" => () => fixture.ViewModel.AddCourseToCurrentPlan(
                addableCourse,
                DuplicateResolution.SkipExisting,
                ConflictResolution.KeepConflict),
            "planner.bulk-add-course" => () => fixture.ViewModel.AddCourseToPlans(
                fixture.ViewModel.OpenPlans,
                addableCourse,
                DuplicateResolution.SkipExisting,
                ConflictResolution.KeepConflict),
            "planner.remove-course" => () => fixture.ViewModel.RemoveCourseFromCurrentPlan(firstCourse.OfferingId),
            "settings.add-semester" => () => fixture.Settings.AddSemester(),
            "settings.save-semester" => () => fixture.Settings.SaveSelectedSemester(
                "must roll back",
                currentSemester.StartDate,
                currentSemester.EndDate,
                currentSemester.WeekStartDay),
            "settings.clear-semester-courses" => () => fixture.Settings.ClearCurrentSemesterCourses(),
            "settings.add-period" => () => fixture.Settings.AddPeriodAfter(1),
            "settings.update-period" => () => fixture.Settings.UpdatePeriodTime(
                1,
                new TimeOnly(8, 1),
                new TimeOnly(8, 46)),
            "settings.delete-period" => () => fixture.Settings.DeletePeriod(12),
            "settings.reset-periods" => PreparePeriodReset(fixture),
            "settings.upsert-label" => PrepareLabelInsert(fixture),
            "settings.delete-label" => fixture.Settings.DeleteSelectedLabel,
            "settings.move-label" => () => fixture.Settings.MoveSelectedLabel(1),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static Action PrepareLabelInsert(Fixture fixture)
    {
        fixture.Settings.NewLabelTemplate();
        return () => fixture.Settings.UpsertLabel("must roll back", LabelKind.Ordinary);
    }

    private static Action PreparePeriodReset(Fixture fixture)
    {
        fixture.Settings.UpdatePeriodTime(1, new TimeOnly(8, 1), new TimeOnly(8, 46));
        return fixture.Settings.ResetDefaultPeriods;
    }

    private sealed record SessionState(
        string DocumentJson,
        int UndoCount,
        int RedoCount,
        long HistoryBytes,
        int EventCount);

    private enum SaveFailureMode
    {
        CommitAttemptedThenThrow,
        ThrowBeforeCommit,
        CommitThirdStateThenThrow,
        VerificationFails
    }

    private enum ControlledSaveMode
    {
        Normal,
        CommitThenThrowVerified,
        ThrowBeforeCommitVerified,
        ThrowBeforeCommitVerificationFails,
        CommitThenThrowVerificationFails
    }

    private sealed class ControlledPlannerFixture : IDisposable
    {
        private ControlledPlannerFixture(
            string directoryPath,
            SqliteAppRepository repository,
            DocumentSession session,
            PlannerViewModel viewModel)
        {
            DirectoryPath = directoryPath;
            Repository = repository;
            Session = session;
            ViewModel = viewModel;
        }

        public string DirectoryPath { get; }
        public SqliteAppRepository Repository { get; }
        public DocumentSession Session { get; }
        public PlannerViewModel ViewModel { get; }
        public ControlledSaveMode SaveMode { get; set; }

        public static ControlledPlannerFixture Create()
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directoryPath);
            var seed = TestDocumentFactory.CreatePopulated();
            seed.Settings.Language = LanguageMode.English;
            repository.Save(seed, "controlled-planner.seed");
            ControlledPlannerFixture? fixture = null;

            void Save(PlannerDocument document, string eventName)
            {
                var mode = fixture?.SaveMode ?? ControlledSaveMode.Normal;
                switch (mode)
                {
                    case ControlledSaveMode.Normal:
                        repository.Save(document, eventName);
                        return;
                    case ControlledSaveMode.CommitThenThrowVerified:
                    case ControlledSaveMode.CommitThenThrowVerificationFails:
                        repository.Save(document, eventName);
                        throw new IOException("controlled failure after commit");
                    case ControlledSaveMode.ThrowBeforeCommitVerified:
                    case ControlledSaveMode.ThrowBeforeCommitVerificationFails:
                        throw new IOException("controlled failure before commit");
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            PlannerDocument Verify()
            {
                if (fixture?.SaveMode is ControlledSaveMode.ThrowBeforeCommitVerificationFails or
                    ControlledSaveMode.CommitThenThrowVerificationFails)
                {
                    throw new IOException("controlled verification failure");
                }

                return repository.LoadExistingForVerification();
            }

            var session = new DocumentSession(
                repository,
                saveDocument: Save,
                verifyPersistedDocument: Verify);
            var localization = new LocalizationService(session);
            var viewModel = new PlannerViewModel(session, localization);
            fixture = new ControlledPlannerFixture(
                directoryPath,
                repository,
                session,
                viewModel);
            return fixture;
        }

        public void Dispose() => DeleteTemporaryDirectory(DirectoryPath);
    }

    private sealed class AmbiguousSaveFixture : IDisposable
    {
        private AmbiguousSaveFixture(
            string directoryPath,
            SqliteAppRepository repository,
            DocumentSession session)
        {
            DirectoryPath = directoryPath;
            Repository = repository;
            Session = session;
        }

        public string DirectoryPath { get; }
        public SqliteAppRepository Repository { get; }
        public DocumentSession Session { get; }

        public static AmbiguousSaveFixture Create(SaveFailureMode mode)
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directoryPath);
            var seed = TestDocumentFactory.CreatePopulated();
            seed.Settings.Language = LanguageMode.English;
            var secondSemester = JsonDefaults.Clone(seed.Semesters[0]);
            secondSemester.SemesterId = "ambiguity-second-semester";
            secondSemester.SemesterName = "Ambiguity second semester";
            secondSemester.DisplayOrder = seed.Semesters.Count;
            seed.Semesters.Add(secondSemester);
            repository.Save(seed, "ambiguity.seed");

            void Save(PlannerDocument document, string eventName)
            {
                switch (mode)
                {
                    case SaveFailureMode.CommitAttemptedThenThrow:
                        repository.Save(document, eventName);
                        throw new IOException("injected save failure after attempted state committed");
                    case SaveFailureMode.ThrowBeforeCommit:
                    case SaveFailureMode.VerificationFails:
                        throw new IOException("injected save failure before commit");
                    case SaveFailureMode.CommitThirdStateThenThrow:
                        var third = JsonDefaults.Clone(document);
                        third.Plans[0].PlanName = "injected third durable state";
                        repository.Save(third, eventName);
                        throw new IOException("injected save failure after third state committed");
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }
            }

            PlannerDocument Verify() => mode == SaveFailureMode.VerificationFails
                ? throw new IOException("injected durable verification read failure")
                : repository.LoadExistingForVerification();

            var session = new DocumentSession(
                repository,
                saveDocument: Save,
                verifyPersistedDocument: Verify);
            return new AmbiguousSaveFixture(directoryPath, repository, session);
        }

        public void Dispose() => DeleteTemporaryDirectory(DirectoryPath);
    }

    private sealed record ParticipantSessionState(
        string DocumentJson,
        int UndoCount,
        int RedoCount,
        long HistoryBytes,
        int EventCount,
        int ParticipantValue);

    private sealed class ParticipantFixture : IDisposable
    {
        private ParticipantFixture(
            string directoryPath,
            SqliteAppRepository repository,
            DocumentSession session,
            FaultingParticipant participant)
        {
            DirectoryPath = directoryPath;
            Repository = repository;
            Session = session;
            Participant = participant;
        }

        public string DirectoryPath { get; }
        public SqliteAppRepository Repository { get; }
        public DocumentSession Session { get; }
        public FaultingParticipant Participant { get; }

        public static ParticipantFixture Create(
            Action<SqliteAppRepository, PlannerDocument, string>? saveDocument = null)
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directoryPath);
            repository.Save(TestDocumentFactory.CreatePopulated(), "participant.seed");
            var session = saveDocument is null
                ? new DocumentSession(repository)
                : new DocumentSession(
                    repository,
                    saveDocument: (document, eventName) =>
                        saveDocument!(repository, document, eventName));
            var participant = new FaultingParticipant();
            session.UndoRedo.RegisterStateParticipant(participant, session.Document);
            return new ParticipantFixture(directoryPath, repository, session, participant);
        }

        public ParticipantSessionState CaptureState() => new(
            Serialize(Session.Document),
            Session.UndoRedo.UndoCount,
            Session.UndoRedo.RedoCount,
            Session.UndoRedo.HistoryBytes,
            Repository.ReadEventSummaries().Count,
            Participant.Value);

        public void AssertState(ParticipantSessionState expected)
        {
            AssertInMemoryState(expected);
            Assert.Equal(expected.EventCount, Repository.ReadEventSummaries().Count);
        }

        public void AssertInMemoryState(ParticipantSessionState expected)
        {
            Assert.Equal(expected.DocumentJson, Serialize(Session.Document));
            Assert.Equal(expected.UndoCount, Session.UndoRedo.UndoCount);
            Assert.Equal(expected.RedoCount, Session.UndoRedo.RedoCount);
            Assert.Equal(expected.HistoryBytes, Session.UndoRedo.HistoryBytes);
            Assert.Equal(expected.ParticipantValue, Participant.Value);
        }

        public void Dispose() => DeleteTemporaryDirectory(DirectoryPath);
    }

    private sealed class FaultingParticipantState(int value) : PlannerUndoRedoState
    {
        public int Value { get; } = value;
    }

    private sealed class FaultingParticipant : IPlannerUndoRedoStateParticipant<FaultingParticipantState>
    {
        private int _captureCalls;
        private int? _failCaptureOnCall;
        private int _restoreCalls;
        private int? _failRestoreOnCall;

        public int Value { get; set; }
        public bool FailRestore { get; set; }
        public bool FailClear { get; set; }
        public bool FailClearAfterReset { get; set; }

        public FaultingParticipantState CaptureState(PlannerDocument document)
        {
            _captureCalls++;
            if (_failCaptureOnCall is { } failCaptureOnCall &&
                _captureCalls == failCaptureOnCall)
                throw new InvalidOperationException("injected participant capture failure");
            return new FaultingParticipantState(Value);
        }

        public bool AreEquivalent(FaultingParticipantState left, FaultingParticipantState right) =>
            left.Value == right.Value;

        public void RestoreState(FaultingParticipantState state)
        {
            _restoreCalls++;
            if (FailRestore ||
                _failRestoreOnCall is { } failRestoreOnCall &&
                _restoreCalls == failRestoreOnCall)
            {
                _failRestoreOnCall = null;
                throw new InvalidOperationException("injected participant restore failure");
            }
            Value = state.Value;
        }

        public void ClearState()
        {
            if (FailClear)
                throw new InvalidOperationException("injected participant clear failure");
            Value = 0;
            if (FailClearAfterReset)
            {
                FailClearAfterReset = false;
                throw new InvalidOperationException("injected participant clear failure after reset");
            }
        }

        public void FailCaptureAfter(int additionalCaptureCalls) =>
            _failCaptureOnCall = checked(_captureCalls + additionalCaptureCalls);

        public void DisableCaptureFailure() => _failCaptureOnCall = null;

        public void FailRestoreAfter(int additionalRestoreCalls) =>
            _failRestoreOnCall = checked(_restoreCalls + additionalRestoreCalls);
    }

    private static void DeleteTemporaryDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            foreach (var databasePath in Directory.EnumerateFiles(
                         directoryPath,
                         "*.sqlite",
                         SearchOption.AllDirectories))
            {
                var builder = new SqliteConnectionStringBuilder { DataSource = databasePath };
                using var pooledConnection = new SqliteConnection(builder.ToString());
                SqliteConnection.ClearPool(pooledConnection);
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            string directoryPath,
            FaultInjectingStorage storage,
            DocumentSession session,
            LocalizationService localization,
            PlannerViewModel viewModel,
            SettingsViewModel settings)
        {
            DirectoryPath = directoryPath;
            Storage = storage;
            Session = session;
            Localization = localization;
            ViewModel = viewModel;
            Settings = settings;
            Session.Changed += (_, _) => ChangedCount++;
        }

        public string DirectoryPath { get; }
        public FaultInjectingStorage Storage { get; }
        public DocumentSession Session { get; }
        public LocalizationService Localization { get; }
        public PlannerViewModel ViewModel { get; }
        public SettingsViewModel Settings { get; }
        public int ChangedCount { get; private set; }

        public static Fixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var repository = new SqliteAppRepository(directory);
            var storage = new FaultInjectingStorage(repository);
            var session = new DocumentSession(
                repository,
                loadDocument: storage.Load,
                saveDocument: storage.Save);
            var seed = TestDocumentFactory.CreatePopulated();
            seed.Settings.Language = LanguageMode.English;
            session.ReplaceDocument(seed, "test.transaction-seed");
            session.UndoRedo.Clear();
            var localization = new LocalizationService(session);
            var viewModel = new PlannerViewModel(session, localization);
            var settings = new SettingsViewModel(session, localization, new TestThemeService());
            return new Fixture(directory, storage, session, localization, viewModel, settings);
        }

        public CourseEditSession BeginChangedCourseEdit(string courseName)
        {
            var edit = ViewModel.BeginNewCourseEdit();
            ViewModel.UpdateActiveCourseEdit(course =>
            {
                course.CourseName = courseName;
                course.Teacher = "T";
                course.Location = "R";
                course.Credits = 1;
            });
            Assert.True(edit.HasChanges);
            return edit;
        }

        public void CommitPlanName(string name)
        {
            Session.CaptureUndo();
            Session.Document.Plans[0].PlanName = name;
            Session.Save("test.committed-change");
            ResetChangedCount();
        }

        public SessionState CaptureState() => new(
            JsonSerializer.Serialize(Session.Document, JsonDefaults.Options),
            Session.UndoRedo.UndoCount,
            Session.UndoRedo.RedoCount,
            Session.UndoRedo.HistoryBytes,
            Session.Repository.ReadEventSummaries().Count);

        public void AssertState(SessionState expected)
        {
            Assert.Equal(expected.DocumentJson, JsonSerializer.Serialize(Session.Document, JsonDefaults.Options));
            Assert.Equal(expected.UndoCount, Session.UndoRedo.UndoCount);
            Assert.Equal(expected.RedoCount, Session.UndoRedo.RedoCount);
            Assert.Equal(expected.HistoryBytes, Session.UndoRedo.HistoryBytes);
            Assert.Equal(expected.EventCount, Session.Repository.ReadEventSummaries().Count);
            Assert.Equal(0, ChangedCount);
        }

        public void AssertPlannerProjectionReferencesCurrentDocument()
        {
            Assert.All(
                ViewModel.Semesters,
                projected => Assert.Contains(Session.Document.Semesters, item => ReferenceEquals(item, projected)));
            Assert.All(
                ViewModel.AllPlans,
                projected => Assert.Contains(Session.Document.Plans, item => ReferenceEquals(item, projected)));
            Assert.All(
                ViewModel.OpenPlans,
                projected => Assert.Contains(Session.Document.Plans, item => ReferenceEquals(item, projected)));
            Assert.All(
                ViewModel.LibraryCourses,
                projected => Assert.Contains(Session.Document.CourseLibrary, item => ReferenceEquals(item, projected)));
        }

        public void AssertSettingsProjectionReferencesCurrentDocument()
        {
            Assert.All(
                Settings.Semesters,
                projected => Assert.Contains(Session.Document.Semesters, item => ReferenceEquals(item, projected)));
            Assert.All(
                Settings.Labels,
                projected => Assert.Contains(Session.Document.Labels, item => ReferenceEquals(item, projected)));
            if (Settings.SelectedSemester is not null)
            {
                Assert.Contains(
                    Session.Document.Semesters,
                    item => ReferenceEquals(item, Settings.SelectedSemester));
            }
            if (Settings.SelectedLabel is not null)
            {
                Assert.Contains(
                    Session.Document.Labels,
                    item => ReferenceEquals(item, Settings.SelectedLabel));
            }
        }

        public void ResetChangedCount() => ChangedCount = 0;

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class TestThemeService : IThemeService
    {
        public ThemeMode RequestedTheme { get; private set; } = ThemeMode.FollowSystem;
        public ResolvedThemeMode ResolvedTheme => ResolveTheme(RequestedTheme);
        public bool IsHighContrast => false;

        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

        public ResolvedThemeMode ResolveTheme(ThemeMode requestedTheme) =>
            requestedTheme == ThemeMode.Dark ? ResolvedThemeMode.Dark : ResolvedThemeMode.Light;

        public void ApplyTheme(ThemeMode theme)
        {
            RequestedTheme = theme;
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(theme, ResolvedTheme, IsHighContrast));
        }

        public void RefreshTheme() =>
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(RequestedTheme, ResolvedTheme, IsHighContrast));
    }

    private sealed class FaultInjectingStorage(SqliteAppRepository repository)
    {
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }

        public PlannerDocument Load() => FailLoad
            ? throw new IOException("simulated load failure")
            : repository.LoadOrCreate();

        public void Save(PlannerDocument document, string eventName)
        {
            if (FailSave)
                throw new IOException("simulated save failure");
            repository.Save(document, eventName);
        }
    }
}

using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class RegistrationOrderPersistenceCoordinatorTests
{
    [Fact]
    public void ScheduledCallbackPersistsTheCapturedOrderExactlyOnce()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var persisted = new List<IReadOnlyList<string>>();

        var ticket = coordinator.Queue(planGeneration, ["snapshot-b", "snapshot-a"]);

        Assert.NotNull(ticket);
        coordinator.ExecuteScheduled(
            ticket.Value,
            planGeneration,
            order => persisted.Add(order));
        coordinator.ExecuteScheduled(
            ticket.Value,
            planGeneration,
            order => persisted.Add(order));

        var onlySave = Assert.Single(persisted);
        Assert.Equal(["snapshot-b", "snapshot-a"], onlySave);
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void RepeatedReordersCoalesceIntoTheLatestImmutableOrder()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var firstOrder = new[] { "snapshot-a", "snapshot-b", "snapshot-c" };
        var persisted = new List<IReadOnlyList<string>>();

        var ticket = coordinator.Queue(planGeneration, firstOrder);
        firstOrder[0] = "mutated-after-queue";
        var duplicateSchedule = coordinator.Queue(
            planGeneration,
            ["snapshot-c", "snapshot-b", "snapshot-a"]);

        Assert.NotNull(ticket);
        Assert.Null(duplicateSchedule);
        coordinator.ExecuteScheduled(
            ticket.Value,
            planGeneration,
            order => persisted.Add(order));

        Assert.Equal(
            ["snapshot-c", "snapshot-b", "snapshot-a"],
            Assert.Single(persisted));
    }

    [Fact]
    public void CloseFlushesPendingOrderOnceAndMakesLateCallbackHarmless()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var persisted = new List<IReadOnlyList<string>>();
        var ticket = coordinator.Queue(planGeneration, ["snapshot-b", "snapshot-a"]);

        coordinator.Close(planGeneration, order => persisted.Add(order));
        coordinator.ExecuteScheduled(
            ticket!.Value,
            planGeneration,
            order => persisted.Add(order));

        Assert.True(coordinator.IsClosed);
        Assert.Null(coordinator.Queue(planGeneration, ["snapshot-a", "snapshot-b"]));
        Assert.Equal(["snapshot-b", "snapshot-a"], Assert.Single(persisted));
    }

    [Fact]
    public void CloseAfterPlanDeletionDiscardsPendingOrderInsteadOfRevivingIt()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var deletedPlanGeneration = new object();
        var persistCalls = 0;
        var ticket = coordinator.Queue(
            deletedPlanGeneration,
            ["snapshot-b", "snapshot-a"]);

        coordinator.Close(null, _ => persistCalls++);
        coordinator.ExecuteScheduled(
            ticket!.Value,
            deletedPlanGeneration,
            _ => persistCalls++);

        Assert.Equal(0, persistCalls);
        Assert.True(coordinator.IsClosed);
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void ReplacedDocumentGenerationCannotReceiveAnOldPendingOrder()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var oldPlanGeneration = new object();
        var replacementPlanGeneration = new object();
        var persistCalls = 0;
        var ticket = coordinator.Queue(
            oldPlanGeneration,
            ["snapshot-b", "snapshot-a"]);

        coordinator.ExecuteScheduled(
            ticket!.Value,
            replacementPlanGeneration,
            _ => persistCalls++);
        coordinator.Close(replacementPlanGeneration, _ => persistCalls++);

        Assert.Equal(0, persistCalls);
        Assert.True(coordinator.IsClosed);
    }

    [Fact]
    public void ScheduledPersistFailureEscapesOnceAndDoesNotLeaveTheCoordinatorStuck()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var failedTicket = coordinator.Queue(
            planGeneration,
            ["snapshot-b", "snapshot-a"]);
        Assert.NotNull(failedTicket);
        var scheduledTicket = failedTicket.GetValueOrDefault();
        var persistAttempts = 0;

        var exception = Assert.Throws<IOException>(() => coordinator.ExecuteScheduled(
            scheduledTicket,
            planGeneration,
            _ =>
            {
                persistAttempts++;
                throw new IOException("Injected registration-order save failure.");
            }));
        coordinator.ExecuteScheduled(
            scheduledTicket,
            planGeneration,
            _ => persistAttempts++);

        Assert.Contains("Injected", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, persistAttempts);
        Assert.False(coordinator.HasPending);

        var retryTicket = coordinator.Queue(
            planGeneration,
            ["snapshot-a", "snapshot-b"]);
        coordinator.ExecuteScheduled(
            retryTicket!.Value,
            planGeneration,
            _ => persistAttempts++);

        Assert.Equal(2, persistAttempts);
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void ClosePersistFailureEscapesButStillFinalizesExactlyOnce()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var ticket = coordinator.Queue(
            planGeneration,
            ["snapshot-b", "snapshot-a"]);
        var persistAttempts = 0;

        Assert.Throws<IOException>(() => coordinator.Close(
            planGeneration,
            _ =>
            {
                persistAttempts++;
                throw new IOException("Injected close flush failure.");
            }));
        coordinator.ExecuteScheduled(
            ticket!.Value,
            planGeneration,
            _ => persistAttempts++);
        coordinator.Close(planGeneration, _ => persistAttempts++);

        Assert.Equal(1, persistAttempts);
        Assert.True(coordinator.IsClosed);
        Assert.False(coordinator.HasPending);
        Assert.Null(coordinator.Queue(planGeneration, ["snapshot-a", "snapshot-b"]));
    }

    [Fact]
    public void ImmediateSmartSortSupersedesQueuedManualOrder()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        var persisted = new List<IReadOnlyList<string>>();
        var manualTicket = coordinator.Queue(
            planGeneration,
            ["snapshot-b", "snapshot-a", "snapshot-c"]);

        coordinator.PersistImmediately(
            planGeneration,
            ["snapshot-c", "snapshot-a", "snapshot-b"],
            order => persisted.Add(order));
        coordinator.ExecuteScheduled(
            manualTicket!.Value,
            planGeneration,
            order => persisted.Add(order));

        Assert.Equal(
            ["snapshot-c", "snapshot-a", "snapshot-b"],
            Assert.Single(persisted));
        Assert.False(coordinator.HasPending);
    }

    [Fact]
    public void PendingOrderCanBeRetainedOnlyForTheSamePlanGenerationAndSnapshotSet()
    {
        var coordinator = new RegistrationOrderPersistenceCoordinator();
        var planGeneration = new object();
        coordinator.Queue(
            planGeneration,
            ["snapshot-b", "snapshot-a", "snapshot-c"]);

        Assert.True(coordinator.CanRetainPending(
            planGeneration,
            ["snapshot-a", "snapshot-b", "snapshot-c"]));
        Assert.False(coordinator.CanRetainPending(
            new object(),
            ["snapshot-a", "snapshot-b", "snapshot-c"]));
        Assert.False(coordinator.CanRetainPending(
            planGeneration,
            ["snapshot-a", "snapshot-b", "snapshot-new"]));

        coordinator.DiscardPending();

        Assert.False(coordinator.HasPending);
        Assert.False(coordinator.CanRetainPending(
            planGeneration,
            ["snapshot-a", "snapshot-b", "snapshot-c"]));
    }
}

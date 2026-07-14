using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class RegistrationOrderWindowLifetimeTests
{
    [Fact]
    public void DeferredCallbackThatArrivesAfterCloseCannotTouchWindowState()
    {
        var lifetime = new RegistrationOrderWindowLifetime();
        var callbackRuns = 0;

        Assert.True(lifetime.TryRunDeferred(() => callbackRuns++));
        Assert.True(lifetime.TryBeginClose());
        Assert.False(lifetime.TryRunDeferred(() => callbackRuns++));
        lifetime.CompleteClose();

        Assert.False(lifetime.TryRunDeferred(() => callbackRuns++));
        Assert.Equal(1, callbackRuns);
        Assert.True(lifetime.IsClosed);
        Assert.False(lifetime.AcceptsInteraction);
    }

    [Fact]
    public void FailedCloseCanReopenInteractionButCompletedCloseCannotBeReopened()
    {
        var lifetime = new RegistrationOrderWindowLifetime();

        Assert.True(lifetime.TryBeginClose());
        Assert.False(lifetime.TryBeginClose());
        Assert.True(lifetime.IsClosing);
        Assert.False(lifetime.AcceptsInteraction);

        lifetime.CancelClose();

        Assert.False(lifetime.IsClosing);
        Assert.False(lifetime.IsClosed);
        Assert.True(lifetime.AcceptsInteraction);
        Assert.True(lifetime.TryBeginClose());

        lifetime.CompleteClose();
        lifetime.CancelClose();

        Assert.True(lifetime.IsClosed);
        Assert.False(lifetime.AcceptsInteraction);
        Assert.False(lifetime.TryBeginClose());
    }
}

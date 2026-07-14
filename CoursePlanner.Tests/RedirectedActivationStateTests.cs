using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class RedirectedActivationStateTests
{
    [Fact]
    public void ActivationsBeforeTheWindowIsReadyAreCoalescedAndReleasedOnce()
    {
        var state = new RedirectedActivationState();

        Assert.False(state.RequestActivation());
        Assert.False(state.RequestActivation());
        Assert.True(state.MarkWindowReady());
        Assert.False(state.MarkWindowReady());
    }

    [Fact]
    public void ActivationsAfterTheWindowIsReadyDispatchImmediately()
    {
        var state = new RedirectedActivationState();
        Assert.False(state.MarkWindowReady());

        Assert.True(state.RequestActivation());
        Assert.True(state.RequestActivation());
    }

    [Fact]
    public void ConcurrentReadyAndActivationSignalsCannotLoseEveryWakeup()
    {
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var state = new RedirectedActivationState();
            var readyReleasedPending = false;
            var dispatched = new bool[32];

            Parallel.Invoke(
                () => readyReleasedPending = state.MarkWindowReady(),
                () => Parallel.For(
                    0,
                    dispatched.Length,
                    index => dispatched[index] = state.RequestActivation()));

            Assert.True(readyReleasedPending || dispatched.Any(value => value));
        }
    }
}

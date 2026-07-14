using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class WindowCloseGuardStateTests
{
    [Fact]
    public void FirstSystemCloseStartsOneResolutionAndEveryConcurrentCloseIsCancelled()
    {
        var state = new WindowCloseGuardState();

        var first = state.InterceptClose();
        var repeated = state.InterceptClose();

        Assert.True(first.Cancel);
        Assert.True(first.StartResolution);
        Assert.True(repeated.Cancel);
        Assert.False(repeated.StartResolution);
        Assert.Equal(WindowCloseGuardPhase.Resolving, state.Phase);
    }

    [Fact]
    public void RejectedResolutionAllowsANewCloseAttempt()
    {
        var state = new WindowCloseGuardState();
        state.InterceptClose();

        state.RejectResolution();
        var retry = state.InterceptClose();

        Assert.True(retry.Cancel);
        Assert.True(retry.StartResolution);
        Assert.Equal(WindowCloseGuardPhase.Resolving, state.Phase);
    }

    [Fact]
    public void ApprovedResolutionReleasesExactlyOneProgrammaticClosePath()
    {
        var state = new WindowCloseGuardState();
        state.InterceptClose();

        Assert.True(state.ApproveResolution());
        Assert.False(state.ApproveResolution());

        var programmaticClose = state.InterceptClose();
        var teardownClose = state.InterceptClose();

        Assert.False(programmaticClose.Cancel);
        Assert.False(programmaticClose.StartResolution);
        Assert.False(teardownClose.Cancel);
        Assert.False(teardownClose.StartResolution);
        Assert.Equal(WindowCloseGuardPhase.Released, state.Phase);
    }

    [Fact]
    public void ConcurrentSystemCloseSignalsCanStartOnlyOneResolver()
    {
        var state = new WindowCloseGuardState();
        var results = new WindowCloseInterception[128];

        Parallel.For(0, results.Length, index => results[index] = state.InterceptClose());

        Assert.Single(results, result => result.StartResolution);
        Assert.All(results, result => Assert.True(result.Cancel));
    }

    [Fact]
    public async Task BackgroundOperationRemainsBusyUntilItsActualWorkCompletes()
    {
        var service = new BackgroundOperationService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = service.RunAsync("Restore", async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        Assert.True(service.IsBusy);
        Assert.Equal("Restore", service.Message);

        release.SetResult();
        await operation;

        Assert.False(service.IsBusy);
        Assert.Equal("", service.Message);
    }
}

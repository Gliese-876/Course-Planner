using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class LatestRequestTrackerConcurrencyTests
{
    [Fact]
    public void ReplacedRequestKeepsAStableCanceledTokenAfterItsSourceIsReleased()
    {
        using var tracker = new LatestRequestTracker();
        using var previous = tracker.Begin();

        using var latest = tracker.Begin();

        Assert.True(previous.Token.IsCancellationRequested);
        Assert.False(latest.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ReplacingARequestWhileItsOwnerReleasesItDoesNotThrow()
    {
        for (var iteration = 0; iteration < 20_000; iteration++)
        {
            using var tracker = new LatestRequestTracker();
            var previous = tracker.Begin();
            using var start = new Barrier(2);

            var replacement = Task.Run(() =>
            {
                start.SignalAndWait();
                return tracker.Begin();
            });

            start.SignalAndWait();
            previous.Dispose();

            using var latest = await replacement;
            Assert.False(latest.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task DisposingTheTrackerWhileItsCurrentRequestIsReleasedDoesNotThrow()
    {
        for (var iteration = 0; iteration < 20_000; iteration++)
        {
            var tracker = new LatestRequestTracker();
            var request = tracker.Begin();
            using var start = new Barrier(2);

            var disposal = Task.Run(() =>
            {
                start.SignalAndWait();
                tracker.Dispose();
            });

            start.SignalAndWait();
            request.Dispose();
            await disposal;
        }
    }
}

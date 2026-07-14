using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class TransientNotificationLifetimeTests
{
    [Fact]
    public async Task DefaultLifetimeExpiresAfterExactlyThreeSeconds()
    {
        TimeSpan? observedDelay = null;
        using var lifetime = new TransientNotificationLifetime(
            delay: (delay, _) =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            });

        var token = lifetime.Restart();

        Assert.True(await lifetime.WaitForExpiryAsync(token));
        Assert.Equal(TimeSpan.FromSeconds(3), observedDelay);
    }

    [Fact]
    public async Task NewNotificationCancelsThePreviousCountdownWithoutExpiringIt()
    {
        using var lifetime = new TransientNotificationLifetime(
            delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var first = lifetime.Restart();
        var firstExpiry = lifetime.WaitForExpiryAsync(first);

        var second = lifetime.Restart();

        Assert.False(await firstExpiry);
        Assert.True(first.IsCancellationRequested);
        Assert.False(lifetime.IsCurrent(first));
        Assert.False(second.IsCancellationRequested);
        Assert.True(lifetime.IsCurrent(second));
    }

    [Fact]
    public async Task ExplicitCancellationPreventsAnAutomaticDismissal()
    {
        using var lifetime = new TransientNotificationLifetime(
            delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var token = lifetime.Restart();
        var expiry = lifetime.WaitForExpiryAsync(token);

        lifetime.Cancel();

        Assert.False(await expiry);
        Assert.True(token.IsCancellationRequested);
    }
}

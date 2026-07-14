using System.Collections.Concurrent;
using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class TransientNotificationLifetimeConcurrencyTests
{
    [Fact]
    public async Task ReplacementCannotBeExpiredByAStaleDelayThatIgnoresCancellation()
    {
        var delays = new ConcurrentDictionary<CancellationToken, TaskCompletionSource>();
        using var lifetime = new TransientNotificationLifetime(
            delay: (_, token) => delays.GetOrAdd(
                token,
                static _ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task);

        var first = lifetime.Restart();
        var firstExpiry = lifetime.WaitForExpiryAsync(first);
        var second = lifetime.Restart();
        var secondExpiry = lifetime.WaitForExpiryAsync(second);

        delays[first].SetResult();
        Assert.False(await firstExpiry.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(secondExpiry.IsCompleted);

        delays[second].SetResult();
        Assert.True(await secondExpiry.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ConcurrentRestartsLeaveExactlyOneCurrentGeneration()
    {
        using var lifetime = new TransientNotificationLifetime();
        var tokens = new ConcurrentBag<CancellationToken>();

        Parallel.For(0, 1_000, _ => tokens.Add(lifetime.Restart()));

        var current = tokens.Where(lifetime.IsCurrent).ToArray();
        Assert.Single(current);
        Assert.False(current[0].IsCancellationRequested);
        Assert.All(
            tokens.Where(token => token != current[0]),
            token => Assert.True(token.IsCancellationRequested));
    }

    [Fact]
    public async Task CancelRacingRestartNeverLeavesMultipleCurrentGenerations()
    {
        for (var iteration = 0; iteration < 500; iteration++)
        {
            using var lifetime = new TransientNotificationLifetime();
            var initial = lifetime.Restart();
            using var start = new ManualResetEventSlim();
            CancellationToken replacement = default;

            var cancel = Task.Run(() =>
            {
                start.Wait();
                lifetime.Cancel();
            });
            var restart = Task.Run(() =>
            {
                start.Wait();
                replacement = lifetime.Restart();
            });

            start.Set();
            await Task.WhenAll(cancel, restart).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.InRange(
                new[] { initial, replacement }.Count(lifetime.IsCurrent),
                0,
                1);
            Assert.False(lifetime.IsCurrent(initial));
        }
    }

    [Fact]
    public async Task DisposeRacingRestartCannotLeaveAUsableGeneration()
    {
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var lifetime = new TransientNotificationLifetime();
            using var start = new ManualResetEventSlim();
            CancellationToken? returned = null;
            Exception? restartFailure = null;

            var dispose = Task.Run(() =>
            {
                start.Wait();
                lifetime.Dispose();
            });
            var restart = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    returned = lifetime.Restart();
                }
                catch (Exception exception)
                {
                    restartFailure = exception;
                }
            });

            start.Set();
            await Task.WhenAll(dispose, restart).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(
                restartFailure is null or ObjectDisposedException,
                $"Unexpected restart failure: {restartFailure}");
            if (returned is { } token)
            {
                Assert.True(token.IsCancellationRequested);
                Assert.False(lifetime.IsCurrent(token));
            }
            Assert.Throws<ObjectDisposedException>(() => lifetime.Restart());
        }
    }

    [Fact]
    public async Task DisposeCompletesAllOutstandingWaitersWithoutExpiry()
    {
        var lifetime = new TransientNotificationLifetime(
            delay: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var token = lifetime.Restart();
        var waiters = Enumerable.Range(0, 32)
            .Select(_ => lifetime.WaitForExpiryAsync(token))
            .ToArray();

        lifetime.Dispose();

        var results = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.All(results, Assert.False);
    }
}

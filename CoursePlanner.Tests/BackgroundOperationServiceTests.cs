using CoursePlanner.Services;

namespace CoursePlanner.Tests;

public sealed class BackgroundOperationServiceTests
{
    [Fact]
    public async Task ConcurrentOperationIsRejectedWithoutRunningAndCannotBeReportedAsSuccessful()
    {
        var service = new BackgroundOperationService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.RunAsync("First", async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;
        var secondCalls = 0;

        var secondRan = await service.RunAsync("Second", () =>
        {
            secondCalls++;
            return Task.CompletedTask;
        });

        Assert.False(secondRan);
        Assert.Equal(0, secondCalls);
        Assert.True(service.IsBusy);
        Assert.Equal("First", service.Message);

        release.SetResult();
        Assert.True(await first);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task SimultaneousCallersCanStartExactlyOneOperation()
    {
        var service = new BackgroundOperationService();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var ready = 0;

        var attempts = Enumerable.Range(0, 64)
            .Select(index => Task.Run(async () =>
            {
                Interlocked.Increment(ref ready);
                await start.Task;
                return await service.RunAsync($"Operation {index}", async () =>
                {
                    Interlocked.Increment(ref entered);
                    await release.Task;
                });
            }))
            .ToArray();

        await Task.WhenAny(
            WaitUntilAsync(() => Volatile.Read(ref ready) == attempts.Length),
            Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(attempts.Length, Volatile.Read(ref ready));
        start.SetResult();
        await Task.WhenAny(
            WaitUntilAsync(() => Volatile.Read(ref entered) == 1 && attempts.Count(task => task.IsCompleted) == attempts.Length - 1),
            Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.Equal(attempts.Length - 1, attempts.Count(task => task.IsCompleted));
        release.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task SubscriberFailureCannotPreventTheOperationOrOtherStateObservers()
    {
        var service = new BackgroundOperationService();
        var operationCalls = 0;
        var observerCalls = 0;
        service.Changed += (_, _) => throw new InvalidOperationException("Broken status projection.");
        service.Changed += (_, _) => observerCalls++;

        var completed = await service.RunAsync("Work", () =>
        {
            operationCalls++;
            return Task.CompletedTask;
        });

        Assert.True(completed);
        Assert.Equal(1, operationCalls);
        Assert.Equal(2, observerCalls);
        Assert.False(service.IsBusy);
        Assert.Equal("", service.Message);
    }

    [Fact]
    public async Task CompletionSubscriberFailureCannotMaskTheOperationException()
    {
        var service = new BackgroundOperationService();
        var operationException = new IOException("Operation failed.");
        var notificationCount = 0;
        service.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref notificationCount) == 2)
                throw new InvalidOperationException("Completion projection failed.");
        };

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            service.RunAsync("Work", () => Task.FromException(operationException)));

        Assert.Same(operationException, thrown);
        Assert.Equal(2, notificationCount);
        Assert.False(service.IsBusy);
        Assert.Equal("", service.Message);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        while (!predicate())
            await Task.Yield();
    }
}

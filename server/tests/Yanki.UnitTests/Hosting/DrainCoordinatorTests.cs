using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Yanki.Core.Hosting;

namespace Yanki.UnitTests.Hosting;

public sealed class DrainCoordinatorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private static DrainCoordinator Create(FakeTimeProvider time) =>
        new(time, NullLogger<DrainCoordinator>.Instance);

    [Fact]
    public void Fresh_coordinator_is_not_draining()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        Assert.False(coordinator.IsDraining);
        Assert.False(coordinator.DrainToken.IsCancellationRequested);
    }

    [Fact]
    public void BeginDrain_flips_readiness_and_cancels_the_token_immediately()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        var drain = coordinator.BeginDrainAsync(Grace);

        Assert.True(coordinator.IsDraining);
        Assert.True(coordinator.DrainToken.IsCancellationRequested);
        Assert.False(drain.IsCompleted);
    }

    [Fact]
    public async Task BeginDrain_completes_only_after_the_grace_period()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        var drain = coordinator.BeginDrainAsync(Grace);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(drain.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await drain;

        Assert.True(coordinator.IsDraining);
    }

    [Fact]
    public async Task BeginDrain_with_zero_grace_completes_immediately()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        await coordinator.BeginDrainAsync(TimeSpan.Zero);

        Assert.True(coordinator.IsDraining);
        Assert.True(coordinator.DrainToken.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginDrain_is_idempotent_and_keeps_the_first_deadline()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        var first = coordinator.BeginDrainAsync(Grace);
        var second = coordinator.BeginDrainAsync(TimeSpan.FromSeconds(1));

        // The second, shorter call must not shorten the drain that is already running.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        time.Advance(Grace);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task BeginDrain_returns_when_the_caller_cancels_but_stays_draining()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);
        using var caller = new CancellationTokenSource();

        var drain = coordinator.BeginDrainAsync(Grace, caller.Token);
        Assert.False(drain.IsCompleted);

        await caller.CancelAsync();
        await drain;

        Assert.True(coordinator.IsDraining);
        Assert.True(coordinator.DrainToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Concurrent_callers_all_observe_the_same_drain()
    {
        var time = new FakeTimeProvider();
        using var coordinator = Create(time);

        var first = coordinator.BeginDrainAsync(Grace);
        var late = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => coordinator.BeginDrainAsync(Grace)))
            .ToArray();

        Assert.True(coordinator.IsDraining);
        time.Advance(Grace);

        await first.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(late).WaitAsync(TimeSpan.FromSeconds(30));
    }
}

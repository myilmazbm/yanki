using Microsoft.Extensions.Logging;

namespace Talky.Core.Hosting;

/// <summary>
/// Default <see cref="IDrainCoordinator"/>. Thread-safe and idempotent: the first caller starts the
/// grace period and every caller — including late ones — completes when that same period elapses.
/// All timing goes through <see cref="TimeProvider"/> so drains are deterministic in tests.
/// </summary>
public sealed class DrainCoordinator : IDrainCoordinator, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DrainCoordinator> _logger;
    private readonly CancellationTokenSource _drainCts = new();

    /// <summary>Completed once <see cref="_graceTask"/> has been published by the first caller.</summary>
    private readonly TaskCompletionSource _gracePublished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;
    private volatile bool _isDraining;
    private Task? _graceTask;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="timeProvider">Clock used for the grace period.</param>
    /// <param name="logger">Logger.</param>
    public DrainCoordinator(TimeProvider timeProvider, ILogger<DrainCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsDraining => _isDraining;

    /// <inheritdoc />
    public CancellationToken DrainToken => _drainCts.Token;

    /// <inheritdoc />
    public async Task BeginDrainAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            // Start the timer before flipping the flag, so nobody can observe a drain that has no
            // deadline yet. The volatile write to _isDraining publishes _graceTask.
            _graceTask = grace <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(grace, _timeProvider, CancellationToken.None);
            _isDraining = true;
            _gracePublished.SetResult();

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Draining started; readiness is now unhealthy and the grace period is {Grace}.",
                    grace);
            }

            await _drainCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _gracePublished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _graceTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the grace period; draining itself stays in effect.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _drainCts.Dispose();
}

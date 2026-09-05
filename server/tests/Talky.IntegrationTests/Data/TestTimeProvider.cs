namespace Talky.IntegrationTests.Data;

/// <summary>
/// A <see cref="TimeProvider"/> the tests can move forward. Talky.Data never calls
/// <c>DateTime.UtcNow</c>, so injecting this is enough to test job backoff, stale claim requeue and
/// cluster node expiry without sleeping.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}

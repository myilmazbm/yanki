using System.ComponentModel.DataAnnotations;
using Yanki.Realtime.Configuration;

namespace Yanki.UnitTests.Realtime;

public sealed class RealtimeOptionsTests
{
    [Fact]
    public void Defaults_MatchTheDocumentedContract()
    {
        var options = new RealtimeOptions();

        Assert.Equal("/ws", options.Path);
        Assert.Equal(25_000, options.HeartbeatIntervalMs);
        Assert.Equal(5_000, options.HelloTimeoutMs);
        Assert.Equal(65_536, options.MaxFrameBytes);
        Assert.Equal(256, options.OutboundQueueCapacity);
        Assert.False(options.AllowDevTokens);
        Assert.Equal(16, options.MaxConnectionsPerUser);
    }

    [Fact]
    public void DerivedValues_FollowTheHeartbeatAndQueueSettings()
    {
        var options = new RealtimeOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(25_000), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(50_000), options.KeepAliveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), options.SweepInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.CloseGrace);
        Assert.Equal(192, options.EphemeralDropThreshold);
    }

    [Theory]
    [InlineData(5_000, 5_000)]
    [InlineData(30_000, 5_000)]
    [InlineData(1_000, 1_000)]
    [InlineData(100, 100)]
    public void SweepInterval_NeverExceedsFiveSecondsOrTheHelloTimeout(int helloTimeoutMs, int expectedSweepMs)
    {
        var options = new RealtimeOptions { HelloTimeoutMs = helloTimeoutMs };

        Assert.Equal(TimeSpan.FromMilliseconds(expectedSweepMs), options.SweepInterval);
    }

    [Fact]
    public void Validation_AcceptsTheDefaults() =>
        Assert.Empty(Validate(new RealtimeOptions()));

    [Theory]
    [InlineData("ws")]
    [InlineData("")]
    [InlineData("http://elsewhere/ws")]
    public void Validation_RejectsARouteThatIsNotAbsolute(string path) =>
        Assert.Contains(Validate(new RealtimeOptions { Path = path }), r => r.MemberNames.Contains(nameof(RealtimeOptions.Path)));

    [Theory]
    [InlineData(nameof(RealtimeOptions.HeartbeatIntervalMs))]
    [InlineData(nameof(RealtimeOptions.MaxFrameBytes))]
    [InlineData(nameof(RealtimeOptions.OutboundQueueCapacity))]
    [InlineData(nameof(RealtimeOptions.MaxConnectionsPerUser))]
    public void Validation_RejectsOutOfRangeValues(string member)
    {
        var options = new RealtimeOptions();
        switch (member)
        {
            case nameof(RealtimeOptions.HeartbeatIntervalMs): options.HeartbeatIntervalMs = 10; break;
            case nameof(RealtimeOptions.MaxFrameBytes): options.MaxFrameBytes = 8; break;
            case nameof(RealtimeOptions.OutboundQueueCapacity): options.OutboundQueueCapacity = 1; break;
            default: options.MaxConnectionsPerUser = 0; break;
        }

        Assert.Contains(Validate(options), r => r.MemberNames.Contains(member));
    }

    private static List<ValidationResult> Validate(RealtimeOptions options)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

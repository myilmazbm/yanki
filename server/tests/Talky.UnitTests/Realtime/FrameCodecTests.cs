using System.Text;
using System.Text.Json;
using Talky.Protocol.V1;
using Talky.Realtime.Protocol;

namespace Talky.UnitTests.Realtime;

public sealed class FrameCodecTests
{
    private const int MaxBytes = 65_536;

    [Fact]
    public void TryParseClientFrame_RoundTripsHello()
    {
        var original = new ClientFrame
        {
            Rid = "0190f0a1-0000-7000-8000-000000000001",
            Hello = new Hello
            {
                ProtoVersion = 1,
                Token = "dev:u1",
                DeviceId = "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                Client = new ClientInfo { Platform = "web", AppVersion = "0.1.0", Os = "windows", Locale = "tr-TR" },
            },
        };
        original.Hello.Since.Add("c1", 42L);

        byte[] utf8 = Encoding.UTF8.GetBytes(FrameCodec.ToJson(original));

        Assert.True(FrameCodec.TryParseClientFrame(utf8, MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(original, parsed);
        Assert.Equal(42L, parsed.Hello.Since["c1"]);
    }

    [Fact]
    public void TryParseClientFrame_IgnoresUnknownFields()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(
            """{"rid":"r1","hello":{"protoVersion":1,"token":"dev:u1","futureField":{"nested":true}},"unknownEnvelopeField":7}""");

        Assert.True(FrameCodec.TryParseClientFrame(utf8, MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(error);
        Assert.Equal("r1", parsed.Rid);
        Assert.Equal(1u, parsed.Hello.ProtoVersion);
    }

    [Fact]
    public void FormatServerFrame_WritesInt64AsString()
    {
        ServerFrame frame = ServerFrames.Pong(1_725_540_000_000L, DateTimeOffset.UnixEpoch);

        using PooledUtf8Payload payload = FrameCodec.FormatServerFrame(frame);
        using JsonDocument document = JsonDocument.Parse(payload.Memory);

        JsonElement ts = document.RootElement.GetProperty("pong").GetProperty("ts");
        Assert.Equal(JsonValueKind.String, ts.ValueKind);
        Assert.Equal("1725540000000", ts.GetString());
    }

    [Fact]
    public void FormatServerFrame_WritesEnumsByProtoName()
    {
        ServerFrame frame = ServerFrames.Error(ErrorCode.HelloRequired, "Hello must be the first frame on a connection.");

        using PooledUtf8Payload payload = FrameCodec.FormatServerFrame(frame);
        using JsonDocument document = JsonDocument.Parse(payload.Memory);

        Assert.Equal("ERROR_CODE_HELLO_REQUIRED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void FormatServerFrame_RoundTripsThroughTheParser()
    {
        ServerFrame frame = ServerFrames.Welcome(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "node-a",
            DateTimeOffset.UnixEpoch.AddSeconds(1_725_540_000),
            25_000,
            "0.1.0");

        using PooledUtf8Payload payload = FrameCodec.FormatServerFrame(frame);
        ServerFrame parsed = FrameCodec.ParseServerFrame(Encoding.UTF8.GetString(payload.Span));

        Assert.Equal(frame, parsed);
    }

    [Fact]
    public void TryParseClientFrame_RejectsOversizedPayload()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("""{"ping":{"ts":"1"}}""");

        Assert.False(FrameCodec.TryParseClientFrame(utf8, maxBytes: 4, out ClientFrame? parsed, out string? error));
        Assert.Null(parsed);
        Assert.Equal(FrameCodec.TooLargeMessage, error);
    }

    [Fact]
    public void TryParseClientFrame_RejectsEmptyPayload()
    {
        Assert.False(FrameCodec.TryParseClientFrame([], MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(parsed);
        Assert.Equal(FrameCodec.EmptyPayloadMessage, error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rid":"r1"}""")]
    [InlineData("""{"unknownBody":{"x":1}}""")]
    public void TryParseClientFrame_AcceptsFramesWithoutAKnownBody(string json)
    {
        Assert.True(FrameCodec.TryParseClientFrame(Encoding.UTF8.GetBytes(json), MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(error);
        Assert.Equal(ClientFrame.BodyOneofCase.None, parsed.BodyCase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"ping":{"ts":""")]
    [InlineData("[1,2,3]")]
    public void TryParseClientFrame_RejectsMalformedJson(string json)
    {
        Assert.False(FrameCodec.TryParseClientFrame(Encoding.UTF8.GetBytes(json), MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseClientFrame_RejectsAValueThatDoesNotMatchTheSchema()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("""{"ping":{"ts":"not-a-number"}}""");

        Assert.False(FrameCodec.TryParseClientFrame(utf8, MaxBytes, out ClientFrame? parsed, out string? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseClientFrame_HandlesMultiByteCharacters()
    {
        var frame = new ClientFrame
        {
            Rid = "r1",
            Hello = new Hello { ProtoVersion = 1, Token = "dev:çğüşöİ", Client = new ClientInfo { Locale = "tr-TR" } },
        };

        byte[] utf8 = Encoding.UTF8.GetBytes(FrameCodec.ToJson(frame));

        Assert.True(FrameCodec.TryParseClientFrame(utf8, MaxBytes, out ClientFrame? parsed, out _));
        Assert.Equal("dev:çğüşöİ", parsed.Hello.Token);
    }

    [Fact]
    public void FormatServerFrame_HandlesPayloadsLargerThanTheInitialPooledBuffer()
    {
        var conversations = Enumerable.Range(0, 500).Select(i => $"conversation-{i:D6}").ToArray();
        ServerFrame frame = ServerFrames.ResyncRequired(full: false, conversations);

        using PooledUtf8Payload payload = FrameCodec.FormatServerFrame(frame);
        using JsonDocument document = JsonDocument.Parse(payload.Memory);

        Assert.Equal(500, document.RootElement.GetProperty("resyncRequired").GetProperty("conversationIds").GetArrayLength());
    }
}

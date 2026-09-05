using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Talky.Core.Sessions;
using Talky.Protocol.V1;
using Talky.Realtime.Protocol;
using Talky.Testing.Realtime;

namespace Talky.IntegrationTests.Realtime;

public sealed class HandshakeTests
{
    private const string DevToken = "dev:u1";

    [Fact]
    public async Task Hello_IsAnsweredWithWelcome()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        Welcome welcome = await client.HandshakeAsync(DevToken, cts.Token, deviceId: "device-1");

        Assert.True(Guid.TryParse(welcome.SessionId, out Guid sessionId));
        Assert.Equal(7, sessionId.Version);
        Assert.True(Guid.TryParse(welcome.UserId, out _));
        Assert.Equal(TestNodeInfo.TestNodeId, welcome.NodeId);
        Assert.Equal(TestNodeInfo.TestVersion, welcome.ServerVersion);
        Assert.Equal(25_000u, welcome.HeartbeatIntervalMs);
        Assert.NotNull(welcome.ServerTime);
        Assert.True(welcome.ServerTime.ToDateTimeOffset() > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPongEchoingTheTimestamp()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        await client.HandshakeAsync(DevToken, cts.Token);

        await client.SendAsync(new ClientFrame { Ping = new Ping { Ts = 1_725_540_000_000L } }, cts.Token);
        ServerFrame frame = await client.ExpectFrameAsync(cts.Token);

        Assert.Equal(ServerFrame.BodyOneofCase.Pong, frame.BodyCase);
        Assert.Equal(1_725_540_000_000L, frame.Pong.Ts);
        Assert.NotNull(frame.Pong.ServerTime);
        Assert.True(Guid.TryParse(frame.Id, out _));
    }

    [Fact]
    public async Task SecondHello_IsRejectedButTheConnectionStaysOpen()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        await client.HandshakeAsync(DevToken, cts.Token);

        await client.SendAsync(RealtimeTestClient.HelloFrame(DevToken, rid: "rid-2"), cts.Token);
        ServerFrame error = await client.ExpectFrameAsync(cts.Token);

        Assert.Equal(ServerFrame.BodyOneofCase.Error, error.BodyCase);
        Assert.Equal(ErrorCode.AlreadyAuthenticated, error.Error.Code);
        Assert.Equal("rid-2", error.Error.Rid);

        await client.SendAsync(new ClientFrame { Ping = new Ping { Ts = 7 } }, cts.Token);
        ServerFrame pong = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(7L, pong.Pong.Ts);
    }

    [Fact]
    public async Task FrameBeforeHello_IsRejectedWithHelloRequiredAnd1008()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendAsync(new ClientFrame { Ping = new Ping { Ts = 1 } }, cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.HelloRequired, error.Error.Code);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_IsRejectedWith1002()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendAsync(RealtimeTestClient.HelloFrame(DevToken, rid: "rid-1", protoVersion: 2), cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.ProtocolVersionUnsupported, error.Error.Code);
        Assert.Equal("rid-1", error.Error.Rid);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.ProtocolError, close.CloseStatus);
    }

    [Fact]
    public async Task UnknownToken_IsRejectedWith1008()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendAsync(RealtimeTestClient.HelloFrame("not-a-dev-token", rid: "rid-1"), cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.Unauthenticated, error.Error.Code);
        Assert.DoesNotContain("not-a-dev-token", error.Error.Message, StringComparison.Ordinal);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
    }

    [Fact]
    public async Task ACustomAuthenticatorReplacesTheDevelopmentOne()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(
            configureServices: services => services.AddSingleton<ISessionAuthenticator, RejectingSessionAuthenticator>());
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        Assert.IsType<RejectingSessionAuthenticator>(host.Services.GetRequiredService<ISessionAuthenticator>());

        await client.SendAsync(RealtimeTestClient.HelloFrame(DevToken), cts.Token);
        ServerFrame error = await client.ExpectFrameAsync(cts.Token);

        Assert.Equal(ErrorCode.Unauthenticated, error.Error.Code);
    }

    [Fact]
    public async Task Bye_ClosesTheConnectionWith1000()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        await client.HandshakeAsync(DevToken, cts.Token);

        await client.SendAsync(new ClientFrame { Bye = new Bye() }, cts.Token);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
    }

    [Fact]
    public async Task UnparsableFrame_IsRejectedWith1003()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendTextAsync("this is not json", cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.InvalidFrame, error.Error.Code);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, close.CloseStatus);
    }

    [Fact]
    public async Task FrameWithNoKnownBody_IsRejectedWith1003()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendTextAsync("""{"rid":"r1","somethingFromTheFuture":{"x":1}}""", cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.InvalidFrame, error.Error.Code);
    }

    [Fact]
    public async Task BinaryFrame_IsRejectedWith1003()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        await client.SendBinaryAsync(new byte[] { 1, 2, 3, 4 }, cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.InvalidFrame, error.Error.Code);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, close.CloseStatus);
    }

    [Fact]
    public async Task OversizedFrame_IsRejectedWith1009()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.MaxFrameBytes = 2_048);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        var oversized = new ClientFrame
        {
            Rid = "r1",
            Hello = new Hello { ProtoVersion = 1, Token = new string('x', 8_000) },
        };
        await client.SendAsync(oversized, cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.InvalidFrame, error.Error.Code);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, close.CloseStatus);
    }

    [Fact]
    public async Task FragmentedFrameWithinTheLimit_IsAccepted()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        // A ~6 KiB Hello: larger than the 4 KiB receive chunk, well under the 64 KiB limit.
        var hello = new ClientFrame
        {
            Rid = "r1",
            Hello = new Hello { ProtoVersion = 1, Token = DevToken, DeviceId = new string('d', 6_000) },
        };
        byte[] utf8 = Encoding.UTF8.GetBytes(FrameCodec.ToJson(hello));
        await client.Socket.SendAsync(utf8.AsMemory(0, 3_000), WebSocketMessageType.Text, endOfMessage: false, cts.Token);
        await client.Socket.SendAsync(utf8.AsMemory(3_000), WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        ServerFrame frame = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Welcome, frame.BodyCase);
    }

    [Fact]
    public async Task NoHello_TimesOutWithHelloRequiredAnd1008()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.HelloTimeoutMs = 200);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        ServerFrame error = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ErrorCode.HelloRequired, error.Error.Code);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
    }

    [Fact]
    public async Task PlainHttpRequest_IsAnsweredWith400()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        using HttpClient http = host.CreateHttpClient();

        using HttpResponseMessage response = await http.GetAsync(new Uri("http://localhost/ws"), cts.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClosedConnections_AreRemovedFromTheRegistry()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using (RealtimeTestClient client = await host.ConnectAsync(cts.Token))
        {
            await client.HandshakeAsync(DevToken, cts.Token);
            Assert.Equal(1, host.Registry.Count);

            await client.SendAsync(new ClientFrame { Bye = new Bye() }, cts.Token);
            await client.ExpectCloseAsync(cts.Token);
        }

        await host.WaitForRegistryAsync(0, cts.Token);
        Assert.Equal(0, host.Registry.Count);
    }
}

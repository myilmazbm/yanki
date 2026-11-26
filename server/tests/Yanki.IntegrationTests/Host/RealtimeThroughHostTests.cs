using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Yanki.IntegrationTests.Host;

/// <summary>
/// End-to-end smoke test of the realtime module through the real host composition: proves that
/// <c>UseWebSockets</c> precedes endpoint execution, that <c>MapYankiRealtime</c> is mapped, and
/// that the dev-token authenticator is active in the test environment.
/// </summary>
public sealed class RealtimeThroughHostTests(YankiApplicationFactory factory) : IClassFixture<YankiApplicationFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Hello_over_the_hosted_ws_endpoint_is_answered_with_welcome()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var client = factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), cts.Token);

        const string hello = """
            {"rid":"0190f0a1-0000-7000-8000-000000000001","hello":{"protoVersion":1,"token":"dev:u1","deviceId":"5a3c9e2b-1f44-4c8a-9d21-7e6b0f1a2c33","client":{"platform":"web","appVersion":"0.1.0","os":"windows","locale":"tr-TR"}}}
            """;
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);

        using var frame = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        Assert.True(
            frame.RootElement.TryGetProperty("welcome", out var welcome),
            $"expected a welcome frame, got: {Encoding.UTF8.GetString(buffer, 0, result.Count)}");
        Assert.False(string.IsNullOrEmpty(welcome.GetProperty("sessionId").GetString()));
        Assert.False(string.IsNullOrEmpty(welcome.GetProperty("nodeId").GetString()));
        Assert.Equal(25000, welcome.GetProperty("heartbeatIntervalMs").GetInt32());

        const string bye = """{"bye":{}}""";
        await socket.SendAsync(Encoding.UTF8.GetBytes(bye), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        var close = await socket.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close.CloseStatus);
    }

    [Fact]
    public async Task Plain_http_request_to_the_ws_path_is_rejected_not_served()
    {
        using var httpClient = factory.CreateClient();
        using var response = await httpClient.GetAsync(new Uri("/ws", UriKind.Relative), CancellationToken.None);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}

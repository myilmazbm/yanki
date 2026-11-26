using Google.Protobuf;
using Talky.Protocol.V1;

namespace Talky.WsLoad;

/// <summary>
/// Proto3-JSON helpers for the load tool. Deliberately a local copy rather than a reference to
/// Talky.Realtime: the tool must stay a plain client that only needs ClientWebSocket.
/// </summary>
internal static class WsLoadCodec
{
    private static readonly JsonFormatter Formatter = JsonFormatter.Default;

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    public static string ToJson(ClientFrame frame) => Formatter.Format(frame);

    public static ServerFrame Parse(string json) => Parser.Parse<ServerFrame>(json);

    public static ClientFrame Hello(string token, string deviceId) => new()
    {
        Rid = Guid.CreateVersion7().ToString("D"),
        Hello = new Hello
        {
            ProtoVersion = 1,
            Token = token,
            DeviceId = deviceId,
            Client = new ClientInfo
            {
                Platform = "loadtest",
                AppVersion = "0.1.0",
                Os = Environment.OSVersion.Platform.ToString(),
                Locale = "en-US",
            },
        },
    };

    public static ClientFrame Ping(long ts) => new() { Ping = new Ping { Ts = ts } };

    public static ClientFrame Bye() => new() { Bye = new Bye() };
}

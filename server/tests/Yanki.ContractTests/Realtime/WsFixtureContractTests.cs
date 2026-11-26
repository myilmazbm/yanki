using System.Text.Json;
using Talky.Realtime.Protocol;
using Talky.Testing.Realtime;

namespace Talky.ContractTests.Realtime;

/// <summary>
/// Replays every golden flow in <c>api/fixtures/ws</c> against a real realtime host: the
/// <c>c2s</c> frames are sent verbatim and the <c>s2c</c> frames are validated structurally. The
/// same files drive the TypeScript client tests from the other side, so the two implementations
/// cannot drift.
/// </summary>
public sealed class WsFixtureContractTests
{
    /// <summary>The flows M0 must cover. Guards against a fixture being deleted or renamed.</summary>
    public static TheoryData<string> RequiredFixtures =>
    [
        "already_authenticated.json",
        "bad_version.json",
        "bye.json",
        "handshake_ok.json",
        "hello_required.json",
        "ping_pong.json",
        "reconnect_on_drain.json",
        "unauthenticated.json",
    ];

    public static TheoryData<string> AllFixtures
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string name in WsFixture.AllNames())
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RequiredFixtures))]
    public void EveryRequiredFixtureExistsAndIsWellFormed(string name)
    {
        var fixture = WsFixture.Load(name);

        Assert.False(string.IsNullOrWhiteSpace(fixture.Description));
        Assert.NotEmpty(fixture.Frames.EnumerateArray());
        foreach (JsonElement entry in fixture.Frames.EnumerateArray())
        {
            string dir = entry.GetProperty("dir").GetString() ?? string.Empty;
            Assert.Contains(dir, new[] { "c2s", "s2c", "server-action" });
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task FixtureReplaysAgainstTheServer(string name)
    {
        var fixture = WsFixture.Load(name);
        using CancellationTokenSource cts = TestTimeouts.Cts();

        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        foreach (JsonElement entry in fixture.Frames.EnumerateArray())
        {
            switch (entry.GetProperty("dir").GetString())
            {
                case "c2s":
                    await client.SendTextAsync(entry.GetProperty("json").GetRawText(), cts.Token);
                    break;

                case "s2c":
                    await AssertNextFrameMatchesAsync(client, entry.GetProperty("json"), fixture.Name, cts.Token);
                    break;

                case "server-action":
                    await RunServerActionAsync(host, entry.GetProperty("action").GetString(), cts.Token);
                    break;

                default:
                    Assert.Fail($"{fixture.Name}: unknown fixture entry direction '{entry.GetProperty("dir")}'.");
                    break;
            }
        }

        if (fixture.ExpectedCloseCode is { } expectedCloseCode)
        {
            ServerMessage close = await client.ExpectCloseAsync(cts.Token);
            Assert.Equal(expectedCloseCode, (int)close.CloseStatus!.Value);
        }
        else
        {
            await client.CloseAsync(cts.Token);
        }
    }

    private static async Task AssertNextFrameMatchesAsync(
        RealtimeTestClient client,
        JsonElement expected,
        string fixtureName,
        CancellationToken cancellationToken)
    {
        ServerMessage message = await client.ReceiveAsync(cancellationToken);
        Assert.True(
            message.Frame is not null,
            $"{fixtureName}: expected a frame but the server closed with {(int?)message.CloseStatus}.");

        using JsonDocument actual = JsonDocument.Parse(FrameCodec.ToJson(message.Frame!));
        StructuralJson.AssertMatches(expected, actual.RootElement, $"{fixtureName}$");
    }

    private static Task RunServerActionAsync(RealtimeTestHost host, string? action, CancellationToken cancellationToken) =>
        action switch
        {
            "drain" => host.Drain.BeginDrainAsync(TimeSpan.FromSeconds(10), cancellationToken),
            _ => throw new InvalidOperationException($"Unknown fixture server action '{action}'."),
        };
}

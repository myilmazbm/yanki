using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Talky.Core.Hosting;
using Talky.Realtime;
using Talky.Realtime.Configuration;
using Talky.Realtime.Connections;

namespace Talky.Testing.Realtime;

/// <summary>
/// A minimal in-memory host that wires only <c>AddTalkyRealtime</c> / <c>MapTalkyRealtime</c> plus
/// test doubles for the contracts owned by other modules (<see cref="INodeInfo"/>,
/// <see cref="IDrainCoordinator"/>, <c>ISessionAuthenticator</c>). Deliberately independent of
/// Talky.Server so the realtime module can be verified on its own.
/// </summary>
internal sealed class RealtimeTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RealtimeTestHost(WebApplication app, TestDrainCoordinator drain, WebSocketAcceptCapture accepts)
    {
        _app = app;
        Drain = drain;
        Accepts = accepts;
    }

    public TestDrainCoordinator Drain { get; }

    public WebSocketAcceptCapture Accepts { get; }

    public IConnectionRegistry Registry => _app.Services.GetRequiredService<IConnectionRegistry>();

    public IServiceProvider Services => _app.Services;

    public string Path { get; private set; } = "/ws";

    public static async Task<RealtimeTestHost> StartAsync(
        Action<RealtimeOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{RealtimeOptions.SectionName}:{nameof(RealtimeOptions.AllowDevTokens)}"] = "true",
        });

        var drain = new TestDrainCoordinator();
        var accepts = new WebSocketAcceptCapture();

        builder.Services.AddSingleton<INodeInfo>(new TestNodeInfo());
        builder.Services.AddSingleton<IDrainCoordinator>(drain);
        builder.Services.AddSingleton(accepts);

        // Runs before AddTalkyRealtime so a test-supplied ISessionAuthenticator wins the TryAdd.
        configureServices?.Invoke(builder.Services);
        builder.Services.AddTalkyRealtime(builder.Configuration, configureOptions);

        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            accepts.Wrap(context);
            await next(context);
        });
        app.MapTalkyRealtime();

        await app.StartAsync();

        var options = new RealtimeOptions();
        configureOptions?.Invoke(options);
        return new RealtimeTestHost(app, drain, accepts) { Path = options.Path };
    }

    /// <summary>Opens a WebSocket against the mapped realtime endpoint.</summary>
    public async Task<RealtimeTestClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        WebSocketClient client = _app.GetTestServer().CreateWebSocketClient();
        System.Net.WebSockets.WebSocket socket = await client.ConnectAsync(
            new Uri($"http://localhost{Path}"),
            cancellationToken);
        return new RealtimeTestClient(socket);
    }

    /// <summary>An HTTP client for the same in-memory server, used to test non-upgrade requests.</summary>
    public HttpClient CreateHttpClient() => _app.GetTestServer().CreateClient();

    /// <summary>Waits until the registry holds exactly <paramref name="expected"/> connections.</summary>
    public async Task WaitForRegistryAsync(int expected, CancellationToken cancellationToken)
    {
        while (Registry.Count != expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

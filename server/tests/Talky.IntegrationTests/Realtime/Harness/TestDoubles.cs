using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Talky.Core.Hosting;
using Talky.Core.Sessions;

namespace Talky.Testing.Realtime;

/// <summary>Stand-in for the host's node identity.</summary>
internal sealed class TestNodeInfo : INodeInfo
{
    public const string TestNodeId = "test-node-01";

    public const string TestVersion = "0.1.0-test";

    public string NodeId => TestNodeId;

    public NodeRole Roles => NodeRole.Api;

    public string Version => TestVersion;

    public string HostName => "test-host";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UnixEpoch;
}

/// <summary>Drain coordinator a test can trigger on demand.</summary>
internal sealed class TestDrainCoordinator : IDrainCoordinator, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public bool IsDraining { get; private set; }

    public CancellationToken DrainToken => _cts.Token;

    public Task BeginDrainAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        IsDraining = true;
        return _cts.CancelAsync();
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>An authenticator that rejects every token; used to test the UNAUTHENTICATED path.</summary>
internal sealed class RejectingSessionAuthenticator : ISessionAuthenticator
{
    public ValueTask<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SessionPrincipal?>(null);
}

/// <summary>
/// Captures the <see cref="WebSocketAcceptContext"/> the realtime handler passes to the server, so
/// tests can assert that the protocol keep-alive knobs really are applied.
/// </summary>
internal sealed class WebSocketAcceptCapture
{
    public WebSocketAcceptContext? LastAcceptContext { get; private set; }

    public void Wrap(HttpContext context)
    {
        if (context.Features.Get<IHttpWebSocketFeature>() is { } inner)
        {
            context.Features.Set<IHttpWebSocketFeature>(new CapturingFeature(inner, this));
        }
    }

    private sealed class CapturingFeature(IHttpWebSocketFeature inner, WebSocketAcceptCapture owner) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            owner.LastAcceptContext = context;
            return inner.AcceptAsync(context);
        }
    }
}

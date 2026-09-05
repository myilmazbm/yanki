namespace Talky.Core.Hosting;

/// <summary>
/// Roles a talky.exe process can run. A single-server deployment runs <see cref="All"/>;
/// large deployments run dedicated Api, Worker, Search and PushRelay nodes.
/// Parsed from <c>--role</c> (comma separated) or <c>Talky:Node:Roles</c>.
/// </summary>
[Flags]
public enum NodeRole
{
    None = 0,

    /// <summary>HTTP + WebSocket front door. Holds client connections, participates in the cluster mesh.</summary>
    Api = 1 << 0,

    /// <summary>Background jobs: AD sync, previews, virus scan, retention, push fan-out.</summary>
    Worker = 1 << 1,

    /// <summary>Embedded Lucene.NET index node.</summary>
    Search = 1 << 2,

    /// <summary>DMZ relay with outbound-only access to APNs/FCM.</summary>
    PushRelay = 1 << 3,

    All = Api | Worker | Search | PushRelay,
}

namespace Talky.Core.Hosting;

/// <summary>
/// Identity of the running talky.exe process. Registered as a singleton by the host.
/// </summary>
public interface INodeInfo
{
    /// <summary>
    /// Stable id of this node. Defaults to the machine name plus a short random suffix so two
    /// processes on one host never collide; can be pinned via <c>Talky:Node:Id</c>.
    /// </summary>
    string NodeId { get; }

    NodeRole Roles { get; }

    /// <summary>Informational semantic version of the binary (assembly informational version).</summary>
    string Version { get; }

    string HostName { get; }

    DateTimeOffset StartedAt { get; }

    bool HasRole(NodeRole role) => (Roles & role) == role;
}

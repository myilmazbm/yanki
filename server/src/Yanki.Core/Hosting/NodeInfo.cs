using System.Reflection;
using System.Security.Cryptography;
using Talky.Core.Configuration;

namespace Talky.Core.Hosting;

/// <summary>
/// Default <see cref="INodeInfo"/>. Registered as a singleton by the host; every field is computed
/// once at construction so reads on the hot path are free.
/// </summary>
public sealed class NodeInfo : INodeInfo
{
    /// <summary>Alphabet of the random node id suffix: Crockford base32 without look-alikes.</summary>
    private const string SuffixAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private const int SuffixLength = 4;

    /// <summary>Creates node identity from already validated options.</summary>
    /// <param name="nodeId">Pinned node id, or null to generate one.</param>
    /// <param name="roles">Roles this process runs.</param>
    /// <param name="version">Informational version of the binary.</param>
    /// <param name="hostName">Machine name.</param>
    /// <param name="startedAt">Process start timestamp.</param>
    public NodeInfo(string? nodeId, NodeRole roles, string version, string hostName, DateTimeOffset startedAt)
    {
        HostName = hostName;
        NodeId = string.IsNullOrWhiteSpace(nodeId) ? GenerateNodeId(hostName) : nodeId.Trim();
        Roles = roles;
        Version = version;
        StartedAt = startedAt;
    }

    /// <inheritdoc />
    public string NodeId { get; }

    /// <inheritdoc />
    public NodeRole Roles { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public string HostName { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Builds node identity from <see cref="NodeOptions"/>. Roles are expected to be valid; the
    /// options validator rejects bad values long before this runs.
    /// </summary>
    /// <param name="options">Bound <c>Talky:Node</c> section.</param>
    /// <param name="timeProvider">Clock used for <see cref="StartedAt"/>.</param>
    /// <returns>A new <see cref="NodeInfo"/>.</returns>
    /// <exception cref="ArgumentException">The configured roles cannot be parsed.</exception>
    public static NodeInfo Create(NodeOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!NodeRoles.TryParse(options.Roles, out var roles, out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }

        return new NodeInfo(options.Id, roles, ResolveVersion(), Environment.MachineName, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Reads <see cref="AssemblyInformationalVersionAttribute"/> from the entry assembly, dropping
    /// the <c>+sourceRevisionId</c> suffix the SDK appends.
    /// </summary>
    public static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(NodeInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }

    private static string GenerateNodeId(string hostName)
    {
        var suffix = RandomNumberGenerator.GetString(SuffixAlphabet, SuffixLength);
        return $"{hostName}-{suffix}";
    }
}

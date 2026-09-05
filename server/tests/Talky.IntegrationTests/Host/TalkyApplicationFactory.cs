using Microsoft.AspNetCore.Mvc.Testing;
using Talky.Server;

namespace Talky.IntegrationTests.Host;

/// <summary>
/// Boots the real talky.exe composition root in memory: the same command line, the same
/// configuration stack and the same endpoints the Windows service runs.
/// </summary>
public sealed class TalkyApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Creates the factory and pins the process environment the host reads at startup.</summary>
    public TalkyApplicationFactory() => TalkyTestEnvironment.Ensure();
}

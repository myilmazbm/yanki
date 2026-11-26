using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yanki.Server;

namespace Yanki.IntegrationTests.Host;

/// <summary>
/// Boots the real yanki.exe composition root in memory: the same command line, the same
/// configuration stack and the same endpoints the Windows service runs.
/// </summary>
public sealed class YankiApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Creates the factory and pins the process environment the host reads at startup.</summary>
    public YankiApplicationFactory() => YankiTestEnvironment.Ensure();

    /// <summary>
    /// yanki.exe reports a startup failure and returns an exit code instead of throwing, so the test
    /// host would otherwise see a disposed host and throw an unrelated ObjectDisposedException.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host;
        bool started;
        try
        {
            host = base.CreateHost(builder);
            started = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested;
        }
        catch (ObjectDisposedException ex)
        {
            throw ExitedBeforeStart(ex);
        }

        return started ? host : throw ExitedBeforeStart(null);
    }

    private static InvalidOperationException ExitedBeforeStart(Exception? inner) => new(
        "yanki exited before the host started; the real error is on stderr of the test run.",
        inner);
}

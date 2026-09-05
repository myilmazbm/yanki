using Talky.Server.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// <c>talky serve</c> — and the default command, so the Windows service can start the bare
/// executable with no arguments at all.
/// </summary>
internal static class ServeCommand
{
    /// <summary>Builds the host, wires the modules and serves until shutdown.</summary>
    /// <param name="arguments">Parsed command line values.</param>
    /// <param name="cancellationToken">Cancelled on Ctrl+C or service stop.</param>
    /// <returns><see cref="ExitCode.Success"/> after a graceful shutdown.</returns>
    public static async Task<int> RunAsync(HostStartupArguments arguments, CancellationToken cancellationToken)
    {
        var builder = TalkyHostFactory.CreateBuilder(arguments);

        Modules.AddModules(builder);

        // Registered last on purpose: hosted services stop in reverse registration order, so this
        // one's StopAsync runs first and the node drains before anything else is torn down.
        builder.Services.AddHostedService<DrainHostedService>();

        var app = builder.Build();

        TalkyHostFactory.ConfigureApplication(app);
        Modules.MapModules(app);

        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);

        return ExitCode.Success;
    }
}

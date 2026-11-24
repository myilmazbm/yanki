using Talky.Server.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// <c>talky serve</c> — and the default command, so the Windows service command line carries only
/// options (<c>--data-dir</c>) and no command.
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

        // The drain runs in StoppingAsync, which the host awaits on every hosted service before any
        // StopAsync (Kestrel's included), so the node drains while it still serves.
        builder.Services.AddHostedService<DrainHostedService>();

        await using var app = builder.Build();

        TalkyHostFactory.ValidateStartup(app);
        TalkyHostFactory.ConfigureApplication(app);
        Modules.MapModules(app);

        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);

        return ExitCode.Success;
    }
}

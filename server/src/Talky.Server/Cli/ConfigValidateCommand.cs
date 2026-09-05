using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Hosting;
using Talky.Server.Hosting;
using Talky.Server.Logging;

namespace Talky.Server.Cli;

/// <summary>
/// <c>talky config validate</c> — binds every <c>Talky</c> section, reports validation failures and
/// prints the effective values, so an operator can see exactly what the node would start with.
/// </summary>
internal static class ConfigValidateCommand
{
    /// <summary>Validates configuration without starting a listener.</summary>
    /// <param name="arguments">Parsed command line values.</param>
    /// <param name="cancellationToken">Unused; kept for a uniform command signature.</param>
    /// <returns><see cref="ExitCode.InvalidConfiguration"/> when anything failed to validate.</returns>
    public static async Task<int> RunAsync(HostStartupArguments arguments, CancellationToken cancellationToken)
    {
        var builder = TalkyHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);

        await using var app = builder.Build();

        NodeOptions node;
        ServerOptions server;
        LoggingOptions logging;
        try
        {
            node = app.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
            server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
            logging = app.Services.GetRequiredService<IOptions<LoggingOptions>>().Value;
        }
        catch (OptionsValidationException ex)
        {
            await Console.Error.WriteLineAsync("Configuration is invalid:");
            foreach (var failure in ex.Failures)
            {
                await Console.Error.WriteLineAsync($"  - {failure}");
            }

            return ExitCode.InvalidConfiguration;
        }

        var exitCode = ExitCode.Success;
        try
        {
            TalkyHostFactory.GuardInsecureConfiguration(server, app.Environment);
        }
        catch (TalkyStartupException ex)
        {
            await Console.Error.WriteLineAsync($"Configuration is invalid: {ex.Message}");
            exitCode = ExitCode.InvalidConfiguration;
        }

        _ = NodeRoles.TryParse(node.Roles, out var roles, out _);

        Console.WriteLine($"environment                 {app.Environment.EnvironmentName}");
        Console.WriteLine($"content root                {app.Environment.ContentRootPath}");
        Console.WriteLine($"Talky:Node:Id               {node.Id ?? "(generated per process)"}");
        Console.WriteLine($"Talky:Node:Roles            {NodeRoles.Format(roles)}");
        Console.WriteLine($"Talky:Server:Urls           {string.Join(", ", server.Urls)}");
        Console.WriteLine($"Talky:Server:PublicUrl      {server.PublicUrl ?? "(first url)"}");
        Console.WriteLine($"Talky:Server:AllowInsecureHttp {server.AllowInsecureHttp}");
        Console.WriteLine($"Talky:Server:DrainSeconds   {server.DrainSeconds.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Talky:Server:KnownProxies   {(server.KnownProxies.Length == 0 ? "(none)" : string.Join(", ", server.KnownProxies))}");
        Console.WriteLine($"  Certificate:Source        {server.Certificate.Source}");
        Console.WriteLine($"  Certificate:StoreLocation {server.Certificate.StoreLocation}\\{server.Certificate.StoreName}");
        Console.WriteLine($"  Certificate:Thumbprint    {server.Certificate.Thumbprint ?? "(none)"}");
        Console.WriteLine($"  Certificate:Subject       {server.Certificate.Subject ?? "(none)"}");
        Console.WriteLine($"  Certificate:PfxPath       {server.Certificate.PfxPath ?? "(none)"}");
        Console.WriteLine($"Talky:Logging:Directory     {TalkyLogging.ExpandDirectory(logging.Directory)}");
        Console.WriteLine($"Talky:Logging:MinimumLevel  {logging.MinimumLevel}");
        Console.WriteLine($"Talky:Logging:RetainedFileCount {logging.RetainedFileCount.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Talky:Logging:EventLog      {FormatTriState(logging.EventLog)}");
        Console.WriteLine($"Talky:Logging:Console       {FormatTriState(logging.Console)}");

        if (exitCode == ExitCode.Success)
        {
            Console.WriteLine();
            Console.WriteLine("Configuration is valid.");
        }

        return exitCode;
    }

    private static string FormatTriState(bool? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "(auto)";
}

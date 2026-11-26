using System.Globalization;
using Microsoft.Extensions.Options;
using Yanki.Core.Configuration;
using Yanki.Core.Hosting;
using Yanki.Server.Hosting;
using Yanki.Server.Logging;

namespace Yanki.Server.Cli;

/// <summary>
/// <c>yanki config validate</c> — binds every <c>Yanki</c> section, reports validation failures and
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
        var builder = YankiHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);

        await using var app = builder.Build();

        // Runs every options validation registered with ValidateOnStart (host and modules), exactly
        // as serve does before it starts listening. Failures are reported by YankiCommandLine.
        app.Services.GetRequiredService<IStartupValidator>().Validate();

        var node = app.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
        var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var logging = app.Services.GetRequiredService<IOptions<LoggingOptions>>().Value;

        var exitCode = ExitCode.Success;
        try
        {
            YankiHostFactory.GuardInsecureConfiguration(server, app.Environment);
            YankiHostFactory.GuardDevTokens(app.Configuration, server, app.Environment);
        }
        catch (YankiStartupException ex)
        {
            await Console.Error.WriteLineAsync($"Configuration is invalid: {ex.Message}");
            exitCode = ExitCode.InvalidConfiguration;
        }

        _ = NodeRoles.TryParse(node.Roles, out var roles, out _);

        Console.WriteLine($"environment                 {app.Environment.EnvironmentName}");
        Console.WriteLine($"content root                {app.Environment.ContentRootPath}");
        Console.WriteLine($"Yanki:Node:Id               {node.Id ?? "(generated per process)"}");
        Console.WriteLine($"Yanki:Node:Roles            {NodeRoles.Format(roles)}");
        Console.WriteLine($"Yanki:Server:Urls           {string.Join(", ", server.Urls)}");
        Console.WriteLine($"Yanki:Server:PublicUrl      {server.PublicUrl ?? "(not set; reserved)"}");
        Console.WriteLine($"Yanki:Server:AllowInsecureHttp {server.AllowInsecureHttp}");
        Console.WriteLine($"Yanki:Server:DrainSeconds   {server.DrainSeconds.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Yanki:Server:KnownProxies   {(server.KnownProxies.Length == 0 ? "(none)" : string.Join(", ", server.KnownProxies))}");
        Console.WriteLine($"  Certificate:Source        {server.Certificate.Source}");
        Console.WriteLine($"  Certificate:StoreLocation {server.Certificate.StoreLocation}\\{server.Certificate.StoreName}");
        Console.WriteLine($"  Certificate:Thumbprint    {server.Certificate.Thumbprint ?? "(none)"}");
        Console.WriteLine($"  Certificate:Subject       {server.Certificate.Subject ?? "(none)"}");
        Console.WriteLine($"  Certificate:PfxPath       {server.Certificate.PfxPath ?? "(none)"}");
        Console.WriteLine($"Yanki:Logging:Directory     {YankiLogging.ExpandDirectory(logging.Directory)}");
        Console.WriteLine($"Yanki:Logging:MinimumLevel  {logging.MinimumLevel}");
        Console.WriteLine($"Yanki:Logging:RetainedFileCount {logging.RetainedFileCount.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Yanki:Logging:EventLog      {FormatTriState(logging.EventLog)}");
        Console.WriteLine($"Yanki:Logging:Console       {FormatTriState(logging.Console)}");

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

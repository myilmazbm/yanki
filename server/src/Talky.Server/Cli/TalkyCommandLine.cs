using System.CommandLine;
using Microsoft.Extensions.Options;
using Talky.Core.Hosting;
using Talky.Server.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// The talky.exe command line. Running the bare executable is identical to <c>talky serve</c>, which
/// is what the Windows service does — the service control manager starts the binary with no
/// arguments.
/// </summary>
internal static class TalkyCommandLine
{
    /// <summary>Parses <paramref name="args"/> and runs the selected command.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var configuration = new InvocationConfiguration
        {
            // The host owns graceful shutdown; System.CommandLine must not kill the process while
            // the node is still draining, and must not swallow the exceptions we handle below.
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = TimeSpan.FromDays(1),
        };

        try
        {
            return await BuildRootCommand().Parse(args).InvokeAsync(configuration, cancellationToken);
        }
        catch (TalkyStartupException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return ExitCode.InvalidConfiguration;
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
        catch (OperationCanceledException)
        {
            return ExitCode.Success;
        }
    }

    /// <summary>Builds a fresh command tree; the options are per-call so nothing is shared.</summary>
    private static RootCommand BuildRootCommand()
    {
        var configOption = new Option<string?>("--config", "-c")
        {
            Description = "Extra JSON configuration file, layered on top of appsettings.json and %ProgramData%\\Talky.",
            HelpName = "file",
            Recursive = true,
        };

        var roleOption = new Option<string?>("--role")
        {
            Description = $"Roles this node runs: '{NodeRoles.AllName}' or a comma separated list of {string.Join(", ", NodeRoles.Names)}.",
            HelpName = "roles",
            Recursive = true,
        };

        var urlsOption = new Option<string[]>("--urls")
        {
            Description = "Endpoints Kestrel binds to, e.g. https://+:8443. Replaces Talky:Server:Urls entirely.",
            HelpName = "urls",
            AllowMultipleArgumentsPerToken = true,
            Recursive = true,
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override a single configuration key, e.g. --set Talky:Server:DrainSeconds=60. Repeatable.",
            HelpName = "key=value",
            AllowMultipleArgumentsPerToken = false,
            Recursive = true,
        };

        HostStartupArguments Arguments(ParseResult parseResult) => new(
            parseResult.GetValue(configOption),
            parseResult.GetValue(roleOption),
            parseResult.GetValue(urlsOption) ?? [],
            parseResult.GetValue(setOption) ?? []);

        var serve = new Command("serve", "Run the Talky server. This is what the Windows service starts.");
        serve.SetAction((parseResult, token) => ServeCommand.RunAsync(Arguments(parseResult), token));

        var migrate = new Command("migrate", "Show and apply pending database migrations, then exit.");
        migrate.SetAction((parseResult, token) => MigrateCommand.RunAsync(Arguments(parseResult), token));

        var doctor = new Command("doctor", "Run every environment check and print a report.");
        doctor.SetAction((parseResult, token) => DoctorCommand.RunAsync(Arguments(parseResult), token));

        var version = new Command("version", "Print the talky.exe version and runtime information.");
        version.SetAction(_ => VersionCommand.Run());

        var configValidate = new Command("validate", "Bind and validate configuration, then print the effective values.");
        configValidate.SetAction((parseResult, token) => ConfigValidateCommand.RunAsync(Arguments(parseResult), token));

        var config = new Command("config", "Configuration utilities.");
        config.Subcommands.Add(configValidate);

        var root = new RootCommand(
            "Talky server: on-premises messaging, calling and meetings. Running talky with no command serves.");

        root.Options.Add(configOption);
        root.Options.Add(roleOption);
        root.Options.Add(urlsOption);
        root.Options.Add(setOption);

        root.Subcommands.Add(serve);
        root.Subcommands.Add(migrate);
        root.Subcommands.Add(doctor);
        root.Subcommands.Add(version);
        root.Subcommands.Add(config);

        // No command means serve, so `sc.exe start Talky` works without arguments.
        root.SetAction((parseResult, token) => ServeCommand.RunAsync(Arguments(parseResult), token));

        return root;
    }
}

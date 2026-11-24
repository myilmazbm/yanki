using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Hosting;
using Talky.Server.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// The talky.exe command line. Running the executable without a command is identical to
/// <c>talky serve</c>, which is what the Windows service does: the installers register it with
/// options only (<c>--role</c>, <c>--data-dir</c>) and no command.
/// </summary>
internal static class TalkyCommandLine
{
    /// <summary>File in <c>&lt;data-dir&gt;\logs</c> that records failures which ended the process.</summary>
    public const string StartupLogFileName = "talky-startup.log";

    /// <summary>Parses <paramref name="args"/> and runs the selected command.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The process exit code.</returns>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Any failure must end in a reported message and an exit code, also when the process runs as a service.")]
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var configuration = new InvocationConfiguration
        {
            // The host owns graceful shutdown; System.CommandLine must not kill the process while
            // the node is still draining, and must not swallow the exceptions handled below.
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = TimeSpan.FromDays(1),
        };

        // Generic host settings such as --environment or --contentRoot are consumed by the host
        // builder, not by this parser, so they are pulled out before parsing and allowed through.
        var hostSettings = HostStartupArguments.ExtractHostSettings(args);

        ParseResult? parseResult = null;
        try
        {
            parseResult = BuildRootCommand(hostSettings).Parse(args);

            var unexpected = parseResult.UnmatchedTokens
                .FirstOrDefault(token => !hostSettings.Contains(token, StringComparer.Ordinal));
            if (unexpected is not null)
            {
                await Console.Error.WriteLineAsync(unexpected.StartsWith('-')
                    ? $"Unknown option '{unexpected}'. Run 'talky --help' to see the available options."
                    : $"Unknown command '{unexpected}'. Run 'talky --help' to see the available commands.");
                return ExitCode.Failure;
            }

            return await parseResult.InvokeAsync(configuration, cancellationToken);
        }
        catch (TalkyStartupException ex)
        {
            await ReportFailureAsync([ex.Message], parseResult);
            return ExitCode.InvalidConfiguration;
        }
        catch (OptionsValidationException ex)
        {
            await ReportFailureAsync(DescribeInvalidConfiguration(ex.Failures), parseResult);
            return ExitCode.InvalidConfiguration;
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(inner => inner is OptionsValidationException))
        {
            // The startup validator throws one AggregateException when several option types fail.
            var failures = ex.Flatten().InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures);
            await ReportFailureAsync(DescribeInvalidConfiguration(failures), parseResult);
            return ExitCode.InvalidConfiguration;
        }
        catch (OperationCanceledException)
        {
            return ExitCode.Success;
        }
        catch (Exception ex)
        {
            await ReportFailureAsync([$"talky failed unexpectedly: {ex}"], parseResult);
            return ExitCode.Failure;
        }
    }

    private static string[] DescribeInvalidConfiguration(IEnumerable<string> failures) =>
        ["Configuration is invalid:", .. failures.Select(failure => $"  - {failure}")];

    /// <summary>
    /// Reports a failure that ends the process. stderr is invisible when the service control manager
    /// starts the process, so the same text also goes, best effort, to the Windows event log (source
    /// <c>Talky</c>) and to <c>&lt;data-dir&gt;\logs\talky-startup.log</c>. Neither needs the host or
    /// its logger, which usually do not exist yet when this runs.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Reporting a failure must never replace it with a second one.")]
    private static async Task ReportFailureAsync(IReadOnlyList<string> lines, ParseResult? parseResult)
    {
        foreach (var line in lines)
        {
            await Console.Error.WriteLineAsync(line);
        }

        var text = string.Join(Environment.NewLine, lines);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                EventLog.WriteEntry(LoggingOptions.EventLogSourceName, text, EventLogEntryType.Error);
            }
            catch (Exception)
            {
                // No event source (it is created at install time) or no permission to write.
            }
        }

        try
        {
            string? dataDirectory = null;
            try
            {
                dataDirectory = parseResult?.GetValue<string?>("--data-dir");
            }
            catch (Exception)
            {
                // The command line could not be parsed; use the default data directory.
            }

            var directory = Path.Combine(
                TalkyConfigurationSources.ResolveDataDirectory(HostStartupArguments.Empty with { DataDirectory = dataDirectory }),
                "logs");
            Directory.CreateDirectory(directory);

            var timestamp = TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            await File.AppendAllTextAsync(
                Path.Combine(directory, StartupLogFileName),
                $"{timestamp} {text.ReplaceLineEndings(" ")}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best effort: the data directory may not exist or may not be writable.
        }
    }

    /// <summary>Builds a fresh command tree; the options are per-call so nothing is shared.</summary>
    /// <param name="hostSettings">Generic host settings handed to the host builder as-is.</param>
    private static RootCommand BuildRootCommand(string[] hostSettings)
    {
        var configOption = new Option<string?>("--config", "-c")
        {
            Description = "Extra JSON configuration file, layered on top of appsettings.json and %ProgramData%\\Talky.",
            HelpName = "file",
            Recursive = true,
        };

        var dataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Data directory of this node (default %ProgramData%\\Talky). Its appsettings.json is loaded, logs default to <dir>\\logs.",
            HelpName = "dir",
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
            parseResult.GetValue(setOption) ?? [],
            hostSettings)
        {
            DataDirectory = parseResult.GetValue(dataDirOption),
        };

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
            "Talky server: on-premises messaging, calling and meetings. Running talky with no command serves.")
        {
            // Generic host settings (--environment, --contentRoot, --applicationName, --webroot)
            // arrive on the command line from deployment scripts and from the ASP.NET Core test
            // host. They are consumed by the host builder, not by this parser.
            TreatUnmatchedTokensAsErrors = false,
        };

        root.Options.Add(configOption);
        root.Options.Add(dataDirOption);
        root.Options.Add(roleOption);
        root.Options.Add(urlsOption);
        root.Options.Add(setOption);

        root.Subcommands.Add(serve);
        root.Subcommands.Add(migrate);
        root.Subcommands.Add(doctor);
        root.Subcommands.Add(version);
        root.Subcommands.Add(config);

        // No command means serve, so the service command line needs no command, only options.
        root.SetAction((parseResult, token) => ServeCommand.RunAsync(Arguments(parseResult), token));

        return root;
    }
}

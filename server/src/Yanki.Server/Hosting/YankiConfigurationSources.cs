using Talky.Core.Configuration;

namespace Talky.Server.Hosting;

/// <summary>
/// Builds the configuration stack in the documented order (see <c>docs/configuration.md</c>).
/// Later sources win:
/// <list type="number">
/// <item><c>appsettings.json</c> next to talky.exe</item>
/// <item><c>appsettings.{Environment}.json</c> next to talky.exe</item>
/// <item><c>%ProgramData%\Talky\appsettings.json</c></item>
/// <item><c>%ProgramData%\Talky\appsettings.Local.json</c></item>
/// <item>the file passed to <c>--config</c></item>
/// <item>environment variables (plain, then <c>TALKY_</c> prefixed)</item>
/// <item>command line (<c>--role</c>, <c>--urls</c>, <c>--set key=value</c>)</item>
/// </list>
/// </summary>
internal static class TalkyConfigurationSources
{
    /// <summary>Directory holding machine-wide configuration and logs.</summary>
    public static string ProgramDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Talky");

    /// <summary>Replaces the default sources of <paramref name="configuration"/> with the Talky stack.</summary>
    /// <param name="configuration">The builder's configuration manager.</param>
    /// <param name="environmentName">Resolved environment name (Development / Production / ...).</param>
    /// <param name="contentRootPath">Resolved content root, always the directory of talky.exe.</param>
    /// <param name="applicationName">Resolved application (assembly) name.</param>
    /// <param name="arguments">Command line values that participate in configuration.</param>
    /// <exception cref="TalkyStartupException">
    /// The <c>--config</c> file does not exist, or one of the JSON files is not valid JSON.
    /// </exception>
    public static void Apply(
        IConfigurationBuilder configuration,
        string environmentName,
        string contentRootPath,
        string applicationName,
        HostStartupArguments arguments)
    {
        configuration.Sources.Clear();

        // Re-state the host settings the cleared sources carried, so that clearing does not reset
        // the environment, content root or application name at Build() time.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [HostDefaults.EnvironmentKey] = environmentName,
            [HostDefaults.ContentRootKey] = contentRootPath,
            [HostDefaults.ApplicationKey] = applicationName,
        });

        var exeDirectory = AppContext.BaseDirectory;
        AddJsonFile(configuration, Path.Combine(exeDirectory, "appsettings.json"), optional: true);
        AddJsonFile(
            configuration,
            Path.Combine(exeDirectory, FormattableString.Invariant($"appsettings.{environmentName}.json")),
            optional: true);

        // --data-dir replaces %ProgramData%\Talky as the node's configuration/data root. Its
        // defaults sit below the data directory's own appsettings files, environment variables and
        // the command line, so anything explicit still wins.
        var dataDirectory = ResolveDataDirectory(arguments);
        var dataDirectoryDefaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TalkyConfiguration.RootSection}:DataDirectory"] = dataDirectory,
        };
        if (!string.IsNullOrWhiteSpace(arguments.DataDirectory))
        {
            dataDirectoryDefaults[$"{TalkyConfiguration.LoggingSection}:Directory"] = Path.Combine(dataDirectory, "logs");
        }

        configuration.AddInMemoryCollection(dataDirectoryDefaults);

        AddJsonFile(configuration, Path.Combine(dataDirectory, "appsettings.json"), optional: true);
        AddJsonFile(configuration, Path.Combine(dataDirectory, "appsettings.Local.json"), optional: true);

        if (!string.IsNullOrWhiteSpace(arguments.ConfigFile))
        {
            var path = Path.GetFullPath(arguments.ConfigFile);
            if (!File.Exists(path))
            {
                throw new TalkyStartupException($"Configuration file '{path}' passed to --config does not exist.");
            }

            AddJsonFile(configuration, path, optional: false);
        }

        configuration.AddEnvironmentVariables();
        configuration.Add(new TalkyEnvironmentVariablesSource());

        var overrides = BuildCommandLineOverrides(arguments);
        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }
    }

    /// <summary>
    /// Adds one JSON file. A required file that is missing, or a file that does not parse, becomes a
    /// <see cref="TalkyStartupException"/> naming the file, so every command reports it as a
    /// configuration error instead of a stack trace.
    /// </summary>
    private static void AddJsonFile(IConfigurationBuilder configuration, string path, bool optional) =>
        configuration.AddJsonFile(source =>
        {
            source.Path = path;
            source.Optional = optional;
            source.ReloadOnChange = false;
            source.ResolveFileProvider();
            source.OnLoadException = context =>
            {
                if (context.Exception is FileNotFoundException)
                {
                    throw new TalkyStartupException($"Configuration file '{path}' does not exist.", context.Exception);
                }

                var cause = context.Exception;
                while (cause.InnerException is not null)
                {
                    cause = cause.InnerException;
                }

                throw new TalkyStartupException(
                    $"Configuration file '{path}' is not valid JSON: {cause.Message}",
                    context.Exception);
            };
        });

    /// <summary>
    /// The node's data directory: <c>--data-dir</c> as an absolute path when given, otherwise
    /// <see cref="ProgramDataDirectory"/>. Trailing quotes and directory separators are dropped: a
    /// service command line such as <c>--data-dir "C:\ProgramData\Talky\"</c> reaches the
    /// process as <c>C:\ProgramData\Talky"</c>.
    /// </summary>
    public static string ResolveDataDirectory(HostStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (string.IsNullOrWhiteSpace(arguments.DataDirectory))
        {
            return ProgramDataDirectory;
        }

        var value = Environment.ExpandEnvironmentVariables(arguments.DataDirectory.Trim().TrimEnd('"'));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    /// <summary>Turns the parsed command line into configuration keys.</summary>
    /// <param name="arguments">Parsed command line values.</param>
    /// <returns>Configuration overrides, highest precedence.</returns>
    /// <exception cref="TalkyStartupException">A <c>--set</c> value is not <c>key=value</c>.</exception>
    public static Dictionary<string, string?> BuildCommandLineOverrides(HostStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(arguments.Roles))
        {
            overrides[$"{TalkyConfiguration.NodeSection}:Roles"] = arguments.Roles;
        }

        // --urls is applied with PostConfigure instead of a configuration key: configuration merges
        // arrays element by element, which would leave stale entries from appsettings.json behind.
        foreach (var setting in arguments.Settings)
        {
            var separator = setting.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new TalkyStartupException(
                    $"--set value '{setting}' is not in 'key=value' form, for example --set Talky:Server:DrainSeconds=60.");
            }

            overrides[setting[..separator].Trim()] = setting[(separator + 1)..];
        }

        return overrides;
    }
}

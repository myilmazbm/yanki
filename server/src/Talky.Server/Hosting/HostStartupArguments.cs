namespace Talky.Server.Hosting;

/// <summary>
/// The subset of the command line that influences configuration. Extracted before the host is
/// built, because the configuration sources themselves depend on it.
/// </summary>
/// <param name="ConfigFile">Value of <c>--config</c>: an extra JSON file layered on top of the defaults.</param>
/// <param name="Roles">Value of <c>--role</c>, overriding <c>Talky:Node:Roles</c>.</param>
/// <param name="Urls">Value of <c>--urls</c>, overriding <c>Talky:Server:Urls</c>.</param>
/// <param name="Settings">Values of <c>--set key=value</c>, overriding any single configuration key.</param>
/// <param name="HostSettings">
/// Generic host settings (<c>--environment</c>, <c>--contentRoot</c>, <c>--applicationName</c>,
/// <c>--webroot</c>) passed straight to <see cref="WebApplicationOptions.Args"/>. The ASP.NET Core
/// test host supplies these, and they must reach the builder before anything else is resolved.
/// </param>
internal sealed record HostStartupArguments(
    string? ConfigFile,
    string? Roles,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> Settings,
    IReadOnlyList<string> HostSettings)
{
    /// <summary>Arguments as passed by the Windows service, which runs the bare executable.</summary>
    public static HostStartupArguments Empty { get; } = new(null, null, [], [], []);

    /// <summary>Names of the generic host settings recognised on the command line.</summary>
    private static readonly string[] HostSettingNames =
        [HostDefaults.EnvironmentKey, HostDefaults.ContentRootKey, HostDefaults.ApplicationKey, "webroot"];

    /// <summary>
    /// Picks the generic host settings out of a raw command line. Everything else is left to the
    /// Talky command parser: feeding the whole command line to <c>AddCommandLine</c> would fail on
    /// bare tokens such as <c>doctor</c>.
    /// </summary>
    /// <param name="args">Raw process arguments.</param>
    /// <returns>Only the recognised <c>--name=value</c> / <c>--name value</c> pairs, in order.</returns>
    public static string[] ExtractHostSettings(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var extracted = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var name = separator < 0 ? arg[2..] : arg[2..separator];
            if (!HostSettingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (separator >= 0)
            {
                extracted.Add(arg);
            }
            else if (i + 1 < args.Count)
            {
                extracted.Add(arg);
                extracted.Add(args[++i]);
            }
        }

        return [.. extracted];
    }

    /// <summary>True when the command line pins the content root itself.</summary>
    public bool HasContentRoot => HostSettings.Any(arg =>
        arg.StartsWith($"--{HostDefaults.ContentRootKey}", StringComparison.OrdinalIgnoreCase));
}

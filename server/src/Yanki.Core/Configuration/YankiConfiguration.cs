using System.Text;

namespace Yanki.Core.Configuration;

/// <summary>
/// Configuration section names and the environment-variable naming convention used by yanki.exe.
/// </summary>
public static class YankiConfiguration
{
    /// <summary>Root section that holds every Yanki-owned setting.</summary>
    public const string RootSection = "Yanki";

    /// <summary>Section bound to <see cref="NodeOptions"/>.</summary>
    public const string NodeSection = "Yanki:Node";

    /// <summary>Section bound to <see cref="ServerOptions"/>.</summary>
    public const string ServerSection = "Yanki:Server";

    /// <summary>Section bound to <see cref="LoggingOptions"/>.</summary>
    public const string LoggingSection = "Yanki:Logging";

    /// <summary>
    /// Prefix of the environment variables that override <see cref="RootSection"/> settings.
    /// The prefix stands for the root section, so <c>YANKI_Server__Urls__0</c> maps to
    /// <c>Yanki:Server:Urls:0</c>.
    /// </summary>
    public const string EnvironmentVariablePrefix = "YANKI_";

    /// <summary>
    /// Maps an environment variable name to a configuration key, or returns <see langword="null"/>
    /// when the variable is not a Yanki override.
    /// </summary>
    /// <param name="variableName">Raw environment variable name, e.g. <c>YANKI_Server__DrainSeconds</c>.</param>
    /// <returns>The configuration key (<c>Yanki:Server:DrainSeconds</c>) or <see langword="null"/>.</returns>
    public static string? TryMapEnvironmentVariable(string? variableName)
    {
        if (string.IsNullOrEmpty(variableName) ||
            !variableName.StartsWith(EnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = variableName[EnvironmentVariablePrefix.Length..];
        if (suffix.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(RootSection.Length + 1 + suffix.Length);
        builder.Append(RootSection).Append(':');

        for (var i = 0; i < suffix.Length; i++)
        {
            if (suffix[i] == '_' && i + 1 < suffix.Length && suffix[i + 1] == '_')
            {
                builder.Append(':');
                i++;
                continue;
            }

            builder.Append(suffix[i]);
        }

        var key = builder.ToString();

        // Reject malformed names such as "YANKI___Foo" that would produce empty path segments.
        return key.Contains("::", StringComparison.Ordinal) || key.EndsWith(':') ? null : key;
    }
}

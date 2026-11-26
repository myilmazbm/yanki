using System.Text;

namespace Talky.Core.Configuration;

/// <summary>
/// Configuration section names and the environment-variable naming convention used by talky.exe.
/// </summary>
public static class TalkyConfiguration
{
    /// <summary>Root section that holds every Talky-owned setting.</summary>
    public const string RootSection = "Talky";

    /// <summary>Section bound to <see cref="NodeOptions"/>.</summary>
    public const string NodeSection = "Talky:Node";

    /// <summary>Section bound to <see cref="ServerOptions"/>.</summary>
    public const string ServerSection = "Talky:Server";

    /// <summary>Section bound to <see cref="LoggingOptions"/>.</summary>
    public const string LoggingSection = "Talky:Logging";

    /// <summary>
    /// Prefix of the environment variables that override <see cref="RootSection"/> settings.
    /// The prefix stands for the root section, so <c>TALKY_Server__Urls__0</c> maps to
    /// <c>Talky:Server:Urls:0</c>.
    /// </summary>
    public const string EnvironmentVariablePrefix = "TALKY_";

    /// <summary>
    /// Maps an environment variable name to a configuration key, or returns <see langword="null"/>
    /// when the variable is not a Talky override.
    /// </summary>
    /// <param name="variableName">Raw environment variable name, e.g. <c>TALKY_Server__DrainSeconds</c>.</param>
    /// <returns>The configuration key (<c>Talky:Server:DrainSeconds</c>) or <see langword="null"/>.</returns>
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

        // Reject malformed names such as "TALKY___Foo" that would produce empty path segments.
        return key.Contains("::", StringComparison.Ordinal) || key.EndsWith(':') ? null : key;
    }
}

using System.Collections;
using Talky.Core.Configuration;

namespace Talky.Server.Hosting;

/// <summary>
/// Configuration source for <c>TALKY_</c> prefixed environment variables. The prefix stands for the
/// <c>Talky</c> root section and <c>__</c> separates sections, so <c>TALKY_Server__DrainSeconds=60</c>
/// sets <c>Talky:Server:DrainSeconds</c>.
/// </summary>
internal sealed class TalkyEnvironmentVariablesSource : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new TalkyEnvironmentVariablesProvider();
}

/// <summary>Provider behind <see cref="TalkyEnvironmentVariablesSource"/>.</summary>
internal sealed class TalkyEnvironmentVariablesProvider : ConfigurationProvider
{
    /// <inheritdoc />
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = TalkyConfiguration.TryMapEnvironmentVariable(entry.Key as string);
            if (key is not null)
            {
                data[key] = entry.Value as string;
            }
        }

        Data = data;
    }
}

using System.Collections;
using Yanki.Core.Configuration;

namespace Yanki.Server.Hosting;

/// <summary>
/// Configuration source for <c>YANKI_</c> prefixed environment variables. The prefix stands for the
/// <c>Yanki</c> root section and <c>__</c> separates sections, so <c>YANKI_Server__DrainSeconds=60</c>
/// sets <c>Yanki:Server:DrainSeconds</c>.
/// </summary>
internal sealed class YankiEnvironmentVariablesSource : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new YankiEnvironmentVariablesProvider();
}

/// <summary>Provider behind <see cref="YankiEnvironmentVariablesSource"/>.</summary>
internal sealed class YankiEnvironmentVariablesProvider : ConfigurationProvider
{
    /// <inheritdoc />
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = YankiConfiguration.TryMapEnvironmentVariable(entry.Key as string);
            if (key is not null)
            {
                data[key] = entry.Value as string;
            }
        }

        Data = data;
    }
}

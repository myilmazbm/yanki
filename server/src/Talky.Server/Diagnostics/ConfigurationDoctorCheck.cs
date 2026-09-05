using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Core.Hosting;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Resolves every <c>Talky</c> options section so that binding and validation errors surface as a
/// doctor row instead of a startup crash.
/// </summary>
internal sealed class ConfigurationDoctorCheck(
    IOptions<NodeOptions> node,
    IOptions<ServerOptions> server,
    IOptions<LoggingOptions> logging,
    IHostEnvironment environment) : IDoctorCheck
{
    /// <inheritdoc />
    public string Name => "config";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nodeOptions = node.Value;
            var serverOptions = server.Value;
            var loggingOptions = logging.Value;

            _ = NodeRoles.TryParse(nodeOptions.Roles, out var roles, out _);

            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["environment"] = environment.EnvironmentName,
                ["roles"] = NodeRoles.Format(roles),
                ["urls"] = string.Join(", ", serverOptions.Urls),
                ["drainSeconds"] = serverOptions.DrainSeconds.ToString(CultureInfo.InvariantCulture),
                ["certificateSource"] = serverOptions.Certificate.Source.ToString(),
                ["logDirectory"] = loggingOptions.Directory,
            };

            return Task.FromResult(DoctorResult.Ok("Configuration binds and validates.", details));
        }
        catch (OptionsValidationException ex)
        {
            return Task.FromResult(DoctorResult.Error(
                "Configuration is invalid.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failures"] = string.Join(" | ", ex.Failures),
                }));
        }
    }
}

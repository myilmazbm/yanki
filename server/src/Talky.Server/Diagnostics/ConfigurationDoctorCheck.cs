using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Core.Hosting;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Runs every options validation registered with <c>ValidateOnStart</c>, so that an invalid module
/// section surfaces as a doctor row. Invalid host sections (<c>Talky:Node</c>, <c>Talky:Server</c>,
/// <c>Talky:Logging</c>) already stop the host from being built and never reach this check.
/// </summary>
internal sealed class ConfigurationDoctorCheck(
    IStartupValidator startupValidator,
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
            startupValidator.Validate();

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
                ["logDirectory"] = Logging.TalkyLogging.ExpandDirectory(loggingOptions.Directory),
            };

            return Task.FromResult(DoctorResult.Ok("Configuration binds and validates.", details));
        }
        catch (OptionsValidationException ex)
        {
            return Task.FromResult(Invalid(ex.Failures));
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(inner => inner is OptionsValidationException))
        {
            return Task.FromResult(Invalid(
                ex.Flatten().InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures)));
        }
    }

    private static DoctorResult Invalid(IEnumerable<string> failures) => DoctorResult.Error(
        "Configuration is invalid.",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["failures"] = string.Join(" | ", failures),
        });
}

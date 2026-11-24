using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Core.Hosting;
using Talky.Server.Hosting;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Runs every options validation registered with <c>ValidateOnStart</c> and the development token
/// guard that <c>talky serve</c> applies, so that a configuration serve refuses is not reported clean.
/// The <c>Validate()</c> call surfaces sections that no check constructor reads (today
/// <c>Talky:Realtime</c>). Invalid host sections (<c>Talky:Node</c>, <c>Talky:Server</c>,
/// <c>Talky:Logging</c>) and invalid <c>Talky:Database</c> options end doctor with exit code 3
/// before any row is printed: they fail while the host or the checks are being constructed.
/// </summary>
internal sealed class ConfigurationDoctorCheck(
    IStartupValidator startupValidator,
    IConfiguration configuration,
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

            TalkyHostFactory.GuardDevTokens(configuration, serverOptions, environment);

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
        catch (TalkyStartupException ex)
        {
            return Task.FromResult(DoctorResult.Error(ex.Message));
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

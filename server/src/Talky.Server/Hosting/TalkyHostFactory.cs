using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Core.Hosting;
using Talky.Server.Diagnostics;
using Talky.Server.Endpoints;
using Talky.Server.Logging;

namespace Talky.Server.Hosting;

/// <summary>
/// Builds the one and only Talky host. Every command uses it: <c>serve</c> runs the resulting
/// application, while <c>migrate</c>, <c>doctor</c> and <c>config validate</c> only need the
/// service provider it produces.
/// </summary>
internal static class TalkyHostFactory
{
    /// <summary>Windows service name; must match the name used by the MSI and by <c>sc.exe</c>.</summary>
    public const string WindowsServiceName = "Talky";

    /// <summary>Seconds added on top of the drain grace period before the host is force stopped.</summary>
    public const int ShutdownTimeoutHeadroomSeconds = 15;

    /// <summary>Creates a fully configured builder: configuration stack, logging, options and services.</summary>
    /// <param name="arguments">Command line values that participate in configuration.</param>
    /// <returns>The builder, ready for module registration.</returns>
    public static WebApplicationBuilder CreateBuilder(HostStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var isWindowsService = WindowsServiceHelpers.IsWindowsService();

        // A Windows service starts with C:\Windows\System32 as its working directory, so the
        // content root must be pinned to the directory holding talky.exe.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        TalkyConfigurationSources.Apply(
            builder.Configuration,
            builder.Environment.EnvironmentName,
            builder.Environment.ContentRootPath,
            builder.Environment.ApplicationName,
            arguments);

        builder.Host.UseWindowsService(options => options.ServiceName = WindowsServiceName);

        var urls = ResolveUrls(builder.Configuration, arguments);
        builder.WebHost.UseUrls(urls);

        AddOptions(builder, urls);
        AddLogging(builder, isWindowsService);
        AddCoreServices(builder);
        ConfigureKestrel(builder);

        return builder;
    }

    /// <summary>
    /// Resolves the endpoints Kestrel binds to. <c>--urls</c> wins over configuration, and both are
    /// applied as a whole so a shorter command line list cannot leave stale entries behind.
    /// </summary>
    /// <param name="configuration">The assembled configuration.</param>
    /// <param name="arguments">Parsed command line values.</param>
    /// <returns>At least one url.</returns>
    public static string[] ResolveUrls(IConfiguration configuration, HostStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(arguments);

        var fromCommandLine = arguments.Urls
            .SelectMany(value => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        if (fromCommandLine.Length > 0)
        {
            return fromCommandLine;
        }

        var configured = configuration
            .GetSection($"{TalkyConfiguration.ServerSection}:Urls")
            .Get<string[]>() ?? [];

        return configured.Length > 0 ? configured : [ServerOptions.DefaultUrl];
    }

    /// <summary>Binds and validates every <c>Talky</c> section, failing fast at startup.</summary>
    private static void AddOptions(WebApplicationBuilder builder, string[] urls)
    {
        builder.Services.AddSingleton<IValidateOptions<NodeOptions>, ValidateNodeOptions>();
        builder.Services.AddOptions<NodeOptions>()
            .Bind(builder.Configuration.GetSection(TalkyConfiguration.NodeSection))
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<ServerOptions>, ValidateServerOptions>();
        builder.Services.AddOptions<ServerOptions>()
            .Bind(builder.Configuration.GetSection(TalkyConfiguration.ServerSection))
            .PostConfigure(options => options.Urls = urls)
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<LoggingOptions>, ValidateLoggingOptions>();
        builder.Services.AddOptions<LoggingOptions>()
            .Bind(builder.Configuration.GetSection(TalkyConfiguration.LoggingSection))
            .ValidateOnStart();

        // Give the drain enough head room to finish before the host force stops.
        builder.Services.AddOptions<HostOptions>()
            .Configure<IOptions<ServerOptions>>((host, server) =>
                host.ShutdownTimeout = TimeSpan.FromSeconds(server.Value.DrainSeconds + ShutdownTimeoutHeadroomSeconds));
    }

    private static void AddLogging(WebApplicationBuilder builder, bool isWindowsService)
    {
        // Serilog is the only logging provider; UseWindowsService adds an event log provider that
        // would otherwise duplicate everything the Serilog event log sink writes.
        builder.Logging.ClearProviders();

        var isDevelopment = builder.Environment.IsDevelopment();
        builder.Services.AddSerilog((services, configuration) => TalkyLogging.Configure(
            configuration,
            services.GetRequiredService<IOptions<LoggingOptions>>().Value,
            services.GetRequiredService<INodeInfo>(),
            isDevelopment,
            isWindowsService));
    }

    private static void AddCoreServices(WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services.AddSingleton<INodeInfo>(services => NodeInfo.Create(
            services.GetRequiredService<IOptions<NodeOptions>>().Value,
            services.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<DrainCoordinator>();
        builder.Services.AddSingleton<IDrainCoordinator>(services => services.GetRequiredService<DrainCoordinator>());

        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks()
            .AddCheck<DrainingHealthCheck>(DrainingHealthCheck.Name, tags: ["ready"]);

        builder.Services.AddSingleton<IDoctorCheck, ConfigurationDoctorCheck>();
        builder.Services.AddSingleton<IDoctorCheck, CertificateDoctorCheck>();
        builder.Services.AddSingleton<IDoctorCheck, LogDirectoryDoctorCheck>();
        builder.Services.AddSingleton<IDoctorCheck, IdentityDoctorCheck>();
        builder.Services.AddSingleton<IDoctorCheck, OperatingSystemDoctorCheck>();
        builder.Services.AddSingleton<IDoctorCheck, RuntimeDoctorCheck>();

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddOpenApi();
        }

        var knownProxies = builder.Configuration
            .GetSection($"{TalkyConfiguration.ServerSection}:KnownProxies")
            .Get<string[]>() ?? [];

        if (knownProxies.Length == 0)
        {
            return;
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }
        });
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder) =>
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = HttpProtocols.Http1AndHttp2);

            var server = kestrel.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>().Value;
            var environment = kestrel.ApplicationServices.GetRequiredService<IHostEnvironment>();
            GuardInsecureConfiguration(server, environment);

            var resolution = CertificateResolver.Resolve(server.Certificate);
            if (resolution.Certificate is not null)
            {
                kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = resolution.Certificate);
            }
        });

    /// <summary>
    /// Refuses the combination that silently downgrades transport security outside development.
    /// </summary>
    /// <param name="server">Validated server options.</param>
    /// <param name="environment">Host environment.</param>
    /// <exception cref="TalkyStartupException">The node would run without a real certificate.</exception>
    public static void GuardInsecureConfiguration(ServerOptions server, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(environment);

        if (server.Certificate.Source != CertificateSource.DevCert ||
            environment.IsDevelopment() ||
            server.AllowInsecureHttp)
        {
            return;
        }

        throw new TalkyStartupException(
            $"Talky:Server:Certificate:Source is 'DevCert' but the environment is '{environment.EnvironmentName}'. " +
            "Configure a Store or Pfx certificate, or set Talky:Server:AllowInsecureHttp to true for a lab node.");
    }

    /// <summary>Applies the host's own middleware and endpoints; called after <c>builder.Build()</c>.</summary>
    /// <param name="app">The built application.</param>
    public static void ConfigureApplication(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

        if (server.KnownProxies.Length > 0)
        {
            app.UseForwardedHeaders();
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseSerilogRequestLogging(options => options.GetLevel = TalkyRequestLogging.GetLevel);

        TalkyEndpoints.Map(app);
    }
}

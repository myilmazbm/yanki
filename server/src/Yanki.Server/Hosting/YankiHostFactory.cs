using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using Yanki.Core.Configuration;
using Yanki.Core.Diagnostics;
using Yanki.Core.Hosting;
using Yanki.Server.Diagnostics;
using Yanki.Server.Endpoints;
using Yanki.Server.Logging;

namespace Yanki.Server.Hosting;

/// <summary>
/// Builds the one and only Yanki host. Every command uses it: <c>serve</c> runs the resulting
/// application, while <c>migrate</c>, <c>doctor</c> and <c>config validate</c> only need the
/// service provider it produces.
/// </summary>
internal static class YankiHostFactory
{
    /// <summary>Windows service name; must match the name used by the MSI and by <c>sc.exe</c>.</summary>
    public const string WindowsServiceName = "Yanki";

    /// <summary>Seconds added on top of the drain grace period before the host is force stopped.</summary>
    public const int ShutdownTimeoutHeadroomSeconds = 15;

    /// <summary>Configuration key of the Realtime module's development token switch.</summary>
    private const string AllowDevTokensKey = "Yanki:Realtime:AllowDevTokens";

    /// <summary>Creates a fully configured builder: configuration stack, logging, options and services.</summary>
    /// <param name="arguments">Command line values that participate in configuration.</param>
    /// <returns>The builder, ready for module registration.</returns>
    public static WebApplicationBuilder CreateBuilder(HostStartupArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var isWindowsService = WindowsServiceHelpers.IsWindowsService();

        // A Windows service starts with C:\Windows\System32 as its working directory, so the
        // content root is pinned to the directory holding yanki.exe unless the command line pins
        // one itself (the ASP.NET Core test host does).
        WebApplicationBuilder builder;
        try
        {
            builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [.. arguments.HostSettings],
                ContentRootPath = arguments.HasContentRoot ? null : AppContext.BaseDirectory,
            });
        }
        catch (InvalidDataException ex)
        {
            // The default builder loads appsettings.json and appsettings.{Environment}.json from the
            // content root itself, before YankiConfigurationSources can wrap their load errors.
            Exception cause = ex;
            while (cause.InnerException is not null)
            {
                cause = cause.InnerException;
            }

            throw new YankiStartupException($"{ex.Message} {cause.Message}", ex);
        }

        YankiConfigurationSources.Apply(
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
            .GetSection($"{YankiConfiguration.ServerSection}:Urls")
            .Get<string[]>() ?? [];

        return configured.Length > 0 ? configured : [ServerOptions.DefaultUrl];
    }

    /// <summary>Binds and validates every <c>Yanki</c> section, failing fast at startup.</summary>
    private static void AddOptions(WebApplicationBuilder builder, string[] urls)
    {
        builder.Services.AddSingleton<IValidateOptions<NodeOptions>, ValidateNodeOptions>();
        builder.Services.AddOptions<NodeOptions>()
            .Bind(builder.Configuration.GetSection(YankiConfiguration.NodeSection))
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<ServerOptions>, ValidateServerOptions>();
        builder.Services.AddOptions<ServerOptions>()
            .Bind(builder.Configuration.GetSection(YankiConfiguration.ServerSection))
            .PostConfigure(options => options.Urls = urls)
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<LoggingOptions>, ValidateLoggingOptions>();
        builder.Services.AddOptions<LoggingOptions>()
            .Bind(builder.Configuration.GetSection(YankiConfiguration.LoggingSection))
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
        builder.Services.AddSerilog((services, configuration) => YankiLogging.Configure(
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
            .GetSection($"{YankiConfiguration.ServerSection}:KnownProxies")
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

            // Resolved when an HTTPS endpoint is actually bound, not while the container is built:
            // `yanki doctor` has to be able to build the host on a node whose certificate is broken.
            kestrel.ConfigureHttpsDefaults(https =>
            {
                var server = kestrel.ApplicationServices.GetRequiredService<IOptions<ServerOptions>>().Value;
                var resolution = CertificateResolver.Resolve(server.Certificate);
                if (resolution.Certificate is not null)
                {
                    https.ServerCertificate = resolution.Certificate;
                }
            });
        });

    /// <summary>
    /// Last gate before the node starts listening: refuses an unsafe transport or authentication
    /// setup and proves the certificate can be loaded and its private key used by this account, so
    /// a bad thumbprint or a missing key permission fails with one readable line instead of a node
    /// that runs but completes no TLS handshake.
    /// </summary>
    /// <param name="app">The built application.</param>
    /// <exception cref="YankiStartupException">The node must not serve with this configuration.</exception>
    public static void ValidateStartup(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var server = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        GuardInsecureConfiguration(server, app.Environment);
        GuardDevTokens(app.Configuration, server, app.Environment);

        if (server.Urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            var resolution = CertificateResolver.Resolve(server.Certificate);
            using var certificate = resolution.Certificate;
            if (certificate is not null)
            {
                CertificateResolver.EnsurePrivateKeyAccessible(certificate, resolution.Description);
            }

            if (app.Logger.IsEnabled(LogLevel.Information))
            {
                if (certificate is null)
                {
                    // DevCert: Kestrel loads the development certificate itself; the description says which.
                    app.Logger.LogInformation("Serving HTTPS with {Certificate}.", resolution.Description);
                }
                else
                {
                    app.Logger.LogInformation(
                        "Serving HTTPS with {Certificate} (thumbprint {Thumbprint}, not after {NotAfter:u}).",
                        resolution.Description,
                        certificate.Thumbprint,
                        certificate.NotAfter.ToUniversalTime());
                }
            }

            if (certificate is not null)
            {
                var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
                if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
                {
                    app.Logger.LogWarning(
                        "The server certificate {Certificate} is outside its validity period; clients will reject it.",
                        resolution.Description);
                }
            }
        }

        var logging = app.Services.GetRequiredService<IOptions<LoggingOptions>>().Value;
        var logDirectory = YankiLogging.ExpandDirectory(logging.Directory);
        if (YankiLogging.ProbeWritable(logDirectory) is not null)
        {
            // The file sink is dropped when its directory cannot be created, and writes nothing when
            // this account cannot write there; say so in the sinks that do work. `yanki doctor`
            // (log-directory) reports the reason.
            app.Logger.LogWarning(
                "Log directory {LogDirectory} is not usable, so the file log is disabled. Run 'yanki doctor' for the reason.",
                logDirectory);
        }
    }

    /// <summary>
    /// Refuses the combination that silently downgrades transport security outside development.
    /// </summary>
    /// <param name="server">Validated server options.</param>
    /// <param name="environment">Host environment.</param>
    /// <exception cref="YankiStartupException">The node would run without a real certificate.</exception>
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

        throw new YankiStartupException(
            $"Yanki:Server:Certificate:Source is 'DevCert' but the environment is '{environment.EnvironmentName}'. " +
            "Configure a Store or Pfx certificate, or set Yanki:Server:AllowInsecureHttp to true for a lab node.");
    }

    /// <summary>
    /// Refuses development tokens (any caller may sign in as any user) outside development, unless
    /// the node is an explicitly insecure lab node.
    /// </summary>
    /// <param name="configuration">The assembled configuration; <c>Yanki:Realtime:AllowDevTokens</c> is read from it.</param>
    /// <param name="server">Validated server options.</param>
    /// <param name="environment">Host environment.</param>
    /// <exception cref="YankiStartupException">Dev tokens are on where they must not be.</exception>
    public static void GuardDevTokens(IConfiguration configuration, ServerOptions server, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(environment);

        var allowDevTokens = bool.TryParse(configuration[AllowDevTokensKey], out var value) && value;
        if (!allowDevTokens || environment.IsDevelopment() || server.AllowInsecureHttp)
        {
            return;
        }

        throw new YankiStartupException(
            $"{AllowDevTokensKey} is true but the environment is '{environment.EnvironmentName}'; dev tokens let " +
            "any caller sign in as any user. Turn it off, or for a lab node run with --environment Development " +
            "or set Yanki:Server:AllowInsecureHttp to true.");
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

        app.UseSerilogRequestLogging(options => options.GetLevel = YankiRequestLogging.GetLevel);

        YankiEndpoints.Map(app);
    }
}

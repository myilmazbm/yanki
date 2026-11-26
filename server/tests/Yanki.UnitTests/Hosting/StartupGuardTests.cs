using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Yanki.Core.Configuration;
using Yanki.Server.Hosting;

namespace Yanki.UnitTests.Hosting;

public sealed class StartupGuardTests
{
    [Theory]
    [InlineData(CertificateSource.DevCert, "Production", false, false, true)]
    [InlineData(CertificateSource.DevCert, "Development", false, false, false)]
    [InlineData(CertificateSource.DevCert, "Production", true, false, false)]
    [InlineData(CertificateSource.Store, "Production", false, false, false)]
    [InlineData(CertificateSource.Store, "Production", false, true, true)]
    [InlineData(CertificateSource.Store, "Production", true, true, false)]
    [InlineData(CertificateSource.Store, "Development", false, true, false)]
    public void Insecure_certificate_and_dev_tokens_are_refused_outside_development(
        CertificateSource source,
        string environmentName,
        bool allowInsecureHttp,
        bool allowDevTokens,
        bool refused)
    {
        var server = new ServerOptions
        {
            AllowInsecureHttp = allowInsecureHttp,
            Certificate = new CertificateOptions { Source = source, Thumbprint = "A1B2" },
        };
        var environment = new HostingEnvironment { EnvironmentName = environmentName };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Yanki:Realtime:AllowDevTokens"] = allowDevTokens ? "true" : "false",
            })
            .Build();

        void Guards()
        {
            YankiHostFactory.GuardInsecureConfiguration(server, environment);
            YankiHostFactory.GuardDevTokens(configuration, server, environment);
        }

        if (refused)
        {
            Assert.Throws<YankiStartupException>(Guards);
        }
        else
        {
            Guards();
        }
    }
}

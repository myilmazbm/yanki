using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Talky.Server.Hosting;

namespace Talky.UnitTests.Hosting;

public sealed class CertificateResolverTests
{
    [Fact]
    public void Private_key_probe_passes_with_a_key_and_fails_without_one()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=talky.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var withKey = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        CertificateResolver.EnsurePrivateKeyAccessible(withKey, "test certificate");

        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        Assert.Throws<TalkyStartupException>(() => CertificateResolver.EnsurePrivateKeyAccessible(publicOnly, "test certificate"));
    }
}

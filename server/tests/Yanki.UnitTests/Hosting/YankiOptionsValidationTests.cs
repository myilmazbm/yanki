using Microsoft.Extensions.Options;
using Talky.Core.Configuration;

namespace Talky.UnitTests.Hosting;

public sealed class NodeOptionsValidationTests
{
    private static ValidateOptionsResult Validate(NodeOptions options) =>
        new ValidateNodeOptions().Validate(Options.DefaultName, options);

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validate(new NodeOptions()).Succeeded);
    }

    [Fact]
    public void Unknown_role_fails_with_the_valid_values()
    {
        var result = Validate(new NodeOptions { Roles = "bogus" });

        Assert.True(result.Failed);
        Assert.Contains("Talky:Node:Roles", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("push-relay", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_role_fails()
    {
        Assert.True(Validate(new NodeOptions { Roles = "" }).Failed);
    }

    [Theory]
    [InlineData("api 01")]
    [InlineData("api/01")]
    [InlineData("   ")]
    [InlineData("this-node-id-is-far-too-long-to-be-useful-and-exceeds-the-sixty-four-character-limit")]
    public void Malformed_node_id_fails(string id)
    {
        var result = Validate(new NodeOptions { Id = id });

        Assert.True(result.Failed);
        Assert.Contains("Talky:Node:Id", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Well_formed_node_id_passes()
    {
        Assert.True(Validate(new NodeOptions { Id = "api-01.corp_local" }).Succeeded);
    }

    [Fact]
    public void Absent_node_id_passes_because_the_host_generates_one()
    {
        Assert.True(Validate(new NodeOptions { Id = null }).Succeeded);
        Assert.True(Validate(new NodeOptions { Id = "" }).Succeeded);
    }
}

public sealed class ServerOptionsValidationTests
{
    private static ValidateOptionsResult Validate(ServerOptions options) =>
        new ValidateServerOptions().Validate(Options.DefaultName, options);

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validate(new ServerOptions()).Succeeded);
    }

    [Fact]
    public void Empty_url_list_fails()
    {
        var result = Validate(new ServerOptions { Urls = [] });

        Assert.True(result.Failed);
        Assert.Contains("Talky:Server:Urls", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_http_fails_unless_explicitly_allowed()
    {
        var options = new ServerOptions { Urls = ["http://+:8080"] };

        var result = Validate(options);
        Assert.True(result.Failed);
        Assert.Contains("AllowInsecureHttp", result.FailureMessage, StringComparison.Ordinal);

        options.AllowInsecureHttp = true;
        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void Url_without_a_scheme_fails()
    {
        Assert.True(Validate(new ServerOptions { Urls = ["localhost:8443"] }).Failed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Drain_seconds_out_of_range_fails(int drainSeconds)
    {
        var result = Validate(new ServerOptions { DrainSeconds = drainSeconds });

        Assert.True(result.Failed);
        Assert.Contains("DrainSeconds", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_proxy_that_is_not_an_ip_fails()
    {
        var result = Validate(new ServerOptions { KnownProxies = ["lb.corp.local"] });

        Assert.True(result.Failed);
        Assert.Contains("KnownProxies", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_url_must_be_absolute()
    {
        Assert.True(Validate(new ServerOptions { PublicUrl = "/talky" }).Failed);
        Assert.True(Validate(new ServerOptions { PublicUrl = "https://talky.corp.local" }).Succeeded);
    }

    [Fact]
    public void Store_certificate_without_thumbprint_or_subject_fails()
    {
        var result = Validate(new ServerOptions
        {
            Certificate = new CertificateOptions { Source = CertificateSource.Store },
        });

        Assert.True(result.Failed);
        Assert.Contains("Thumbprint nor Subject", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_certificate_with_a_non_hexadecimal_thumbprint_fails()
    {
        var result = Validate(new ServerOptions
        {
            Certificate = new CertificateOptions
            {
                Source = CertificateSource.Store,
                Thumbprint = "not-a-thumbprint",
            },
        });

        Assert.True(result.Failed);
        Assert.Contains("Thumbprint", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_certificate_accepts_a_thumbprint_copied_from_the_windows_ui()
    {
        var result = Validate(new ServerOptions
        {
            Certificate = new CertificateOptions
            {
                Source = CertificateSource.Store,
                Thumbprint = "a1 b2 c3 d4 e5 f6 07 18 29 3a 4b 5c 6d 7e 8f 90 12 34 56 78",
            },
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Unknown_store_location_fails()
    {
        var result = Validate(new ServerOptions
        {
            Certificate = new CertificateOptions
            {
                Source = CertificateSource.Store,
                Subject = "talky.corp.local",
                StoreLocation = "Machine",
            },
        });

        Assert.True(result.Failed);
        Assert.Contains("StoreLocation", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Pfx_certificate_without_a_path_fails()
    {
        var result = Validate(new ServerOptions
        {
            Certificate = new CertificateOptions { Source = CertificateSource.Pfx },
        });

        Assert.True(result.Failed);
        Assert.Contains("PfxPath", result.FailureMessage, StringComparison.Ordinal);
    }
}

public sealed class LoggingOptionsValidationTests
{
    private static ValidateOptionsResult Validate(LoggingOptions options) =>
        new ValidateLoggingOptions().Validate(Options.DefaultName, options);

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(Validate(new LoggingOptions()).Succeeded);
    }

    [Fact]
    public void Unknown_minimum_level_fails_with_the_valid_values()
    {
        var result = Validate(new LoggingOptions { MinimumLevel = "Chatty" });

        Assert.True(result.Failed);
        Assert.Contains("MinimumLevel", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Information", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Minimum_level_is_case_insensitive()
    {
        Assert.True(Validate(new LoggingOptions { MinimumLevel = "warning" }).Succeeded);
    }

    [Fact]
    public void Empty_directory_fails()
    {
        Assert.True(Validate(new LoggingOptions { Directory = "  " }).Failed);
    }

    [Theory]
    [InlineData("logs", false)]
    [InlineData(@"%TALKY_TEST_UNDEFINED_VARIABLE%\logs", false)]
    [InlineData(@"C:\Talky\logs", true)]
    public void Directory_must_be_fully_qualified_after_expansion(string directory, bool valid)
    {
        var result = Validate(new LoggingOptions { Directory = directory });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("Talky:Logging:Directory", result.FailureMessage, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void Retained_file_count_out_of_range_fails(int retained)
    {
        var result = Validate(new LoggingOptions { RetainedFileCount = retained });

        Assert.True(result.Failed);
        Assert.Contains("RetainedFileCount", result.FailureMessage, StringComparison.Ordinal);
    }
}

using Yanki.Core.Configuration;

namespace Yanki.UnitTests.Hosting;

public sealed class YankiConfigurationTests
{
    [Theory]
    [InlineData("YANKI_Server__DrainSeconds", "Yanki:Server:DrainSeconds")]
    [InlineData("YANKI_Node__Id", "Yanki:Node:Id")]
    [InlineData("YANKI_Server__Urls__0", "Yanki:Server:Urls:0")]
    [InlineData("YANKI_Server__Certificate__Thumbprint", "Yanki:Server:Certificate:Thumbprint")]
    [InlineData("YANKI_Logging__MinimumLevel", "Yanki:Logging:MinimumLevel")]
    [InlineData("yanki_Logging__Console", "Yanki:Logging:Console")]
    [InlineData("YANKI_Node", "Yanki:Node")]
    public void Yanki_prefixed_variables_map_onto_the_root_section(string variable, string expected)
    {
        Assert.Equal(expected, YankiConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PATH")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("YANKI")]
    [InlineData("YANKI_")]
    public void Other_variables_are_ignored(string? variable)
    {
        Assert.Null(YankiConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData("YANKI_Server____Urls")]
    [InlineData("YANKI_Server__")]
    [InlineData("YANKI___Node")]
    public void Malformed_names_are_ignored(string variable)
    {
        Assert.Null(YankiConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Fact]
    public void Section_names_stay_in_sync_with_the_root()
    {
        Assert.Equal("Yanki", YankiConfiguration.RootSection);
        Assert.StartsWith(YankiConfiguration.RootSection + ":", YankiConfiguration.NodeSection, StringComparison.Ordinal);
        Assert.StartsWith(YankiConfiguration.RootSection + ":", YankiConfiguration.ServerSection, StringComparison.Ordinal);
        Assert.StartsWith(YankiConfiguration.RootSection + ":", YankiConfiguration.LoggingSection, StringComparison.Ordinal);
    }
}

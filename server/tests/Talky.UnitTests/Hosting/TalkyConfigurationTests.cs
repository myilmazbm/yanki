using Talky.Core.Configuration;

namespace Talky.UnitTests.Hosting;

public sealed class TalkyConfigurationTests
{
    [Theory]
    [InlineData("TALKY_Server__DrainSeconds", "Talky:Server:DrainSeconds")]
    [InlineData("TALKY_Node__Id", "Talky:Node:Id")]
    [InlineData("TALKY_Server__Urls__0", "Talky:Server:Urls:0")]
    [InlineData("TALKY_Server__Certificate__Thumbprint", "Talky:Server:Certificate:Thumbprint")]
    [InlineData("TALKY_Logging__MinimumLevel", "Talky:Logging:MinimumLevel")]
    [InlineData("talky_Logging__Console", "Talky:Logging:Console")]
    [InlineData("TALKY_Node", "Talky:Node")]
    public void Talky_prefixed_variables_map_onto_the_root_section(string variable, string expected)
    {
        Assert.Equal(expected, TalkyConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PATH")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("TALKY")]
    [InlineData("TALKY_")]
    public void Other_variables_are_ignored(string? variable)
    {
        Assert.Null(TalkyConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Theory]
    [InlineData("TALKY_Server____Urls")]
    [InlineData("TALKY_Server__")]
    [InlineData("TALKY___Node")]
    public void Malformed_names_are_ignored(string variable)
    {
        Assert.Null(TalkyConfiguration.TryMapEnvironmentVariable(variable));
    }

    [Fact]
    public void Section_names_stay_in_sync_with_the_root()
    {
        Assert.Equal("Talky", TalkyConfiguration.RootSection);
        Assert.StartsWith(TalkyConfiguration.RootSection + ":", TalkyConfiguration.NodeSection, StringComparison.Ordinal);
        Assert.StartsWith(TalkyConfiguration.RootSection + ":", TalkyConfiguration.ServerSection, StringComparison.Ordinal);
        Assert.StartsWith(TalkyConfiguration.RootSection + ":", TalkyConfiguration.LoggingSection, StringComparison.Ordinal);
    }
}

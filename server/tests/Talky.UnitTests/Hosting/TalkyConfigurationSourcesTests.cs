using Microsoft.Extensions.Configuration;
using Talky.Server.Hosting;

namespace Talky.UnitTests.Hosting;

/// <summary>
/// <c>--data-dir</c> is the deployment contract the MSI and Install-Talky.ps1 rely on: the
/// Windows service is started as <c>talky.exe --role … --data-dir "…"</c>.
/// </summary>
public sealed class TalkyConfigurationSourcesTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "talky-tests", Guid.NewGuid().ToString("N"));

    public TalkyConfigurationSourcesTests()
    {
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public void Data_directory_defaults_to_program_data()
    {
        var configuration = Build(HostStartupArguments.Empty);

        Assert.Equal(TalkyConfigurationSources.ProgramDataDirectory, configuration["Talky:DataDirectory"]);
    }

    [Fact]
    public void Data_dir_loads_its_appsettings_and_defaults_the_log_directory()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Talky": { "Node": { "Id": "from-data-dir" }, "Server": { "DrainSeconds": 7 } } }""");
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.Local.json"),
            """{ "Talky": { "Server": { "DrainSeconds": 9 } } }""");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = _dataDir });

        Assert.Equal(_dataDir, configuration["Talky:DataDirectory"]);
        Assert.Equal("from-data-dir", configuration["Talky:Node:Id"]);
        Assert.Equal("9", configuration["Talky:Server:DrainSeconds"]);
        Assert.Equal(Path.Combine(_dataDir, "logs"), configuration["Talky:Logging:Directory"]);

        // A quoted service argument ending in a backslash arrives with a trailing quote.
        Assert.Equal(
            "from-data-dir",
            Build(HostStartupArguments.Empty with { DataDirectory = _dataDir + "\"" })["Talky:Node:Id"]);
    }

    [Fact]
    public void Invalid_json_in_the_data_dir_appsettings_is_a_startup_error()
    {
        var path = Path.Combine(_dataDir, "appsettings.json");
        File.WriteAllText(path, """{ "Talky": { "Server": { "Urls": [__URLS__] } } }""");

        var error = Assert.Throws<TalkyStartupException>(
            () => Build(HostStartupArguments.Empty with { DataDirectory = _dataDir }));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_log_directory_in_the_data_dir_appsettings_wins_over_the_default()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Talky": { "Logging": { "Directory": "D:\\logs\\talky" } } }""");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = _dataDir });

        Assert.Equal(@"D:\logs\talky", configuration["Talky:Logging:Directory"]);
    }

    [Fact]
    public void Set_overrides_beat_the_data_dir_appsettings()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Talky": { "Node": { "Id": "from-data-dir" } } }""");

        var configuration = Build(
            HostStartupArguments.Empty with { DataDirectory = _dataDir, Settings = ["Talky:Node:Id=from-set"] });

        Assert.Equal("from-set", configuration["Talky:Node:Id"]);
    }

    [Fact]
    public void Missing_data_dir_is_tolerated()
    {
        var missing = Path.Combine(_dataDir, "does-not-exist");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = missing });

        Assert.Equal(missing, configuration["Talky:DataDirectory"]);
        Assert.Equal(Path.Combine(missing, "logs"), configuration["Talky:Logging:Directory"]);
    }

    private static ConfigurationManager Build(HostStartupArguments arguments)
    {
        var manager = new ConfigurationManager();
        TalkyConfigurationSources.Apply(manager, "Production", AppContext.BaseDirectory, "talky", arguments);
        return manager;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp directory is per test instance.
        }
    }
}

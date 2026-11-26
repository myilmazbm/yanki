using Microsoft.Extensions.Configuration;
using Yanki.Server.Hosting;

namespace Yanki.UnitTests.Hosting;

/// <summary>
/// <c>--data-dir</c> is the deployment contract the MSI and Install-Yanki.ps1 rely on: the
/// Windows service is started as <c>yanki.exe --role … --data-dir "…"</c>.
/// </summary>
public sealed class YankiConfigurationSourcesTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "yanki-tests", Guid.NewGuid().ToString("N"));

    public YankiConfigurationSourcesTests()
    {
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public void Data_directory_defaults_to_program_data()
    {
        var configuration = Build(HostStartupArguments.Empty);

        Assert.Equal(YankiConfigurationSources.ProgramDataDirectory, configuration["Yanki:DataDirectory"]);
    }

    [Fact]
    public void Data_dir_loads_its_appsettings_and_defaults_the_log_directory()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Yanki": { "Node": { "Id": "from-data-dir" }, "Server": { "DrainSeconds": 7 } } }""");
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.Local.json"),
            """{ "Yanki": { "Server": { "DrainSeconds": 9 } } }""");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = _dataDir });

        Assert.Equal(_dataDir, configuration["Yanki:DataDirectory"]);
        Assert.Equal("from-data-dir", configuration["Yanki:Node:Id"]);
        Assert.Equal("9", configuration["Yanki:Server:DrainSeconds"]);
        Assert.Equal(Path.Combine(_dataDir, "logs"), configuration["Yanki:Logging:Directory"]);

        // A quoted service argument ending in a backslash arrives with a trailing quote.
        Assert.Equal(
            "from-data-dir",
            Build(HostStartupArguments.Empty with { DataDirectory = _dataDir + "\"" })["Yanki:Node:Id"]);
    }

    [Fact]
    public void Invalid_json_in_the_data_dir_appsettings_is_a_startup_error()
    {
        var path = Path.Combine(_dataDir, "appsettings.json");
        File.WriteAllText(path, """{ "Yanki": { "Server": { "Urls": [__URLS__] } } }""");

        var error = Assert.Throws<YankiStartupException>(
            () => Build(HostStartupArguments.Empty with { DataDirectory = _dataDir }));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_log_directory_in_the_data_dir_appsettings_wins_over_the_default()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Yanki": { "Logging": { "Directory": "D:\\logs\\yanki" } } }""");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = _dataDir });

        Assert.Equal(@"D:\logs\yanki", configuration["Yanki:Logging:Directory"]);
    }

    [Fact]
    public void Set_overrides_beat_the_data_dir_appsettings()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "appsettings.json"),
            """{ "Yanki": { "Node": { "Id": "from-data-dir" } } }""");

        var configuration = Build(
            HostStartupArguments.Empty with { DataDirectory = _dataDir, Settings = ["Yanki:Node:Id=from-set"] });

        Assert.Equal("from-set", configuration["Yanki:Node:Id"]);
    }

    [Fact]
    public void Missing_data_dir_is_tolerated()
    {
        var missing = Path.Combine(_dataDir, "does-not-exist");

        var configuration = Build(HostStartupArguments.Empty with { DataDirectory = missing });

        Assert.Equal(missing, configuration["Yanki:DataDirectory"]);
        Assert.Equal(Path.Combine(missing, "logs"), configuration["Yanki:Logging:Directory"]);
    }

    private static ConfigurationManager Build(HostStartupArguments arguments)
    {
        var manager = new ConfigurationManager();
        YankiConfigurationSources.Apply(manager, "Production", AppContext.BaseDirectory, "yanki", arguments);
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

using Talky.Server.Cli;
using Talky.Server.Hosting;

namespace Talky.IntegrationTests.Host;

/// <summary>
/// Covers the parts of the command line that the Windows service, the deployment scripts and the
/// ASP.NET Core test host depend on. These run the real entry point, so every case must terminate.
/// </summary>
public sealed class CommandLineTests
{
    static CommandLineTests() => TalkyTestEnvironment.Ensure();

    [Fact]
    public void Host_settings_are_extracted_from_the_raw_command_line()
    {
        string[] args =
        [
            "serve",
            "--ENVIRONMENT=Development",
            "--role", "api",
            "--contentRoot", @"C:\talky",
            "--applicationName=talky",
            "--set", "Talky:Server:DrainSeconds=60",
        ];

        Assert.Equal(
            ["--ENVIRONMENT=Development", "--contentRoot", @"C:\talky", "--applicationName=talky"],
            HostStartupArguments.ExtractHostSettings(args));
    }

    [Fact]
    public void Talky_options_are_not_mistaken_for_host_settings()
    {
        Assert.Empty(HostStartupArguments.ExtractHostSettings(["doctor", "--role", "api,worker", "--urls", "https://+:8443"]));
    }

    [Fact]
    public void Content_root_from_the_command_line_is_detected()
    {
        var withRoot = HostStartupArguments.Empty with
        {
            HostSettings = HostStartupArguments.ExtractHostSettings(["--contentRoot=C:\\talky"]),
        };
        Assert.True(withRoot.HasContentRoot);
        Assert.False(HostStartupArguments.Empty.HasContentRoot);
    }

    [Fact]
    public async Task Version_command_succeeds()
    {
        Assert.Equal(ExitCode.Success, await TalkyCommandLine.RunAsync(["version"], CancellationToken.None));
    }

    [Fact]
    public async Task Mistyped_command_fails_without_starting_a_host()
    {
        Assert.Equal(ExitCode.Failure, await TalkyCommandLine.RunAsync(["doctorr"], CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_option_fails_without_starting_a_host()
    {
        Assert.Equal(ExitCode.Failure, await TalkyCommandLine.RunAsync(["version", "--nope"], CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_role_is_reported_as_a_configuration_error()
    {
        Assert.Equal(
            ExitCode.InvalidConfiguration,
            await TalkyCommandLine.RunAsync(["config", "validate", "--role", "bogus"], CancellationToken.None));
    }

    [Fact]
    public async Task Missing_config_file_is_reported_as_a_configuration_error()
    {
        Assert.Equal(
            ExitCode.InvalidConfiguration,
            await TalkyCommandLine.RunAsync(["doctor", "--config", "no-such-file.json"], CancellationToken.None));
    }

    [Fact]
    public async Task Migrate_reports_a_missing_migration_runner()
    {
        // Talky.Data is not wired into Modules.cs yet, so this is the documented exit code the
        // deployment script keys off.
        Assert.Equal(
            ExitCode.MissingDependency,
            await TalkyCommandLine.RunAsync(["migrate"], CancellationToken.None));
    }

    [Fact]
    public async Task Config_validate_accepts_the_shipped_defaults()
    {
        Assert.Equal(
            ExitCode.Success,
            await TalkyCommandLine.RunAsync(["config", "validate"], CancellationToken.None));
    }
}

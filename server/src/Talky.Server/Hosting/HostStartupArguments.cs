namespace Talky.Server.Hosting;

/// <summary>
/// The subset of the command line that influences configuration. Extracted before the host is
/// built, because the configuration sources themselves depend on it.
/// </summary>
/// <param name="ConfigFile">Value of <c>--config</c>: an extra JSON file layered on top of the defaults.</param>
/// <param name="Roles">Value of <c>--role</c>, overriding <c>Talky:Node:Roles</c>.</param>
/// <param name="Urls">Value of <c>--urls</c>, overriding <c>Talky:Server:Urls</c>.</param>
/// <param name="Settings">Values of <c>--set key=value</c>, overriding any single configuration key.</param>
internal sealed record HostStartupArguments(
    string? ConfigFile,
    string? Roles,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> Settings)
{
    /// <summary>Arguments as passed by the Windows service, which runs the bare executable.</summary>
    public static HostStartupArguments Empty { get; } = new(null, null, [], []);
}

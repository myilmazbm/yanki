using Talky.Server.Cli;

namespace Talky.Server;

/// <summary>
/// Entry point of talky.exe. Everything the process can do is a command in
/// <see cref="TalkyCommandLine"/>; running it without a command (as the Windows service does,
/// with options only) is the same as running <c>talky serve</c>.
/// </summary>
public sealed partial class Program
{
    private Program()
    {
    }

    /// <summary>Process entry point.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <returns>Process exit code, see <see cref="ExitCode"/>.</returns>
    public static Task<int> Main(string[] args) => TalkyCommandLine.RunAsync(args, CancellationToken.None);
}

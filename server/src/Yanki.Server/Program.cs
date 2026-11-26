using Yanki.Server.Cli;

namespace Yanki.Server;

/// <summary>
/// Entry point of yanki.exe. Everything the process can do is a command in
/// <see cref="YankiCommandLine"/>; running it without a command (as the Windows service does,
/// with options only) is the same as running <c>yanki serve</c>.
/// </summary>
public sealed partial class Program
{
    private Program()
    {
    }

    /// <summary>Process entry point.</summary>
    /// <param name="args">Raw command line arguments.</param>
    /// <returns>Process exit code, see <see cref="ExitCode"/>.</returns>
    public static Task<int> Main(string[] args) => YankiCommandLine.RunAsync(args, CancellationToken.None);
}

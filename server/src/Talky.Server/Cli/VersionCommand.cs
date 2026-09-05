using System.Runtime.InteropServices;
using Talky.Core.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// <c>talky version</c> — one screen of facts that belong in every support ticket.
/// </summary>
internal static class VersionCommand
{
    /// <summary>Prints version and runtime information.</summary>
    /// <returns>Always <see cref="ExitCode.Success"/>.</returns>
    public static int Run()
    {
        Console.WriteLine($"talky {NodeInfo.ResolveVersion()}");
        Console.WriteLine($"  runtime   {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  platform  {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"  os        {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  location  {AppContext.BaseDirectory}");
        return ExitCode.Success;
    }
}

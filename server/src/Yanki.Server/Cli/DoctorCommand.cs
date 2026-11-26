using Yanki.Core.Diagnostics;
using Yanki.Server.Diagnostics;
using Yanki.Server.Hosting;

namespace Yanki.Server.Cli;

/// <summary>
/// <c>yanki doctor</c> — runs every registered <see cref="IDoctorCheck"/> in parallel and prints a
/// table. Meant to be the first thing an operator runs on a new node.
/// </summary>
internal static class DoctorCommand
{
    /// <summary>Runs the checks and prints the report.</summary>
    /// <param name="arguments">Parsed command line values.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns><see cref="ExitCode.Failure"/> when any check reported an error.</returns>
    public static async Task<int> RunAsync(HostStartupArguments arguments, CancellationToken cancellationToken)
    {
        var builder = YankiHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);

        await using var app = builder.Build();

        var outcomes = await DoctorRunner.RunAsync(
            app.Services.GetServices<IDoctorCheck>(),
            app.Services.GetRequiredService<TimeProvider>(),
            cancellationToken);

        Console.Write(DoctorRunner.Render(outcomes));

        var errors = outcomes.Count(outcome => outcome.Result.Severity == DoctorSeverity.Error);
        var warnings = outcomes.Count(outcome => outcome.Result.Severity == DoctorSeverity.Warning);
        Console.WriteLine();
        Console.WriteLine($"{outcomes.Count} check(s): {errors} error(s), {warnings} warning(s).");

        return errors > 0 ? ExitCode.Failure : ExitCode.Success;
    }
}

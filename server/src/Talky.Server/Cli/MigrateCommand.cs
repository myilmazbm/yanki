using Talky.Core.Data;
using Talky.Server.Hosting;

namespace Talky.Server.Cli;

/// <summary>
/// <c>talky migrate</c> — prints the migration status and then applies whatever is pending.
/// Run once per upgrade, before the nodes are restarted.
/// </summary>
internal static class MigrateCommand
{
    /// <summary>Resolves <see cref="IMigrationRunner"/> from the host and applies pending migrations.</summary>
    /// <param name="arguments">Parsed command line values.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns><see cref="ExitCode.Success"/>, or <see cref="ExitCode.Failure"/> when the migration failed.</returns>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failing migration must print one readable line and exit 1, not a stack trace.")]
    public static async Task<int> RunAsync(HostStartupArguments arguments, CancellationToken cancellationToken)
    {
        var builder = TalkyHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);

        await using var app = builder.Build();

        // The runner is registered scoped (it owns a DbContext), so it must come from a scope,
        // not from the root provider.
        await using var scope = app.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        try
        {
            var status = await runner.GetStatusAsync(cancellationToken);
            WriteStatus(status);

            if (status.Pending.Count == 0)
            {
                Console.WriteLine("Nothing to do; the database is up to date.");
                return ExitCode.Success;
            }

            Console.WriteLine($"Applying {status.Pending.Count} migration(s)...");
            await runner.MigrateAsync(cancellationToken);

            var applied = await runner.GetStatusAsync(cancellationToken);
            Console.WriteLine($"Done. {applied.Applied.Count} migration(s) applied, {applied.Pending.Count} pending.");
            return applied.Pending.Count == 0 ? ExitCode.Success : ExitCode.Failure;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Migration cancelled.");
            return ExitCode.Failure;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Migration failed: {ex.Message}");
            return ExitCode.Failure;
        }
    }

    private static void WriteStatus(MigrationStatus status)
    {
        Console.WriteLine(status.DatabaseExists
            ? $"Database exists. Applied: {status.Applied.Count}, pending: {status.Pending.Count}."
            : "Database does not exist yet; it will be created.");

        foreach (var migration in status.Pending)
        {
            Console.WriteLine($"  pending  {migration}");
        }
    }
}

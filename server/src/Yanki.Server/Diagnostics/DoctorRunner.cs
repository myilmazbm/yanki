using System.Globalization;
using System.Text;
using Yanki.Core.Diagnostics;

namespace Yanki.Server.Diagnostics;

/// <summary>Outcome of one check, including the time it took.</summary>
/// <param name="Name">Check name.</param>
/// <param name="Result">What the check reported.</param>
/// <param name="Elapsed">Wall clock duration.</param>
internal sealed record DoctorCheckOutcome(string Name, DoctorResult Result, TimeSpan Elapsed);

/// <summary>
/// Runs every registered <see cref="IDoctorCheck"/> in parallel with a per-check timeout and
/// renders the result as a fixed-width table.
/// </summary>
internal static class DoctorRunner
{
    /// <summary>How long a single check may take before it is reported as an error.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs all checks concurrently.</summary>
    /// <param name="checks">Checks resolved from DI.</param>
    /// <param name="timeProvider">Clock used for the timeout and the elapsed column.</param>
    /// <param name="cancellationToken">Cancels the whole run.</param>
    /// <returns>One outcome per check, ordered by name.</returns>
    public static async Task<IReadOnlyList<DoctorCheckOutcome>> RunAsync(
        IEnumerable<IDoctorCheck> checks,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var outcomes = await Task.WhenAll(checks.Select(check => RunOneAsync(check, timeProvider, cancellationToken)));
        return [.. outcomes.OrderBy(outcome => outcome.Name, StringComparer.Ordinal)];
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A misbehaving check must be reported as a failed row, never crash 'yanki doctor'.")]
    private static async Task<DoctorCheckOutcome> RunOneAsync(
        IDoctorCheck check,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();

        using var timeout = new CancellationTokenSource(CheckTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        DoctorResult result;
        try
        {
            result = await check.RunAsync(linked.Token) ?? DoctorResult.Error("The check returned no result.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            result = DoctorResult.Error($"The check did not finish within {CheckTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            result = DoctorResult.Error($"The check threw {ex.GetType().Name}: {ex.Message}");
        }

        return new DoctorCheckOutcome(check.Name, result, timeProvider.GetElapsedTime(started));
    }

    /// <summary>Renders the outcomes as a table: name, severity, message, details.</summary>
    /// <param name="outcomes">Outcomes from <see cref="RunAsync"/>.</param>
    /// <returns>The text to print, ending with a newline.</returns>
    public static string Render(IReadOnlyList<DoctorCheckOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var rows = outcomes
            .Select(outcome => new[]
            {
                outcome.Name,
                outcome.Result.Severity.ToString().ToUpperInvariant(),
                string.Create(CultureInfo.InvariantCulture, $"{outcome.Elapsed.TotalMilliseconds:0} ms"),
                outcome.Result.Message,
            })
            .ToList();

        string[] headers = ["CHECK", "SEVERITY", "TIME", "MESSAGE"];
        var widths = new int[headers.Length];
        for (var column = 0; column < headers.Length; column++)
        {
            widths[column] = rows.Aggregate(headers[column].Length, (width, row) => Math.Max(width, row[column].Length));
        }

        var builder = new StringBuilder();
        AppendRow(builder, headers, widths);
        AppendRow(builder, [.. widths.Select(width => new string('-', width))], widths);

        for (var i = 0; i < rows.Count; i++)
        {
            AppendRow(builder, rows[i], widths);

            var details = outcomes[i].Result.Details;
            if (details is null)
            {
                continue;
            }

            foreach (var detail in details.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append("    ").Append(detail.Key).Append(": ").AppendLine(detail.Value);
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, string[] cells, int[] widths)
    {
        for (var column = 0; column < cells.Length; column++)
        {
            builder.Append(cells[column]);
            if (column == cells.Length - 1)
            {
                continue;
            }

            builder.Append(' ', widths[column] - cells[column].Length).Append("  ");
        }

        builder.AppendLine();
    }
}

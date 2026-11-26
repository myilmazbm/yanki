namespace Yanki.Core.Diagnostics;

/// <summary>
/// One check executed by <c>yanki doctor</c> (and shown in the admin panel's environment page).
/// Modules register their checks in DI (<c>services.AddSingleton&lt;IDoctorCheck, SqlDoctorCheck&gt;()</c>).
/// Checks must never throw; they return <see cref="DoctorResult"/> with a human-readable, English message.
/// </summary>
public interface IDoctorCheck
{
    /// <summary>Short stable id, e.g. "sql", "share", "livekit", "ad:corp.local".</summary>
    string Name { get; }

    Task<DoctorResult> RunAsync(CancellationToken cancellationToken);
}

public enum DoctorSeverity
{
    Ok,

    /// <summary>Works, but something should be fixed before production (e.g. dev certificate).</summary>
    Warning,

    /// <summary>The node cannot serve traffic correctly.</summary>
    Error,
}

/// <param name="Severity">Outcome.</param>
/// <param name="Message">One line, English, safe to log.</param>
/// <param name="Details">Optional key/value details (versions, endpoints, latencies).</param>
public sealed record DoctorResult(
    DoctorSeverity Severity,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public static DoctorResult Ok(string message, IReadOnlyDictionary<string, string>? details = null) =>
        new(DoctorSeverity.Ok, message, details);

    public static DoctorResult Warning(string message, IReadOnlyDictionary<string, string>? details = null) =>
        new(DoctorSeverity.Warning, message, details);

    public static DoctorResult Error(string message, IReadOnlyDictionary<string, string>? details = null) =>
        new(DoctorSeverity.Error, message, details);
}

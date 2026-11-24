using System.Data;
using Microsoft.Data.SqlClient;

namespace Talky.Data;

/// <summary>
/// Clock helpers for the SQL layer. Nothing in Talky.Data ever calls <c>DateTime.UtcNow</c>: every
/// timestamp comes from an injected <see cref="TimeProvider"/> so tests can control time.
/// </summary>
internal static class SqlTime
{
    /// <summary>
    /// Current UTC time truncated to milliseconds, so what we send is exactly what
    /// <c>datetime2(3)</c> stores and reads back (no rounding surprises in assertions).
    /// </summary>
    public static DateTime UtcNow(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    /// <summary>Adds a <c>datetime2(3)</c> parameter, matching the column type of every timestamp.</summary>
    public static void AddTimestamp(SqlCommand command, string name, DateTime value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = 3;
        parameter.Value = value;
    }
}

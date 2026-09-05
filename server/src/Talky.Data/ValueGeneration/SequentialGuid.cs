using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Talky.Data.ValueGeneration;

/// <summary>
/// Produces GUIDs that are ascending in SQL Server's <c>uniqueidentifier</c> collation.
/// <para>
/// SQL Server compares <c>uniqueidentifier</c> starting from the last byte group, so
/// <see cref="Guid.NewGuid"/> (random) and <see cref="Guid.CreateVersion7()"/> (time ordered in the
/// <em>first</em> bytes) both land randomly inside a clustered index and cause page splits and
/// fragmentation on every insert. EF's <see cref="SequentialGuidValueGenerator"/> puts an increasing
/// counter into exactly the bytes SQL Server sorts by, so clustered GUID keys append at the end of
/// the index like an identity column would.
/// </para>
/// <para>
/// EF applies the generator itself for tracked inserts; this wrapper is for the raw-SQL write paths
/// (job queue, message appender) which never build a <c>DbContext</c> change tracker entry.
/// <c>messages.id</c> is the deliberate exception: it is a UUIDv7 because it is a nonclustered,
/// externally visible id and its time ordering is useful to clients and to federation.
/// </para>
/// </summary>
public static class SequentialGuid
{
    private static readonly SequentialGuidValueGenerator Generator = new();

    /// <summary>Next GUID in SQL Server sort order. Thread-safe.</summary>
    public static Guid Next() => Generator.Next(null!);
}

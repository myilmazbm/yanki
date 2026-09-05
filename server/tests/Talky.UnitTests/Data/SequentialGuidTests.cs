using System.Data.SqlTypes;
using Talky.Core.Domain;
using Talky.Data.ValueGeneration;

namespace Talky.UnitTests.Data;

/// <summary>
/// Pins the GUID strategy the schema depends on: clustered keys must be generated in SQL Server's
/// <c>uniqueidentifier</c> sort order, and <c>messages.id</c> must not be.
/// </summary>
public sealed class SequentialGuidTests
{
    private const int Samples = 2_000;

    [Fact]
    public void SequentialGuid_is_ascending_in_sql_server_order()
    {
        var values = new Guid[Samples];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = SequentialGuid.Next();
        }

        for (var i = 1; i < values.Length; i++)
        {
            Assert.True(
                new SqlGuid(values[i - 1]).CompareTo(new SqlGuid(values[i])) < 0,
                $"Value {i} ({values[i]}) did not sort after {values[i - 1]} in SQL Server order.");
        }
    }

    [Fact]
    public async Task SequentialGuid_is_thread_safe_and_collision_free()
    {
        const int Workers = 16;
        const int PerWorker = 500;

        var results = await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(() =>
        {
            var local = new Guid[PerWorker];
            for (var i = 0; i < local.Length; i++)
            {
                local[i] = SequentialGuid.Next();
            }

            return local;
        })));

        var all = results.SelectMany(x => x).ToArray();
        Assert.Equal(Workers * PerWorker, all.Distinct().Count());
    }

    [Fact]
    public void Guid_NewGuid_would_not_be_ordered_which_is_why_it_is_not_used_for_clustered_keys()
    {
        var values = Enumerable.Range(0, Samples).Select(_ => Guid.NewGuid()).ToArray();
        var ascending = CountAscending(values);

        // Random GUIDs land uniformly, so roughly half the steps go backwards; the point is only
        // that they are nowhere near monotonic, which is what fragments a clustered index.
        Assert.InRange(ascending, Samples / 4, Samples * 3 / 4);
    }

    [Fact]
    public void CreateVersion7_is_ordered_for_clients_but_not_for_sql_server()
    {
        var values = new Guid[Samples];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Guid.CreateVersion7();
        }

        // The leading 48 bits are a millisecond timestamp, so the values never go backwards in time
        // for a client that sorts them as bytes (which is what makes UUIDv7 a good external id).
        for (var i = 1; i < values.Length; i++)
        {
            var previous = values[i - 1].ToByteArray(bigEndian: true).AsSpan(0, 6);
            var current = values[i].ToByteArray(bigEndian: true).AsSpan(0, 6);
            Assert.True(previous.SequenceCompareTo(current) <= 0);
        }

        // ...but not by SQL Server's, which starts at the last byte group. That is exactly why
        // messages.id is a UUIDv7 behind a NONCLUSTERED index and never a clustered key.
        Assert.InRange(CountAscending(values), Samples / 4, Samples * 3 / 4);
    }

    [Fact]
    public void DmPair_Order_uses_sql_server_ordering_so_the_check_constraint_holds()
    {
        for (var i = 0; i < 500; i++)
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            var (low, high) = DmPair.Order(first, second);

            Assert.True(new SqlGuid(low).CompareTo(new SqlGuid(high)) < 0);
            Assert.Equal((low, high), DmPair.Order(second, first));
        }
    }

    private static int CountAscending(Guid[] values)
    {
        var ascending = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (new SqlGuid(values[i - 1]).CompareTo(new SqlGuid(values[i])) < 0)
            {
                ascending++;
            }
        }

        return ascending;
    }
}

using System.Data.SqlTypes;
using Talky.Core.Domain;
using Talky.Data.ValueGeneration;

namespace Talky.UnitTests.Data;

/// <summary>
/// Pins the GUID strategy the schema depends on: clustered keys must be generated in SQL Server's
/// <c>uniqueidentifier</c> sort order.
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
}

using Microsoft.EntityFrameworkCore;

namespace Yanki.Data.Modeling;

/// <summary>
/// Stores every enum property as <c>tinyint</c> instead of EF's default <c>int</c>.
/// </summary>
/// <remarks>
/// All Yanki enums are small, closed sets with explicit numeric values (see
/// <c>Yanki.Core.Domain</c>). One byte instead of four keeps the narrow, very hot rows
/// (<c>messages</c>, <c>conversation_members</c>, <c>jobs</c>) smaller, which means more rows per
/// 8 KB page and fewer logical reads. Flag enums are stored as their own integer column instead
/// (see <c>cluster_nodes.roles</c>), so nothing here has to widen.
/// </remarks>
public static class TinyIntEnums
{
    /// <summary>
    /// Maps every enum-typed property in the model to <c>tinyint</c>, unless a configuration already
    /// set an explicit provider type or value converter for it.
    /// </summary>
    public static ModelBuilder UseTinyIntEnums(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType.IsEnum
                    && property.GetProviderClrType() is null
                    && property.GetValueConverter() is null)
                {
                    property.SetProviderClrType(typeof(byte));
                }
            }
        }

        return modelBuilder;
    }
}

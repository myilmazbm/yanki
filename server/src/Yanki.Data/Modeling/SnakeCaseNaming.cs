using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Yanki.Data.Modeling;

/// <summary>
/// Turns the CLR names EF derives by convention into <c>snake_case</c> database names.
/// <para>
/// This is a small <see cref="ModelBuilder"/> pass instead of a third-party naming package: the whole
/// behaviour we need is "lower-case with underscores, unless a configuration said otherwise", and
/// owning it keeps the dependency surface of yanki.exe smaller and the rule auditable.
/// </para>
/// <para>
/// It only renames tables and columns. Key, index, foreign key and check constraint names are left to
/// EF, whose defaults are derived from the (already renamed) table and column names, so they come out
/// as <c>PK_messages</c>, <c>IX_messages_sender_id_client_msg_id</c> and so on.
/// </para>
/// </summary>
public static class SnakeCaseNaming
{
    /// <summary>
    /// Applies snake_case table and column names to every entity in the model. Must run at the end of
    /// <see cref="DbContext.OnModelCreating"/>, after all entity configurations.
    /// <para>
    /// <see cref="ToSnakeCase"/> is idempotent for names that are already snake_case, so a name a
    /// configuration set explicitly (<c>metrics_1m</c>, <c>type</c>, <c>message</c>) survives the pass
    /// unchanged. The rule for configurations is therefore: if you name a table or column by hand,
    /// name it in snake_case.
    /// </para>
    /// </summary>
    public static ModelBuilder UseSnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is { } tableName)
            {
                entityType.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName() is { } columnName)
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }
        }

        return modelBuilder;
    }

    /// <summary>
    /// <c>ConversationId</c> becomes <c>conversation_id</c>, <c>ClientMsgId</c> becomes
    /// <c>client_msg_id</c>, <c>Sha256</c> becomes <c>sha256</c>, <c>UserA</c> becomes <c>user_a</c>,
    /// <c>P95</c> becomes <c>p95</c>. A boundary is inserted before an upper-case letter that follows a
    /// lower-case letter or a digit, and before the last upper-case letter of a run that is followed by
    /// a lower-case letter (so <c>IPAddress</c> would become <c>ip_address</c>).
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (!char.IsUpper(current))
            {
                builder.Append(current);
                continue;
            }

            var previous = i > 0 ? name[i - 1] : '\0';
            var next = i + 1 < name.Length ? name[i + 1] : '\0';
            var startsWord = i > 0
                && previous != '_'
                && (!char.IsUpper(previous) || char.IsLower(next));

            if (startsWord)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}

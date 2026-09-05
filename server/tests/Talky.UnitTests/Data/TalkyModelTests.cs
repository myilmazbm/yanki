using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Talky.Core.Domain;
using Talky.Data;

namespace Talky.UnitTests.Data;

/// <summary>
/// Model-shape tests. They build the EF model in memory and never touch a database, so they are the
/// cheap guard rail for the schema decisions the design depends on.
/// </summary>
public sealed partial class TalkyModelTests
{
    private static readonly IModel Model = CreateModel();

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SnakeCase { get; }

    [Fact]
    public void Every_table_name_is_snake_case()
    {
        foreach (var entityType in Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            Assert.NotNull(table);
            Assert.Matches(SnakeCase, table);
        }
    }

    [Fact]
    public void Every_column_name_is_snake_case()
    {
        foreach (var entityType in Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                Assert.Matches(SnakeCase, property.GetColumnName());
            }
        }
    }

    [Fact]
    public void Every_enum_is_stored_as_tinyint()
    {
        foreach (var entityType in Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType.IsEnum)
                {
                    Assert.Equal("tinyint", property.GetColumnType());
                }
            }
        }
    }

    [Fact]
    public void Every_timestamp_is_datetime2_with_millisecond_precision()
    {
        foreach (var entityType in Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clrType == typeof(DateTime))
                {
                    Assert.Equal("datetime2(3)", property.GetColumnType());
                }
            }
        }
    }

    [Fact]
    public void Messages_are_clustered_on_conversation_and_seq()
    {
        var messages = Model.FindEntityType(typeof(Message))!;
        Assert.Equal("messages", messages.GetTableName());

        var key = messages.FindPrimaryKey()!;
        Assert.Equal(["conversation_id", "seq"], key.Properties.Select(p => p.GetColumnName()));
        Assert.True(key.IsClustered());
    }

    [Fact]
    public void Message_id_is_a_nonclustered_unique_index_not_a_key()
    {
        var messages = Model.FindEntityType(typeof(Message))!;
        var index = messages.GetIndexes().Single(i =>
            i.Properties.Count == 1 && i.Properties[0].GetColumnName() == "id");

        Assert.True(index.IsUnique);
        Assert.NotEqual(true, index.IsClustered());
    }

    [Fact]
    public void Sends_are_idempotent_through_a_unique_sender_and_client_message_index()
    {
        var messages = Model.FindEntityType(typeof(Message))!;
        var index = messages.GetIndexes().Single(i =>
            i.Properties.Select(p => p.GetColumnName()).SequenceEqual(["sender_id", "client_msg_id"]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Thread_replies_use_a_filtered_index()
    {
        var messages = Model.FindEntityType(typeof(Message))!;
        var index = messages.GetIndexes().Single(i =>
            i.Properties.Select(p => p.GetColumnName()).SequenceEqual(["parent_id", "seq"]));

        Assert.False(index.IsUnique);
        Assert.Equal("[parent_id] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Messages_carry_no_foreign_keys()
    {
        // Retention purges messages in batches along the clustered key; an inbound or outbound FK
        // would turn every DELETE into a constraint check on the hottest table in the schema.
        var messages = Model.FindEntityType(typeof(Message))!;
        Assert.Empty(messages.GetForeignKeys());
        Assert.DoesNotContain(
            Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()),
            fk => fk.PrincipalEntityType.ClrType == typeof(Message));
    }

    [Theory]
    [InlineData(typeof(User))]
    [InlineData(typeof(Conversation))]
    [InlineData(typeof(Team))]
    [InlineData(typeof(Attachment))]
    [InlineData(typeof(Job))]
    [InlineData(typeof(AuditLog))]
    public void Guid_primary_keys_are_clustered_and_sequential(Type entityClrType)
    {
        var entityType = Model.FindEntityType(entityClrType)!;
        var key = entityType.FindPrimaryKey()!;

        var idProperty = Assert.Single(key.Properties);
        Assert.Equal(typeof(Guid), idProperty.ClrType);
        Assert.True(key.IsClustered());
        Assert.Equal(ValueGenerated.OnAdd, idProperty.ValueGenerated);

        // SQL Server sorts uniqueidentifier from the last byte group, so a clustered GUID key has to
        // be generated in that order or every insert is a page split.
        Assert.Equal(
            typeof(Microsoft.EntityFrameworkCore.ValueGeneration.SequentialGuidValueGenerator),
            idProperty.GetValueGeneratorFactory()!(idProperty, entityType).GetType());
    }

    [Fact]
    public void Conversation_member_carries_the_read_cursor_so_unread_is_never_counted()
    {
        var member = Model.FindEntityType(typeof(ConversationMember))!;
        Assert.Equal(
            ["conversation_id", "user_id"],
            member.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()));

        Assert.Contains(member.GetProperties(), p => p.GetColumnName() == "last_read_seq");
        Assert.Contains(member.GetProperties(), p => p.GetColumnName() == "mention_count");

        var byUser = member.GetIndexes().Single(i =>
            i.Properties.Select(p => p.GetColumnName()).SequenceEqual(["user_id"]));
        Assert.NotNull(byUser);
    }

    [Fact]
    public void Users_and_conversations_carry_a_home_domain()
    {
        foreach (var clrType in new[] { typeof(User), typeof(Conversation), typeof(Team) })
        {
            var entityType = Model.FindEntityType(clrType)!;
            Assert.Contains(entityType.GetProperties(), p => p.GetColumnName() == "home_domain");
        }
    }

    [Fact]
    public void Every_expected_table_is_mapped()
    {
        string[] expected =
        [
            "attachments", "audit_logs", "call_participants", "calls", "client_errors",
            "cluster_nodes", "conversation_members", "conversations", "devices", "dm_pairs",
            "federation_peers", "identity_sources", "jobs", "messages", "metrics_1m", "reactions",
            "sessions", "team_members", "teams", "thread_meta", "users",
        ];

        var actual = Model.GetEntityTypes().Select(e => e.GetTableName()!).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static IModel CreateModel()
    {
        // UseSqlServer only needs a syntactically valid connection string to build the model; nothing
        // here opens a connection.
        var options = new DbContextOptionsBuilder<TalkyDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=talky_model_only")
            .Options;

        using var context = new TalkyDbContext(options);

        // The design-time model keeps the provider annotations (clustered keys, value generator
        // factories) that the runtime model trims away.
        return context.GetService<IDesignTimeModel>().Model;
    }
}

using Yanki.Data.Modeling;

namespace Yanki.UnitTests.Data;

public sealed class SnakeCaseNamingTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("ConversationId", "conversation_id")]
    [InlineData("ClientMsgId", "client_msg_id")]
    [InlineData("LastSeq", "last_seq")]
    [InlineData("LastMessageAt", "last_message_at")]
    [InlineData("UserA", "user_a")]
    [InlineData("UserB", "user_b")]
    [InlineData("Sha256", "sha256")]
    [InlineData("P95", "p95")]
    [InlineData("IsOrgAdmin", "is_org_admin")]
    [InlineData("Metrics1m", "metrics1m")]
    [InlineData("GrpcPort", "grpc_port")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("HTTPSPort", "https_port")]
    public void ToSnakeCase_converts_pascal_case(string input, string expected) =>
        Assert.Equal(expected, SnakeCaseNaming.ToSnakeCase(input));

    [Theory]
    [InlineData("metrics_1m")]
    [InlineData("conversation_members")]
    [InlineData("type")]
    [InlineData("message")]
    [InlineData("title")]
    public void ToSnakeCase_is_idempotent_for_names_that_are_already_snake_case(string name)
    {
        // The model pass runs over every name, including the ones a configuration set by hand, so
        // this property is what makes explicit names survive it unchanged.
        Assert.Equal(name, SnakeCaseNaming.ToSnakeCase(name));
        Assert.Equal(name, SnakeCaseNaming.ToSnakeCase(SnakeCaseNaming.ToSnakeCase(name)));
    }

    [Fact]
    public void ToSnakeCase_rejects_empty_names()
    {
        Assert.Throws<ArgumentNullException>(() => SnakeCaseNaming.ToSnakeCase(null!));
        Assert.Throws<ArgumentException>(() => SnakeCaseNaming.ToSnakeCase(string.Empty));
    }
}

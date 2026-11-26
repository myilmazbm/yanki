using Talky.Data;

namespace Talky.UnitTests.Data;

public sealed class DatabaseOptionsTests
{
    [Theory]
    [InlineData("Server=db01;Databse=talky;Integrated Security=true", false)]
    [InlineData("Server=db01;Integrated Security=true", false)]
    [InlineData("Server=db01;Database=talky;Integrated Security=true", true)]
    public void The_connection_string_must_parse_and_name_a_database(string connectionString, bool valid)
    {
        var result = new ValidateDatabaseOptions().Validate(null, new DatabaseOptions { ConnectionString = connectionString });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.DoesNotContain("db01", result.FailureMessage, StringComparison.Ordinal);
        }
    }
}

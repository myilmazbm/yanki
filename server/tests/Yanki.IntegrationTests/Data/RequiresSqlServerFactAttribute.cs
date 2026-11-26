namespace Talky.IntegrationTests.Data;

/// <summary>
/// A <see cref="FactAttribute"/> that reports itself as skipped when
/// <c>TALKY_SKIP_DB_TESTS=1</c>. The decision is made at discovery time, which is all xunit v2
/// supports, and it is what lets the whole suite run on a machine without SQL Server.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresSqlServerFactAttribute : FactAttribute
{
    public RequiresSqlServerFactAttribute()
    {
        if (TalkyDatabaseFixture.SkipDatabaseTests)
        {
            Skip = $"{TalkyDatabaseFixture.SkipVariable}=1";
        }
    }
}

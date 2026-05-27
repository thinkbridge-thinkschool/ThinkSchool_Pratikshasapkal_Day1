namespace Quotes.Tests.Integration;

/// <summary>
/// Each xUnit test class gets a new instance, so every [Fact] here boots
/// its own isolated factory → fresh SQL Server database → fresh HttpClient.
/// </summary>
[Collection("SqlServer")]
public sealed class SmokeTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public SmokeTests(SqlServerContainerFixture fixture)
    {
        _factory = new CustomWebApplicationFactory(fixture);
    }

    // Proves: host boots against SQL Server, EnsureCreated() builds the schema, admin user is seeded,
    // the auth endpoint is reachable, and a valid login returns 200 + access_token.
    [Fact]
    public async Task App_Boots_AndLoginWithSeededAdminReturns200WithToken()
    {
        // Arrange
        var client = _factory.CreateClient();
        var body = new StringContent(
            """{"email":"admin@example.com","password":"password123"}""",
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await client.PostAsync("/api/auth/login", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("access_token");
    }

    // Proves: EnsureCreated() built the schema from the C# EF Core model against SQL Server
    // and every domain table is queryable (no SQLite-generated DDL type mismatches).
    [Fact]
    public void Schema_AppliesCleanly_AllTablesExistAndAreQueryable()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Act & Assert — each query hits SQL Server; a missing table throws InvalidOperationException.
        // EnsureCreated() (used instead of Migrate()) creates tables from the C# model with proper
        // SQL Server types (datetime2, nvarchar(max)) — bypassing the SQLite-generated migration
        // DDL that uses type:"TEXT" for DateTime columns and causes datetime2→text conversion errors.
        var queryQuotes      = () => db.Quotes.Count();
        var queryUsers       = () => db.Users.Count();
        var queryTokens      = () => db.RefreshTokens.Count();
        var queryCollections = () => db.Collections.Count();

        queryQuotes.Should().NotThrow(
            because: "EnsureCreated() must have created the Quotes table");
        queryUsers.Should().NotThrow(
            because: "EnsureCreated() must have created the Users table");
        queryTokens.Should().NotThrow(
            because: "EnsureCreated() must have created the RefreshTokens table");
        queryCollections.Should().NotThrow(
            because: "EnsureCreated() must have created the Collections table");
    }

    public void Dispose() => _factory.Dispose();
}

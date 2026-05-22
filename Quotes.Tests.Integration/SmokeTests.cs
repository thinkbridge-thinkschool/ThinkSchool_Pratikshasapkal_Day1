namespace Quotes.Tests.Integration;

/// <summary>
/// Each xUnit test class gets a new instance, so every [Fact] here boots
/// its own isolated factory → fresh DB → fresh HttpClient.
/// </summary>
public sealed class SmokeTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public SmokeTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    // Proves: host boots, EnsureCreated runs, admin user is seeded,
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

    public void Dispose() => _factory.Dispose();
}

namespace Quotes.Tests.Integration;

public sealed class AuthEndpointTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthEndpointTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    // ── POST /api/auth/login ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithTokens()
    {
        // Arrange
        var client = _factory.CreateClient();
        var body = LoginBody("admin@example.com", "password123");

        // Act
        var response = await client.PostAsync("/api/auth/login", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        doc.GetProperty("refresh_token").GetString().Should().NotBeNullOrEmpty();
        doc.GetProperty("expires_in").GetInt32().Should().Be(900);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        // Arrange
        var client = _factory.CreateClient();
        var body = LoginBody("admin@example.com", "wrongpassword");

        // Act
        var response = await client.PostAsync("/api/auth/login", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        // Arrange
        var client = _factory.CreateClient();
        var body = LoginBody("nobody@example.com", "password123");

        // Act
        var response = await client.PostAsync("/api/auth/login", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/auth/refresh ───────────────────────────────────────────────

    [Fact]
    public async Task Refresh_WithValidToken_Returns200WithNewTokens()
    {
        // Arrange — log in to get the initial refresh token
        var client = _factory.CreateClient();
        var loginResp = await client.PostAsync("/api/auth/login",
            LoginBody("admin@example.com", "password123"));
        var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync()).RootElement;
        var refreshToken = loginDoc.GetProperty("refresh_token").GetString()!;

        // Act
        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(refreshToken));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        var newRefreshToken = doc.GetProperty("refresh_token").GetString();
        newRefreshToken.Should().NotBeNullOrEmpty();
        newRefreshToken.Should().NotBe(refreshToken);
    }

    [Fact]
    public async Task Refresh_WithConsumedToken_Returns401()
    {
        // Arrange — log in, then rotate the token once so the original is consumed
        var client = _factory.CreateClient();
        var loginResp = await client.PostAsync("/api/auth/login",
            LoginBody("admin@example.com", "password123"));
        var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync()).RootElement;
        var originalToken = loginDoc.GetProperty("refresh_token").GetString()!;

        // First refresh — consumes the original token
        await client.PostAsync("/api/auth/refresh", RefreshBody(originalToken));

        // Act — replay the original (now consumed) token
        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(originalToken));

        // Assert — reuse detection must reject and revoke the family
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StringContent LoginBody(string email, string password) =>
        new($$"""{"email":"{{email}}","password":"{{password}}"}""",
            Encoding.UTF8, "application/json");

    private static StringContent RefreshBody(string token) =>
        new($$"""{"refreshToken":"{{token}}"}""",
            Encoding.UTF8, "application/json");

    public void Dispose() => _factory.Dispose();
}

namespace Quotes.Tests.Integration;

/// <summary>
/// Day 3 Piece 3 — Integration tests for the five core auth/authz scenarios.
///
/// ┌─────┬──────────────────────────────────────────────────────────────────────┐
/// │  #  │ Test name (appears verbatim in CI output)                            │
/// ├─────┼──────────────────────────────────────────────────────────────────────┤
/// │  1  │ Anonymous_Request_Returns_401                                        │
/// │  2  │ Authenticated_User_With_Wrong_Policy_Returns_403                     │
/// │  3  │ Authenticated_User_With_Valid_Policy_Returns_200                     │
/// │  4  │ Expired_Token_Returns_401                                            │
/// │  5  │ Revoked_Refresh_Token_Chain_Returns_401                              │
/// └─────┴──────────────────────────────────────────────────────────────────────┘
/// </summary>
[Collection("SqlServer")]
public sealed class AuthorizationScenarioTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthorizationScenarioTests(SqlServerContainerFixture fixture)
    {
        _factory = new CustomWebApplicationFactory(fixture);
    }

    // ── Scenario 1 ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Anonymous_Request_Returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "GET /api/quotes requires authentication; " +
                     "a request with no Bearer token must receive 401");
    }

    // ── Scenario 2 ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Authenticated_User_With_Wrong_Policy_Returns_403()
    {
        var token  = JwtTestHelper.CreateToken(includeWriteScope: false);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var body = PostQuoteBody("Seneca", "Luck is what happens when preparation meets opportunity.");

        var response = await client.PostAsync("/api/quotes", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "valid JWT without scope=quotes.write must be 403, not 401");
    }

    // ── Scenario 3 ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Authenticated_User_With_Valid_Policy_Returns_200()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var body = PostQuoteBody(
            "Marcus Aurelius",
            "The impediment to action advances action. What stands in the way becomes the way.");

        var response = await client.PostAsync("/api/quotes", body);

        // POST /api/quotes returns 201 Created — "200" in the test name means "success"
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "authenticated caller with scope=quotes.write must reach the endpoint");
    }

    // ── Scenario 4 ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Expired_Token_Returns_401()
    {
        var expiredToken = JwtTestHelper.CreateExpiredToken();
        var client       = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", expiredToken);

        var response = await client.GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "ValidateLifetime=true must reject tokens whose exp is in the past");
    }

    // ── Scenario 5 ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Revoked_Refresh_Token_Chain_Returns_401()
    {
        var client = _factory.CreateClient();

        // Step 1: login — issue token_1
        var loginResp = await client.PostAsync("/api/auth/login",
            LoginBody("admin@example.com", "password123"));

        loginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "login must succeed before the revocation scenario");

        var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync()).RootElement;
        var token1   = loginDoc.GetProperty("refresh_token").GetString()!;

        // Step 2: rotate token_1 → issue token_2
        var rotateResp = await client.PostAsync("/api/auth/refresh", RefreshBody(token1));
        rotateResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "first rotation of a fresh token must succeed");

        var rotateDoc = JsonDocument.Parse(await rotateResp.Content.ReadAsStringAsync()).RootElement;
        var token2    = rotateDoc.GetProperty("refresh_token").GetString()!;

        token2.Should().NotBe(token1, because: "rotation must issue a new token");

        // Step 3: replay token_1 — triggers reuse detection + RevokeFamily()
        var reuseResp = await client.PostAsync("/api/auth/refresh", RefreshBody(token1));
        reuseResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "replaying a consumed token must return 401");

        // Step 4: token_2 is now revoked by RevokeFamily()
        var chainResp = await client.PostAsync("/api/auth/refresh", RefreshBody(token2));
        chainResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "family revocation must invalidate all tokens in the chain");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static StringContent LoginBody(string email, string password) =>
        new($$"""{"email":"{{email}}","password":"{{password}}"}""",
            Encoding.UTF8, "application/json");

    private static StringContent RefreshBody(string token) =>
        new($$"""{"refreshToken":"{{token}}"}""",
            Encoding.UTF8, "application/json");

    private static StringContent PostQuoteBody(string author, string text) =>
        new($$"""{"author":"{{author}}","text":"{{text}}"}""",
            Encoding.UTF8, "application/json");

    public void Dispose() => _factory.Dispose();
}

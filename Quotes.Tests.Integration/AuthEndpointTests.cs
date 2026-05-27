namespace Quotes.Tests.Integration;

[Collection("SqlServer")]
public sealed class AuthEndpointTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthEndpointTests(SqlServerContainerFixture fixture)
    {
        _factory = new CustomWebApplicationFactory(fixture);
    }

    // ── POST /api/auth/login ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithTokens()
    {
        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/login", LoginBody("admin@example.com", "password123"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        doc.GetProperty("refresh_token").GetString().Should().NotBeNullOrEmpty();
        doc.GetProperty("expires_in").GetInt32().Should().Be(900);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/login", LoginBody("admin@example.com", "wrongpassword"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/login", LoginBody("nobody@example.com", "password123"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_WithEmptyEmail_Returns401()
    {
        // Empty email finds no user → 401 (not a server error)
        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/login", LoginBody("", "password123"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/auth/refresh ───────────────────────────────────────────────

    [Fact]
    public async Task Refresh_WithValidToken_Returns200WithRotatedTokens()
    {
        var client = _factory.CreateClient();
        var loginDoc = await LoginAsAdmin(client);
        var refreshToken = loginDoc.GetProperty("refresh_token").GetString()!;

        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(refreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        var newToken = doc.GetProperty("refresh_token").GetString();
        newToken.Should().NotBeNullOrEmpty();
        newToken.Should().NotBe(refreshToken, because: "each successful refresh issues a brand-new token");
    }

    [Fact]
    public async Task Refresh_WithNonExistentToken_Returns401()
    {
        // A random token that was never issued should not be found in the DB
        var fakeToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/refresh", RefreshBody(fakeToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_WithConsumedToken_Returns401AndRevokesFamily()
    {
        var client = _factory.CreateClient();
        var loginDoc = await LoginAsAdmin(client);
        var originalToken = loginDoc.GetProperty("refresh_token").GetString()!;

        // First refresh — marks originalToken as used (IsUsed → true)
        await client.PostAsync("/api/auth/refresh", RefreshBody(originalToken));

        // Replay the consumed token — triggers reuse detection
        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(originalToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_AfterFamilyRevocation_RotatedTokenIsAlsoBlocked()
    {
        // Demonstrates that reuse detection protects the WHOLE rotation chain:
        // token_1 → rotate → token_2 → replay token_1 → family revoked → token_2 now blocked
        var client = _factory.CreateClient();
        var loginDoc = await LoginAsAdmin(client);
        var token1 = loginDoc.GetProperty("refresh_token").GetString()!;

        // Legitimate rotation: consume token_1, get token_2
        var rotateResp = await client.PostAsync("/api/auth/refresh", RefreshBody(token1));
        var token2 = JsonDocument.Parse(await rotateResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("refresh_token").GetString()!;

        // Replay token_1 — triggers family revocation
        await client.PostAsync("/api/auth/refresh", RefreshBody(token1));

        // token_2 (the rotated successor) must now also be rejected
        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(token2));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "family revocation must invalidate all tokens in the chain including the rotated successor");
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_Returns401()
    {
        // Insert an expired refresh token directly so we don't have to wait 7 days
        int adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            adminId = scope.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Users.Single(u => u.Email == "admin@example.com").Id;
        }

        const string rawToken = "expired_token_for_testing_purposes";
        var hash = HashToken(rawToken);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                TokenHash   = hash,
                UserId      = adminId,
                FamilyId    = Guid.NewGuid().ToString(),
                ExpiresAt   = DateTime.UtcNow.AddDays(-1) // already expired
            });
            await db.SaveChangesAsync();
        }

        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/refresh", RefreshBody(rawToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "an expired token must be rejected even if its hash is in the database");
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_Returns401()
    {
        // Insert a refresh token that was explicitly revoked (RevokedAt is set)
        int adminId;
        using (var scope = _factory.Services.CreateScope())
        {
            adminId = scope.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Users.Single(u => u.Email == "admin@example.com").Id;
        }

        const string rawToken = "revoked_token_for_testing_purposes";
        var hash = HashToken(rawToken);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RefreshTokens.Add(new RefreshToken
            {
                TokenHash   = hash,
                UserId      = adminId,
                FamilyId    = Guid.NewGuid().ToString(),
                ExpiresAt   = DateTime.UtcNow.AddDays(7),
                RevokedAt   = DateTime.UtcNow.AddHours(-1) // explicitly revoked
            });
            await db.SaveChangesAsync();
        }

        var response = await _factory.CreateClient()
            .PostAsync("/api/auth/refresh", RefreshBody(rawToken));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "a revoked token must be rejected regardless of its expiry");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<JsonElement> LoginAsAdmin(HttpClient client)
    {
        var resp = await client.PostAsync("/api/auth/login",
            LoginBody("admin@example.com", "password123"));
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    // Mirrors the hashing logic in Program.cs so tests can seed pre-hashed tokens.
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLower();
    }

    private static StringContent LoginBody(string email, string password) =>
        new($$"""{"email":"{{email}}","password":"{{password}}"}""",
            Encoding.UTF8, "application/json");

    private static StringContent RefreshBody(string token) =>
        new($$"""{"refreshToken":"{{token}}"}""",
            Encoding.UTF8, "application/json");

    public void Dispose() => _factory.Dispose();
}

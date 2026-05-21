using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;

namespace QuotesApi.Tests;

public class RefreshTokenSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RefreshTokenSecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                connection.Open();

                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null) services.Remove(descriptor);

                services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlite(connection));

                var optBuilder = new DbContextOptionsBuilder<AppDbContext>();
                optBuilder.UseSqlite(connection);
                using var ctx = new AppDbContext(optBuilder.Options);
                ctx.Database.EnsureCreated();
            });
        });
    }

    // Scenario: replay attack — old refresh token is replayed after rotation.
    // Expected: family is revoked, both old and new tokens return 401.
    [Fact]
    public async Task Refresh_ConsumedToken_RevokesFamilyAndReturns401()
    {
        var client = _factory.CreateClient();

        var (_, refresh1) = await LoginAsync(client);

        // First legitimate rotation: refresh1 is consumed, refresh2 is issued.
        var (_, refresh2) = await RefreshAsync(client, refresh1);

        // Replay attack: reuse refresh1 (already consumed).
        // Server detects reuse, revokes entire family.
        var replayResp = await client.PostAsync("/api/auth/refresh", RefreshBody(refresh1));
        Assert.Equal(HttpStatusCode.Unauthorized, replayResp.StatusCode);

        // The legitimate refresh2 belongs to the now-revoked family — also rejected.
        var legitResp = await client.PostAsync("/api/auth/refresh", RefreshBody(refresh2));
        Assert.Equal(HttpStatusCode.Unauthorized, legitResp.StatusCode);
    }

    // Scenario: expired refresh token → server rejects it → 401.
    [Fact]
    public async Task Refresh_ExpiredToken_Returns401()
    {
        var client = _factory.CreateClient();
        var (_, rawRefresh) = await LoginAsync(client);

        // Expire the stored token directly in the DB.
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawRefresh))).ToLower();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.RefreshTokens.FirstAsync(t => t.TokenHash == hash);
            stored.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync("/api/auth/refresh", RefreshBody(rawRefresh));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<(string accessToken, string refreshToken)> LoginAsync(HttpClient client)
    {
        var resp = await client.PostAsync(
            "/api/auth/login",
            new StringContent(
                """{"email":"admin@example.com","password":"password123"}""",
                Encoding.UTF8,
                "application/json"));

        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        return (
            doc.RootElement.GetProperty("access_token").GetString()!,
            doc.RootElement.GetProperty("refresh_token").GetString()!
        );
    }

    private async Task<(string accessToken, string refreshToken)> RefreshAsync(HttpClient client, string token)
    {
        var resp = await client.PostAsync("/api/auth/refresh", RefreshBody(token));
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        return (
            doc.RootElement.GetProperty("access_token").GetString()!,
            doc.RootElement.GetProperty("refresh_token").GetString()!
        );
    }

    private static StringContent RefreshBody(string token) =>
        new($$"""{"refreshToken":"{{token}}"}""", Encoding.UTF8, "application/json");
}

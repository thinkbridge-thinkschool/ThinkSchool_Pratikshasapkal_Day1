using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Tests.Helpers;

namespace QuotesApi.Tests;

public class QuoteAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public QuoteAuthorizationTests(WebApplicationFactory<Program> factory)
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

    // Scenario: authenticated but missing scope claim → can-edit-quotes policy fails → 403
    [Fact]
    public async Task PostQuote_AuthenticatedWithoutScopeClaim_Returns403()
    {
        var client = _factory.CreateClient();
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();

        var token = JwtTestHelper.Generate(cfg, "user@example.com", includeScope: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/quotes",
            QuoteBody("Seneca", "Luck is what happens when preparation meets opportunity."));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Scenario: expired access token → JWT bearer rejects it → 401
    [Fact]
    public async Task GetQuotes_ExpiredToken_Returns401()
    {
        var client = _factory.CreateClient();
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();

        var expired = JwtTestHelper.Generate(
            cfg, "user@example.com", expires: DateTime.UtcNow.AddSeconds(-1));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        var response = await client.GetAsync("/api/quotes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Scenario: authenticated as user B, trying to delete a quote owned by user A → 403
    [Fact]
    public async Task DeleteQuote_NotOwner_Returns403()
    {
        var client = _factory.CreateClient();
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();

        var tokenA = JwtTestHelper.Generate(cfg, "user-a@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var createResp = await client.PostAsync("/api/quotes",
            QuoteBody("Aristotle", "We are what we repeatedly do."));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var doc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var quoteId = doc.RootElement.GetProperty("id").GetInt32();

        var tokenB = JwtTestHelper.Generate(cfg, "user-b@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var deleteResp = await client.DeleteAsync($"/api/quotes/{quoteId}");

        Assert.Equal(HttpStatusCode.Forbidden, deleteResp.StatusCode);
    }

    // Scenario: authenticated as owner, deleting own quote → 200
    [Fact]
    public async Task DeleteQuote_Owner_Returns200()
    {
        var client = _factory.CreateClient();
        var cfg = _factory.Services.GetRequiredService<IConfiguration>();

        var token = JwtTestHelper.Generate(cfg, "owner@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createResp = await client.PostAsync("/api/quotes",
            QuoteBody("Epictetus", "Make the best use of what is in your power."));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var doc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var quoteId = doc.RootElement.GetProperty("id").GetInt32();

        var deleteResp = await client.DeleteAsync($"/api/quotes/{quoteId}");

        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);
    }

    private static StringContent QuoteBody(string author, string text) =>
        new($$"""{"author":"{{author}}","text":"{{text}}"}""", Encoding.UTF8, "application/json");
}

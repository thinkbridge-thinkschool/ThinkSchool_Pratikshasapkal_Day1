using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Tests;

public class AuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // "Testing" makes Program.cs skip MigrateAsync() and its seeding block,
            // so we control schema creation and seeding entirely from here.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Use a shared open in-memory SQLite connection so the seeding
                // done below and request handlers all see the same data.
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

                // Seed the admin user since Program.cs seeding is skipped in Testing.
                if (!ctx.Users.Any())
                {
                    ctx.Users.Add(new User("admin@example.com", "password123"));
                    ctx.SaveChanges();
                }
            });
        });
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/login", LoginBody("admin@example.com", "password123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("access_token", body);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/login", LoginBody("admin@example.com", "wrong"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/login", LoginBody("nobody@example.com", "password123"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/quotes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithValidToken_Returns200()
    {
        var client = _factory.CreateClient();

        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/quotes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static StringContent LoginBody(string email, string password) =>
        new(
            $$"""{"email":"{{email}}","password":"{{password}}"}""",
            System.Text.Encoding.UTF8,
            "application/json");

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/auth/login", LoginBody("admin@example.com", "password123"));
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }
}

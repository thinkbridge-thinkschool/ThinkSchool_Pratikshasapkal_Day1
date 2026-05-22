namespace Quotes.Tests.Integration;

/// <summary>
/// Per-test WebApplicationFactory:
///   - Replaces the file-backed SQLite DB with an isolated in-memory SQLite connection
///     that lives exactly as long as this factory instance.
///   - Replaces IClock with a controllable FakeClock exposed via the Clock property.
///   - Applies EF schema (EnsureCreated) and seeds the admin user after the host is built.
///   - Sets the environment to "Testing" so Program.cs skips its own seeding block.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    /// <summary>Controllable clock — tests can call Clock.Advance() or Clock.Set().</summary>
    public FakeClock Clock { get; }

    public CustomWebApplicationFactory()
    {
        Clock = new FakeClock();
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove AppDbContext, DbContextOptions<AppDbContext>, and the internal
            // IDbContextOptionsConfiguration<AppDbContext> that stores the SQLite file path.
            // The (serviceProvider, options) => form of AddDbContext registers the last one,
            // which is why we match by generic argument rather than just by service type.
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                    d.ServiceType == typeof(AppDbContext) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericArguments().Contains(typeof(AppDbContext))))
                .ToList();

            foreach (var descriptor in toRemove)
                services.Remove(descriptor);

            // Bind the context to the shared open connection so the in-memory database
            // survives for the full lifetime of this factory.
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(_connection));

            // Swap the production clock for the controllable fake.
            var clockDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IClock));
            if (clockDescriptor is not null)
                services.Remove(clockDescriptor);

            services.AddSingleton<IClock>(Clock);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Create all tables from the current EF model.
        // (No migrations exist; EnsureCreated is the right call here.)
        db.Database.EnsureCreated();

        // Program.cs seeding is guarded by !IsEnvironment("Testing"),
        // so we seed the admin user ourselves.
        if (!db.Users.Any())
        {
            db.Users.Add(new User("admin@example.com", "password123"));
            db.SaveChanges();
        }

        return host;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Logs in and returns a raw JWT access token.</summary>
    public async Task<string> GetTokenAsync(
        HttpClient client,
        string email    = "admin@example.com",
        string password = "password123")
    {
        var body = new StringContent(
            $$"""{"email":"{{email}}","password":"{{password}}"}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/auth/login", body);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// Creates an HttpClient whose Authorization header is already set
    /// to a valid Bearer token for the given credentials.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string email    = "admin@example.com",
        string password = "password123")
    {
        var client = CreateClient();
        var token  = await GetTokenAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _connection.Dispose();
        base.Dispose(disposing);
    }
}

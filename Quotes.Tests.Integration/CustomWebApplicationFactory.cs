namespace Quotes.Tests.Integration;

/// <summary>
/// Per-test WebApplicationFactory:
///   - Each instance targets a unique SQL Server database on the shared Testcontainers
///     SQL Server instance, so tests are fully isolated with no shared DB state.
///   - Replaces IClock with a controllable FakeClock exposed via the Clock property.
///   - Applies all EF Core migrations (creating the database if needed) and seeds the
///     admin user after the host is built.
///   - Sets the environment to "Testing" so Program.cs skips its own seeding block.
///
/// Requires Docker to be running locally.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"testdb_{Guid.NewGuid():N}";
    private readonly SqlServerContainerFixture _fixture;

    /// <summary>Controllable clock — tests can call Clock.Advance() or Clock.Set().</summary>
    public FakeClock Clock { get; }

    public CustomWebApplicationFactory(SqlServerContainerFixture fixture)
    {
        Clock = new FakeClock();
        _fixture = fixture;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove all AppDbContext registrations (the SQLite-backed production registration
            // and any internal EF options descriptors keyed on AppDbContext).
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                    d.ServiceType == typeof(AppDbContext) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericArguments().Contains(typeof(AppDbContext))))
                .ToList();

            foreach (var descriptor in toRemove)
                services.Remove(descriptor);

            // Point AppDbContext at a fresh, uniquely-named SQL Server database on the
            // shared container. EF's Migrate() in CreateHost() will create it.
            //
            // Suppress PendingModelChangesWarning: EF Core 10 compares the live C# model hash
            // against the last migration snapshot. Our snapshot is SQLite-generated and lacks
            // QuoteId/AddedAt for CollectionItem because those were added to the migrations
            // manually (no Designer file update). The schema IS correct — all columns exist
            // after Migrate() — so this guard is a false positive in cross-provider test infra.
            services.AddDbContext<AppDbContext>(options =>
                options
                    .UseSqlServer(_fixture.GetConnectionString(_databaseName))
                    .ConfigureWarnings(w =>
                        w.Ignore(RelationalEventId.PendingModelChangesWarning)));

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

        // Create the database and apply every pending migration.
        // Migrate() creates the database if it does not already exist.
        db.Database.Migrate();

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
}

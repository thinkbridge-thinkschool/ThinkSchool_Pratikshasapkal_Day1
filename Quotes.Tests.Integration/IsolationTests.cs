namespace Quotes.Tests.Integration;

/// <summary>
/// Proves that each test runs against a completely isolated, freshly-created database.
///
/// xUnit creates a new class instance per [Fact], so every test here gets its own
/// CustomWebApplicationFactory → its own SqliteConnection("Data Source=:memory:")
/// → its own schema → zero shared state.
/// </summary>
public sealed class IsolationTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public IsolationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    // ── 1. Fresh DB per test ─────────────────────────────────────────────────

    [Fact]
    public async Task GetQuotes_AtStartup_ReturnsEmptyArray()
    {
        // Arrange — brand-new factory, no other test has touched this DB
        var client = await _factory.CreateAuthenticatedClientAsync();

        // Act
        var response = await client.GetAsync("/api/quotes");

        // Assert — zero quotes, proving no data leaked from any other test
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        root.GetArrayLength().Should().Be(0,
            because: "each test gets a pristine in-memory DB — no quotes exist at startup");
    }

    [Fact]
    public async Task QuotesCreatedInThisTest_AreVisible_ButCountStartsAtZero()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();

        // Assert initial state
        var before = await client.GetAsync("/api/quotes");
        var beforeRoot = JsonDocument.Parse(await before.Content.ReadAsStringAsync()).RootElement;
        beforeRoot.GetArrayLength().Should().Be(0,
            because: "DB is empty at the start of every test");

        // Act — write data inside this test
        var body = new StringContent(
            """{"author":"Epictetus","text":"Make the best use of what is in your power."}""",
            Encoding.UTF8, "application/json");
        await client.PostAsync("/api/quotes", body);

        // Assert — data is visible within the same factory/DB scope
        var after = await client.GetAsync("/api/quotes");
        var afterRoot = JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement;
        afterRoot.GetArrayLength().Should().Be(1,
            because: "the quote created in this test is persisted to this test's DB");
    }

    // ── 2. No data leaks between factories ───────────────────────────────────

    [Fact]
    public async Task DataCreatedInOneFactory_IsNotVisibleInAnotherFactory()
    {
        // Arrange — factory A creates a quote
        var clientA = await _factory.CreateAuthenticatedClientAsync();
        var body = new StringContent(
            """{"author":"Seneca","text":"Per aspera ad astra."}""",
            Encoding.UTF8, "application/json");
        var createResp = await clientA.PostAsync("/api/quotes", body);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Arrange — factory B is a completely independent instance
        using var factoryB = new CustomWebApplicationFactory();
        var clientB = await factoryB.CreateAuthenticatedClientAsync();

        // Act
        var response = await clientB.GetAsync("/api/quotes");

        // Assert — factory B's DB has zero quotes; Seneca's quote is invisible
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        root.GetArrayLength().Should().Be(0,
            because: "each factory owns a separate in-memory SQLite connection; data cannot cross that boundary");
    }

    // ── 3. Schema is applied automatically ───────────────────────────────────

    [Fact]
    public void Schema_IsAppliedAutomatically_AllDomainTablesExist()
    {
        // Arrange — access AppDbContext directly from DI
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Act & Assert — each Count() call hits the DB; a missing table would throw
        var queryQuotes    = () => db.Quotes.Count();
        var queryUsers     = () => db.Users.Count();
        var queryTokens    = () => db.RefreshTokens.Count();

        queryQuotes.Should().NotThrow(
            because: "EnsureCreated() in CreateHost() must have created the Quotes table");
        queryUsers.Should().NotThrow(
            because: "EnsureCreated() in CreateHost() must have created the Users table");
        queryTokens.Should().NotThrow(
            because: "EnsureCreated() in CreateHost() must have created the RefreshTokens table");

        // Seeded admin user is present
        db.Users.Count().Should().Be(1,
            because: "CreateHost() seeds exactly one admin user via db.Users.Add(...)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void Dispose() => _factory.Dispose();
}

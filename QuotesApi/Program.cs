using Microsoft.EntityFrameworkCore;
using QuotesApi.Abstractions;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Utilities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.ResponseCompression;
using Azure.Messaging.ServiceBus;
using QuotesApi.Messaging;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Metrics;
using QuotesApi.Options;
using System.Diagnostics;


var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "DynamicScheme";
        options.DefaultChallengeScheme = "DynamicScheme";
    })

    .AddPolicyScheme(
        "DynamicScheme",
        "JWT or Entra",
        options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader =
                    context.Request.Headers.Authorization
                        .FirstOrDefault();

                if (authHeader?.StartsWith("Bearer ") == true)
                {
                    try
                    {
                        var jwt = new JwtSecurityTokenHandler()
                            .ReadJwtToken(authHeader["Bearer ".Length..]);

                        if (jwt.Issuer.Contains("login.microsoftonline.com"))
                            return "Entra";
                    }
                    catch { }
                }

                return JwtBearerDefaults.AuthenticationScheme;
            };
        })

    .AddJwtBearer(
        JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            options.TokenValidationParameters =
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    ValidIssuer =
                        configuration["Jwt:Issuer"],

                    ValidAudience =
                        configuration["Jwt:Audience"],

                    IssuerSigningKey =
                        new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(
                                configuration["Jwt:Key"]!))
                };
        })

    .AddJwtBearer(
        "Entra",
        options =>
        {
            options.Authority =
                $"https://login.microsoftonline.com/{configuration["Entra:TenantId"]}/v2.0";

            options.TokenValidationParameters =
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,

                    ValidAudience =
                        configuration["Entra:Audience"]
                };
        });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("can-edit-quotes", policy =>
        policy.RequireClaim("scope", "quotes.write"));
});

builder.Services.AddScoped<IAuthorizationHandler, DeleteOwnQuoteHandler>();

builder.Services.AddDbContext<AppDbContext>((_, options) =>
{
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrEmpty(connStr))
        options.UseSqlite("Data Source=dev.db");
    else
        options.UseSqlServer(connStr);

    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.LogTo(Console.WriteLine, LogLevel.Information);
    }
});

var allowedOrigins = builder.Configuration["AllowedOrigins"]
    ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? ["http://localhost:4200", "https://mango-river-03f3a6100.7.azurestaticapps.net"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

builder.Services.AddScoped<
    ICollectionRepository,
    CollectionRepository>();

builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();

// ── Background task queue ──────────────────────────────────────────────────
// BackgroundTaskQueue is registered as a singleton so both the HTTP endpoint
// and the hosted service share the same channel instance.
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddHostedService<QueuedHostedService>();

// ── Azure Service Bus ──────────────────────────────────────────────────────
var sbOptions = builder.Configuration
    .GetSection(ServiceBusOptions.SectionName)
    .Get<ServiceBusOptions>() ?? new ServiceBusOptions();

builder.Services.AddSingleton(sbOptions);

// Register the client only when a connection string is present.
// In production on Azure Container Apps, set ConnectionStrings__ServiceBus
// (or use DefaultAzureCredential with FullyQualifiedNamespace).
if (!string.IsNullOrWhiteSpace(sbOptions.ConnectionString))
{
    builder.Services.AddSingleton(new ServiceBusClient(sbOptions.ConnectionString));
    builder.Services.AddSingleton<IMessagePublisher, ServiceBusPublisher>();
    builder.Services.AddHostedService<QuoteEventConsumer>();
}

// ── HybridCache ───────────────────────────────────────────────────────────────
// Two-tier write-through cache: L1 = in-process IMemoryCache, L2 = Redis
// (or DistributedMemoryCache when Redis is not configured for local dev).
//
// HybridCache collapses concurrent cache misses for the same key into a single
// factory invocation (stampede protection) — 50 concurrent requests to a cold
// key trigger exactly one DB query; the other 49 await that same Task.
var cacheOpts = builder.Configuration
    .GetSection(CacheOptions.SectionName)
    .Get<CacheOptions>() ?? new CacheOptions();

builder.Services.AddSingleton(cacheOpts);
builder.Services.AddSingleton<CacheMetrics>();

if (!string.IsNullOrWhiteSpace(cacheOpts.RedisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(opts =>
        opts.Configuration = cacheOpts.RedisConnectionString);
}
else
{
    // No Redis configured — fall back to an in-process distributed cache.
    // Stampede protection and L1 are still active; L2 is simply co-located.
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddHybridCache(opts =>
{
    opts.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration           = TimeSpan.FromSeconds(cacheOpts.QuoteByIdTtlSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(cacheOpts.LocalCacheTtlSeconds),
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<BrotliCompressionProvider>();
    opts.Providers.Add<GzipCompressionProvider>();
});


string GenerateRefreshToken()
{
    var bytes = new byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

string HashToken(string token)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    return Convert.ToHexString(bytes).ToLower();
}

string CreateAccessToken(User user, IConfiguration cfg)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim("scope", "quotes.write")
    };

    var key = new SymmetricSecurityKey(
        Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));

    var jwtToken = new JwtSecurityToken(
        issuer: cfg["Jwt:Issuer"],
        audience: cfg["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(
            Convert.ToDouble(cfg["Jwt:ExpiryMinutes"])),
        signingCredentials: new SigningCredentials(
            key, SecurityAlgorithms.HmacSha256));

    return new JwtSecurityTokenHandler().WriteToken(jwtToken);
}

async Task RevokeFamily(AppDbContext db, string familyId, CancellationToken ct)
{
    var active = await db.RefreshTokens
        .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
        .ToListAsync(ct);

    foreach (var t in active)
        t.RevokedAt = DateTime.UtcNow;

    await db.SaveChangesAsync(ct);
}

var app = builder.Build();

app.UseResponseCompression();
app.UseRouting();
app.UseCors("Angular");
app.UseAuthentication();
app.UseAuthorization();

// Home route
app.MapGet("/", () =>
{
    return "Quotes API Running";
});

// Deliberately slow endpoint: N+1 query pattern + missing index on AuthorId → table scan.
// Uses AsSplitQuery() so EF Core emits separate SELECT statements:
//   Query 1: SELECT * FROM Authors
//   Query 2: SELECT Quotes.* FROM Quotes WHERE AuthorId IN (...)  ← table scan (no index)
app.MapGet("/authors-with-quotes", async (AppDbContext db, CancellationToken ct) =>
{
    var authors = await db.Authors
        .Include(a => a.Quotes.Where(q => !q.IsDeleted))
        .AsSplitQuery()
        .AsNoTracking()
        .ToListAsync(ct);

    return Results.Ok(authors);
});
app.MapGet("/slow-authors-with-quotes", async (AppDbContext db) =>
{
    var authors = await db.Authors.ToListAsync();

    var result = new List<object>();

    foreach (var author in authors)
    {
        var quotes = await db.Quotes
            .Where(q => EF.Property<int>(q, "AuthorId") == author.Id)
            .ToListAsync();

        result.Add(new
        {
            Author = author.Name,
            Quotes = quotes
        });
    }

    return Results.Ok(result);
});


// Optimized endpoint: single LEFT JOIN query, covering index (no key lookups),
// only live quotes, only columns needed by the caller.
//
// Generated SQL (verify with LogTo or SSMS):
//   SELECT a.Id, a.Name, q.Id, q.Text
//   FROM Authors AS a
//   LEFT JOIN Quotes AS q ON a.Id = q.AuthorId AND q.IsDeleted = 0
//   ORDER BY a.Id
//
// Execution plan should show: Index Seek on IX_Quotes_AuthorId_Covering — no Key Lookup.
app.MapGet("/fast-authors-with-quotes-projection",
    async (AppDbContext db) =>
{
    var result = await db.Authors
        .AsNoTracking()
        .Select(a => new
        {
            a.Id,
            a.Name,
            QuoteCount = a.Quotes.Count
        })
        .ToListAsync();

    return Results.Ok(result);
});

// Seed endpoint — populates Authors table and creates 500 demo quotes assigned to authors.
// Uses raw SQL inserts to bypass EF Core's change-tracker complexity with shadow FK properties.
// Call once before load-testing: POST /seed-demo-data
app.MapPost("/seed-demo-data", async (AppDbContext db, CancellationToken ct) =>
{
    if (await db.Authors.AnyAsync(ct))
        return Results.Ok(new { message = "Already seeded", authorCount = await db.Authors.CountAsync(ct) });

    var names = new[]
    {
        "Marcus Aurelius", "Seneca", "Epictetus", "Friedrich Nietzsche", "Albert Camus",
        "Fyodor Dostoevsky", "Leo Tolstoy", "Simone de Beauvoir", "Bertrand Russell", "William James"
    };

    var authors = names.Select(n => new Author(n)).ToList();
    db.Authors.AddRange(authors);
    await db.SaveChangesAsync(ct);

    var authorIds = await db.Authors.Select(a => new { a.Id, a.Name }).ToListAsync(ct);
    var rng = new Random(42);

    for (int i = 0; i < 500; i++)
    {
        var author = authorIds[rng.Next(authorIds.Count)];
        var text = $"Demo quote #{i + 1}: {Guid.NewGuid()}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Quotes (Author, Text, IsDeleted, CreatedByEmail, AuthorId) VALUES ({author.Name}, {text}, 0, 'seed@demo.com', {author.Id})",
            ct);
    }

    return Results.Ok(new { authors = names.Length, quotes = 500 });
});



// Create a new quote
app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var userEmail = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
    var result = Quote.Create(request.Author, request.Text, userEmail);

    if (!result.IsSuccess)
        return Results.Problem(detail: result.Error, statusCode: 400);

    db.Quotes.Add(result.Value!);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
}).RequireAuthorization("can-edit-quotes");



// get all quotes with pagination — cache key includes page + size so each page
// is cached independently; stale entries expire after QuoteListTtlSeconds.
app.MapGet("/api/quotes", async (
    AppDbContext db,
    HybridCache cache,
    CacheMetrics cacheMetrics,
    CacheOptions cacheCfg,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken,
    int page = 1,
    int size = 10) =>
{
    var logger   = loggerFactory.CreateLogger("QuotesApi.Endpoints");
    var cacheKey = $"quotes:page:{page}:size:{size}";
    var dbCalled = false;

    var quotes = await cache.GetOrCreateAsync<List<QuoteResponse>>(
        cacheKey,
        async ct =>
        {
            dbCalled = true;
            cacheMetrics.RecordMiss("quote_list");

            logger.LogInformation(
                "Cache miss — fetching quotes page:{Page} size:{Size} from database.",
                page, size);

            var sw = Stopwatch.StartNew();
            var result = await db.Quotes
                .Where(q => !q.IsDeleted)
                .OrderByDescending(q => q.Id)
                .Skip((page - 1) * size)
                .Take(size)
                .Select(q => new QuoteResponse(q.Id, q.Author, q.Text, q.IsDeleted, q.CreatedByEmail))
                .ToListAsync(ct);
            sw.Stop();

            cacheMetrics.RecordDbQuery("quote_list", sw.Elapsed.TotalMilliseconds);
            return result;
        },
        new HybridCacheEntryOptions
        {
            Expiration           = TimeSpan.FromSeconds(cacheCfg.QuoteListTtlSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(cacheCfg.LocalCacheTtlSeconds),
        },
        cancellationToken: cancellationToken);

    if (!dbCalled)
        cacheMetrics.RecordHit("quote_list");

    return Results.Ok(quotes);
}).RequireAuthorization();


// Hot read — cached by quote ID.  Stampede protection: if 50 concurrent
// requests arrive for the same cold key, HybridCache calls the factory exactly
// once; the other 49 await the in-flight ValueTask.  Observe this in logs:
// only one "Cache miss" line appears regardless of concurrency.
app.MapGet("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    HybridCache cache,
    CacheMetrics cacheMetrics,
    CacheOptions cacheCfg,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger   = loggerFactory.CreateLogger("QuotesApi.Endpoints");
    var cacheKey = $"quote:{id}";
    var dbCalled = false;

    var quote = await cache.GetOrCreateAsync<QuoteResponse?>(
        cacheKey,
        async ct =>
        {
            dbCalled = true;
            cacheMetrics.RecordMiss("quote_by_id");

            logger.LogInformation(
                "Cache miss — fetching quote:{QuoteId} from database.", id);

            var sw = Stopwatch.StartNew();
            var result = await db.Quotes
                .Where(q => q.Id == id && !q.IsDeleted)
                .Select(q => new QuoteResponse(q.Id, q.Author, q.Text, q.IsDeleted, q.CreatedByEmail))
                .FirstOrDefaultAsync(ct);
            sw.Stop();

            cacheMetrics.RecordDbQuery("quote_by_id", sw.Elapsed.TotalMilliseconds);
            return result;
        },
        new HybridCacheEntryOptions
        {
            Expiration           = TimeSpan.FromSeconds(cacheCfg.QuoteByIdTtlSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(cacheCfg.LocalCacheTtlSeconds),
        },
        cancellationToken: cancellationToken);

    if (!dbCalled)
        cacheMetrics.RecordHit("quote_by_id");

    if (quote is null)
        return Results.NotFound();

    return Results.Ok(quote);
}).RequireAuthorization();



// soft-delete a quote by id — ownership enforced by DeleteOwnQuoteHandler.
// Evicts the cached entry so the next reader gets the deleted state immediately
// rather than waiting for the TTL to expire.
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    HybridCache cache,
    IAuthorizationService authService,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    var authResult = await authService.AuthorizeAsync(
        httpContext.User, quote, new DeleteOwnQuoteRequirement());

    if (!authResult.Succeeded)
        return Results.Forbid();

    quote.Delete();

    await db.SaveChangesAsync(cancellationToken);
    await cache.RemoveAsync($"quote:{id}", cancellationToken);

    return Results.Ok(new { message = "Quote deleted successfully" });
}).RequireAuthorization();


app.MapGet("/api/collections/{id}", async (
    int id,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(id, cancellationToken);

    if (collection is null)
        return Results.NotFound();

    return Results.Ok(collection);
}).RequireAuthorization();

app.MapPost("/api/collections", async (
    string name,
    int ownerId,
    IClock clock,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = new Collection(
        name,
        ownerId,
        clock);

    await repository.Add(
        collection,
        cancellationToken);

    return Results.Created(
        $"/api/collections/{collection.Id}",
        collection);
}).RequireAuthorization();

app.MapPost("/api/collections/{id}/items", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(
        id,
        cancellationToken);

    if (collection == null)
    {
        return Results.NotFound();
    }

    try
    {
        collection.AddItem(quoteId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 400);
    }

    await repository.Update(
        collection,
        cancellationToken);

    return Results.Ok(collection);
}).RequireAuthorization();

app.MapDelete("/api/collections/{id}/items/{quoteId}", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(
        id,
        cancellationToken);

    if (collection == null)
    {
        return Results.NotFound();
    }

    try
    {
        collection.RemoveItem(quoteId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 400);
    }

    await repository.Update(
        collection,
        cancellationToken);

    return Results.Ok(collection);
}).RequireAuthorization();

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AppDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users
        .FirstOrDefaultAsync(x => x.Email == request.Email, cancellationToken);

    if (user is null || !user.VerifyPassword(request.Password))
        return Results.Unauthorized();

    var rawToken = GenerateRefreshToken();
    var expiryDays = Convert.ToInt32(configuration["Jwt:RefreshExpiryDays"] ?? "7");

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = HashToken(rawToken),
        UserId = user.Id,
        FamilyId = Guid.NewGuid().ToString(),
        ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        access_token = CreateAccessToken(user, configuration),
        refresh_token = rawToken,
        expires_in = 900
    });
});

app.MapPost("/api/auth/refresh", async (
    RefreshRequest request,
    AppDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var tokenHash = HashToken(request.RefreshToken);

    var stored = await db.RefreshTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    if (stored is null || stored.IsExpired)
        return Results.Unauthorized();

    // Reuse detected: legitimate holder already rotated this token.
    // Revoke the entire family to protect both parties.
    if (stored.IsUsed)
    {
        await RevokeFamily(db, stored.FamilyId, cancellationToken);
        return Results.Unauthorized();
    }

    if (stored.IsRevoked)
        return Results.Unauthorized();

    // Rotate: mark old token as consumed, issue a fresh one in the same family.
    var newRaw = GenerateRefreshToken();
    var newHash = HashToken(newRaw);
    var expiryDays = Convert.ToInt32(configuration["Jwt:RefreshExpiryDays"] ?? "7");

    stored.ReplacedByToken = newHash;

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = newHash,
        UserId = stored.UserId,
        FamilyId = stored.FamilyId,
        ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        access_token = CreateAccessToken(stored.User, configuration),
        refresh_token = newRaw,
        expires_in = 900
    });
});




// ── Service Bus endpoints ──────────────────────────────────────────────────

// POST /api/messages/publish
// Publishes a normal "quote.created" event. Returns 202 immediately;
// subscribers process it asynchronously.
app.MapPost("/api/messages/publish", async (
    IServiceProvider  services,
    CancellationToken cancellationToken) =>
{
    var publisher = services.GetService<IMessagePublisher>();
    if (publisher is null)
        return Results.Problem(
            detail: "Service Bus is not configured. Set ServiceBus:ConnectionString.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var evt = new QuoteEvent(
        EventType: "quote.created",
        QuoteId:   Random.Shared.Next(1, 10000),
        Author:    "Marcus Aurelius");

    await publisher.PublishAsync(evt, cancellationToken);

    return Results.Accepted(value: new
    {
        status  = "published",
        topic   = sbOptions.TopicName,
        eventType = evt.EventType,
    });
});

// POST /api/messages/publish-poison
// Publishes a message with Poison=true. Every delivery attempt is abandoned
// by the consumer, so Service Bus retries MaxDeliveryCount times then moves
// the message to the Dead Letter Queue.
app.MapPost("/api/messages/publish-poison", async (
    IServiceProvider  services,
    CancellationToken cancellationToken) =>
{
    var publisher = services.GetService<IMessagePublisher>();
    if (publisher is null)
        return Results.Problem(
            detail: "Service Bus is not configured. Set ServiceBus:ConnectionString.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var evt = new QuoteEvent(
        EventType: "quote.poison",
        QuoteId:   null,
        Author:    null,
        Poison:    true);

    await publisher.PublishAsync(evt, cancellationToken);

    return Results.Accepted(value: new
    {
        status  = "published",
        poison  = true,
        message = "Message will be retried MaxDeliveryCount times then moved to the DLQ.",
    });
});

// ── Background task test endpoint ─────────────────────────────────────────
// Returns 202 immediately; the simulated work runs asynchronously in the
// QueuedHostedService loop on a background thread.
app.MapPost("/api/background/test", async (
    IBackgroundTaskQueue queue,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var jobId  = Guid.NewGuid().ToString("N")[..8];
    var logger = loggerFactory.CreateLogger("BackgroundJob");

    await queue.EnqueueAsync(async ct =>
    {
        logger.LogInformation(
            "Job {JobId} started (simulating 5 s of work).", jobId);

        // Simulate long-running work — respects the host shutdown token.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        logger.LogInformation(
            "Job {JobId} completed.", jobId);
    }, cancellationToken);

    return Results.Accepted(
        value: new { jobId, status = "queued", message = "Work item enqueued." });
});

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connStr))
    {
        // No SQL Server configured → SQLite local dev.
        // EnsureCreated() generates the schema from the model without migrations.
        db.Database.EnsureCreated();

        if (!db.Users.Any())
        {
            db.Users.Add(new User("admin@example.com", "password123"));
            db.SaveChanges();
        }
    }
}

app.Run();
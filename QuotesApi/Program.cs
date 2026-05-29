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
using System.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;


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

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSqlServer(
        "Server=tcp:pratiksha-sql-server-01.database.windows.net,1433;Initial Catalog=quotes-sql-db;Persist Security Info=False;User ID=pratiksha-quotesdb;Password=@Database123;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
    );

    options.EnableSensitiveDataLogging();
    options.LogTo(Console.WriteLine, LogLevel.Information);
});

builder.Services.AddScoped<
    ICollectionRepository,
    CollectionRepository>();

builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();
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



// get all quotes with pagination
app.MapGet("/api/quotes", async (
    AppDbContext db,
    CancellationToken cancellationToken,
    int page = 1,
    int size = 10) =>
{
    var quotes = await db.Quotes
        .Where(q => !q.IsDeleted)
        .OrderBy(q => q.Id)
        .Skip((page - 1) * size)
        .Take(size)
        .ToListAsync(cancellationToken);

    return Results.Ok(quotes);
}).RequireAuthorization();


app.MapGet("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    return Results.Ok(quote);
}).RequireAuthorization();



// soft-delete a quote by id — ownership enforced by DeleteOwnQuoteHandler
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
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




if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();

    var db = scope.ServiceProvider
        .GetRequiredService<AppDbContext>();

    // if (!db.Users.Any())
    // {
    //     db.Users.Add(new User(
    //         "admin@example.com",
    //         "password123"));

    //     db.SaveChanges();
    // }
}

using (var tempScope = builder.Services.BuildServiceProvider().CreateScope())
{
    var context = tempScope.ServiceProvider.GetRequiredService<AppDbContext>();

    // ---------------- TRACKED QUERY ----------------

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long trackedBefore = GC.GetAllocatedBytesForCurrentThread();

    var trackedWatch = Stopwatch.StartNew();

    var trackedQuotes = await context.Quotes
        .OrderBy(q => q.Id)
        .Take(10000)
        .ToListAsync();

    trackedWatch.Stop();

    long trackedAfter = GC.GetAllocatedBytesForCurrentThread();

    Console.WriteLine("==== TRACKED QUERY ====");
    Console.WriteLine($"Rows: {trackedQuotes.Count}");
    Console.WriteLine($"Time: {trackedWatch.ElapsedMilliseconds} ms");
    Console.WriteLine($"Allocated: {trackedAfter - trackedBefore} bytes");



    // ---------------- AS NO TRACKING QUERY ----------------

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long noTrackBefore = GC.GetAllocatedBytesForCurrentThread();

    var noTrackWatch = Stopwatch.StartNew();

    var noTrackQuotes = await context.Quotes
        .AsNoTracking()
        .OrderBy(q => q.Id)
        .Take(10000)
        .ToListAsync();

    noTrackWatch.Stop();

    long noTrackAfter = GC.GetAllocatedBytesForCurrentThread();

    Console.WriteLine("==== AS NO TRACKING QUERY ====");
    Console.WriteLine($"Rows: {noTrackQuotes.Count}");
    Console.WriteLine($"Time: {noTrackWatch.ElapsedMilliseconds} ms");
    Console.WriteLine($"Allocated: {noTrackAfter - noTrackBefore}");



    // ---------------- FULL ENTITY QUERY ----------------

    Console.WriteLine("==== FULL ENTITY QUERY ====");

    var fullQuotes = await context.Quotes
        .OrderBy(q => q.Id)
        .Take(5)
        .ToListAsync();



    // ---------------- PROJECTED DTO QUERY ----------------

    Console.WriteLine("==== PROJECTED DTO QUERY ====");

    var projectedQuotes = await context.Quotes
        .Select(q => new
        {
            q.Id,
            q.Author
        })
        .OrderBy(q => q.Id)
        .Take(5)
        .ToListAsync();



    // ---------------- CLIENT SIDE EVALUATION ----------------

    Console.WriteLine("==== CLIENT SIDE EVALUATION ====");

    bool IsLongAuthor(string author)
    {
        return author.Length > 10;
    }

    var clientEval = context.Quotes
        .AsEnumerable()
        .Where(q => IsLongAuthor(q.Author))
        .Take(5)
        .ToList();

    Console.WriteLine($"Client-side rows: {clientEval.Count}");
}


app.Run();
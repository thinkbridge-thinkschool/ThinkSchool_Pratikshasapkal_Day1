using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Abstractions;
using QuotesApi.Authorization;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Metrics;
using QuotesApi.Middleware;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Telemetry;
using QuotesApi.Utilities;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Serilog replaces the default Microsoft.Extensions.Logging infrastructure.
// ILogger<T> and ILoggerFactory still work — they route through Serilog automatically.
//
// Structured logging vs interpolated logging:
//   BAD  (interpolated): logger.LogInformation($"Created quote {id} for {email}");
//        → the message is a plain string; you cannot query on id or email separately.
//   GOOD (structured):   logger.LogInformation("Created quote {QuoteId} for {Email}", id, email);
//        → Serilog captures QuoteId and Email as first-class properties so log
//          management tools (Seq, Grafana Loki, Application Insights) can filter,
//          group, and alert on them: WHERE QuoteId = 42, GROUP BY Email, etc.
//
// Levels and sinks live in appsettings.json / appsettings.{env}.json so they can
// be changed without redeployment. appsettings.Development.json enables
// Microsoft.EntityFrameworkCore.Database.Command at Debug to show generated SQL.
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration));

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
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

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

    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = configuration["Jwt:Issuer"],
            ValidAudience = configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!))
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var log = ctx.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");

                var userId = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                log.LogInformation("Token validated UserId={UserId}", userId);

                return Task.CompletedTask;
            },

            OnAuthenticationFailed = ctx =>
            {
                var svc = ctx.HttpContext.RequestServices;
                var log = svc.GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");
                var metrics = svc.GetRequiredService<ApiMetrics>();

                if (ctx.Exception is SecurityTokenExpiredException)
                {
                    log.LogInformation("JWT expired Path={Path}", ctx.HttpContext.Request.Path.Value);
                    metrics.RecordJwtFailure("bearer", "expired");
                }
                else
                {
                    log.LogWarning(
                        "JWT validation failed ExceptionType={ExceptionType} Path={Path}",
                        ctx.Exception.GetType().Name,
                        ctx.HttpContext.Request.Path.Value);
                    metrics.RecordJwtFailure("bearer", "invalid");
                }

                return Task.CompletedTask;
            },

            // Fires when JwtBearer is asked to issue a 401 challenge.
            // AuthenticateFailure is non-null when a token was present but invalid —
            // OnAuthenticationFailed already logged that case, so we only log here
            // for the "no token provided at all" path to avoid duplicate lines.
            OnChallenge = ctx =>
            {
                if (ctx.AuthenticateFailure is null)
                {
                    var log = ctx.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");

                    log.LogWarning(
                        "No bearer token in request Path={Path}",
                        ctx.HttpContext.Request.Path.Value);
                }

                return Task.CompletedTask;
            }
        };
    })

    .AddJwtBearer("Entra", options =>
    {
        options.Authority =
            $"https://login.microsoftonline.com/{configuration["Entra:TenantId"]}/v2.0";

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidAudience = configuration["Entra:Audience"]
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                var svc = ctx.HttpContext.RequestServices;
                var log = svc.GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");
                var metrics = svc.GetRequiredService<ApiMetrics>();

                if (ctx.Exception is SecurityTokenExpiredException)
                {
                    log.LogInformation("Entra JWT expired Path={Path}", ctx.HttpContext.Request.Path.Value);
                    metrics.RecordJwtFailure("entra", "expired");
                }
                else
                {
                    log.LogWarning(
                        "Entra JWT validation failed ExceptionType={ExceptionType} Path={Path}",
                        ctx.Exception.GetType().Name,
                        ctx.HttpContext.Request.Path.Value);
                    metrics.RecordJwtFailure("entra", "invalid");
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("can-edit-quotes", policy =>
        policy.RequireClaim("scope", "quotes.write"));
});

builder.Services.AddScoped<IAuthorizationHandler, DeleteOwnQuoteHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, LoggingAuthorizationResultHandler>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSqlite("Data Source=quotes.db");
});

builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMetrics();
builder.Services.AddSingleton<ApiMetrics>();

// OpenTelemetry distributed tracing.
//
// Logs vs traces:
//   Logs (Serilog) record discrete events — "Quote created QuoteId=7".
//   Traces record causally-linked spans with start/end timing and a parent–child
//   hierarchy that shows the full call tree for a single request.
//   Both are correlated via the same 32-char W3C TraceId.
//
// Automatic instrumentation — zero application code:
//   AddAspNetCoreInstrumentation → one root span per HTTP request
//   AddEntityFrameworkCoreInstrumentation → one child span per EF query
//   AddHttpClientInstrumentation → one child span per outbound HTTP call (Entra OIDC)
//
// Custom instrumentation — AppActivitySource spans in endpoint handlers:
//   Business operations the frameworks can't observe (quote.create, token.rotate, …).
//   Each StartActivity() call creates a child of Activity.Current (the request span).
//
// Serilog TraceId alignment:
//   CorrelationIdMiddleware reads Activity.Current.TraceId, so every log line
//   carries the same ID as the OTel span — one search finds both logs and traces.
//
// Collector configuration (no code change needed):
//   OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317  (Jaeger / Grafana Tempo / Honeycomb)
//   OTEL_EXPORTER_OTLP_PROTOCOL=grpc                  (default)
//   OTEL_SERVICE_NAME=QuotesApi                        (overrides the value below)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: builder.Configuration["OpenTelemetry:ServiceName"] ?? "QuotesApi",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(tracing => tracing
        .AddSource(AppActivitySource.Name)          // custom business spans
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.RecordException = true;
        })
        .AddEntityFrameworkCoreInstrumentation()    // child span per EF query
        .AddHttpClientInstrumentation()             // child span for Entra calls
        .AddOtlpExporter());


string GenerateRefreshToken()
{
    var bytes = new byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));

    var jwtToken = new JwtSecurityToken(
        issuer: cfg["Jwt:Issuer"],
        audience: cfg["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(cfg["Jwt:ExpiryMinutes"])),
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

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

// Singletons captured for use in endpoint closures — all thread-safe.
// Loggers: Serilog LogContext.PushProperty in CorrelationIdMiddleware uses AsyncLocal
//   so every log within a request automatically carries TraceId.
// Metrics: aggregated across requests, never carry per-request identity.
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var quotesLog = loggerFactory.CreateLogger("QuotesApi.Quotes");
var authLog = loggerFactory.CreateLogger("QuotesApi.Auth");
var collectionsLog = loggerFactory.CreateLogger("QuotesApi.Collections");
var metrics = app.Services.GetRequiredService<ApiMetrics>();

// Pipeline order:
//   RequestMetrics (outermost) — captures full duration + final status code
//   ExceptionHandler           — converts unhandled exceptions to 500 before metrics reads status
//   CorrelationId              — pushes TraceId into Serilog LogContext for all subsequent logs
//   SerilogRequestLogging      — emits one structured line per request (with TraceId enriched)
//   Authentication / Authorization
app.UseMiddleware<RequestMetricsMiddleware>();
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is not null)
        {
            var exLog = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("QuotesApi.Exceptions");

            exLog.LogError(
                feature.Error,
                "Unhandled exception Method={Method} Path={Path}",
                context.Request.Method,
                context.Request.Path.Value);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    });
});

app.UseMiddleware<CorrelationIdMiddleware>();

// One structured line per request. Level is promoted to Warning for 4xx and Error for 5xx/exceptions.
// RouteTemplate uses the pattern ("/api/quotes/{id}") not the concrete path — keeps cardinality low.
app.UseSerilogRequestLogging(opts =>
{
    opts.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} → {StatusCode} [{RouteTemplate}] in {Elapsed:0.000}ms";

    opts.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error :
        ctx.Response.StatusCode >= 400 ? LogEventLevel.Warning :
        LogEventLevel.Information;

    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        var endpoint = ctx.GetEndpoint() as RouteEndpoint;
        diag.Set("RouteTemplate", endpoint?.RoutePattern.RawText ?? "unmatched");
    };
});

app.UseAuthentication();
app.UseAuthorization();


app.MapGet("/", () => "Quotes API Running");


app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var userEmail = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    // StartActivity returns null when no collector is listening — ?. makes every
    // tag call a no-op so there is zero overhead when tracing is disabled.
    // The using block records the span end time when the handler returns.
    using var activity = AppActivitySource.Instance.StartActivity("quote.create");
    activity?.SetTag("user.id", userId);
    activity?.SetTag("quote.author", request.Author);

    var result = Quote.Create(request.Author, request.Text, userEmail);

    if (!result.IsSuccess)
    {
        quotesLog.LogWarning(
            "Quote validation failed UserEmail={UserEmail} Error={ValidationError}",
            userEmail, result.Error);

        activity?.SetStatus(ActivityStatusCode.Error, result.Error);
        return Results.Problem(detail: result.Error, statusCode: 400);
    }

    db.Quotes.Add(result.Value!);
    await db.SaveChangesAsync(cancellationToken);

    activity?.SetTag("quote.id", result.Value!.Id);
    quotesLog.LogInformation(
        "Quote created QuoteId={QuoteId} Author={Author} CreatedBy={UserEmail}",
        result.Value!.Id, result.Value.Author, userEmail);
    metrics.RecordQuoteCreated();

    return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
}).RequireAuthorization("can-edit-quotes");


app.MapGet("/api/quotes", async (
    AppDbContext db,
    HttpContext httpContext,
    CancellationToken cancellationToken,
    int page = 1,
    int size = 10) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    using var activity = AppActivitySource.Instance.StartActivity("quotes.list");
    activity?.SetTag("user.id", userId);
    activity?.SetTag("page", page);
    activity?.SetTag("page.size", size);

    quotesLog.LogInformation(
        "Listing quotes UserId={UserId} Page={Page} Size={Size}", userId, page, size);

    var quotes = await db.Quotes
        .Where(q => !q.IsDeleted)
        .OrderBy(q => q.Id)
        .Skip((page - 1) * size)
        .Take(size)
        .ToListAsync(cancellationToken);

    activity?.SetTag("result.count", quotes.Count);
    quotesLog.LogInformation(
        "Returning {QuoteCount} quotes UserId={UserId} Page={Page}", quotes.Count, userId, page);

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


// Soft-delete — ownership enforced by DeleteOwnQuoteHandler, which logs denial
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    IAuthorizationService authService,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    using var activity = AppActivitySource.Instance.StartActivity("quote.delete");
    activity?.SetTag("quote.id", id);
    activity?.SetTag("user.id", userId);

    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    var authResult = await authService.AuthorizeAsync(
        httpContext.User, quote, new DeleteOwnQuoteRequirement());

    if (!authResult.Succeeded)
    {
        activity?.SetTag("authz.result", "denied");
        metrics.RecordAuthorizationFailure("quote");
        return Results.Forbid();
    }

    activity?.SetTag("authz.result", "allowed");
    var userEmail = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
    quote.Delete();
    await db.SaveChangesAsync(cancellationToken);

    quotesLog.LogInformation(
        "Quote deleted QuoteId={QuoteId} DeletedBy={UserEmail}",
        id, userEmail);
    metrics.RecordQuoteDeleted();

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
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = new Collection(name, ownerId);
    await repository.Add(collection, cancellationToken);

    collectionsLog.LogInformation(
        "Collection created CollectionId={CollectionId} Name={Name} OwnerId={OwnerId}",
        collection.Id, collection.Name, ownerId);

    return Results.Created($"/api/collections/{collection.Id}", collection);
}).RequireAuthorization();

app.MapPost("/api/collections/{id}/items", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    IClock clock,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(id, cancellationToken);

    if (collection == null)
        return Results.NotFound();

    try
    {
        collection.AddItem(quoteId, clock);
    }
    catch (InvalidOperationException ex)
    {
        collectionsLog.LogWarning(
            "Add item rejected CollectionId={CollectionId} QuoteId={QuoteId} Reason={Reason}",
            id, quoteId, ex.Message);

        return Results.Problem(detail: ex.Message, statusCode: 400);
    }

    await repository.Update(collection, cancellationToken);

    collectionsLog.LogInformation(
        "Item added CollectionId={CollectionId} QuoteId={QuoteId}",
        id, quoteId);

    return Results.Ok(collection);
}).RequireAuthorization();

app.MapDelete("/api/collections/{id}/items/{quoteId}", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(id, cancellationToken);

    if (collection == null)
        return Results.NotFound();

    try
    {
        collection.RemoveItem(quoteId);
    }
    catch (InvalidOperationException ex)
    {
        collectionsLog.LogWarning(
            "Remove item rejected CollectionId={CollectionId} QuoteId={QuoteId} Reason={Reason}",
            id, quoteId, ex.Message);

        return Results.Problem(detail: ex.Message, statusCode: 400);
    }

    await repository.Update(collection, cancellationToken);

    collectionsLog.LogInformation(
        "Item removed CollectionId={CollectionId} QuoteId={QuoteId}",
        id, quoteId);

    return Results.Ok(collection);
}).RequireAuthorization();


app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AppDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    using var activity = AppActivitySource.Instance.StartActivity("auth.login");

    var user = await db.Users
        .FirstOrDefaultAsync(x => x.Email == request.Email, cancellationToken);

    if (user is null || !user.VerifyPassword(request.Password))
    {
        // Deliberately don't distinguish "not found" from "wrong password" —
        // same log, same response — prevents account enumeration.
        authLog.LogWarning("Login failed Email={Email}", request.Email);
        metrics.RecordLogin("failed");
        activity?.SetTag("auth.result", "failed");
        return Results.Unauthorized();
    }

    var familyId = Guid.NewGuid().ToString();
    var rawToken = GenerateRefreshToken();
    var expiryDays = Convert.ToInt32(configuration["Jwt:RefreshExpiryDays"] ?? "7");

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = HashToken(rawToken),
        UserId = user.Id,
        FamilyId = familyId,
        ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
    });

    await db.SaveChangesAsync(cancellationToken);

    authLog.LogInformation(
        "Login succeeded UserId={UserId} FamilyId={FamilyId}",
        user.Id, familyId);
    metrics.RecordLogin("success");
    activity?.SetTag("auth.result", "success");
    activity?.SetTag("user.id", user.Id);

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
    using var activity = AppActivitySource.Instance.StartActivity("token.rotate");

    var tokenHash = HashToken(request.RefreshToken);

    var stored = await db.RefreshTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    if (stored is null)
    {
        authLog.LogWarning("Refresh token not found");
        metrics.RecordTokenRefresh("not_found");
        activity?.SetTag("result", "not_found");
        return Results.Unauthorized();
    }

    activity?.SetTag("user.id", stored.UserId);
    activity?.SetTag("token.family_id", stored.FamilyId);

    if (stored.IsExpired)
    {
        authLog.LogInformation(
            "Refresh token expired UserId={UserId} FamilyId={FamilyId}",
            stored.UserId, stored.FamilyId);
        metrics.RecordTokenRefresh("expired");
        activity?.SetTag("result", "expired");
        return Results.Unauthorized();
    }

    if (stored.IsUsed)
    {
        authLog.LogWarning(
            "Refresh token reuse detected — revoking family UserId={UserId} FamilyId={FamilyId}",
            stored.UserId, stored.FamilyId);
        metrics.RecordTokenRefresh("reuse_detected");
        activity?.SetTag("result", "reuse_detected");
        await RevokeFamily(db, stored.FamilyId, cancellationToken);
        return Results.Unauthorized();
    }

    if (stored.IsRevoked)
    {
        authLog.LogWarning(
            "Refresh token already revoked UserId={UserId} FamilyId={FamilyId}",
            stored.UserId, stored.FamilyId);
        metrics.RecordTokenRefresh("revoked");
        activity?.SetTag("result", "revoked");
        return Results.Unauthorized();
    }

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

    authLog.LogInformation(
        "Token rotated UserId={UserId} FamilyId={FamilyId}",
        stored.UserId, stored.FamilyId);
    metrics.RecordTokenRefresh("rotated");
    activity?.SetTag("result", "rotated");

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

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (!db.Users.Any())
    {
        db.Users.Add(new User("admin@example.com", "password123"));
        db.SaveChanges();
    }
}

app.Run();

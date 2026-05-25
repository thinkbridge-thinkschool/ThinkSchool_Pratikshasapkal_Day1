using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Abstractions;
using QuotesApi.Authorization;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Metrics;
using QuotesApi.Middleware;
using QuotesApi.Models;
using QuotesApi.Options;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Telemetry;
using QuotesApi.Utilities;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

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

// ── Options: early bindings ───────────────────────────────────────────────────
// Read typed options from configuration before the DI container is built.
// These local variables are used only during service registration below.
// At runtime, endpoints and services receive options via IOptionsSnapshot / IOptionsMonitor.
//
// Configuration precedence (highest → lowest):
//   1. Environment variables          (Jwt__Key, KeyVault__Uri, …)
//   2. appsettings.{Environment}.json (appsettings.Testing.json overrides the test JWT key)
//   3. appsettings.json               (non-secret defaults)
//   4. user-secrets                   (Development only — run: dotnet user-secrets set "Jwt:Key" "…")
//
// See CONFIGURATION.md for full setup instructions.
var jwtCfg   = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>()           ?? new JwtOptions();
var entraCfg = builder.Configuration.GetSection(EntraOptions.Section).Get<EntraOptions>()       ?? new EntraOptions();
var otelCfg  = builder.Configuration.GetSection(OpenTelemetryOptions.Section).Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();
var kvCfg    = builder.Configuration.GetSection(KeyVaultOptions.Section).Get<KeyVaultOptions>() ?? new KeyVaultOptions();

// ── Options: DI registration + validation ────────────────────────────────────
// JwtOptions: required at startup — ValidateOnStart() fails fast before the first
// request if Jwt:Key is missing or too short.  All other sections are optional.
builder.Services
    .AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// IOptionsSnapshot<T>  → scoped  (new snapshot per HTTP request; used in endpoints)
// IOptionsMonitor<T>   → singleton (change-notification capable; used in singletons)
// Both are registered automatically by Configure<T> / AddOptions<T>.BindConfiguration.
builder.Services.Configure<EntraOptions>(
    builder.Configuration.GetSection(EntraOptions.Section));

builder.Services.Configure<KeyVaultOptions>(
    builder.Configuration.GetSection(KeyVaultOptions.Section));

// IOptionsMonitor<OpenTelemetryOptions> is available to any singleton that needs
// the service name at runtime (e.g. health-check responses, metrics labels).
builder.Services
    .AddOptions<OpenTelemetryOptions>()
    .BindConfiguration(OpenTelemetryOptions.Section);

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
        // jwtCfg is the early-bound snapshot used only at service-registration time.
        // At request time the signing key is already baked into the JwtBearerOptions
        // registered here; key rotation requires an app restart (acceptable trade-off
        // for a symmetric key scheme).
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtCfg.Issuer,
            ValidAudience            = jwtCfg.Audience,
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtCfg.Key))
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
                var svc     = ctx.HttpContext.RequestServices;
                var log     = svc.GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");
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
        // entraCfg.Authority and entraCfg.Audience are derived from the early binding.
        // Entra is optional: if EntraOptions.IsConfigured is false the "Entra" scheme
        // simply never matches any token (the DynamicScheme selector routes away from it).
        options.Authority = entraCfg.Authority;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer    = true,
            ValidateAudience  = true,
            ValidateLifetime  = true,
            ValidAudience     = entraCfg.Audience
        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                var svc     = ctx.HttpContext.RequestServices;
                var log     = svc.GetRequiredService<ILoggerFactory>().CreateLogger("QuotesApi.Auth");
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

// ── SQLite path resolution ────────────────────────────────────────────────
// "Data Source=quotes.db" is a bare relative path. In a Linux container the
// working directory is often / (root), which the non-root app user cannot
// write to → SQLite Error 14: 'unable to open database file'.
//
// Fix strategy:
//   1. DOTNET_RUNNING_IN_CONTAINER=true is injected by every aspnet base image.
//      When true, use /tmp/quotesapi-data — always writable by any Linux user,
//      regardless of which UID the container process runs as.
//   2. Outside containers, use AppContext.BaseDirectory/data — absolute,
//      consistent, and never inside the source tree.
//   3. Allow full override via ConnectionStrings__Default so production can
//      mount a persistent volume at any path without rebuilding the image.
//   4. Always create the parent directory of whichever path is in use,
//      including a custom path supplied via the env var.
var isContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
var defaultDbDir = isContainer
    ? "/tmp/quotesapi-data"
    : Path.Combine(AppContext.BaseDirectory, "data");
var defaultConnStr = $"Data Source={Path.Combine(defaultDbDir, "quotes.db")}";
var connectionString = builder.Configuration.GetConnectionString("Default") ?? defaultConnStr;

// Parse the final data source and ensure its parent directory exists.
// This covers both the computed default and any custom path from config/env.
var parsedSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrEmpty(parsedSource) && parsedSource != ":memory:")
{
    var dbParentDir = Path.GetDirectoryName(Path.GetFullPath(parsedSource));
    if (!string.IsNullOrEmpty(dbParentDir))
        Directory.CreateDirectory(dbParentDir);
}

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseSqlite(connectionString);
});

builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMetrics();
builder.Services.AddSingleton<ApiMetrics>();
// Singleton: uses IOptions<JwtOptions> (frozen at startup), so singleton lifetime is correct.
builder.Services.AddSingleton<ITokenService, TokenService>();

// ── Outbound HTTP: Quote Enrichment Service ───────────────────────────────────
//
// Scoped: IHttpClientFactory (singleton) is safe to inject into scoped services.
// The factory creates a new HttpClient wrapper per call but shares the pooled
// SocketsHttpHandler so neither socket exhaustion nor DNS staleness can occur.
builder.Services.AddScoped<IQuoteEnrichmentService, QuoteEnrichmentService>();

// Named HttpClient "quote-enrichment" with a four-layer Polly v8 resilience pipeline.
//
// PIPELINE EXECUTION ORDER (outer → inner; each layer wraps everything inside it):
//
//  ┌──────────────────────────────────────────────────────────────────────────┐
//  │  Layer 1 — Total request timeout  (10 s)                                 │
//  │    Hard ceiling for the ENTIRE operation including all retries.           │
//  │    TimeoutRejectedException propagates if the budget is exceeded anywhere.│
//  │  ┌────────────────────────────────────────────────────────────────────┐  │
//  │  │  Layer 2 — Retry  (3×, exponential back-off, jitter)              │  │
//  │  │    Re-runs layers 3 + 4 + the HTTP call on transient failure.     │  │
//  │  │    Handles: HttpRequestException, 5xx, 408, 429.                  │  │
//  │  │    Does NOT handle BrokenCircuitException → it propagates out.    │  │
//  │  │  ┌──────────────────────────────────────────────────────────────┐ │  │
//  │  │  │  Layer 3 — Circuit Breaker  (50% / 30 s window)             │ │  │
//  │  │  │    Counts every attempt (including retries) toward the rate. │ │  │
//  │  │  │    OPEN state: immediately throws BrokenCircuitException     │ │  │
//  │  │  │    → retry does NOT catch it → propagates to caller (503).   │ │  │
//  │  │  │  ┌────────────────────────────────────────────────────────┐  │ │  │
//  │  │  │  │  Layer 4 — Per-attempt timeout  (3 s)                  │  │ │  │
//  │  │  │  │    Caps each individual HTTP round-trip.               │  │ │  │
//  │  │  │  │    TimeoutRejectedException → caught by retry layer    │  │ │  │
//  │  │  │  │    → counts as transient failure → next retry.         │  │ │  │
//  │  │  │  │  ┌──────────────────────────────────────────────────┐  │  │ │  │
//  │  │  │  │  │  Actual HTTP call                                │  │  │ │  │
//  │  │  │  │  └──────────────────────────────────────────────────┘  │  │ │  │
//  │  │  │  └────────────────────────────────────────────────────────┘  │ │  │
//  │  │  └──────────────────────────────────────────────────────────────┘ │  │
//  │  └────────────────────────────────────────────────────────────────────┘  │
//  └──────────────────────────────────────────────────────────────────────────┘
//
// COMPATIBILITY WITH OpenTelemetry HttpClient instrumentation:
//   AddHttpClientInstrumentation() hooks into SocketsHttpHandler via DiagnosticListener.
//   Polly adds DelegatingHandlers on top of that handler — OTel still sees every
//   actual HTTP call, and each retry attempt produces its own OTel span.
//   All spans share the same W3C TraceId (from Activity.Current) so retry spans
//   appear as siblings under the parent request span in Jaeger / App Insights.
builder.Services
    .AddHttpClient("quote-enrichment", client =>
    {
        // BaseAddress comes from config; fall back to a placeholder so startup
        // always succeeds.  Override per environment:
        //   appsettings.Production.json  →  "QuoteEnrichment": { "BaseUrl": "https://…" }
        //   env var                      →  QuoteEnrichment__BaseUrl=https://…
        client.BaseAddress = new Uri(
            builder.Configuration["QuoteEnrichment:BaseUrl"] ?? "https://api.example.com");

        client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Disable HttpClient's own 100 s timeout.
        // Polly manages all timeouts via AddTimeout() below.
        // Without this line, the default timeout races with (and can beat) the
        // Polly total timeout, producing a confusing TaskCanceledException instead
        // of the expected TimeoutRejectedException.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .AddResilienceHandler(
        "quote-enrichment-pipeline",
        // The second overload provides ResilienceHandlerContext.ServiceProvider so
        // we can resolve the logger from DI at pipeline-construction time.
        // The pipeline instance is created once per named-client registration,
        // not per request, so this is safe for singleton-scoped services.
        (pipeline, context) =>
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("QuotesApi.HttpResilience");

            // ── Layer 1: Total request timeout ─────────────────────────────────
            // Wraps everything.  If the complete operation (all retries included)
            // takes longer than 10 s, TimeoutRejectedException is thrown.
            pipeline.AddTimeout(TimeSpan.FromSeconds(10));

            // ── Layer 2: Retry with exponential back-off + jitter ───────────────
            // HttpRetryStrategyOptions pre-configures ShouldHandle to catch:
            //   HttpRequestException  (network errors, DNS failures, TLS errors)
            //   HTTP 5xx              (server-side errors)
            //   HTTP 408              (request timeout)
            //   HTTP 429              (rate limited — respects Retry-After header)
            //
            // Back-off schedule (base 1 s, Exponential, with random jitter ±50%):
            //   Attempt 1 fails → wait ~1 s  → retry 1
            //   Retry 1 fails   → wait ~2 s  → retry 2
            //   Retry 2 fails   → wait ~4 s  → retry 3
            //   Retry 3 fails   → HttpRequestException propagates to caller
            //
            // Total retry overhead ≈ 1 + 2 + 4 = 7 s worst-case (within the 10 s
            // total budget; actual delays are shorter due to jitter).
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType      = DelayBackoffType.Exponential,
                UseJitter        = true,
                Delay            = TimeSpan.FromSeconds(1),

                // Called before each retry delay begins.
                // AttemptNumber is 0-indexed: 0 = first retry, 1 = second, …
                OnRetry = args =>
                {
                    // These log lines correlate with the OTel spans emitted by
                    // AddHttpClientInstrumentation() for each attempt — same TraceId.
                    logger.LogWarning(
                        "HTTP retry {Attempt}/{Max} in {DelayMs:N0} ms — {Reason}",
                        args.AttemptNumber + 1,
                        3,
                        args.RetryDelay.TotalMilliseconds,
                        args.Outcome.Exception?.Message
                            ?? $"HTTP {(int?)args.Outcome.Result?.StatusCode}");

                    return default; // ValueTask; no async work needed here
                }
            });

            // ── Layer 3: Circuit Breaker ────────────────────────────────────────
            // Prevents the API from hammering a downstream service that is
            // clearly unavailable (avoids thundering-herd / retry storms).
            //
            // State machine:
            //
            //   CLOSED ──(≥50% failures in 30 s window, min 5 requests)──► OPEN
            //     ↑                                                           │
            //     └──(probe succeeds)── HALF-OPEN ◄──(break duration 15 s)───┘
            //                               │
            //                         (probe fails)
            //                               │
            //                             OPEN (reset timer)
            //
            // MinimumThroughput = 5:  the circuit cannot open until at least 5
            //   requests have been observed in the sampling window.  Prevents
            //   the CB from tripping on a single slow startup request.
            //
            // BreakDuration = 15 s: how long the circuit stays OPEN before one
            //   probe request is allowed through (HALF-OPEN state).
            //
            // The CB is INSIDE the retry so that:
            //   • Each retry attempt counts toward the failure rate (realistic signal).
            //   • When the circuit opens, BrokenCircuitException propagates through
            //     the retry layer (not in ShouldHandle) → immediate fail-fast, no
            //     more retry delays, no wasted wait time.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                SamplingDuration  = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                FailureRatio      = 0.5,
                BreakDuration     = TimeSpan.FromSeconds(15),

                OnOpened = args =>
                {
                    logger.LogError(
                        "Circuit breaker OPENED for {BreakSec:N0} s — " +
                        "failure rate exceeded 50% in the last 30 s window. " +
                        "All calls will fast-fail until the circuit half-opens.",
                        args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = args =>
                {
                    logger.LogInformation(
                        "Circuit breaker CLOSED — downstream service has recovered.");
                    return default;
                },
                OnHalfOpened = args =>
                {
                    logger.LogInformation(
                        "Circuit breaker HALF-OPEN — sending one probe request.");
                    return default;
                }
            });

            // ── Layer 4: Per-attempt timeout ────────────────────────────────────
            // Caps each individual HTTP round-trip to 3 s, independently of the
            // outer 10 s total budget.
            //
            // Interaction with retry:
            //   TimeoutRejectedException thrown here propagates to the retry layer.
            //   The retry layer does NOT catch TimeoutRejectedException in its
            //   default ShouldHandle, so the exception propagates outward.
            //
            // If you want slow responses to be retried, change ShouldHandle in the
            // retry layer to also handle TimeoutRejectedException.
            pipeline.AddTimeout(TimeSpan.FromSeconds(3));
        });

// OpenTelemetry + Azure Monitor / Application Insights.
//
// Logs vs traces:
//   Logs (Serilog) record discrete events — "Quote created QuoteId=7".
//   Traces record causally-linked spans with start/end timing and a parent–child
//   hierarchy that shows the full call tree for a single request.
//   Both are correlated via the same 32-char W3C TraceId (see CorrelationIdMiddleware).
//
// Automatic instrumentation — zero application code:
//   AddAspNetCoreInstrumentation → one root span per HTTP request
//   AddEntityFrameworkCoreInstrumentation → one child span per EF query
//   AddHttpClientInstrumentation → one child span per outbound HTTP call (Entra OIDC)
//
// Custom instrumentation — AppActivitySource spans in endpoint handlers:
//   Business operations the frameworks cannot observe (quote.create, token.rotate, …).
//   Each StartActivity() call creates a child of Activity.Current (the request span).
//
// Azure Monitor connection string — never hardcoded, loaded in priority order:
//   1. Azure Key Vault  (KeyVault:Uri set in config → secret "appinsights-connection-string")
//   2. Environment variable  APPLICATIONINSIGHTS_CONNECTION_STRING  (staging / CI)
//   3. Absent → UseAzureMonitor() is skipped; OTLP (Jaeger/Tempo) still works
//
// DefaultAzureCredential resolution order:
//   In Azure (App Service / AKS): ManagedIdentityCredential  — zero config needed
//   Locally: AzureCliCredential  — run `az login` once
//
// Local development:
//   Set OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 to also send to Jaeger/Tempo.
//   Leave unset in production — OTLP exporter fails silently if no collector is reachable.

// ── Load App Insights connection string from Key Vault ────────────────────
// kvCfg is the early-bound KeyVaultOptions; at runtime IOptionsMonitor<KeyVaultOptions>
// is available in any singleton service that needs to re-check the URI.
var appInsightsCs = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

if (string.IsNullOrEmpty(appInsightsCs) && kvCfg.IsConfigured)
{
    try
    {
        var kvClient = new SecretClient(new Uri(kvCfg.Uri), new DefaultAzureCredential());
        appInsightsCs = kvClient.GetSecret("appinsights-connection-string").Value.Value;
    }
    catch (Exception ex)
    {
        // Non-fatal: telemetry gaps are preferable to crashing the application.
        // Serilog is not yet configured here so write to stderr directly.
        Console.Error.WriteLine(
            $"[WARN] Key Vault secret load failed — Azure Monitor inactive: {ex.Message}");
    }
}

// ── Classic Application Insights SDK ─────────────────────────────────────────
// WHY THIS EXISTS alongside UseAzureMonitor() below:
//
// Azure.Monitor.OpenTelemetry.AspNetCore is an OTel "distro" — it internally
// pins specific OTel SDK versions.  This project also references
// OpenTelemetry.Instrumentation.* 1.15.x packages at HIGHER versions than the
// distro was built against.  NuGet resolves all OTel packages to the higher
// version, which silently breaks the distro's TRACE export path (metrics use a
// separate path and still arrive).  Result: the 'requests' table stays empty
// even though customMetric / http.server.active_requests appear fine.
//
// AddApplicationInsightsTelemetry() bypasses OTel entirely.  It registers
// RequestTrackingTelemetryModule, which hooks into ASP.NET Core's DiagnosticSource
// pipeline and emits RequestTelemetry directly via TelemetryClient →
// Application Insights 'requests' table.  This makes the following KQL work:
//
//   requests
//   | where timestamp > ago(30m)
//   | summarize count(), p50=percentile(duration,50), p99=percentile(duration,99) by name
//   | order by p99 desc
//
// Coexistence with the OTel pipeline:
//   • AI SDK  → requests, dependencies, exceptions tables  (reliable, classic schema)
//   • OTel    → customMetrics (http.server.active_requests, etc.), custom spans
//   No duplication in 'requests': the OTel trace→requests path is broken, so
//   only the AI SDK writes there.
//
// Connection string: reuses appInsightsCs already resolved from Key Vault /
// APPLICATIONINSIGHTS_CONNECTION_STRING env var above — no second lookup needed.
if (!string.IsNullOrEmpty(appInsightsCs))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
        options.ConnectionString = appInsightsCs);
}

// UseAzureMonitor() throws at startup when no connection string is available,
// so gate it explicitly. When inactive, OTLP (Jaeger/Tempo) is still exported.
var otelBuilder = builder.Services.AddOpenTelemetry();

if (!string.IsNullOrEmpty(appInsightsCs))
    otelBuilder.UseAzureMonitor(opts => opts.ConnectionString = appInsightsCs);

otelBuilder
    .ConfigureResource(r => r.AddService(
        serviceName:    otelCfg.ServiceName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(tracing => tracing
        .AddSource(AppActivitySource.Name)
        .AddAspNetCoreInstrumentation(opts => { opts.RecordException = true; })
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());   // local Jaeger/Tempo; no-op if OTEL_EXPORTER_OTLP_ENDPOINT unset


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
var loggerFactory   = app.Services.GetRequiredService<ILoggerFactory>();
var quotesLog       = loggerFactory.CreateLogger("QuotesApi.Quotes");
var authLog         = loggerFactory.CreateLogger("QuotesApi.Auth");
var collectionsLog  = loggerFactory.CreateLogger("QuotesApi.Collections");
var metrics         = app.Services.GetRequiredService<ApiMetrics>();

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

// Health endpoint — used by Docker health checks and orchestrators
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));


app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var userId    = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
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

    // ── Batched collection-count query (N+1 fix) ─────────────────────────────
    // Single IN (...) + GROUP BY replaces N individual COUNT round-trips.
    // Regardless of page size, exactly ONE child EF Core span is emitted.
    //
    // DB round-trips per request (page size = 10):
    //   Before fix: 11  (1 paged SELECT + 10 COUNT queries)
    //   After fix:   2  (1 paged SELECT + 1 GROUP BY SELECT)
    //
    // Span "quotes.collection-counts" tag strategy=batched confirms the fix in Jaeger.
    var quoteIds = quotes.Select(q => q.Id).ToList();

    using var countActivity = AppActivitySource.Instance.StartActivity("quotes.collection-counts");
    countActivity?.SetTag("strategy", "batched");
    countActivity?.SetTag("quote.count", quotes.Count);
    countActivity?.SetTag("queries.issued", 1);

    var collectionCounts = await db.Collections
        .SelectMany(c => c.Items)
        .Where(i => quoteIds.Contains(i.QuoteId))
        .GroupBy(i => i.QuoteId)
        .Select(g => new { QuoteId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.QuoteId, x => x.Count, cancellationToken);

    var results = quotes
        .Select(q => new QuoteWithCollectionCount(
            q.Id, q.Author, q.Text, q.CreatedByEmail, q.IsDeleted,
            collectionCounts.GetValueOrDefault(q.Id, 0)))
        .ToList();
    // ── end batched fix ───────────────────────────────────────────────────────

    activity?.SetTag("result.count", results.Count);
    quotesLog.LogInformation(
        "Returning {QuoteCount} quotes UserId={UserId} Page={Page}", results.Count, userId, page);

    return Results.Ok(results);
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


// ── GET /api/quotes/{id}/enriched ─────────────────────────────────────────────
// Demo endpoint: fetches a quote then calls the external enrichment service via
// the resilient HttpClient to add sentiment / keyword metadata.
//
// Resilience exception → HTTP status mapping:
//
//   BrokenCircuitException   → 503 Service Unavailable  (circuit OPEN; downstream down)
//   TimeoutRejectedException → 504 Gateway Timeout      (total or per-attempt budget hit)
//   HttpRequestException     → 502 Bad Gateway           (retries exhausted or HTTP error)
//   OperationCanceledException → re-thrown               (caller cancelled; ASP.NET returns 499)
//
// Trace visibility in Jaeger / Application Insights:
//   Each retry attempt produces a child HttpClient span under this request's root span.
//   The retry log lines ("HTTP retry 1/3 …") carry the same TraceId, so you can
//   cross-reference OTel spans with structured log entries in KQL:
//
//     traces
//     | where customDimensions.TraceId == "<id>"
//     | order by timestamp asc
app.MapGet("/api/quotes/{id}/enriched", async (
    int id,
    AppDbContext db,
    IQuoteEnrichmentService enrichment,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    try
    {
        var result = await enrichment.EnrichAsync(quote.Text, cancellationToken);

        return Results.Ok(new
        {
            quote.Id,
            quote.Author,
            quote.Text,
            result.Sentiment,
            result.Confidence,
            result.Keywords
        });
    }
    catch (Polly.CircuitBreaker.BrokenCircuitException)
    {
        // Circuit is OPEN — downstream rejected immediately without an HTTP call.
        // Retry again after the break duration (15 s) has elapsed.
        return Results.Problem(
            detail: "Enrichment service is temporarily unavailable. Please retry shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Polly.Timeout.TimeoutRejectedException)
    {
        // Either the 10 s total budget or the 3 s per-attempt timeout was exceeded.
        return Results.Problem(
            detail: "Enrichment service did not respond in time.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException ex)
    {
        // All 3 retries were exhausted, or a non-transient HTTP error occurred.
        quotesLog.LogError(ex,
            "Enrichment service unavailable after retries QuoteId={QuoteId}", id);

        return Results.Problem(
            detail: $"Enrichment service failed: {ex.Message}",
            statusCode: StatusCodes.Status502BadGateway);
    }
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


// ── Auth endpoints ────────────────────────────────────────────────────────────
// Both endpoints inject IOptionsSnapshot<JwtOptions> — the scoped (per-request)
// variant — so that configuration changes take effect on the next request
// without requiring an app restart.

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AppDbContext db,
    ITokenService tokens,
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
    var rawToken = tokens.GenerateRefreshToken();

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = tokens.HashToken(rawToken),
        UserId    = user.Id,
        FamilyId  = familyId,
        ExpiresAt = DateTime.UtcNow.Add(tokens.RefreshTokenLifetime)
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
        access_token  = tokens.CreateAccessToken(user),
        refresh_token = rawToken,
        expires_in    = (int)tokens.AccessTokenLifetime.TotalSeconds
    });
});

app.MapPost("/api/auth/refresh", async (
    RefreshRequest request,
    AppDbContext db,
    ITokenService tokens,
    CancellationToken cancellationToken) =>
{
    using var activity = AppActivitySource.Instance.StartActivity("token.rotate");

    var tokenHash = tokens.HashToken(request.RefreshToken);

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

    var newRaw  = tokens.GenerateRefreshToken();
    var newHash = tokens.HashToken(newRaw);

    stored.ReplacedByToken = newHash;

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = newHash,
        UserId    = stored.UserId,
        FamilyId  = stored.FamilyId,
        ExpiresAt = DateTime.UtcNow.Add(tokens.RefreshTokenLifetime)
    });

    await db.SaveChangesAsync(cancellationToken);

    authLog.LogInformation(
        "Token rotated UserId={UserId} FamilyId={FamilyId}",
        stored.UserId, stored.FamilyId);
    metrics.RecordTokenRefresh("rotated");
    activity?.SetTag("result", "rotated");

    return Results.Ok(new
    {
        access_token  = tokens.CreateAccessToken(stored.User),
        refresh_token = newRaw,
        expires_in    = (int)tokens.AccessTokenLifetime.TotalSeconds
    });
});


// ════════════════════════════════════════════════════════════════════════════
// ⚠  TEMPORARY — DEVELOPMENT/TESTING ONLY  ⚠
//
//  GET /api/test/resilience
//
//  Purpose:
//    A no-auth endpoint that intentionally fires EnrichAsync("test") against
//    the configured BaseUrl ("https://api.example.com" unless overridden).
//    Because the target is unreachable the Polly resilience pipeline activates
//    every time, letting you observe retry logs, circuit-breaker state changes,
//    and timeout behaviour live.
//
//  HOW TO REMOVE:
//    Delete this block (between the two ════ banners) before going to production.
//    The surrounding production endpoints are NOT affected.
//
//  How to trigger each resilience behaviour:
//
//    Retries + exponential back-off
//      → Call the endpoint once with BaseUrl pointing to a non-existent host.
//         Watch the console/Application Insights for:
//           WARN  HTTP retry 1/3 in ~1000 ms — …
//           WARN  HTTP retry 2/3 in ~2000 ms — …
//           WARN  HTTP retry 3/3 in ~4000 ms — …
//         The endpoint returns 502 once all retries are exhausted.
//
//    Circuit Breaker opening (50 % failure rate, ≥5 requests in 30 s window)
//      → Hit the endpoint ≥5 times quickly (curl loop / k6 / hey).
//         After the 5th failure you will see:
//           ERROR Circuit breaker OPENED for 15 s …
//         Subsequent calls return 503 immediately — no HTTP attempt, no wait.
//
//    Circuit Breaker closing (HALF-OPEN probe)
//      → Wait 15 s after the circuit opened, then send one more request.
//         If it fails: circuit re-opens.
//         Watch for:
//           INFO  Circuit breaker HALF-OPEN — sending one probe request.
//           INFO  Circuit breaker CLOSED — downstream service has recovered.
//
//    Total timeout (10 s budget including retries)
//      → Point QuoteEnrichment__BaseUrl at a slow host that never responds.
//         After 10 s you will see TimeoutRejectedException → 504.
//
//    Per-attempt timeout (3 s per round-trip)
//      → Same as above but with a host that accepts the connection then stalls.
//         Each individual attempt times out after 3 s; the retry adds jitter and
//         tries again until the 10 s total budget is consumed.
//
//  How to observe logs:
//    Local:       dotnet run  → structured console output (Serilog)
//    Azure ACA:   az containerapp logs show -n quotes-api -g <rg> --follow
//    App Insights: traces | where message startswith "HTTP retry"
//                          | order by timestamp asc
//
//  How to run a quick loop to trip the circuit breaker (bash / PowerShell):
//    for i in {1..8}; do curl -s -o /dev/null -w "%{http_code}\n" \
//        https://<host>/api/test/resilience; done
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/api/test/resilience", async (
    IQuoteEnrichmentService enrichment,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    // Resolve a local logger so we can add context around the call.
    var log = loggerFactory.CreateLogger("QuotesApi.ResilienceTest");

    log.LogInformation(
        "Resilience test: calling EnrichAsync(\"test\") against BaseUrl {BaseUrl}. " +
        "Expect retries, circuit-breaker events, and/or timeout logs below.",
        "QuoteEnrichment:BaseUrl");

    try
    {
        // Calls the outbound HttpClient "quote-enrichment" which is backed by the
        // four-layer Polly pipeline:
        //   Total timeout (10 s) → Retry (3×) → Circuit Breaker (50%/30 s) → Per-attempt timeout (3 s)
        //
        // With BaseUrl = "https://api.example.com" (the default placeholder) the
        // connection will be refused/timed-out, so the full retry cycle runs.
        var result = await enrichment.EnrichAsync("test", cancellationToken);

        // Reached only if BaseUrl points to a real service that returns valid JSON.
        log.LogInformation(
            "Resilience test SUCCEEDED Sentiment={Sentiment} Confidence={Confidence}",
            result.Sentiment, result.Confidence);

        return Results.Ok(new
        {
            status     = "succeeded",
            sentiment  = result.Sentiment,
            confidence = result.Confidence,
            keywords   = result.Keywords
        });
    }
    catch (Polly.CircuitBreaker.BrokenCircuitException ex)
    {
        // Circuit is OPEN — downstream has failed too many times recently.
        // No HTTP call was made; Polly rejected it immediately.
        log.LogWarning(
            "Resilience test: circuit breaker OPEN — {Message}", ex.Message);

        return Results.Problem(
            title:      "Circuit Open",
            detail:     "Enrichment circuit is OPEN. The downstream failure rate " +
                        "exceeded 50 % in the last 30 s window. Retry after 15 s.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Polly.Timeout.TimeoutRejectedException ex)
    {
        // Either the 10 s total budget or the 3 s per-attempt timeout fired.
        log.LogWarning(
            "Resilience test: timeout — {Message}", ex.Message);

        return Results.Problem(
            title:      "Timeout",
            detail:     "Enrichment call exceeded the time budget (10 s total / 3 s per attempt).",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException ex)
    {
        // All three retries were exhausted (network error, DNS failure, or 5xx).
        // The retry log lines ("HTTP retry 1/3 …") already appeared above this point.
        log.LogError(ex,
            "Resilience test: all retries exhausted — {Message}", ex.Message);

        return Results.Problem(
            title:      "Retries Exhausted",
            detail:     $"All 3 retry attempts failed: {ex.Message}. " +
                        "Check the retry Warning logs emitted by QuotesApi.HttpResilience.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    // OperationCanceledException (caller cancelled) is intentionally NOT caught —
    // it propagates naturally; ASP.NET Core returns a 499 / connection-reset.
});
// ════════════════════════════════════════════════════════════════════════════
// END TEMPORARY RESILIENCE TEST ENDPOINT
// ════════════════════════════════════════════════════════════════════════════


if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Apply all pending EF migrations at startup — safe no-op when schema is
    // already current. Required in containers: a fresh image has no quotes.db
    // and no schema, so every table must be created before any query runs.
    await db.Database.MigrateAsync();

    if (!await db.Users.AnyAsync())
    {
        db.Users.Add(new User("admin@example.com", "password123"));
        await db.SaveChangesAsync();
    }
}

app.Run();

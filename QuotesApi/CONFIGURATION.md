# Configuration — QuotesApi

## Precedence (highest → lowest)

```
Environment variables          Jwt__Key=…  KeyVault__Uri=…
appsettings.{Environment}.json appsettings.Production.json  (gitignored)
                               appsettings.Testing.json     (test JWT key only)
                               appsettings.Development.json (Serilog EF debug)
appsettings.json               Non-secret defaults (issuer, audience, timeouts, …)
user-secrets                   Development only — dotnet user-secrets set "Jwt:Key" "…"
```

ASP.NET Core's `ASPNETCORE_ENVIRONMENT` variable determines which `appsettings.{env}.json`
file is loaded. Values from higher sources override lower ones.

---

## Strongly-typed options classes

| Class | Section | Registered as |
|---|---|---|
| `JwtOptions` | `Jwt` | `IOptions` / `IOptionsSnapshot` / `IOptionsMonitor` |
| `EntraOptions` | `Entra` | `IOptions` / `IOptionsSnapshot` / `IOptionsMonitor` |
| `KeyVaultOptions` | `KeyVault` | `IOptions` / `IOptionsSnapshot` / `IOptionsMonitor` |
| `OpenTelemetryOptions` | `OpenTelemetry` | `IOptions` / `IOptionsSnapshot` / `IOptionsMonitor` |

### When to use each interface

| Interface | Lifetime | Use when |
|---|---|---|
| `IOptions<T>` | Singleton | Value is read once at startup and never changes |
| `IOptionsSnapshot<T>` | Scoped | Endpoint handlers and request-scoped services — fresh snapshot per request |
| `IOptionsMonitor<T>` | Singleton | Singleton services that need change notifications (e.g. background workers) |

The login and refresh endpoints use **`IOptionsSnapshot<JwtOptions>`** so that signing
key rotation (via env-var change + rolling restart) takes effect on the next request
without a full redeploy.

---

## Required secrets

### `Jwt:Key`

The HMAC-SHA256 signing key for access tokens. **Must be ≥ 32 characters.**
`ValidateOnStart()` prevents the app from starting if this is missing or too short.

`appsettings.json` holds an **empty string** — the app refuses to start until you
supply the key through one of the three channels below.

**Local development — user-secrets (recommended)**

```powershell
dotnet user-secrets set "Jwt:Key" "dev-only-key-change-in-production-at-least-32chars" `
  --project day-1/QuotesApi/QuotesApi.csproj
```

User-secrets are stored in
`%APPDATA%\Microsoft\UserSecrets\quotesapi-dev-2026\secrets.json` on Windows
and `~/.microsoft/usersecrets/quotesapi-dev-2026/secrets.json` on Linux/macOS.
They are **never** committed to git.

**Staging / CI — environment variable**

```bash
export Jwt__Key="<random-256-bit-base64-string>"
# PowerShell:
$env:Jwt__Key = "<random-256-bit-base64-string>"
```

Note the double-underscore `__` separator: ASP.NET Core maps `Jwt__Key` → `Jwt:Key`.

Generate a secure key:
```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

**Production — Azure Key Vault** (via `appinsights-connection-string` pattern)

The current Key Vault integration loads the App Insights connection string.
To also load the JWT signing key from Key Vault, add to the Key Vault loading block
in `Program.cs`:

```csharp
if (kvCfg.IsConfigured)
{
    var kv = new SecretClient(new Uri(kvCfg.Uri), new DefaultAzureCredential());
    builder.Configuration["Jwt:Key"] = kv.GetSecret("jwt-signing-key").Value.Value;
}
```

Grant the Managed Identity the **Key Vault Secrets User** role.

---

## Optional secrets

### `APPLICATIONINSIGHTS_CONNECTION_STRING`

Loaded in priority order:
1. Key Vault secret `appinsights-connection-string` (when `KeyVault:Uri` is set)
2. Environment variable `APPLICATIONINSIGHTS_CONNECTION_STRING`
3. Absent → Azure Monitor is skipped; OTLP export still works

```powershell
$env:APPLICATIONINSIGHTS_CONNECTION_STRING = "InstrumentationKey=…;IngestionEndpoint=…"
```

---

## Non-secret configuration (safe in appsettings.json)

| Key | Default | Notes |
|---|---|---|
| `Jwt:Issuer` | `QuotesApi` | Included in every access token |
| `Jwt:Audience` | `QuotesApi` | Validated on every access token |
| `Jwt:ExpiryMinutes` | `15` | Access token lifetime (1–1440) |
| `Jwt:RefreshExpiryDays` | `7` | Refresh token lifetime (1–365) |
| `Entra:TenantId` | `…` | Public OAuth2 identifier — not a secret |
| `Entra:ClientId` | `…` | Public OAuth2 identifier — not a secret |
| `Entra:Audience` | `api://…` | Public OAuth2 identifier — not a secret |
| `KeyVault:Uri` | `""` | Set to enable Key Vault secret loading |
| `OpenTelemetry:ServiceName` | `QuotesApi` | Shown in Jaeger / App Insights |

---

## Integration tests

The test host sets `ASPNETCORE_ENVIRONMENT=Testing`, which causes ASP.NET Core to load
`appsettings.Testing.json`. That file provides a **test-only** JWT signing key so that
`ValidateOnStart()` passes without any additional setup:

```json
// appsettings.Testing.json (committed — test key only, not a production secret)
{
  "Jwt": {
    "Key": "integration-tests-only-signing-key-not-for-production-use"
  }
}
```

No manual setup is required to run the integration test suite locally.

---

## Validation behaviour

`JwtOptions` is registered with both `ValidateDataAnnotations()` and `ValidateOnStart()`:

```csharp
builder.Services
    .AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

If `Jwt:Key` is missing, empty, or shorter than 32 characters the app throws
`OptionsValidationException` at startup — before the first HTTP request is ever
processed. The error message names the exact constraint that failed.

`EntraOptions`, `KeyVaultOptions`, and `OpenTelemetryOptions` have no required fields
(all features they control are optional) and therefore do not use `ValidateOnStart`.

---

## gitignore rules

```
appsettings.Production.json   # production overrides — never committed
appsettings.*.local.json      # per-machine local overrides
*.secrets.json
.env
.env.*
```

`appsettings.Testing.json` is **not** gitignored — it contains only a
non-production test key and must be present for CI to pass.

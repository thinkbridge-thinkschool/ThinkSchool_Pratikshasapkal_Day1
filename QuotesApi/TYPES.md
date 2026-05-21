# QuotesApi — Type Reference

Quick reference for all models, DTOs, and abstractions in this project.

---

## Models (`QuotesApi.Models`)

### `Quote`

Represents a quotation stored in the system.

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Auto-generated PK |
| `Author` | `string` | 1–200 chars |
| `Text` | `string` | 1–1000 chars |
| `IsDeleted` | `bool` | Soft-delete flag |

**Factory:** `Quote.Create(author, text)` → `Result<Quote>`  
Returns `Result.Fail(...)` if `Author` or `Text` fails validation.

**Methods:**
- `Delete()` — sets `IsDeleted = true`

---

### `User`

Represents an API user with BCrypt-hashed credentials.

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Auto-generated PK |
| `Email` | `string` | Unique identifier for login |
| `PasswordHash` | `string` | BCrypt hash (never store plaintext) |

**Constructor:** `User(email, password)` — hashes `password` with BCrypt on construction.

**Methods:**
- `VerifyPassword(password)` → `bool` — BCrypt.Verify against stored hash

---

### `RefreshToken`

Persisted refresh token record used for secure token rotation.

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Auto-generated PK |
| `TokenHash` | `string` | SHA-256 hex of the raw token |
| `UserId` | `int` | FK → `Users` |
| `FamilyId` | `string` | GUID grouping all rotations of a single login |
| `ExpiresAt` | `DateTime` | UTC expiry |
| `RevokedAt` | `DateTime?` | Set when explicitly revoked |
| `ReplacedByToken` | `string?` | Hash of successor token; non-null = consumed |
| `User` | `User` | Navigation property |

**Computed:**
- `IsExpired` — `UtcNow >= ExpiresAt`
- `IsRevoked` — `RevokedAt is not null`
- `IsUsed` — `ReplacedByToken is not null`
- `IsActive` — `!IsExpired && !IsRevoked && !IsUsed`

**Security notes:**
- Raw token is never stored — only `SHA-256(token)` is persisted.
- Presenting a token where `IsUsed = true` triggers family-wide revocation (reuse attack).

---

### `Collection`

A named list of up to 50 quotes owned by a user.

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Auto-generated PK |
| `Name` | `string` | 3–80 chars |
| `OwnerId` | `int` | FK-like reference to a user |
| `Items` | `List<CollectionItem>` | Owned entity, max 50 |

**Constructor:** `Collection(name, ownerId, clock)` — validates name via `SetName`.

**Methods:**
- `SetName(name)` — throws `ArgumentException` if blank or outside 3–80 chars
- `AddItem(quoteId)` — throws `InvalidOperationException` if count ≥ 50 or duplicate
- `RemoveItem(quoteId)` — throws `InvalidOperationException` if not found

---

### `CollectionItem`

Owned child entity of `Collection` (stored in `CollectionItem` table).

| Member | Type | Notes |
|---|---|---|
| `Id` | `int` | Auto-generated |
| `QuoteId` | `int` | Reference to the quote |
| `AddedAt` | `DateTime` | UTC timestamp from `IClock` |

---

### `Result<T>`

Discriminated union for operation results — avoids throwing exceptions for expected failures.

| Member | Type | Notes |
|---|---|---|
| `IsSuccess` | `bool` | |
| `Value` | `T?` | Present when `IsSuccess = true` |
| `Error` | `string?` | Present when `IsSuccess = false` |

**Static factories:**
- `Result<T>.Ok(value)` → success
- `Result<T>.Fail(error)` → failure

---

## DTOs (`QuotesApi.Dtos`)

### `CreateQuoteRequest`

Body for `POST /api/quotes`.

| Property | Type | Validation |
|---|---|---|
| `Author` | `string` | `[Required]` |
| `Text` | `string` | `[Required]` |

---

### `LoginRequest`

Body for `POST /api/auth/login`.

| Property | Type |
|---|---|
| `Email` | `string` |
| `Password` | `string` |

---

### `RefreshRequest`

Body for `POST /api/auth/refresh`.

| Property | Type |
|---|---|
| `RefreshToken` | `string` |

---

## Abstractions (`QuotesApi.Abstractions`)

### `IClock`

Seam for time, makes code testable without `DateTime.UtcNow` calls in business logic.

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
```

**Production implementation:** `SystemClock` — registered as `AddSingleton<IClock, SystemClock>()`.

---

## Auth Response Shape

Both `/api/auth/login` and `/api/auth/refresh` return:

```json
{
  "access_token": "<JWT>",
  "refresh_token": "<url-safe base64, 32 bytes>",
  "expires_in": 900
}
```

- `access_token` — short-lived JWT (15 min default, see `appsettings.json → Jwt:ExpiryMinutes`)
- `refresh_token` — opaque token; rotate via `/api/auth/refresh` before expiry (7 days default)
- `expires_in` — access token TTL in seconds




# Entra(Microsoft Azure) Auth :

Application (client) ID :
d660803f-8f13-407b-afc9-fc48e9577584

Object ID :
defd5b95-db0a-46e2-8906-c42c07a1da1e

Directory (tenant) ID :
0a0aa63d-82d0-4ba1-b909-d7986ece4c4c

Application ID URI
api://d660803f-8f13-407b-afc9-fc48e9577584

# Audit Logging via MVC Action Filter — Design Spec

**Date:** 2026-07-19
**Project:** BaseClean
**Source inspiration:** `D:\GitHub\PDFs\PersonalImplementation\spec\2026-07-12-audit-action-filter-design.md`
**Scope:** Write pipeline only — filter → queue → background writer → DB. No read endpoint.

---

## 1. Background

The pattern comes from IAHR's `AuditMiddleware`, cleaned up and reimplemented as an opt-in action filter. The key improvements over the original:

| IAHR quirk | This design |
|---|---|
| Logs all mutating HTTP verbs | `[Audit]` attribute — opt-in per controller or action |
| Raw body buffering + multipart strip loop | Filter receives model-bound arguments; serialize those |
| Self-constructed `DbContext`, blocks the request | Scoped `DbContext` resolved in a `BackgroundService` |
| No response info | Captures `StatusCode` + `DurationMs` |
| No secret masking | Redacts `password`/`token`/`secret`-named fields |
| `DateTime.Now` | `DateTimeOffset.UtcNow` |

---

## 2. Architecture

```
[Audit] on controller/action
        │
AuditFilter (global IAsyncActionFilter)
        ├─ no [Audit]? → await next(); return   ← zero cost
        └─ [Audit]:
             SensitiveDataRedactor.SerializeAndRedact(ActionArguments)
             var executed = await next();         ← action runs
             read StatusCode + DurationMs
             AuditQueue.TryWrite(entry)           ← in-memory, returns instantly
                          │
AuditWriterService ───────┘  (BackgroundService, singleton)
        ReadAllAsync → scoped DbContext → SaveChangesAsync
        (failures logged + swallowed; loop never crashes)
```

**Why a filter and not middleware?**
The filter runs inside MVC — after model binding and after `UseAuthentication`/`UseAuthorization` — so `HttpContext.User` is populated and action arguments are already bound objects. No body buffering needed.

**Why attribute + separate filter (not the attribute being the filter)?**
The filter gets real constructor DI and is unit-testable in isolation. The attribute is a zero-logic marker. The filter is a no-op unless `[Audit]` is present, so registering it globally costs nothing on untagged endpoints.

---

## 3. Layer Placement

BaseClean enforces a strict one-way dependency rule: `Web → Application → Domain`, `Web → Infrastructure → Domain`.

| Component | Layer | File path |
|---|---|---|
| `AuditLog` | Domain | `BaseClean.Domain/Entities/AuditLog.cs` |
| `AuditEntry` | Web | `BaseClean.Web/Auditing/AuditEntry.cs` |
| `AuditAttribute` | Web | `BaseClean.Web/Auditing/AuditAttribute.cs` |
| `AuditFilter` | Web | `BaseClean.Web/Auditing/AuditFilter.cs` |
| `AuditQueue` | Web | `BaseClean.Web/Auditing/AuditQueue.cs` |
| `AuditWriterService` | Web | `BaseClean.Web/Auditing/AuditWriterService.cs` |
| `SensitiveDataRedactor` | Web | `BaseClean.Web/Auditing/SensitiveDataRedactor.cs` |
| `AuditMappingExtensions` | Web | `BaseClean.Web/Auditing/AuditMappingExtensions.cs` |
| `AuditingExtensions` (DI wiring) | Web | `BaseClean.Web/Extensions/AuditingExtensions.cs` |
| `ApplicationDbContext` (add DbSet) | Infrastructure | `BaseClean.Infrastructure/Data/ApplicationDbContext.cs` |

All auditing logic lives in `BaseClean.Web/Auditing/`. The only cross-layer artifact is `AuditLog` in Domain, because it's a persisted entity that Infrastructure (EF Core) must know about.

---

## 4. Components

### 4.1 `AuditLog` — EF Entity (Domain)

Does **not** extend `BaseEntity<T>`. Audit records are append-only and immutable; `IsDeleted`, `DeletedAt`, and `UpdatedAtUtc` have no meaning here, and the generic repository constraint would pull in unnecessary infrastructure.

```csharp
public class AuditLog
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Username { get; set; }
    public string HttpMethod { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string Action { get; set; } = "";         // "Controller/Action"
    public string? RequestData { get; set; }          // redacted JSON, max 8 KB
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
}
```

`RequestData` is capped at 8 192 characters before enqueue to prevent large payloads bloating the table.

### 4.2 `AuditEntry` — Captured Record (Web)

Immutable `record` passed from filter → queue → writer. Decouples capture from persistence.

```csharp
public sealed record AuditEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string? Username { get; init; }
    public string HttpMethod { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Ip { get; init; }
    public string? UserAgent { get; init; }
    public string Action { get; init; } = "";
    public string? RequestData { get; init; }
    public int StatusCode { get; init; }
    public long DurationMs { get; init; }
}
```

### 4.3 `AuditAttribute` — Opt-In Marker (Web)

No logic. Valid on classes (whole controller) and methods (single action).

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditAttribute : Attribute { }
```

### 4.4 `SensitiveDataRedactor` — Arg Serialization + Masking (Web)

Serializes `ActionArguments` (the already-model-bound dictionary) to compact JSON, then recursively walks the tree masking sensitive values.

**Rules:**
- Skip `IFormFile` / `IFormFileCollection` / `IEnumerable<IFormFile>` args entirely — replaces IAHR's multipart strip loop.
- Replace string values whose **property name** contains `"password"`, `"token"`, `"secret"`, or `"pwd"` (case-insensitive substring match) with `"***"`. Applied recursively through nested objects.
- Return `null` for empty input, all-filtered input, or any serialization error — logging must never throw into the request.
- Truncate result to 8 192 characters.

### 4.5 `AuditQueue` — Non-Blocking Buffer (Web)

Singleton wrapper over `System.Threading.Channels.Channel<AuditEntry>`.

- Bounded capacity: **1 000**.
- Full mode: `DropWrite` — a write burst silently drops excess entries rather than backpressuring the request thread.
- `SingleReader = true` (only `AuditWriterService` reads).

API: `bool TryWrite(AuditEntry)` and `IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken)`.

### 4.6 `AuditFilter` — The Interceptor (Web)

Registered globally; no-ops without `[Audit]`.

Detection: `context.ActionDescriptor.EndpointMetadata.OfType<AuditAttribute>().Any()` — works whether the attribute is on the action or the controller class.

**Execution order:**
1. Detect `[Audit]` — skip immediately if absent.
2. Redact args via `SensitiveDataRedactor`.
3. Start `Stopwatch`.
4. `var executed = await next()` — action runs.
5. Stop stopwatch, read `executed.HttpContext.Response.StatusCode`.
6. Build `AuditEntry`.
7. `AuditQueue.TryWrite(entry)` — returns instantly.

**Claim type:** `ClaimTypes.NameIdentifier` — matches BaseClean's `AuthenticationExtensions.cs` (`NameClaimType = ClaimTypes.NameIdentifier`).

```csharp
Username = http.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
```

Any exception inside the filter (excluding `next()` itself) is caught, logged, and swallowed — auditing must never surface errors to the caller.

### 4.7 `AuditWriterService` — Background Drain (Web)

`BackgroundService` (singleton lifetime). Loops over `AuditQueue.ReadAllAsync`.

Per entry:
1. Create a new DI scope (`IServiceScopeFactory.CreateScope()`).
2. Resolve `ApplicationDbContext` from scope.
3. `db.AuditLogs.Add(entry.ToAuditLog())`.
4. `await db.SaveChangesAsync(stoppingToken)`.
5. Dispose scope.

A `SaveChangesAsync` failure is caught, logged via `ILogger<AuditWriterService>`, and the loop continues — one bad write cannot stall the drain.

### 4.8 `AuditMappingExtensions` — Record → Entity (Web)

Static extension: `AuditLog ToAuditLog(this AuditEntry e)` — field-by-field copy. Kept in the Web layer so Infrastructure has no knowledge of the auditing pipeline.

### 4.9 `AuditingExtensions` — DI Registration (Web)

New extension method `AddAuditLogging(this IServiceCollection services)` to keep `ApplicationServices.cs` clean:

```csharp
services.AddSingleton<AuditQueue>();
services.AddSingleton<SensitiveDataRedactor>();
services.AddHostedService<AuditWriterService>();
```

`AuditFilter` is registered differently — via `AddControllers(o => o.Filters.Add<AuditFilter>())` in `Program.cs`, because the filter needs to be added to the MVC filter pipeline, not the DI container directly.

---

## 5. `ApplicationDbContext` Changes

Add one property to `BaseClean.Infrastructure/Data/ApplicationDbContext.cs`:

```csharp
public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
```

Then create and apply one migration:

```bash
dotnet ef migrations add AddAuditLog --project BaseClean.Infrastructure --startup-project BaseClean.Web
dotnet ef database update --project BaseClean.Infrastructure --startup-project BaseClean.Web
```

---

## 6. `Program.cs` Changes

```csharp
// Change:
builder.Services.AddControllers();
// To:
builder.Services.AddControllers(o => o.Filters.Add<AuditFilter>());

// Add after existing service registrations:
builder.Services.AddAuditLogging();
```

---

## 7. Usage

```csharp
// Tag a whole controller — every action is audited
[Audit]
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase { ... }

// Or tag a single action
[HttpDelete("{id}")]
[Audit]
public IActionResult Delete(int id) { ... }
```

---

## 8. Data Flow Summary

```
HTTP Request
    → AuditFilter (global, ~0 ms overhead on untagged endpoints)
        → SensitiveDataRedactor.SerializeAndRedact()   [sync]
        → action executes
        → AuditQueue.TryWrite()                        [~1 µs, in-memory]
HTTP Response returned to caller

[background thread]
    AuditWriterService drains channel
        → scoped DbContext.SaveChangesAsync()
        → row written to AuditLogs table
```

---

## 9. Decisions Made

| Decision | Choice | Reason |
|---|---|---|
| `AuditLog` extends `BaseEntity<T>`? | No | Audit records are immutable; soft-delete fields are meaningless; generic repo constraint unnecessary |
| Read endpoint? | Out of scope | Write pipeline only; read via SQL or future feature |
| Claim type | `ClaimTypes.NameIdentifier` | Matches `AuthenticationExtensions.cs` |
| `RequestData` size cap | 8 192 chars | Prevents large payloads bloating the table |
| Registration location | New `AuditingExtensions.cs` | Keeps `ApplicationServices.cs` single-responsibility |
| Test project | Out of scope | BaseClean has no test project; add when one is created |
| Channel full mode | `DropWrite` | Auditing is best-effort; never backpressure request threads |

---

## 10. File Checklist

```
BaseClean.Domain/
  Entities/AuditLog.cs                          ← new

BaseClean.Infrastructure/
  Data/ApplicationDbContext.cs                  ← add DbSet<AuditLog>
  Migrations/<timestamp>_AddAuditLog.cs         ← generated

BaseClean.Web/
  Auditing/
    AuditAttribute.cs                           ← new
    AuditEntry.cs                               ← new
    AuditFilter.cs                              ← new
    AuditQueue.cs                               ← new
    AuditWriterService.cs                       ← new
    SensitiveDataRedactor.cs                    ← new
    AuditMappingExtensions.cs                   ← new
  Extensions/
    AuditingExtensions.cs                       ← new
  Program.cs                                    ← modify (2 lines)
```

Total: **8 new files**, **2 modified files**, **1 migration**.

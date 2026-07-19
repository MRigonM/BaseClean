# Audit Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in audit logging — tag any controller or action with `[Audit]` and every call is recorded (who, what, request payload, HTTP status, duration) without blocking the request.

**Architecture:** A marker attribute (`[Audit]`) plus one globally-registered `IAsyncActionFilter` that no-ops unless the action is tagged. When tagged, the filter captures request metadata + redacted arguments, runs the action, records status/duration, and enqueues an `AuditEntry` to a bounded in-memory `Channel`. A `BackgroundService` drains the channel and persists rows via a scoped `DbContext`, so the DB write never sits on the request thread.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core 10, `System.Threading.Channels` (built-in), `System.Text.Json` (built-in).

**Spec:** `docs/superpowers/specs/2026-07-19-audit-logging-design.md`

**Note on tests:** BaseClean has no test project. Build verification (`dotnet build`) replaces the failing-test gate at each step. Tests should be added in a follow-up once a test project is created.

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `BaseClean.Domain/Entities/AuditLog.cs` | Create | EF entity — persisted audit row |
| `BaseClean.Infrastructure/Data/ApplicationDbContext.cs` | Modify | Add `DbSet<AuditLog>` |
| `BaseClean.Web/Auditing/AuditEntry.cs` | Create | Immutable captured-data record (filter → queue → writer) |
| `BaseClean.Web/Auditing/AuditMappingExtensions.cs` | Create | `AuditEntry.ToAuditLog()` — record → entity |
| `BaseClean.Web/Auditing/SensitiveDataRedactor.cs` | Create | Serialize action args to JSON, mask secrets, skip file args |
| `BaseClean.Web/Auditing/AuditQueue.cs` | Create | Singleton bounded-channel buffer |
| `BaseClean.Web/Auditing/AuditAttribute.cs` | Create | Opt-in marker attribute |
| `BaseClean.Web/Auditing/AuditFilter.cs` | Create | Global filter: capture → run action → enqueue |
| `BaseClean.Web/Auditing/AuditWriterService.cs` | Create | BackgroundService: drain queue → persist via scoped DbContext |
| `BaseClean.Web/Extensions/AuditingExtensions.cs` | Create | DI registration: queue, redactor, hosted service |
| `BaseClean.Web/Program.cs` | Modify | Register filter in MVC + call `AddAuditLogging()` |

---

## Task 1: `AuditLog` entity + `DbSet` + migration

**Files:**
- Create: `BaseClean.Domain/Entities/AuditLog.cs`
- Modify: `BaseClean.Infrastructure/Data/ApplicationDbContext.cs`

- [ ] **Step 1: Create `AuditLog.cs`**

```csharp
// BaseClean.Domain/Entities/AuditLog.cs
namespace BaseClean.Domain.Entities;

public class AuditLog
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Username { get; set; }
    public string HttpMethod { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public string Action { get; set; } = "";
    public string? RequestData { get; set; }
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
}
```

Does **not** extend `BaseEntity<T>` — audit records are append-only; soft-delete and update-timestamp fields are meaningless here.

- [ ] **Step 2: Add `DbSet<AuditLog>` to `ApplicationDbContext`**

Open `BaseClean.Infrastructure/Data/ApplicationDbContext.cs`. The `using BaseClean.Domain.Entities;` is already present. Add the property inside the class:

```csharp
using BaseClean.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BaseClean.Infrastructure.Data;

public class ApplicationDbContext : IdentityDbContext<AppUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Create the migration**

```bash
dotnet ef migrations add AddAuditLog --project BaseClean.Infrastructure --startup-project BaseClean.Web
```

Expected: a new file `BaseClean.Infrastructure/Migrations/<timestamp>_AddAuditLog.cs` containing an `AuditLogs` table with all 11 columns.

- [ ] **Step 5: Apply the migration**

```bash
dotnet ef database update --project BaseClean.Infrastructure --startup-project BaseClean.Web
```

Expected: `Done.` (or `No pending migrations.` if DB doesn't exist yet — that's fine; it will apply on first run).

- [ ] **Step 6: Commit**

```bash
git add BaseClean.Domain/Entities/AuditLog.cs BaseClean.Infrastructure/Data/ApplicationDbContext.cs BaseClean.Infrastructure/Migrations/
git commit -m "feat(audit): add AuditLog entity, DbSet, and AddAuditLog migration"
```

---

## Task 2: `AuditEntry` record + `AuditMappingExtensions`

**Files:**
- Create: `BaseClean.Web/Auditing/AuditEntry.cs`
- Create: `BaseClean.Web/Auditing/AuditMappingExtensions.cs`

- [ ] **Step 1: Create `AuditEntry.cs`**

```csharp
// BaseClean.Web/Auditing/AuditEntry.cs
namespace BaseClean.Web.Auditing;

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

This is the in-flight record that moves filter → channel → writer. It is never persisted directly — `AuditMappingExtensions` maps it to `AuditLog` for EF.

- [ ] **Step 2: Create `AuditMappingExtensions.cs`**

```csharp
// BaseClean.Web/Auditing/AuditMappingExtensions.cs
using BaseClean.Domain.Entities;

namespace BaseClean.Web.Auditing;

public static class AuditMappingExtensions
{
    public static AuditLog ToAuditLog(this AuditEntry e) => new()
    {
        Timestamp   = e.Timestamp,
        Username    = e.Username,
        HttpMethod  = e.HttpMethod,
        Path        = e.Path,
        Ip          = e.Ip,
        UserAgent   = e.UserAgent,
        Action      = e.Action,
        RequestData = e.RequestData,
        StatusCode  = e.StatusCode,
        DurationMs  = e.DurationMs,
    };
}
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add BaseClean.Web/Auditing/AuditEntry.cs BaseClean.Web/Auditing/AuditMappingExtensions.cs
git commit -m "feat(audit): add AuditEntry record and ToAuditLog mapping"
```

---

## Task 3: `SensitiveDataRedactor`

**Files:**
- Create: `BaseClean.Web/Auditing/SensitiveDataRedactor.cs`

- [ ] **Step 1: Create `SensitiveDataRedactor.cs`**

```csharp
// BaseClean.Web/Auditing/SensitiveDataRedactor.cs
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BaseClean.Web.Auditing;

public sealed class SensitiveDataRedactor
{
    private static readonly string[] Sensitive = ["password", "token", "secret", "pwd"];
    private const int MaxLength = 8192;

    public string? SerializeAndRedact(IDictionary<string, object?> args)
    {
        if (args is null || args.Count == 0) return null;

        var filtered = args
            .Where(kv => kv.Value is not IFormFile
                      && kv.Value is not IFormFileCollection
                      && kv.Value is not IEnumerable<IFormFile>)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (filtered.Count == 0) return null;

        try
        {
            using var doc = JsonSerializer.SerializeToDocument(filtered);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteRedacted(doc.RootElement, writer, propertyName: null);
            }
            var result = Encoding.UTF8.GetString(stream.ToArray());
            return result.Length > MaxLength ? result[..MaxLength] : result;
        }
        catch
        {
            return null; // serialization must never break the request
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, string? propertyName)
    {
        if (propertyName is not null && IsSensitive(propertyName))
        {
            writer.WriteStringValue("***");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteRedacted(prop.Value, writer, prop.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(item, writer, null);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
        => Sensitive.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
}
```

**What this does:**
- Drops `IFormFile` / `IFormFileCollection` / `IEnumerable<IFormFile>` args (replaces IAHR's multipart byte-strip loop).
- Recursively walks the serialized JSON tree; any property whose name contains `"password"`, `"token"`, `"secret"`, or `"pwd"` (case-insensitive substring) has its value replaced with `"***"`. This catches `ConfirmPassword`, `AccessToken`, `ApiSecret`, etc.
- Returns `null` on empty input, all-filtered input, or any serialization error.
- Truncates output to 8 192 characters.

- [ ] **Step 2: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add BaseClean.Web/Auditing/SensitiveDataRedactor.cs
git commit -m "feat(audit): serialize and redact action args, skip file uploads"
```

---

## Task 4: `AuditQueue`

**Files:**
- Create: `BaseClean.Web/Auditing/AuditQueue.cs`

- [ ] **Step 1: Create `AuditQueue.cs`**

```csharp
// BaseClean.Web/Auditing/AuditQueue.cs
using System.Threading.Channels;

namespace BaseClean.Web.Auditing;

public sealed class AuditQueue
{
    private readonly Channel<AuditEntry> _channel =
        Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public bool TryWrite(AuditEntry entry) => _channel.Writer.TryWrite(entry);

    public IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
```

**Why `DropWrite`:** auditing is best-effort. Under a request burst, excess entries are silently dropped rather than blocking the request thread. Capacity 1 000 is ample for any low-to-medium traffic personal project.

- [ ] **Step 2: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add BaseClean.Web/Auditing/AuditQueue.cs
git commit -m "feat(audit): add non-blocking bounded-channel AuditQueue"
```

---

## Task 5: `AuditAttribute` + `AuditFilter`

**Files:**
- Create: `BaseClean.Web/Auditing/AuditAttribute.cs`
- Create: `BaseClean.Web/Auditing/AuditFilter.cs`

- [ ] **Step 1: Create `AuditAttribute.cs`**

```csharp
// BaseClean.Web/Auditing/AuditAttribute.cs
namespace BaseClean.Web.Auditing;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditAttribute : Attribute { }
```

- [ ] **Step 2: Create `AuditFilter.cs`**

```csharp
// BaseClean.Web/Auditing/AuditFilter.cs
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BaseClean.Web.Auditing;

public sealed class AuditFilter : IAsyncActionFilter
{
    private readonly AuditQueue _queue;
    private readonly SensitiveDataRedactor _redactor;

    public AuditFilter(AuditQueue queue, SensitiveDataRedactor redactor)
    {
        _queue = queue;
        _redactor = redactor;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var isAudited = context.ActionDescriptor.EndpointMetadata.OfType<AuditAttribute>().Any();
        if (!isAudited)
        {
            await next();
            return;
        }

        var http = context.HttpContext;

        string? requestData = null;
        try { requestData = _redactor.SerializeAndRedact(context.ActionArguments); }
        catch { /* swallow — never break the request */ }

        var sw = Stopwatch.StartNew();
        var executed = await next();
        sw.Stop();

        try
        {
            var entry = new AuditEntry
            {
                Timestamp   = DateTimeOffset.UtcNow,
                Username    = http.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                HttpMethod  = http.Request.Method,
                Path        = http.Request.Path,
                Ip          = http.Connection.RemoteIpAddress?.ToString(),
                UserAgent   = http.Request.Headers.UserAgent.ToString(),
                Action      = $"{context.RouteData.Values["controller"]}/{context.RouteData.Values["action"]}",
                RequestData = requestData,
                StatusCode  = executed.HttpContext.Response.StatusCode,
                DurationMs  = sw.ElapsedMilliseconds,
            };
            _queue.TryWrite(entry);
        }
        catch { /* swallow — auditing must never surface errors to the caller */ }
    }
}
```

**Key points:**
- `ClaimTypes.NameIdentifier` matches the `NameClaimType` set in `AuthenticationExtensions.cs`.
- Detection via `EndpointMetadata.OfType<AuditAttribute>()` works whether `[Audit]` is on the action or the controller class.
- The entire capture-and-enqueue block is wrapped in `try/catch` — a serialization edge case or bad route data must never reach the caller.
- `next()` itself is **not** wrapped — its exceptions belong to the request, not to auditing.

- [ ] **Step 3: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add BaseClean.Web/Auditing/AuditAttribute.cs BaseClean.Web/Auditing/AuditFilter.cs
git commit -m "feat(audit): add [Audit] marker and AuditFilter — captures status, duration, redacted args"
```

---

## Task 6: `AuditWriterService`

**Files:**
- Create: `BaseClean.Web/Auditing/AuditWriterService.cs`

- [ ] **Step 1: Create `AuditWriterService.cs`**

```csharp
// BaseClean.Web/Auditing/AuditWriterService.cs
using BaseClean.Infrastructure.Data;

namespace BaseClean.Web.Auditing;

public sealed class AuditWriterService : BackgroundService
{
    private readonly AuditQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditWriterService> _logger;

    public AuditWriterService(
        AuditQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<AuditWriterService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.AuditLogs.Add(entry.ToAuditLog());
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist audit entry for {Action}", entry.Action);
            }
        }
    }
}
```

**Why `IServiceScopeFactory`:** `AuditWriterService` is a singleton (all `BackgroundService` registrations are). `ApplicationDbContext` is scoped. A singleton cannot hold a scoped dependency directly — it must create a new scope per unit of work and resolve `DbContext` from that scope.

- [ ] **Step 2: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add BaseClean.Web/Auditing/AuditWriterService.cs
git commit -m "feat(audit): drain queue to DB on a background service, tolerant of write failures"
```

---

## Task 7: Wire-up — `AuditingExtensions` + `Program.cs`

**Files:**
- Create: `BaseClean.Web/Extensions/AuditingExtensions.cs`
- Modify: `BaseClean.Web/Program.cs`

- [ ] **Step 1: Create `AuditingExtensions.cs`**

```csharp
// BaseClean.Web/Extensions/AuditingExtensions.cs
using BaseClean.Web.Auditing;

namespace BaseClean.Web.Extensions;

public static class AuditingExtensions
{
    public static IServiceCollection AddAuditLogging(this IServiceCollection services)
    {
        services.AddSingleton<AuditQueue>();
        services.AddSingleton<SensitiveDataRedactor>();
        services.AddHostedService<AuditWriterService>();
        return services;
    }
}
```

Note: `AuditFilter` is **not** registered here — it is added to the MVC filter pipeline in `Program.cs`, which gives it DI constructor injection automatically.

- [ ] **Step 2: Update `Program.cs`**

Replace the existing `AddControllers()` call and add `AddAuditLogging()`. The full updated file:

```csharp
using BaseClean.Web.Auditing;
using BaseClean.Web.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<AuditFilter>());

builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddSwaggerDocumentation();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddAuditLogging();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerDocumentation();
}

app.UseHttpsRedirection();

var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = ""
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add BaseClean.Web/Extensions/AuditingExtensions.cs BaseClean.Web/Program.cs
git commit -m "feat(audit): register audit pipeline — filter, queue, redactor, writer service"
```

---

## Task 8: Tag an endpoint and verify end-to-end

This task has no new files. It validates the full pipeline runs correctly against a real DB.

- [ ] **Step 1: Tag a controller or action**

Open any existing controller (or create a minimal one for testing). Add `[Audit]` to a `POST` or `DELETE` action. Example — if no controllers exist yet, create `BaseClean.Web/Controllers/TestController.cs`:

```csharp
using BaseClean.Web.Auditing;
using Microsoft.AspNetCore.Mvc;

namespace BaseClean.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    [HttpPost]
    [Audit]
    public IActionResult Echo([FromBody] object? body) => Ok(body);

    [HttpGet]
    public IActionResult Ping() => Ok("pong"); // not audited
}
```

- [ ] **Step 2: Run the app**

```bash
dotnet run --project BaseClean.Web
```

Swagger UI opens at `http://localhost:5278`.

- [ ] **Step 3: Hit the tagged endpoint**

In Swagger UI, call `POST /api/test` with any JSON body (e.g. `{"name": "hello"}`). Then call `GET /api/test` (the un-tagged action).

- [ ] **Step 4: Query the database**

```sql
SELECT TOP 5 * FROM AuditLogs ORDER BY Id DESC;
```

Expected:
- One row for the `POST` call: `HttpMethod = 'POST'`, `Action = 'Test/Echo'`, `StatusCode = 200`, `DurationMs > 0`, `RequestData` contains `{"body":{"name":"hello"}}`.
- No row for the `GET` call.

- [ ] **Step 5: Verify sensitive field masking**

Call `POST /api/test` with `{"password": "hunter2", "username": "rigon"}`.

Expected `RequestData` in DB: `{"body":{"password":"***","username":"rigon"}}` — `password` is masked, `username` is not.

- [ ] **Step 6: Commit**

```bash
git add BaseClean.Web/Controllers/TestController.cs  # only if you created it; remove if you tagged an existing controller
git commit -m "feat(audit): wire-up verified end-to-end — filter enqueues, writer persists to AuditLogs"
```

---

## Manual Verification Checklist

Run through these after Task 8 to confirm the full feature:

- [ ] Tagged endpoint writes one row per call
- [ ] Un-tagged endpoint writes no row
- [ ] `password`/`token`/`secret`/`pwd` field values show `***` in `RequestData`
- [ ] Nested sensitive fields are also masked (e.g. `{"dto":{"Nested":{"Token":"abc"}}}` → `"Token":"***"`)
- [ ] File upload to a tagged endpoint: row is written, file bytes are absent from `RequestData`
- [ ] App does not crash or return 500 if the DB is temporarily unavailable — check logs for `LogError`, request still returns normally
- [ ] `DurationMs` is a positive number
- [ ] `Ip` and `UserAgent` are populated

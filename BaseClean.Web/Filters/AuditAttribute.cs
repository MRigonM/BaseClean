using System.Diagnostics;
using System.Security.Claims;
using BaseClean.Domain.Audit;
using BaseClean.Web.Auditing;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BaseClean.Web.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var queue    = services.GetRequiredService<AuditQueue>();
        var redactor = services.GetRequiredService<SensitiveDataRedactor>();

        var http = context.HttpContext;

        string? requestData = null;
        try
        {
            requestData = redactor.SerializeAndRedact(context.ActionArguments);
        }
        catch
        {
        }

        var timestamp = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var executed = await next();
        sw.Stop();

        try
        {
            var entry = new AuditEntry
            {
                Timestamp   = timestamp,
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
            queue.TryWrite(entry);
        }
        catch { /* auditing must never surface errors to the caller */ }
    }
}
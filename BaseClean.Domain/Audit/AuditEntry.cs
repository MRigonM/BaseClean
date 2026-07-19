namespace BaseClean.Domain.Audit;

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

using System.ComponentModel.DataAnnotations;

namespace BaseClean.Domain.Entities;

public class AuditLog
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    [MaxLength(256)]
    public string? Username { get; set; }

    [MaxLength(20)]
    public string HttpMethod { get; set; } = "";

    [MaxLength(2000)]
    public string Path { get; set; } = "";

    [MaxLength(45)]
    public string? Ip { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }

    [MaxLength(500)]
    public string Action { get; set; } = "";

    public string? RequestData { get; set; }

    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
}

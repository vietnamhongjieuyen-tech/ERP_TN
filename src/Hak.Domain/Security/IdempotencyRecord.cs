namespace Hak.Domain.Security;

/// <summary>
/// A create / update request sent with an Idempotency-Key header. The first request runs; a repeat with the same key and the
/// same content (the client lost the reply and sends again) gets the stored reply instead of creating the record twice. The
/// same key with different content is refused. Records are per user and are removed after a few days.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Key { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int? StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
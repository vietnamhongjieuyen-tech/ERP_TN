namespace Hak.Domain.Vouchers;

/// <summary>
/// A source document kept with a voucher (scanned invoice, contract, payment order…). The bytes live in the database, so the
/// encrypted backup carries them. Attachments are evidence: they are never deleted, only marked removed with a reason.
/// </summary>
public sealed class VoucherAttachment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VoucherId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>Lower-case extension without the dot; decides the content type served back (never taken from the upload).</summary>
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public DateTime UploadedAt { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime? RemovedAt { get; set; }
    public Guid? RemovedBy { get; set; }
    public string? RemoveReason { get; set; }
}

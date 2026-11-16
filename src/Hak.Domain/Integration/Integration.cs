namespace Hak.Domain.Integration;

public enum ImportBatchStatus { DryRun = 1, Committed = 2, Rejected = 3 }

/// <summary>One received JSON package. package_id is unique per company, which blocks replay.</summary>
public sealed class ImportBatch
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string PackageId { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string Checksum { get; set; } = "";
    public ImportBatchStatus Status { get; set; }
    public int ItemCount { get; set; }
    public int CreatedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
    public string? ResultJson { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Guid ReceivedBy { get; set; }
}

/// <summary>Shared secret per external system for HMAC authentication of packages (v1).</summary>
public sealed class ExchangePartnerKey
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string SourceSystem { get; set; } = "";
    public string KeyId { get; set; } = "";
    /// <summary>The secret as stored: DPAPI-wrapped ("dpapi:…") on Windows, plain Base64 for legacy rows (Application SecretProtector).</summary>
    public string SecretBase64 { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

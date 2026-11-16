namespace Hak.Domain.Vouchers;

/// <summary>
/// Định khoản mẫu: a named set of voucher lines that users start a new voucher from (rent, bank fees, salary…). It is only
/// a starting point for data entry — nothing is posted from a template and vouchers keep no link to it.
/// </summary>
public sealed class VoucherTemplate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string VoucherType { get; set; } = VoucherTypes.General;
    public string Description { get; set; } = "";
    /// <summary>JSON array of lines: accountId, side (D/C), amount (decimal text or null), partnerId, description, cashFlowCode, analysisCodeId.</summary>
    public string LinesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
}

/// <summary>
/// Mã phân tích: a management dimension put on a voucher line beside the account (khoản mục phí, bộ phận, hợp đồng, chuyến
/// tàu…). Kind groups the codes; the ledger keeps the code of each posted line so results can be reported per code.
/// </summary>
public sealed class AnalysisCode
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>Group name chosen by the company, e.g. "Chuyến tàu", "Bộ phận".</summary>
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

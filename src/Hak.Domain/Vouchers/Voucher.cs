using Hak.Domain.Common;

namespace Hak.Domain.Vouchers;

public enum VoucherStatus
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Posted = 4,
    Reversed = 5,
    Cancelled = 9,
}

/// <summary>GL voucher types (docs/01 §6.4). Subledger modules add their own types later.</summary>
public static class VoucherTypes
{
    public const string General = "GJ";
    public const string Closing = "CLS";
    public const string Allocation = "ALC";
    public const string Adjustment = "ADJ";
    public const string Reversal = "REV";
    public const string Revaluation = "FXR";
    /// <summary>Opening balances (initial setup or migration from another system).</summary>
    public const string OpeningBalance = "OPB";
    /// <summary>Cash receipt / cash payment (phiếu thu / phiếu chi).</summary>
    public const string CashReceipt = "CR";
    public const string CashPayment = "CP";
    /// <summary>Bank credit advice / bank debit advice (báo Có / báo Nợ).</summary>
    public const string BankReceipt = "BR";
    public const string BankPayment = "BP";
    /// <summary>System vouchers produced by the inventory module (posted together with the stock document or costing run).</summary>
    public const string InventoryReceipt = "IR";
    public const string InventoryIssue = "II";
    public const string InventoryCosting = "IC";
    public const string PurchaseInvoice = "PI";
    public const string SalesInvoice = "SI";
    /// <summary>Fixed-asset depreciation / tool allocation run, and asset disposal (ghi giảm).</summary>
    public const string Depreciation = "DEP";
    public const string AssetDisposal = "FAD";
    /// <summary>Loan interest accrual run.</summary>
    public const string LoanInterest = "LIA";

    /// <summary>Source systems whose vouchers may only be reversed through their own module.</summary>
    public static readonly IReadOnlySet<string> ModuleOwnedSources = new HashSet<string> { "HAK.STOCK", "HAK.COSTING", "HAK.INVOICE", "HAK.ASSET", "HAK.FX", "HAK.LOAN", "HAK.PRODUCTION" };

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { General, Closing, Allocation, Adjustment, Reversal, Revaluation, OpeningBalance, CashReceipt, CashPayment, BankReceipt, BankPayment, InventoryReceipt, InventoryIssue, InventoryCosting, PurchaseInvoice, SalesInvoice, Depreciation, AssetDisposal, LoanInterest };

    /// <summary>Business-document types: created only through the posting-rule engine, never as free-form vouchers.</summary>
    public static readonly IReadOnlySet<string> RuleDriven =
        new HashSet<string> { CashReceipt, CashPayment, BankReceipt, BankPayment };

    /// <summary>Types users may create directly; REV is produced only by ReverseVoucher.</summary>
    public static readonly IReadOnlySet<string> UserCreatable =
        new HashSet<string> { General, Closing, Allocation, Adjustment, Revaluation, OpeningBalance };
}

/// <summary>Debit = +1, Credit = −1. Signed balance = Σ side × amount.</summary>
public enum Side { Debit = 1, Credit = -1 }

public sealed class Voucher
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? BranchId { get; set; }
    public string VoucherType { get; set; } = VoucherTypes.General;
    /// <summary>Assigned on creation from the company/type/fiscal-year sequence.</summary>
    public string Number { get; set; } = "";
    public Guid FiscalYearId { get; set; }
    public DateOnly VoucherDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Description { get; set; } = "";
    public string CurrencyCode { get; set; } = "VND";
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public string? SourceSystem { get; set; }
    public string? SourceId { get; set; }
    public string? SourceVersion { get; set; }

    public Guid? ReversesVoucherId { get; set; }
    public Guid? AdjustsVoucherId { get; set; }

    /// <summary>Business event and rule that generated the lines (null for free-form GL vouchers).</summary>
    public string? BusinessEvent { get; set; }
    public Guid? PostingRuleId { get; set; }
    public int? PostingRuleVersion { get; set; }
    /// <summary>Cash fund / bank account the document is about (cash-book key).</summary>
    public Guid? MainAccountId { get; set; }
    public Guid? PartnerId { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? SubmittedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public long RowVersion { get; set; }

    public List<VoucherLine> Lines { get; set; } = [];

    public decimal TotalDebit => Lines.Where(l => l.Side == Side.Debit).Sum(l => l.Amount);
    public decimal TotalCredit => Lines.Where(l => l.Side == Side.Credit).Sum(l => l.Amount);

    public void EnsureEditable()
    {
        if (Status != VoucherStatus.Draft)
            throw new DomainException(
                Status is VoucherStatus.Posted or VoucherStatus.Reversed ? ErrorCodes.LedgerImmutable : ErrorCodes.InvalidStateTransition,
                $"Chứng từ {Number} ở trạng thái {Status}, chỉ chứng từ Nháp mới được sửa.");
    }

    public void Submit(Guid userId) { Transition(VoucherStatus.Draft, VoucherStatus.Submitted); SubmittedBy = userId; }

    public void Approve(Guid userId, bool requireDistinctApprover)
    {
        if (requireDistinctApprover && userId == CreatedBy)
            throw new DomainException(ErrorCodes.SodViolation, "Người duyệt không được trùng người lập chứng từ.");
        Transition(VoucherStatus.Submitted, VoucherStatus.Approved);
        ApprovedBy = userId;
    }

    public void Reject()
    {
        if (Status is not (VoucherStatus.Submitted or VoucherStatus.Approved))
            throw Invalid("trả lại");
        Status = VoucherStatus.Draft;
        SubmittedBy = null;
        ApprovedBy = null;
    }

    public void Cancel()
    {
        if (Status is not (VoucherStatus.Draft or VoucherStatus.Submitted)) throw Invalid("hủy");
        Status = VoucherStatus.Cancelled;
    }

    public void MarkPosted(Guid userId, DateTime at)
    {
        if (Status == VoucherStatus.Posted)
            throw new DomainException(ErrorCodes.AlreadyPosted, $"Chứng từ {Number} đã ghi sổ.");
        Transition(VoucherStatus.Approved, VoucherStatus.Posted);
        PostedBy = userId;
        PostedAt = at;
    }

    public void MarkReversed() => Transition(VoucherStatus.Posted, VoucherStatus.Reversed);

    private void Transition(VoucherStatus from, VoucherStatus to)
    {
        if (Status != from) throw Invalid($"chuyển sang {to}");
        Status = to;
    }

    private DomainException Invalid(string action) =>
        new(ErrorCodes.InvalidStateTransition, $"Không thể {action} chứng từ {Number} đang ở trạng thái {Status}.");
}

public sealed class VoucherLine
{
    public Guid Id { get; set; }
    public Guid VoucherId { get; set; }
    public Guid CompanyId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    public Guid? PartnerId { get; set; }
    public Side Side { get; set; }
    /// <summary>Amount in functional currency, always &gt; 0.</summary>
    public decimal Amount { get; set; }
    public string? ForeignCurrency { get; set; }
    public decimal? ForeignAmount { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string? Description { get; set; }
    /// <summary>Due date of the obligation (open-item accounts); defaults to the posting date.</summary>
    public DateOnly? DueDate { get; set; }
    /// <summary>Posted journal line (opposite side, same account and partner) this line settles on posting.</summary>
    public Guid? SettleJournalLineId { get; set; }
    /// <summary>Cash-flow statement code (Mã số B03-DN: 01–07, 21–27, 31–36, 61) chosen for a cash line; null = derived from the counter-account.</summary>
    public string? CashFlowCode { get; set; }
    /// <summary>Mã phân tích (optional management dimension), copied to the journal line on posting.</summary>
    public Guid? AnalysisCodeId { get; set; }
}

public sealed class VoucherHistory
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VoucherId { get; set; }
    public VoucherStatus? FromStatus { get; set; }
    public VoucherStatus ToStatus { get; set; }
    public string Action { get; set; } = "";
    public string? Note { get; set; }
    public Guid UserId { get; set; }
    public DateTime At { get; set; }
    public long VoucherRowVersion { get; set; }
}

public sealed class VoucherNumberSequence
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FiscalYearId { get; set; }
    public string VoucherType { get; set; } = "";
    public long LastNumber { get; set; }
    public long RowVersion { get; set; }
}

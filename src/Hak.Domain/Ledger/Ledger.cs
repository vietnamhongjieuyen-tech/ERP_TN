using Hak.Domain.Vouchers;

namespace Hak.Domain.Ledger;

/// <summary>Immutable once inserted (EF interceptor + DB triggers, docs/02 ADR-07).</summary>
public sealed class JournalEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VoucherId { get; set; }
    public Guid PeriodId { get; set; }
    public Guid RegimeVersionId { get; set; }
    public DateOnly PostingDate { get; set; }
    public string VoucherType { get; set; } = "";
    public string VoucherNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime PostedAt { get; set; }
    public Guid PostedBy { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public sealed class JournalLine
{
    public Guid Id { get; set; }
    public Guid JournalEntryId { get; set; }
    public Guid CompanyId { get; set; }
    public int LineNo { get; set; }
    public Guid AccountId { get; set; }
    public Guid? PartnerId { get; set; }
    public Side Side { get; set; }
    public decimal Amount { get; set; }
    public string? ForeignCurrency { get; set; }
    public decimal? ForeignAmount { get; set; }
    public decimal? ExchangeRate { get; set; }
    /// <summary>Denormalised for range queries without joining the header.</summary>
    public DateOnly PostingDate { get; set; }
    public string? Description { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Cash-flow statement code copied from the voucher line (null = derived at report time).</summary>
    public string? CashFlowCode { get; set; }
    /// <summary>Mã phân tích of the voucher line (optional).</summary>
    public Guid? AnalysisCodeId { get; set; }
}

/// <summary>Signed-balance arithmetic (docs/01 §1, master prompt §36).</summary>
public static class BalanceMath
{
    /// <summary>Closing = Opening + Debit − Credit (signed: debit positive).</summary>
    public static decimal Closing(decimal openingSigned, decimal debit, decimal credit) => openingSigned + debit - credit;

    /// <summary>Splits a signed balance into (debit column, credit column).</summary>
    public static (decimal Debit, decimal Credit) ToColumns(decimal signed)
        => signed >= 0 ? (signed, 0m) : (0m, -signed);
}

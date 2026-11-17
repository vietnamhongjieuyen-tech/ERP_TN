using Hak.Domain.Common;

namespace Hak.Domain.Banking;

/// <summary>
/// The six payment-order states are kept separate on purpose (master prompt §11): approval, sending,
/// bank execution, accounting and statement reconciliation are different facts recorded by different people.
/// </summary>
public enum PaymentOrderStatus
{
    Created = 1,
    Approved = 2,
    SentToBank = 3,
    ExecutedByBank = 4,
    Posted = 5,
    Reconciled = 6,
    Cancelled = 9,
}

public sealed class PaymentOrder
{
    public const string SourceSystem = "HAK.PAYMENT_ORDER";

    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Number { get; set; } = "";
    /// <summary>Bank (cash-equivalent) account paid from.</summary>
    public Guid BankAccountId { get; set; }
    public Guid PartnerId { get; set; }
    public string BusinessEvent { get; set; } = "SUPPLIER_PAYMENT";
    public decimal Amount { get; set; }
    public DateOnly RequestedDate { get; set; }
    public string Description { get; set; } = "";
    public string? BeneficiaryAccountNo { get; set; }
    public string? BeneficiaryBank { get; set; }
    /// <summary>Optional payable line to settle when the payment is accounted for.</summary>
    public Guid? SettleJournalLineId { get; set; }

    public PaymentOrderStatus Status { get; set; } = PaymentOrderStatus.Created;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? SentAt { get; set; }
    public Guid? SentBy { get; set; }
    public DateOnly? ExecutedDate { get; set; }
    public string? BankReference { get; set; }
    public Guid? VoucherId { get; set; }
    public string? CancelReason { get; set; }
    public long RowVersion { get; set; }

    public void Approve(Guid userId, bool requireDistinctApprover = true)
    {
        if (requireDistinctApprover && userId == CreatedBy) throw new DomainException(ErrorCodes.SodViolation, "Người duyệt lệnh chi không được trùng người lập.");
        Move(PaymentOrderStatus.Created, PaymentOrderStatus.Approved);
        ApprovedBy = userId;
    }

    public void MarkSent(Guid userId, DateTime at)
    {
        Move(PaymentOrderStatus.Approved, PaymentOrderStatus.SentToBank);
        SentBy = userId;
        SentAt = at;
    }

    public void MarkExecuted(DateOnly date, string bankReference)
    {
        if (string.IsNullOrWhiteSpace(bankReference))
            throw new DomainException(ErrorCodes.ValidationFailed, "Phải nhập mã giao dịch ngân hàng khi xác nhận đã thực hiện.");
        Move(PaymentOrderStatus.SentToBank, PaymentOrderStatus.ExecutedByBank);
        ExecutedDate = date;
        BankReference = bankReference.Trim();
    }

    public void MarkPosted(Guid voucherId)
    {
        Move(PaymentOrderStatus.ExecutedByBank, PaymentOrderStatus.Posted);
        VoucherId = voucherId;
    }

    /// <summary>The accounting voucher was reversed: the bank execution still stands, accounting must be redone.</summary>
    public void UndoPosting()
    {
        Move(PaymentOrderStatus.Posted, PaymentOrderStatus.ExecutedByBank);
        VoucherId = null;
    }

    public void MarkReconciled() => Move(PaymentOrderStatus.Posted, PaymentOrderStatus.Reconciled);

    public void UndoReconciliation() => Move(PaymentOrderStatus.Reconciled, PaymentOrderStatus.Posted);

    /// <summary>Only before the bank has executed it; afterwards money has moved and needs a real transaction.</summary>
    public void Cancel(string reason)
    {
        if (Status is not (PaymentOrderStatus.Created or PaymentOrderStatus.Approved or PaymentOrderStatus.SentToBank))
            throw new DomainException(ErrorCodes.InvalidStateTransition, $"Lệnh {Number} ở trạng thái {Status}; chỉ hủy được trước khi ngân hàng thực hiện.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy lệnh.");
        Status = PaymentOrderStatus.Cancelled;
        CancelReason = reason;
    }

    private void Move(PaymentOrderStatus from, PaymentOrderStatus to)
    {
        if (Status != from)
            throw new DomainException(ErrorCodes.InvalidStateTransition, $"Lệnh {Number} đang ở trạng thái {Status}, không chuyển sang {to} được.");
        Status = to;
    }
}

/// <summary>An imported bank statement for one bank account and date range. The source file is kept by checksum.</summary>
public sealed class BankStatement
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid BankAccountId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public DateTime ImportedAt { get; set; }
    public Guid ImportedBy { get; set; }
    public List<BankStatementLine> Lines { get; set; } = [];
}

public enum StatementLineStatus { Unmatched = 0, PendingConfirmation = 1, Matched = 2 }

public sealed class BankStatementLine
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StatementId { get; set; }
    public Guid BankAccountId { get; set; }
    public int LineNo { get; set; }
    public DateOnly TransactionDate { get; set; }
    /// <summary>Signed from the company's point of view: + money in (credit on the statement), − money out.</summary>
    public decimal Amount { get; set; }
    public string BankReference { get; set; } = "";
    public string Description { get; set; } = "";
    public string? CounterpartyName { get; set; }
    public string? CounterpartyAccount { get; set; }
    public StatementLineStatus Status { get; set; }
    /// <summary>JSON list of candidate journal line ids when several match equally well.</summary>
    public string? CandidatesJson { get; set; }
}

public enum MatchOrigin { Auto = 1, Manual = 2 }

/// <summary>Links a statement line to a posted journal line on the bank account. Never changes the voucher.</summary>
public sealed class ReconciliationMatch
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid StatementLineId { get; set; }
    public Guid JournalLineId { get; set; }
    public MatchOrigin Origin { get; set; }
    public int Score { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
}

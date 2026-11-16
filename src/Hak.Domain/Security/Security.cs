namespace Hak.Domain.Security;

public sealed class User
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>Lower-cased user name for case-insensitive uniqueness.</summary>
    public string NormalizedUserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>System administrators manage companies/users; they get no accounting rights implicitly.</summary>
    public bool IsSystemAdmin { get; set; }
    /// <summary>Consecutive failed logins; reset by a successful login or a password reset.</summary>
    public int FailedLoginCount { get; set; }
    /// <summary>Temporary lockout after too many failed logins (brute-force protection).</summary>
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class Role
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public List<RolePermission> Permissions { get; set; } = [];
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>Grants a role to a user inside one company only (data isolation per legal entity).</summary>
public sealed class UserCompanyRole
{
    public Guid UserId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class LoginSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>SHA-256 of the bearer token; the token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    public string? Device { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Hash-chained audit record (docs/02 ADR-08). Chain is per company; Guid.Empty = system scope.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public long Seq { get; set; }
    public DateTime At { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? Device { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Result { get; set; } = "OK";
    public long? EntityRowVersion { get; set; }
    public string? DataJson { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public static class Permissions
{
    public const string VoucherView = "voucher.view";
    public const string VoucherCreate = "voucher.create";
    public const string VoucherEditDraft = "voucher.edit_draft";
    public const string VoucherSubmit = "voucher.submit";
    public const string VoucherApprove = "voucher.approve";
    /// <summary>Approve vouchers above the company's approval limit (Company.ApprovalLimit).</summary>
    public const string VoucherApproveHigh = "voucher.approve_high";
    public const string VoucherPost = "voucher.post";
    public const string VoucherReverse = "voucher.reverse";
    public const string PeriodLock = "period.lock";
    public const string PeriodReopen = "period.reopen";
    public const string DataExport = "data.export";
    public const string DataImport = "data.import";
    public const string SensitiveView = "sensitive.view";
    public const string ConfigManage = "config.manage";
    public const string CatalogManage = "catalog.manage";
    public const string ReportView = "report.view";
    public const string AuditView = "audit.view";
    public const string SettlementManage = "settlement.manage";
    public const string CashDocument = "cash.document";
    public const string BankDocument = "bank.document";
    public const string PaymentOrderApprove = "payment_order.approve";
    public const string BankReconcile = "bank.reconcile";
    public const string StockDocument = "stock.document";
    public const string InvoiceDocument = "invoice.document";
    /// <summary>Create, activate and dispose fixed assets / tools (thẻ TSCĐ, CCDC).</summary>
    public const string AssetManage = "asset.manage";
    /// <summary>Create and activate loan contracts, link drawdowns/repayments, change rates.</summary>
    public const string LoanManage = "loan.manage";
}

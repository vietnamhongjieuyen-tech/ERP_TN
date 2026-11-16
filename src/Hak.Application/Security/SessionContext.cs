using Hak.Domain.Common;
using Hak.Domain.Security;

namespace Hak.Application.Security;

/// <summary>
/// Who is acting, in which company, with which permissions. Resolved server-side per request;
/// every engine operation takes one and checks it (master prompt §42: backend enforcement).
/// </summary>
public sealed record SessionContext(
    Guid UserId,
    Guid CompanyId,
    IReadOnlySet<string> Permissions,
    Guid? SessionId = null,
    string? Device = null)
{
    public void Require(string permission)
    {
        if (!Permissions.Contains(permission))
            throw new DomainException(ErrorCodes.Forbidden, $"Người dùng không có quyền '{permission}' tại công ty này.");
    }

    public void RequireCompany(Guid companyId)
    {
        if (companyId != CompanyId)
            throw new DomainException(ErrorCodes.Forbidden, "Dữ liệu không thuộc phạm vi công ty của phiên làm việc.");
    }
}

/// <summary>Default role → permission sets for the 10 roles in master prompt §42. Editable later per installation.</summary>
public static class RoleCatalog
{
    private static readonly string[] Viewer = [Permissions.VoucherView, Permissions.ReportView];

    public static readonly IReadOnlyDictionary<string, (string Name, string[] Perms)> Defaults =
        new Dictionary<string, (string, string[])>
        {
            ["ADMIN"] = ("Administrator", [Permissions.ConfigManage, Permissions.CatalogManage, Permissions.AuditView, Permissions.DataExport, Permissions.DataImport]),
            ["CHIEF_ACCOUNTANT"] = ("Kế toán trưởng", [.. Viewer, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit,
                Permissions.VoucherApprove, Permissions.VoucherApproveHigh, Permissions.VoucherPost, Permissions.VoucherReverse, Permissions.PeriodLock, Permissions.PeriodReopen,
                Permissions.CatalogManage, Permissions.DataExport, Permissions.DataImport, Permissions.SensitiveView, Permissions.AuditView,
                Permissions.SettlementManage, Permissions.CashDocument, Permissions.BankDocument, Permissions.PaymentOrderApprove, Permissions.BankReconcile, Permissions.StockDocument, Permissions.InvoiceDocument, Permissions.AssetManage, Permissions.LoanManage]),
            ["GL_ACCOUNTANT"] = ("Kế toán tổng hợp", [.. Viewer, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit,
                Permissions.VoucherPost, Permissions.CatalogManage, Permissions.DataExport, Permissions.DataImport,
                Permissions.SettlementManage, Permissions.CashDocument, Permissions.BankDocument, Permissions.BankReconcile, Permissions.StockDocument, Permissions.InvoiceDocument, Permissions.AssetManage, Permissions.LoanManage]),
            ["ARAP_ACCOUNTANT"] = ("Kế toán công nợ", [.. Viewer, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit, Permissions.SettlementManage, Permissions.InvoiceDocument]),
            ["BANK_ACCOUNTANT"] = ("Kế toán ngân hàng", [.. Viewer, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit,
                Permissions.BankDocument, Permissions.SettlementManage, Permissions.BankReconcile]),
            ["INVENTORY_ACCOUNTANT"] = ("Kế toán kho", [.. Viewer, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit, Permissions.StockDocument, Permissions.InvoiceDocument, Permissions.AssetManage, Permissions.LoanManage]),
            ["CASHIER"] = ("Thủ quỹ", [Permissions.VoucherView, Permissions.CashDocument, Permissions.VoucherCreate, Permissions.VoucherEditDraft, Permissions.VoucherSubmit]),
            ["APPROVER"] = ("Người duyệt", [.. Viewer, Permissions.VoucherApprove, Permissions.PaymentOrderApprove]),
            ["DIRECTOR"] = ("Giám đốc", [.. Viewer, Permissions.VoucherApprove, Permissions.VoucherApproveHigh, Permissions.SensitiveView, Permissions.PaymentOrderApprove]),
            ["INTERNAL_AUDITOR"] = ("Kiểm toán nội bộ", [.. Viewer, Permissions.AuditView]),
        };
}

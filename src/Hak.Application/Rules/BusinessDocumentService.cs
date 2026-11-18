using Hak.Application.Accounting;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Rules;

/// <summary>
/// Cash and bank documents (master prompt §10–11): phiếu thu (CR), phiếu chi (CP), báo Có (BR), báo Nợ (BP).
/// The document is turned into voucher lines by the active posting rule, then follows the normal
/// Draft → Submitted → Approved → Posted workflow of the Accounting Engine.
/// </summary>
public sealed class BusinessDocumentService(HakDbContext db, AccountingEngine engine, PostingRuleService rules)
{
    public async Task<Voucher> CreateAsync(SessionContext ctx, BusinessDocument doc)
    {
        ctx.Require(Permissions.VoucherCreate);   // before reading the document: a user without the right is told so first
        var (input, meta) = await PrepareAsync(ctx, doc);
        return await engine.CreateDraftCoreAsync(ctx, input, null, meta);
    }

    public async Task<Voucher> UpdateAsync(SessionContext ctx, Guid voucherId, long expectedRowVersion, BusinessDocument doc)
    {
        var (input, meta) = await PrepareAsync(ctx, doc);
        return await engine.UpdateDraftCoreAsync(ctx, voucherId, expectedRowVersion, input, meta);
    }

    private async Task<(VoucherInput, DocumentMeta)> PrepareAsync(SessionContext ctx, BusinessDocument doc)
    {
        var (permission, kind) = doc.DocumentType switch
        {
            VoucherTypes.CashReceipt or VoucherTypes.CashPayment => (Permissions.CashDocument, CashKind.Cash),
            VoucherTypes.BankReceipt or VoucherTypes.BankPayment => (Permissions.BankDocument, CashKind.Bank),
            _ => throw new DomainException(ErrorCodes.ValidationFailed, $"Loại chứng từ {doc.DocumentType} không phải chứng từ thu/chi."),
        };
        ctx.Require(permission);
        if (doc.Lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Chứng từ chưa có dòng chi tiết.");
        if (string.IsNullOrWhiteSpace(doc.BusinessEvent)) throw new DomainException(ErrorCodes.ValidationFailed, "Phải chọn nghiệp vụ.");

        if (doc.MainAccountId is { } mainId)
        {
            var main = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == mainId && a.CompanyId == ctx.CompanyId)
                       ?? throw new DomainException(ErrorCodes.AccountWrongCompany, "Quỹ/tài khoản ngân hàng không thuộc công ty.");
            if (main.CashKind != kind)
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"Tài khoản {main.Code} không phải {(kind == CashKind.Cash ? "quỹ tiền mặt" : "tài khoản tiền gửi ngân hàng")}.");
        }

        var rule = await rules.ResolveAsync(ctx.CompanyId, doc.DocumentType, doc.BusinessEvent, doc.PostingDate);
        var lines = await rules.EvaluateAsync(rule, doc);
        var input = new VoucherInput(doc.DocumentType, doc.VoucherDate, doc.PostingDate, doc.Description, lines,
            SourceSystem: doc.SourceSystem, SourceId: doc.SourceId);
        return (input, new DocumentMeta(doc.BusinessEvent, rule.Id, rule.Version, doc.MainAccountId, doc.PartnerId));
    }
}

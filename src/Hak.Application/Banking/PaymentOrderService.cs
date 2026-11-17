using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Rules;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Banking;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Banking;

public sealed record PaymentOrderInput(Guid BankAccountId, Guid PartnerId, decimal Amount, DateOnly RequestedDate, string Description,
    string? BeneficiaryAccountNo = null, string? BeneficiaryBank = null, Guid? SettleJournalLineId = null, string BusinessEvent = "SUPPLIER_PAYMENT");

/// <summary>
/// Payment orders (ủy nhiệm chi). Created → Approved → SentToBank → ExecutedByBank → Posted → Reconciled.
/// No accounting entry exists until the bank has executed the order; the BP voucher generated then goes
/// through the normal approval workflow, and the order becomes Posted only when that voucher is posted.
/// </summary>
public sealed class PaymentOrderService(HakDbContext db, BusinessDocumentService documents, AuditService audit, IClock clock)
{
    public async Task<PaymentOrder> CreateAsync(SessionContext ctx, PaymentOrderInput input)
    {
        ctx.Require(Permissions.BankDocument);
        if (input.Amount <= 0) throw new DomainException(ErrorCodes.AmountNotPositive, "Số tiền lệnh chi phải lớn hơn 0.");
        if (string.IsNullOrWhiteSpace(input.Description)) throw new DomainException(ErrorCodes.ValidationFailed, "Nội dung thanh toán bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            var bank = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.BankAccountId && a.CompanyId == ctx.CompanyId);
            if (bank is null || bank.CashKind != CashKind.Bank)
                throw new DomainException(ErrorCodes.ValidationFailed, "Tài khoản chi phải là tài khoản tiền gửi ngân hàng của công ty.");
            if (!await db.BusinessPartners.AnyAsync(p => p.Id == input.PartnerId && p.CompanyId == ctx.CompanyId))
                throw new DomainException(ErrorCodes.PartnerWrongCompany, "Đối tượng nhận tiền không thuộc công ty.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var dp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
            if (!MoneyMath.HasAtMostDecimals(input.Amount, dp))
                throw new DomainException(ErrorCodes.AmountPrecision, $"Số tiền vượt quá {dp} chữ số thập phân.");
            var fy = await db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= input.RequestedDate && f.EndDate >= input.RequestedDate)
                     ?? throw new DomainException(ErrorCodes.FiscalYearNotFound, $"Không có năm tài chính chứa ngày {input.RequestedDate}.");

            var seq = await db.VoucherNumberSequences.FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.FiscalYearId == fy.Id && s.VoucherType == "UNC");
            if (seq is null) db.VoucherNumberSequences.Add(seq = new VoucherNumberSequence { Id = Ids.New(), CompanyId = ctx.CompanyId, FiscalYearId = fy.Id, VoucherType = "UNC" });
            seq.LastNumber++;
            seq.RowVersion++;

            var po = new PaymentOrder
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Number = $"UNC{fy.Code}-{seq.LastNumber:D6}", BankAccountId = input.BankAccountId,
                PartnerId = input.PartnerId, BusinessEvent = input.BusinessEvent, Amount = input.Amount, RequestedDate = input.RequestedDate,
                Description = input.Description.Trim(), BeneficiaryAccountNo = input.BeneficiaryAccountNo, BeneficiaryBank = input.BeneficiaryBank,
                SettleJournalLineId = input.SettleJournalLineId, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
            };
            db.PaymentOrders.Add(po);
            await audit.AppendAsync(ctx.CompanyId, ctx, "payment_order.create", "payment_order", po.Id.ToString(), new { po.Number, po.Amount, po.PartnerId }, po.RowVersion);
            await db.SaveChangesAsync();
            return po;
        });
    }

    public Task<PaymentOrder> ApproveAsync(SessionContext ctx, Guid id)
        => TransitionAsync(ctx, id, Permissions.PaymentOrderApprove, "approve", po => po.Approve(ctx.UserId,
            db.Companies.AsNoTracking().Where(c => c.Id == ctx.CompanyId).Select(c => c.RequireDistinctApprover).First()));

    public Task<PaymentOrder> MarkSentAsync(SessionContext ctx, Guid id)
        => TransitionAsync(ctx, id, Permissions.BankDocument, "send", po => po.MarkSent(ctx.UserId, clock.UtcNow));

    public Task<PaymentOrder> CancelAsync(SessionContext ctx, Guid id, string reason)
        => TransitionAsync(ctx, id, Permissions.BankDocument, "cancel", po => po.Cancel(reason));

    /// <summary>
    /// Records the bank's execution and generates the BP Draft voucher (via the posting rule for the order's
    /// business event). The voucher description carries the bank reference so statement matching can find it.
    /// </summary>
    public async Task<(PaymentOrder Order, Voucher Voucher)> MarkExecutedAsync(SessionContext ctx, Guid id, DateOnly executedDate, string bankReference)
    {
        ctx.Require(Permissions.BankDocument);
        return await db.InTransactionAsync(async () =>
        {
            var po = await LoadAsync(ctx, id);
            po.MarkExecuted(executedDate, bankReference);
            po.RowVersion++;
            var v = await CreateVoucherCoreAsync(ctx, po);
            await audit.AppendAsync(ctx.CompanyId, ctx, "payment_order.execute", "payment_order", po.Id.ToString(),
                new { po.Number, executedDate, bankReference, voucher = v.Number }, po.RowVersion);
            await db.SaveChangesAsync();
            return (po, v);
        });
    }

    /// <summary>Regenerates the accounting voucher after the previous one was cancelled or reversed.</summary>
    public async Task<Voucher> RecreateVoucherAsync(SessionContext ctx, Guid id)
    {
        ctx.Require(Permissions.BankDocument);
        return await db.InTransactionAsync(async () =>
        {
            var po = await LoadAsync(ctx, id);
            if (po.Status != PaymentOrderStatus.ExecutedByBank)
                throw new DomainException(ErrorCodes.InvalidStateTransition, $"Lệnh {po.Number} ở trạng thái {po.Status}; chỉ lập lại chứng từ khi ngân hàng đã thực hiện mà chưa hạch toán.");
            if (po.VoucherId is { } existing && await db.Vouchers.AnyAsync(v => v.Id == existing && v.Status != VoucherStatus.Cancelled && v.Status != VoucherStatus.Reversed))
                throw new DomainException(ErrorCodes.DuplicateSource, "Lệnh đã có chứng từ hạch toán đang hiệu lực; một khoản chi không được ghi sổ hai lần.");
            var v = await CreateVoucherCoreAsync(ctx, po);
            po.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "payment_order.voucher", "payment_order", po.Id.ToString(), new { po.Number, voucher = v.Number }, po.RowVersion);
            await db.SaveChangesAsync();
            return v;
        });
    }

    private async Task<Voucher> CreateVoucherCoreAsync(SessionContext ctx, PaymentOrder po)
    {
        var attempt = await db.Vouchers.CountAsync(v => v.CompanyId == po.CompanyId && v.SourceSystem == PaymentOrder.SourceSystem
                                                        && v.SourceId != null && v.SourceId.StartsWith(po.Id.ToString())) + 1;
        var date = po.ExecutedDate!.Value;
        var v = await documents.CreateAsync(ctx, new BusinessDocument(VoucherTypes.BankPayment, po.BusinessEvent, date, date,
            $"{po.Description} (UNC {po.Number}, GD NH {po.BankReference})", po.PartnerId, po.BankAccountId,
            [new BusinessDocumentLine(po.Amount, SettleJournalLineId: po.SettleJournalLineId, Description: $"{po.Description} - {po.BankReference}")],
            PaymentOrder.SourceSystem, $"{po.Id}:{attempt}"));
        po.VoucherId = v.Id;
        return v;
    }

    private async Task<PaymentOrder> TransitionAsync(SessionContext ctx, Guid id, string permission, string action, Action<PaymentOrder> apply)
    {
        ctx.Require(permission);
        return await db.InTransactionAsync(async () =>
        {
            var po = await LoadAsync(ctx, id);
            var from = po.Status;
            apply(po);
            po.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, $"payment_order.{action}", "payment_order", po.Id.ToString(), new { po.Number, from, to = po.Status, po.CancelReason }, po.RowVersion);
            await db.SaveChangesAsync();
            return po;
        });
    }

    private async Task<PaymentOrder> LoadAsync(SessionContext ctx, Guid id)
        => await db.PaymentOrders.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == ctx.CompanyId)
           ?? throw new DomainException(ErrorCodes.NotFound, "Lệnh thanh toán không tồn tại.");

    /// <summary>Called by the posting engine inside the posting/reversal transaction.</summary>
    internal static async Task OnVoucherPostedAsync(HakDbContext db, Voucher v)
    {
        if (OrderIdOf(v) is not { } id) return;
        var po = await db.PaymentOrders.FirstAsync(p => p.Id == id && p.CompanyId == v.CompanyId);
        po.MarkPosted(v.Id);
        po.RowVersion++;
    }

    internal static async Task OnVoucherReversedAsync(HakDbContext db, Voucher original)
    {
        if (OrderIdOf(original) is not { } id) return;
        var po = await db.PaymentOrders.FirstAsync(p => p.Id == id && p.CompanyId == original.CompanyId);
        if (po.Status == PaymentOrderStatus.Reconciled)
            throw new DomainException(ErrorCodes.InvalidStateTransition, $"Lệnh {po.Number} đã đối chiếu sao kê; hủy kết quả đối chiếu trước khi đảo chứng từ.");
        po.UndoPosting();
        po.RowVersion++;
    }

    private static Guid? OrderIdOf(Voucher v)
        => v.SourceSystem == PaymentOrder.SourceSystem && v.SourceId is { } s && Guid.TryParse(s.Split(':')[0], out var id) ? id : null;
}

using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Inventory;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Trade;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Trade;

public sealed record OrderLineInput(Guid ItemId, decimal Quantity, decimal UnitPrice, PricingBasis Basis = PricingBasis.Quantity, decimal? VatRate = null, string? Description = null);

public sealed record OrderInput(InvoiceKind Kind, Guid PartnerId, DateOnly OrderDate, string Description, IReadOnlyList<OrderLineInput> Lines,
    DateOnly? ExpectedDate = null, string? ContractNo = null, string? Currency = null, decimal OverDeliveryTolerancePercent = 0);

public sealed record OrderLineFulfilment(int LineNo, Guid ItemId, string ItemCode, decimal Ordered, PricingBasis Basis, decimal UnitPrice,
    decimal StockPosted, decimal StockDraft, decimal Bdmt, decimal Invoiced, decimal InvoicedAmount, decimal ReturnedQty, decimal ReturnedAmount, decimal Outstanding);

public sealed record OrderInvoiceView(Guid InvoiceId, string Number, string InvoiceSeries, string InvoiceNo, InvoicePurpose Purpose, decimal Total, decimal Settled, decimal Open);

public sealed record OrderFulfilment(Guid OrderId, string Number, InvoiceKind Kind, OrderStatus Status, IReadOnlyList<OrderLineFulfilment> Lines, IReadOnlyList<OrderInvoiceView> Invoices);

/// <summary>Purchase/sales orders and their fulfilment chain (order → stock → invoice → payment).</summary>
public sealed class OrderService(HakDbContext db, AuditService audit, IClock clock)
{
    public async Task<TradeOrder> CreateAsync(SessionContext ctx, OrderInput input)
    {
        ctx.Require(Permissions.InvoiceDocument);
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Đơn hàng chưa có dòng.");
        if (string.IsNullOrWhiteSpace(input.Description)) throw new DomainException(ErrorCodes.ValidationFailed, "Diễn giải bắt buộc.");
        if (input.OverDeliveryTolerancePercent is < 0 or > 100) throw new DomainException(ErrorCodes.ValidationFailed, "Dung sai giao vượt từ 0 đến 100%.");
        return await db.InTransactionAsync(async () =>
        {
            if (!await db.BusinessPartners.AnyAsync(p => p.Id == input.PartnerId && p.CompanyId == ctx.CompanyId))
                throw new DomainException(ErrorCodes.PartnerWrongCompany, "Đối tượng không thuộc công ty.");
            if (input.Currency is { } cur && !await db.Currencies.AnyAsync(c => c.Code == cur))
                throw new DomainException(ErrorCodes.ValidationFailed, $"Đồng tiền {cur} chưa khai báo.");
            var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
            var fy = await db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= input.OrderDate && f.EndDate >= input.OrderDate)
                     ?? throw new DomainException(ErrorCodes.FiscalYearNotFound, $"Không có năm tài chính chứa ngày {input.OrderDate:dd/MM/yyyy}.");
            var order = new TradeOrder
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Kind = input.Kind, PartnerId = input.PartnerId, ContractNo = input.ContractNo, OrderDate = input.OrderDate,
                ExpectedDate = input.ExpectedDate, Currency = input.Currency, Description = input.Description.Trim(),
                OverDeliveryTolerancePercent = input.OverDeliveryTolerancePercent, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
                Number = await NextNumberAsync(ctx.CompanyId, fy, input.Kind == InvoiceKind.Purchase ? "DMH" : "DBH"),
            };
            var n = 0;
            foreach (var l in input.Lines)
            {
                n++;
                if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive) throw new DomainException(ErrorCodes.NotFound, $"Dòng {n}: vật tư không tồn tại.");
                if (l.Quantity <= 0 || !MoneyMath.HasAtMostDecimals(l.Quantity, MoneyMath.StorageScale)) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: số lượng không hợp lệ.");
                if (l.UnitPrice < 0 || !MoneyMath.HasAtMostDecimals(l.UnitPrice, MoneyMath.StorageScale)) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: đơn giá không hợp lệ.");
                if (l.Basis == PricingBasis.Bdmt && !item.TracksDryness) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: vật tư {item.Code} không theo dõi BDMT.");
                order.Lines.Add(new TradeOrderLine
                {
                    Id = Ids.New(), OrderId = order.Id, CompanyId = ctx.CompanyId, LineNo = n, ItemId = l.ItemId, Quantity = l.Quantity, Basis = l.Basis,
                    UnitPrice = l.UnitPrice, VatRate = l.VatRate, Description = l.Description,
                });
            }
            db.TradeOrders.Add(order);
            await audit.AppendAsync(ctx.CompanyId, ctx, "order.create", "order", order.Id.ToString(), new { order.Number, order.Kind, lines = order.Lines.Count });
            await db.SaveChangesAsync();
            return order;
        });
    }

    public Task<TradeOrder> ConfirmAsync(SessionContext ctx, Guid id) => TransitionAsync(ctx, id, Permissions.VoucherApprove, "confirm", async (o, company) =>
    {
        o.Confirm(ctx.UserId, clock.UtcNow, company.RequireDistinctApprover);
        await Task.CompletedTask;
    });

    public Task<TradeOrder> CloseAsync(SessionContext ctx, Guid id, string reason)
        => TransitionAsync(ctx, id, Permissions.InvoiceDocument, "close", (o, _) => { o.Close(reason); return Task.CompletedTask; });

    /// <summary>Cancelling is refused once goods moved or an invoice exists; close the order instead.</summary>
    public Task<TradeOrder> CancelAsync(SessionContext ctx, Guid id, string reason)
        => TransitionAsync(ctx, id, Permissions.InvoiceDocument, "cancel", async (o, _) =>
        {
            var used = await (from l in db.StockDocumentLines join d in db.StockDocuments on l.DocumentId equals d.Id
                              where l.OrderId == o.Id && d.Status != StockDocumentStatus.Cancelled select l.Id).AnyAsync()
                       || await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                                 where l.OrderId == o.Id && i.Status != InvoiceStatus.Cancelled select l.Id).AnyAsync();
            if (used) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Đơn {o.Number} đã có phiếu kho/hóa đơn; dùng chức năng đóng đơn.");
            o.Cancel(reason);
        });

    public async Task<OrderFulfilment> FulfilmentAsync(SessionContext ctx, Guid orderId)
    {
        ctx.Require(Permissions.ReportView);
        var order = await db.TradeOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Đơn hàng không tồn tại.");
        var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
        var stock = await (from l in db.StockDocumentLines join d in db.StockDocuments on l.DocumentId equals d.Id
                           where l.OrderId == orderId && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft)
                           select new { l.DocumentId, l.LineNo, l.OrderLineNo, l.Quantity, d.Status }).AsNoTracking().ToListAsync();
        var posted = stock.Where(s => s.Status == StockDocumentStatus.Posted).ToList();
        var docIds = posted.Select(p => p.DocumentId).Distinct().ToList();
        var measures = await db.StockMeasurements.AsNoTracking().Where(m => docIds.Contains(m.DocumentId) && m.SupersededAt == null)
            .Select(m => new { m.DocumentId, m.LineNo, m.Bdmt }).ToListAsync();
        var invLines = await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                              where l.OrderId == orderId && i.Status == InvoiceStatus.Posted
                              select new { i.Id, l.LineNo, l.OrderLineNo, l.StockQuantity, l.FunctionalAmount, i.Purpose }).AsNoTracking().ToListAsync();
        var origInvoiceIds = invLines.Select(x => x.Id).Distinct().ToList();
        var returns = await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                             where i.Status == InvoiceStatus.Posted && i.Purpose == InvoicePurpose.Return && i.AdjustsInvoiceId != null && origInvoiceIds.Contains(i.AdjustsInvoiceId.Value)
                             select new { Orig = i.AdjustsInvoiceId!.Value, l.AdjustsLineNo, l.StockQuantity, l.FunctionalAmount }).AsNoTracking().ToListAsync();

        var lines = order.Lines.OrderBy(l => l.LineNo).Select(ol =>
        {
            var p = posted.Where(s => s.OrderLineNo == ol.LineNo).ToList();
            var inv = invLines.Where(x => x.OrderLineNo == ol.LineNo && x.Purpose == InvoicePurpose.Normal).ToList();
            var ret = returns.Where(r => inv.Any(i => i.Id == r.Orig && i.LineNo == r.AdjustsLineNo)).ToList();
            var bdmt = measures.Where(m => p.Any(s => s.DocumentId == m.DocumentId && s.LineNo == m.LineNo)).Sum(m => m.Bdmt);
            var stockPosted = p.Sum(s => s.Quantity);
            return new OrderLineFulfilment(ol.LineNo, ol.ItemId, items[ol.ItemId].Code, ol.Quantity, ol.Basis, ol.UnitPrice, stockPosted,
                stock.Where(s => s.Status == StockDocumentStatus.Draft && s.OrderLineNo == ol.LineNo).Sum(s => s.Quantity), bdmt,
                inv.Sum(x => x.StockQuantity ?? 0), inv.Sum(x => x.FunctionalAmount), ret.Sum(r => r.StockQuantity ?? 0), ret.Sum(r => r.FunctionalAmount),
                ol.Quantity - stockPosted);
        }).ToList();

        // Invoices touching the order and how much of each is settled (paid / offset) per the AR/AP subledger.
        var allInvoiceIds = origInvoiceIds.Concat(await db.CommercialInvoices.Where(i => i.AdjustsInvoiceId != null && origInvoiceIds.Contains(i.AdjustsInvoiceId.Value) && i.Status == InvoiceStatus.Posted)
            .Select(i => i.Id).ToListAsync()).Distinct().ToList();
        var invoices = new List<OrderInvoiceView>();
        foreach (var inv in await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).Where(i => allInvoiceIds.Contains(i.Id)).OrderBy(i => i.PostingDate).ToListAsync())
        {
            var total = inv.Lines.Sum(l => l.FunctionalAmount + l.VatAmount);
            var partyLine = await (from jl in db.JournalLines join e in db.JournalEntries on jl.JournalEntryId equals e.Id
                                   join a in db.Accounts on jl.AccountId equals a.Id
                                   where e.VoucherId == inv.VoucherId && jl.PartnerId == inv.PartnerId && a.OpenItemNature != Domain.Catalog.OpenItemNature.None
                                   select jl.Id).FirstOrDefaultAsync();
            var settled = await db.SettlementAllocations.Where(a => a.VoidedAt == null && (a.DebitLineId == partyLine || a.CreditLineId == partyLine))
                .SumAsync(a => (decimal?)a.Amount) ?? 0;
            invoices.Add(new OrderInvoiceView(inv.Id, inv.Number, inv.InvoiceSeries, inv.InvoiceNo, inv.Purpose, total, settled, total - settled));
        }
        return new OrderFulfilment(order.Id, order.Number, order.Kind, order.Status, lines, invoices);
    }

    private async Task<TradeOrder> TransitionAsync(SessionContext ctx, Guid id, string permission, string action, Func<TradeOrder, Company, Task> apply)
    {
        ctx.Require(permission);
        return await db.InTransactionAsync(async () =>
        {
            var o = await db.TradeOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Đơn hàng không tồn tại.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var from = o.Status;
            await apply(o, company);
            o.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, $"order.{action}", "order", o.Id.ToString(), new { o.Number, from, to = o.Status, o.StatusReason }, o.RowVersion);
            await db.SaveChangesAsync();
            return o;
        });
    }

    private async Task<string> NextNumberAsync(Guid companyId, FiscalYear fy, string prefix)
    {
        var key = "ORD-" + prefix;
        var seq = await db.VoucherNumberSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.FiscalYearId == fy.Id && s.VoucherType == key);
        if (seq is null) db.VoucherNumberSequences.Add(seq = new VoucherNumberSequence { Id = Ids.New(), CompanyId = companyId, FiscalYearId = fy.Id, VoucherType = key });
        seq.LastNumber++;
        seq.RowVersion++;
        return $"{prefix}{fy.Code}-{seq.LastNumber:D6}";
    }

    /// <summary>
    /// Shared guard used by stock documents: the order line must belong to a confirmed order of the right kind and
    /// partner, for the same item, and cumulative quantity (draft + posted) may not exceed ordered × (1 + tolerance).
    /// </summary>
    internal static async Task CheckStockAgainstOrderAsync(HakDbContext db, Guid companyId, StockDocumentType type, Guid? partnerId,
        Guid orderId, int orderLineNo, Guid itemId, decimal quantity, Guid? excludeDocumentId, decimal alsoInThisDocument)
    {
        var order = await db.TradeOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId && o.CompanyId == companyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Đơn hàng không tồn tại.");
        var expected = type switch { StockDocumentType.Receipt => InvoiceKind.Purchase, StockDocumentType.Issue => InvoiceKind.Sales, _ => (InvoiceKind?)null };
        if (expected is null || order.Kind != expected)
            throw new DomainException(ErrorCodes.OrderMismatch, $"Phiếu kho loại {type} không gắn được với đơn {order.Number}.");
        if (order.Status != OrderStatus.Confirmed)
            throw new DomainException(ErrorCodes.OrderMismatch, $"Đơn {order.Number} ở trạng thái {order.Status}; chỉ nhận/giao theo đơn đã xác nhận.");
        if (order.PartnerId != partnerId)
            throw new DomainException(ErrorCodes.OrderMismatch, $"Đối tượng của phiếu kho khác đối tượng của đơn {order.Number}.");
        var line = order.Lines.FirstOrDefault(l => l.LineNo == orderLineNo)
                   ?? throw new DomainException(ErrorCodes.NotFound, $"Đơn {order.Number} không có dòng {orderLineNo}.");
        if (line.ItemId != itemId) throw new DomainException(ErrorCodes.OrderMismatch, $"Vật tư khác vật tư ở dòng {orderLineNo} của đơn {order.Number}.");
        var used = (await (from l in db.StockDocumentLines join d in db.StockDocuments on l.DocumentId equals d.Id
                           where l.OrderId == orderId && l.OrderLineNo == orderLineNo && d.Id != excludeDocumentId
                                 && (d.Status == StockDocumentStatus.Draft || d.Status == StockDocumentStatus.Posted)
                           select l.Quantity).ToListAsync()).Sum();
        var limit = line.Quantity * (1 + order.OverDeliveryTolerancePercent / 100m);
        if (used + alsoInThisDocument + quantity > limit)
            throw new DomainException(ErrorCodes.OverDelivered,
                $"Đơn {order.Number} dòng {orderLineNo}: đặt {line.Quantity} (dung sai {order.OverDeliveryTolerancePercent}%), đã có {used + alsoInThisDocument}, thêm {quantity} vượt giới hạn {limit:0.####}.");
    }
}

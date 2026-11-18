using Hak.Application.Security;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Trade;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Trade;

/// <summary>One line of the sales detail book (S35-DN): a sale, or a return / discount shown as a deduction.</summary>
public sealed record SalesBookRow(DateOnly PostingDate, string Number, DateOnly InvoiceDate, string InvoiceRef, string Description, string? CounterAccount,
    decimal? Quantity, decimal? UnitPrice, decimal Revenue, decimal Tax, decimal Deduction);

public sealed record SalesBook(Guid ItemId, string ItemCode, string ItemName, string Unit, IReadOnlyList<SalesBookRow> Rows,
    decimal TotalQuantity, decimal TotalRevenue, decimal TotalTax, decimal TotalDeduction, decimal NetRevenue, decimal CostOfSales, bool CostComplete, decimal GrossProfit);

public sealed partial class InvoiceService
{
    /// <summary>
    /// Sổ chi tiết bán hàng of one item over a period: posted sales invoices (and their posted increases) as revenue, posted returns and
    /// discounts as deductions, all in functional currency. Cost of sales = the valuation of the stock issues those invoices cover, less the
    /// returns' receipts (pro rata where an invoice covers part of a stock line). "Thuế" (taxes deducted from revenue such as export or
    /// special consumption tax) is not tracked separately and stays 0 — VAT is not revenue.
    /// </summary>
    public async Task<SalesBook> SalesBookAsync(SessionContext ctx, Guid itemId, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == ctx.CompanyId)
            ?? throw new DomainException(ErrorCodes.NotFound, "Vật tư không tồn tại.");

        var invoices = await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.CompanyId == ctx.CompanyId && i.Kind == InvoiceKind.Sales && i.Status == InvoiceStatus.Posted && i.PostingDate >= from && i.PostingDate <= to)
            .OrderBy(i => i.PostingDate).ThenBy(i => i.Number).ToListAsync();
        // Adjustment lines may leave the item empty and point at the line of the original invoice instead.
        var originalIds = invoices.Where(i => i.AdjustsInvoiceId != null).Select(i => i.AdjustsInvoiceId!.Value).Distinct().ToList();
        var originals = await db.CommercialInvoiceLines.AsNoTracking().Where(l => originalIds.Contains(l.InvoiceId))
            .Select(l => new { l.InvoiceId, l.LineNo, l.ItemId }).ToListAsync();
        Guid? ItemOf(CommercialInvoice inv, CommercialInvoiceLine l) => l.ItemId
            ?? originals.FirstOrDefault(o => o.InvoiceId == inv.AdjustsInvoiceId && o.LineNo == l.AdjustsLineNo)?.ItemId;

        // TK đối ứng: the receivable line of each invoice's entry (the line carrying the customer).
        var voucherIds = invoices.Where(i => i.VoucherId != null).Select(i => i.VoucherId!.Value).ToList();
        var counters = await (from l in db.JournalLines
                              join e in db.JournalEntries on l.JournalEntryId equals e.Id
                              join a in db.Accounts on l.AccountId equals a.Id
                              where voucherIds.Contains(e.VoucherId) && l.PartnerId != null
                              select new { e.VoucherId, l.PartnerId, a.Code }).AsNoTracking().ToListAsync();

        var rows = new List<SalesBookRow>();
        var stockShares = new List<(Guid DocumentId, int LineNo, decimal? Quantity, int Sign)>();
        foreach (var inv in invoices)
        {
            var counter = counters.FirstOrDefault(c => c.VoucherId == inv.VoucherId && c.PartnerId == inv.PartnerId)?.Code;
            var reference = $"{inv.InvoiceSeries}{inv.InvoiceNo}";
            foreach (var l in inv.Lines.Where(l => ItemOf(inv, l) == itemId).OrderBy(l => l.LineNo))
            {
                var text = l.Description ?? inv.Description;
                if (inv.Purpose is InvoicePurpose.Normal or InvoicePurpose.Increase)
                {
                    var qty = inv.Purpose == InvoicePurpose.Normal ? l.BasisQuantity : (decimal?)null;
                    var price = qty is > 0 ? Math.Round(l.FunctionalAmount / qty.Value, 4, MidpointRounding.AwayFromZero) : (decimal?)null;
                    rows.Add(new(inv.PostingDate, inv.Number, inv.InvoiceDate, reference, text, counter, qty, price, l.FunctionalAmount, 0, 0));
                    if (inv.Purpose == InvoicePurpose.Normal && l.StockDocumentId is { } d && l.StockLineNo is { } n) stockShares.Add((d, n, l.StockQuantity, 1));
                }
                else
                {
                    rows.Add(new(inv.PostingDate, inv.Number, inv.InvoiceDate, reference,
                        $"{(inv.Purpose == InvoicePurpose.Return ? "Hàng bán bị trả lại" : "Giảm giá / chiết khấu")}: {text}", counter, null, null, 0, 0, l.FunctionalAmount));
                    if (inv.Purpose == InvoicePurpose.Return && l.StockDocumentId is { } d && l.StockLineNo is { } n) stockShares.Add((d, n, l.StockQuantity, -1));
                }
            }
        }

        // Cost of sales from the valuations of the covered stock lines.
        var docIds = stockShares.Select(s => s.DocumentId).Distinct().ToList();
        var moves = await db.StockMovements.AsNoTracking().Where(m => docIds.Contains(m.DocumentId) && m.ItemId == itemId)
            .Select(m => new { m.Id, m.DocumentId, m.LineNo, m.Quantity }).ToListAsync();
        var moveIds = moves.Select(m => m.Id).ToList();
        var values = await db.StockValuations.AsNoTracking().Where(v => moveIds.Contains(v.MovementId) && v.VoidedAt == null)
            .ToDictionaryAsync(v => v.MovementId, v => v.Value);
        decimal cost = 0; var complete = true;
        foreach (var s in stockShares)
            foreach (var mv in moves.Where(m => m.DocumentId == s.DocumentId && m.LineNo == s.LineNo))
            {
                if (!values.TryGetValue(mv.Id, out var v)) { complete = false; continue; }   // issue not yet costed (monthly average)
                var share = s.Quantity is { } q && mv.Quantity != 0 ? q / Math.Abs(mv.Quantity) : 1m;
                cost += s.Sign * Math.Round(Math.Abs(v) * Math.Min(share, 1m), 0, MidpointRounding.AwayFromZero);
            }

        var revenue = rows.Sum(r => r.Revenue); var tax = rows.Sum(r => r.Tax); var deduction = rows.Sum(r => r.Deduction);
        var net = revenue - tax - deduction;
        return new SalesBook(item.Id, item.Code, item.Name, item.Unit, rows, rows.Sum(r => r.Quantity ?? 0), revenue, tax, deduction, net, cost, complete, net - cost);
    }
}

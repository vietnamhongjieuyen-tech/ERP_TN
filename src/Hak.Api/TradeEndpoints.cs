using Hak.Application.Trade;
using Hak.Data;
using Hak.Domain.Security;
using Hak.Domain.Trade;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

public sealed record ReturnReceiptRequest(int OriginalLineNo, decimal Quantity, DateOnly Date, Guid? WarehouseId, string? Description);

public static class TradeEndpoints
{
    private static object Dto(CommercialInvoice i) => new
    {
        i.Id, i.Number, kind = i.Kind.ToString(), purpose = i.Purpose.ToString(), i.AdjustsInvoiceId, i.InvoiceSeries, i.InvoiceNo, i.InvoiceDate, i.PostingDate, i.DueDate, i.PartnerId, i.Currency, i.ExchangeRate,
        i.Description, status = i.Status.ToString(), i.VoucherId, i.CreatedBy, i.RowVersion,
        total = i.Lines.Sum(l => l.FunctionalAmount), vat = i.Lines.Sum(l => l.VatAmount),
        lines = i.Lines.OrderBy(l => l.LineNo).Select(l => new
        {
            l.LineNo, l.AdjustsLineNo, l.ItemId, l.StockDocumentId, l.StockLineNo, l.StockQuantity, l.AccountId, basis = l.Basis.ToString(), l.BasisQuantity, l.UnitPrice,
            l.Amount, l.FunctionalAmount, l.VatRate, l.VatAmount, l.Description,
        }),
    };

    public static void MapTradeEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/invoices", async (HttpContext http, HakDbContext db, InvoiceKind? kind) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            var q = db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).Where(i => i.CompanyId == ctx.CompanyId);
            if (kind is not null) q = q.Where(i => i.Kind == kind);
            return Results.Ok((await q.OrderByDescending(i => i.PostingDate).ThenByDescending(i => i.Number).Take(500).ToListAsync()).Select(Dto));
        });
        api.MapPost("/invoices", async (HttpContext http, InvoiceInput r, InvoiceService s) => Results.Ok(Dto(await s.CreateAsync(await http.SessionAsync(), r))));
        api.MapGet("/invoices/{id:guid}/price-differences", async (HttpContext http, Guid id, InvoiceService s) => Results.Ok(await s.PriceDifferencesAsync(await http.SessionAsync(), id)));
        api.MapPost("/invoices/{id:guid}/post", async (HttpContext http, Guid id, bool? acknowledgePriceDifferences, InvoiceService s) =>
            Results.Ok(Dto(await s.PostAsync(await http.SessionAsync(), id, acknowledgePriceDifferences ?? false))));
        api.MapPost("/invoices/{id:guid}/cancel", async (HttpContext http, Guid id, ReasonRequest r, InvoiceService s) => Results.Ok(Dto(await s.CancelAsync(await http.SessionAsync(), id, r.Reason))));
        api.MapPost("/invoices/{id:guid}/reverse", async (HttpContext http, Guid id, ReverseRequest r, InvoiceService s) =>
            Results.Ok(Dto(await s.ReverseAsync(await http.SessionAsync(), id, r.ReversalDate, r.Reason))));
        // Purchase return, step 1: a Draft issue back to the supplier (valued by the warehouse costing method when posted).
        api.MapPost("/invoices/{id:guid}/return-issue", async (HttpContext http, Guid id, ReturnReceiptRequest r, InvoiceService s) =>
        {
            var d = await s.CreatePurchaseReturnIssueAsync(await http.SessionAsync(), id, r.OriginalLineNo, r.Quantity, r.Date, r.Description);
            return Results.Ok(new { d.Id, d.Number });
        });
        api.MapPost("/invoices/{id:guid}/return-receipt", async (HttpContext http, Guid id, ReturnReceiptRequest r, InvoiceService s) =>
        {
            var d = await s.CreateReturnReceiptAsync(await http.SessionAsync(), id, r.OriginalLineNo, r.Quantity, r.Date, r.WarehouseId, r.Description);
            return Results.Ok(new { d.Id, d.Number, value = d.Lines[0].Amount });
        });
        api.MapPost("/invoices/adjustments", async (HttpContext http, AdjustmentInput r, InvoiceService s) => Results.Ok(Dto(await s.CreateAdjustmentAsync(await http.SessionAsync(), r))));
        api.MapGet("/orders", async (HttpContext http, HakDbContext db, InvoiceKind? kind) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            var q = db.TradeOrders.AsNoTracking().Include(o => o.Lines).Where(o => o.CompanyId == ctx.CompanyId);
            if (kind is not null) q = q.Where(o => o.Kind == kind);
            return Results.Ok((await q.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Number).Take(500).ToListAsync()).Select(o => new
            {
                o.Id, o.Number, kind = o.Kind.ToString(), o.PartnerId, o.ContractNo, o.OrderDate, o.ExpectedDate, o.Currency, o.Description,
                o.OverDeliveryTolerancePercent, status = o.Status.ToString(), o.StatusReason, o.CreatedBy,
                lines = o.Lines.OrderBy(l => l.LineNo).Select(l => new { l.LineNo, l.ItemId, l.Quantity, basis = l.Basis.ToString(), l.UnitPrice, l.VatRate, l.Description }),
            }));
        });
        api.MapPost("/orders", async (HttpContext http, OrderInput r, OrderService s) => Results.Ok(new { (await s.CreateAsync(await http.SessionAsync(), r)).Id }));
        api.MapPost("/orders/{id:guid}/confirm", async (HttpContext http, Guid id, OrderService s) => { await s.ConfirmAsync(await http.SessionAsync(), id); return Results.NoContent(); });
        api.MapPost("/orders/{id:guid}/close", async (HttpContext http, Guid id, ReasonRequest r, OrderService s) => { await s.CloseAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
        api.MapPost("/orders/{id:guid}/cancel", async (HttpContext http, Guid id, ReasonRequest r, OrderService s) => { await s.CancelAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
        api.MapGet("/orders/{id:guid}/fulfilment", async (HttpContext http, Guid id, OrderService s) => Results.Ok(await s.FulfilmentAsync(await http.SessionAsync(), id)));
        api.MapPost("/stock-documents/{id:guid}/cancel", async (HttpContext http, Guid id, ReasonRequest r, Hak.Application.Inventory.StockService s) =>
        { await s.CancelDocumentAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });
        api.MapGet("/reports/uninvoiced", async (HttpContext http, InvoiceKind kind, DateOnly asOf, InvoiceService s) =>
            Results.Ok(await s.UninvoicedAsync(await http.SessionAsync(), kind, asOf)));
        api.MapGet("/reports/sales-book", async (HttpContext http, Guid itemId, DateOnly from, DateOnly to, InvoiceService s) =>
            Results.Ok(await s.SalesBookAsync(await http.SessionAsync(), itemId, from, to)));
    }
}

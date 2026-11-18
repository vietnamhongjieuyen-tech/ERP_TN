using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Inventory;
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

public sealed record InvoiceLineInput(decimal UnitPrice, Guid? StockDocumentId = null, int? StockLineNo = null, decimal? StockQuantity = null,
    PricingBasis Basis = PricingBasis.Quantity, decimal? Quantity = null, Guid? AccountId = null, decimal? VatRate = null, string? Description = null,
    Guid? OrderId = null, int? OrderLineNo = null, Guid? AnalysisCodeId = null);

public sealed record InvoiceInput(InvoiceKind Kind, Guid PartnerId, string InvoiceSeries, string InvoiceNo, DateOnly InvoiceDate, DateOnly PostingDate,
    string Description, IReadOnlyList<InvoiceLineInput> Lines, DateOnly? DueDate = null, string? Currency = null, decimal? ExchangeRate = null);

public sealed record PriceDifferenceWarning(int LineNo, string ReceiptNumber, decimal ReceiptValue, decimal InvoiceValue, decimal Difference,
    decimal ReceiptQuantity, decimal QuantityInStock, decimal InventoryPart, decimal CogsPart, string Method);

public sealed record UninvoicedLine(Guid StockDocumentId, string StockDocumentNumber, DateOnly PostingDate, int LineNo, Guid ItemId, string ItemCode,
    decimal Quantity, decimal InvoicedQuantity, decimal? Bdmt, Guid? PartnerId);

/// <summary>
/// Purchase and sales invoices (master prompt §12–13). Account roles used (company configuration, never hard-coded):
/// AP_TRADE, AR_TRADE, GRNI (goods received not invoiced), VAT_INPUT, VAT_OUTPUT, SALES_REVENUE.
/// Purchase: stock receipts are carried on GRNI; the invoice moves GRNI to AP (+ input VAT). A Draft receipt is valued
/// from the invoice (BDMT × price) and posted with it. Sales: the issue already booked cost of goods; the invoice books
/// AR / revenue / output VAT. A stock line can never be invoiced beyond its quantity (no double revenue).
/// </summary>
public sealed partial class InvoiceService(HakDbContext db, AccountingEngine engine, StockService stock, AuditService audit, IClock clock)
{
    public const string Source = "HAK.INVOICE";

    public async Task<CommercialInvoice> CreateAsync(SessionContext ctx, InvoiceInput input)
    {
        ctx.Require(Permissions.InvoiceDocument);
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Hóa đơn chưa có dòng.");
        if (string.IsNullOrWhiteSpace(input.InvoiceNo) || string.IsNullOrWhiteSpace(input.InvoiceSeries))
            throw new DomainException(ErrorCodes.ValidationFailed, "Phải nhập ký hiệu và số hóa đơn.");
        if (string.IsNullOrWhiteSpace(input.Description)) throw new DomainException(ErrorCodes.ValidationFailed, "Diễn giải bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            if (!await db.BusinessPartners.AnyAsync(p => p.Id == input.PartnerId && p.CompanyId == ctx.CompanyId))
                throw new DomainException(ErrorCodes.PartnerWrongCompany, "Đối tượng không thuộc công ty.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var (currency, rate, curDp, funcDp) = await CurrencyAsync(company, input.Currency, input.ExchangeRate);
            await CheckDuplicateAsync(ctx.CompanyId, input.Kind, input.PartnerId, input.InvoiceSeries.Trim(), input.InvoiceNo.Trim(), null);

            var fy = await db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= input.PostingDate && f.EndDate >= input.PostingDate)
                     ?? throw new DomainException(ErrorCodes.FiscalYearNotFound, $"Không có năm tài chính chứa ngày {input.PostingDate:dd/MM/yyyy}.");
            var inv = new CommercialInvoice
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Kind = input.Kind, InvoiceSeries = input.InvoiceSeries.Trim(), InvoiceNo = input.InvoiceNo.Trim(),
                InvoiceDate = input.InvoiceDate, PostingDate = input.PostingDate, DueDate = input.DueDate, PartnerId = input.PartnerId,
                Currency = currency, ExchangeRate = rate, Description = input.Description.Trim(), CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
            };
            inv.Number = await NextNumberAsync(ctx.CompanyId, fy, input.Kind == InvoiceKind.Purchase ? "HDM" : "HDB");
            var n = 0;
            foreach (var l in input.Lines)
                inv.Lines.Add(await BuildLineAsync(ctx, inv, l, ++n, curDp, funcDp));
            db.CommercialInvoices.Add(inv);
            await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.create", "invoice", inv.Id.ToString(),
                new { inv.Number, inv.Kind, inv.InvoiceSeries, inv.InvoiceNo, total = inv.Lines.Sum(x => x.FunctionalAmount + x.VatAmount) });
            await db.SaveChangesAsync();
            return inv;
        });
    }

    /// <summary>
    /// Posts the invoice (and, for purchases, any Draft receipt it values) in one transaction. Re-checks quantities
    /// and BDMT against the current measurements so a superseded certificate cannot be invoiced at a stale value.
    /// </summary>
    /// <summary>
    /// Price differences the posting would book (purchase invoices against receipts already posted at another value):
    /// the part still in stock adjusts inventory, the part already issued goes to cost of goods sold (role COGS).
    /// Shown to the user before posting; posting requires explicit acknowledgement when this list is not empty.
    /// </summary>
    public async Task<IReadOnlyList<PriceDifferenceWarning>> PriceDifferencesAsync(SessionContext ctx, Guid invoiceId)
    {
        ctx.Require(Permissions.VoucherView);
        var inv = await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == ctx.CompanyId)
                  ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn không tồn tại.");
        var result = new List<PriceDifferenceWarning>();
        if (inv.Kind != InvoiceKind.Purchase || inv.Purpose != InvoicePurpose.Normal) return result;
        foreach (var l in inv.Lines.Where(x => x.StockDocumentId is not null).OrderBy(x => x.LineNo))
        {
            var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == l.StockDocumentId);
            if (receipt.Status != StockDocumentStatus.Posted) continue;
            var rl = receipt.Lines.Single(x => x.LineNo == l.StockLineNo);
            var diff = l.FunctionalAmount - (rl.Amount ?? 0);
            if (diff == 0) continue;
            var split = await stock.PriceDifferenceSplitAsync(ctx.CompanyId, receipt.Id, rl.LineNo, diff);
            result.Add(new PriceDifferenceWarning(l.LineNo, receipt.Number, rl.Amount ?? 0, l.FunctionalAmount, diff, split.ReceiptQuantity, split.QuantityInStock,
                split.InStockPart, split.IssuedPart, split.Method.ToString()));
        }
        return result;
    }

    public async Task<CommercialInvoice> PostAsync(SessionContext ctx, Guid invoiceId, bool acknowledgePriceDifferences = false)
    {
        ctx.Require(Permissions.VoucherPost);
        return await db.InTransactionAsync(async () =>
        {
            var inv = await db.CommercialInvoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn không tồn tại.");
            if (inv.Status != InvoiceStatus.Draft)
                throw new DomainException(inv.Status == InvoiceStatus.Posted ? ErrorCodes.AlreadyPosted : ErrorCodes.InvalidStateTransition, $"Hóa đơn {inv.Number} ở trạng thái {inv.Status}.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            if (company.RequireDistinctApprover && inv.CreatedBy == ctx.UserId)
                throw new DomainException(ErrorCodes.SodViolation, "Người ghi sổ hóa đơn không được trùng người lập.");
            await CheckDuplicateAsync(ctx.CompanyId, inv.Kind, inv.PartnerId, inv.InvoiceSeries, inv.InvoiceNo, inv.Id);
            if (inv.Purpose != InvoicePurpose.Normal) return await PostAdjustmentCoreAsync(ctx, inv);
            var warnings = await PriceDifferencesAsync(ctx, inv.Id);
            if (warnings.Count > 0 && !acknowledgePriceDifferences)
                throw new DomainException(ErrorCodes.PriceDifference,
                    $"Hóa đơn lệch giá với {warnings.Count} dòng phiếu nhập đã ghi sổ (tổng {warnings.Sum(w => w.Difference)}): " +
                    $"điều chỉnh tồn kho {warnings.Sum(w => w.InventoryPart)}, điều chỉnh giá vốn {warnings.Sum(w => w.CogsPart)}. Xác nhận để ghi sổ.",
                    warnings.Select(w => new ValidationError(ErrorCodes.PriceDifference,
                        $"Phiếu {w.ReceiptNumber}: nhập kho {w.ReceiptValue}, hóa đơn {w.InvoiceValue}, chênh {w.Difference} → tồn kho {w.InventoryPart} (còn {w.QuantityInStock}/{w.ReceiptQuantity}), giá vốn {w.CogsPart}", w.LineNo)).ToList());
            var itemAccounts = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id, i => i.InventoryAccountId);

            var roles = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).ToDictionaryAsync(m => m.RoleCode, m => m.AccountId);
            Guid Role(string code) => roles.TryGetValue(code, out var id) ? id
                : throw new DomainException(ErrorCodes.RuleRoleUnmapped, $"Chưa ánh xạ vai trò tài khoản {code}.");

            var gl = new List<VoucherLineInput>();
            foreach (var l in inv.Lines.OrderBy(x => x.LineNo))
            {
                await RecheckLineAsync(ctx, inv, l);
                if (inv.Kind == InvoiceKind.Purchase)
                {
                    var debit = l.StockDocumentId is null ? l.AccountId!.Value : Role("GRNI");
                    if (l.StockDocumentId is { } sd)
                    {
                        var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == sd);
                        var rl = receipt.Lines.Single(x => x.LineNo == l.StockLineNo);
                        if (receipt.Status == StockDocumentStatus.Draft)
                        {
                            if (l.StockQuantity != rl.Quantity)
                                throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {l.LineNo}: hóa đơn định giá phiếu nhập nháp phải bao trọn số lượng dòng phiếu.");
                            await stock.SetDraftReceiptValueAsync(ctx.CompanyId, receipt.Id, rl.LineNo, l.FunctionalAmount, Role("GRNI"));
                            await stock.PostDocumentAsync(ctx, receipt.Id);
                        }
                        else
                        {
                            if (rl.CounterAccountId != Role("GRNI") || l.StockQuantity != rl.Quantity)
                                throw new DomainException(ErrorCodes.PriceDifference,
                                    $"Dòng {l.LineNo}: phiếu {receipt.Number} không ghi vào TK hàng về chưa có hóa đơn hoặc hóa đơn không bao trọn dòng phiếu; không đối trừ tự động được.");
                            var diff = l.FunctionalAmount - rl.Amount!.Value;
                            if (diff != 0)
                            {
                                // Policy confirmed by the business (2026-10-15): stock still on hand → inventory value; goods already issued → COGS.
                                var split = await stock.AdjustReceiptValueAsync(ctx, receipt.Id, rl.LineNo, diff, inv.PostingDate, inv.Id,
                                    $"Chênh lệch giá HĐ {inv.InvoiceSeries}/{inv.InvoiceNo}");
                                gl.Add(new VoucherLineInput(Role("GRNI"), Side.Debit, rl.Amount.Value, inv.PartnerId, Description: l.Description));
                                if (split.InStockPart != 0)
                                    gl.Add(new VoucherLineInput(itemAccounts[rl.ItemId], split.InStockPart > 0 ? Side.Debit : Side.Credit, Math.Abs(split.InStockPart),
                                        Description: $"Điều chỉnh giá trị tồn kho theo HĐ {inv.InvoiceNo}"));
                                if (split.IssuedPart != 0)
                                    gl.Add(new VoucherLineInput(Role("COGS"), split.IssuedPart > 0 ? Side.Debit : Side.Credit, Math.Abs(split.IssuedPart),
                                        Description: $"Điều chỉnh giá vốn hàng đã xuất theo HĐ {inv.InvoiceNo}"));
                                continue;
                            }
                        }
                    }
                    // GRNI is followed per supplier (company decision 16/10/2026: a 331 detail account), as the receipt posted it.
                    // Mã phân tích: on an expense / service line; a goods line clears "goods received not invoiced", the code sits on the stock issue instead.
                    gl.Add(new VoucherLineInput(debit, Side.Debit, l.FunctionalAmount, l.StockDocumentId is null ? null : inv.PartnerId, Description: l.Description,
                        AnalysisCodeId: l.StockDocumentId is null ? l.AnalysisCodeId : null));
                }
                else
                {
                    var revenue = l.AccountId ?? Role("SALES_REVENUE");
                    gl.Add(new VoucherLineInput(revenue, Side.Credit, l.FunctionalAmount, Description: l.Description, AnalysisCodeId: l.AnalysisCodeId));
                }
            }
            var vat = inv.Lines.Sum(x => x.VatAmount);
            var total = inv.Lines.Sum(x => x.FunctionalAmount) + vat;
            if (inv.Kind == InvoiceKind.Purchase)
            {
                if (vat > 0) gl.Add(new VoucherLineInput(Role("VAT_INPUT"), Side.Debit, vat, Description: "Thuế GTGT đầu vào"));
                gl.AddRange(await PartnerLinesAsync(ctx.CompanyId, inv, Role("AP_TRADE"), Side.Credit, $"HĐ {inv.InvoiceSeries}/{inv.InvoiceNo}", inv.DueDate, null));
            }
            else
            {
                gl.InsertRange(0, await PartnerLinesAsync(ctx.CompanyId, inv, Role("AR_TRADE"), Side.Debit, $"HĐ {inv.InvoiceSeries}/{inv.InvoiceNo}", inv.DueDate, null));
                if (vat > 0) gl.Add(new VoucherLineInput(Role("VAT_OUTPUT"), Side.Credit, vat, Description: "Thuế GTGT đầu ra"));
            }
            gl.RemoveAll(x => x.Amount == 0);

            var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(inv.Kind == InvoiceKind.Purchase ? VoucherTypes.PurchaseInvoice : VoucherTypes.SalesInvoice,
                inv.InvoiceDate, inv.PostingDate, $"{inv.Description} (HĐ {inv.InvoiceSeries}/{inv.InvoiceNo})", gl, SourceSystem: Source, SourceId: inv.Id.ToString()));
            inv.VoucherId = v.Id;
            inv.Status = InvoiceStatus.Posted;
            inv.PostedBy = ctx.UserId;
            inv.PostedAt = clock.UtcNow;
            inv.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.post", "invoice", inv.Id.ToString(),
                new { inv.Number, voucher = v.Number, total, priceDifferencesAcknowledged = warnings }, inv.RowVersion);
            await db.SaveChangesAsync();
            return inv;
        });
    }

    /// <summary>Reverses the invoice voucher (settlements released by the engine). Receipts stay on GRNI awaiting a new invoice.</summary>
    public async Task<CommercialInvoice> ReverseAsync(SessionContext ctx, Guid invoiceId, DateOnly reversalDate, string reason)
    {
        ctx.Require(Permissions.VoucherReverse);
        return await db.InTransactionAsync(async () =>
        {
            var inv = await db.CommercialInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn không tồn tại.");
            if (inv.Status != InvoiceStatus.Posted) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Hóa đơn {inv.Number} chưa ghi sổ hoặc đã đảo.");
            await engine.ReverseCoreAsync(ctx, inv.VoucherId!.Value, reversalDate, $"Đảo hóa đơn {inv.InvoiceSeries}/{inv.InvoiceNo}: {reason}", allowModuleOwned: true);
            // The voucher reversal takes the inventory part of price differences out of the ledger; take it out of the stock
            // ledger too with an opposite value-only adjustment (adjustments are immutable, never deleted).
            await stock.ReverseValueAdjustmentsAsync(ctx, inv.Id, reversalDate, $"Đảo hóa đơn {inv.InvoiceSeries}/{inv.InvoiceNo}");
            inv.Status = InvoiceStatus.Reversed;
            inv.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.reverse", "invoice", inv.Id.ToString(), new { inv.Number, reason }, inv.RowVersion);
            await db.SaveChangesAsync();
            return inv;
        });
    }

    public async Task<CommercialInvoice> CancelAsync(SessionContext ctx, Guid invoiceId, string reason)
    {
        ctx.Require(Permissions.InvoiceDocument);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy.");
        return await db.InTransactionAsync(async () =>
        {
            var inv = await db.CommercialInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn không tồn tại.");
            if (inv.Status != InvoiceStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ hủy được hóa đơn nháp.");
            inv.Status = InvoiceStatus.Cancelled;
            inv.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.cancel", "invoice", inv.Id.ToString(), new { inv.Number, reason }, inv.RowVersion);
            await db.SaveChangesAsync();
            return inv;
        });
    }

    /// <summary>
    /// Stock lines not yet (fully) invoiced: for purchases, receipts carried on GRNI (hàng về chưa có hóa đơn);
    /// for sales, deliveries not yet invoiced (đối chiếu xuất bán ↔ hóa đơn).
    /// </summary>
    public async Task<IReadOnlyList<UninvoicedLine>> UninvoicedAsync(SessionContext ctx, InvoiceKind kind, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var type = kind == InvoiceKind.Purchase ? StockDocumentType.Receipt : StockDocumentType.Issue;
        var lines = await (from d in db.StockDocuments
                           join l in db.StockDocumentLines on d.Id equals l.DocumentId
                           join i in db.Items on l.ItemId equals i.Id
                           where d.CompanyId == ctx.CompanyId && d.Type == type && d.Status == StockDocumentStatus.Posted && d.PostingDate <= asOf && d.ReversesDocumentId == null
                           select new { d.Id, d.Number, d.PostingDate, d.PartnerId, l.LineNo, ItemId = i.Id, ItemCode = i.Code, l.Quantity, l.CounterAccountId })
            .AsNoTracking().ToListAsync();
        if (kind == InvoiceKind.Purchase)
        {
            var grni = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.RoleCode == "GRNI").Select(m => (Guid?)m.AccountId).FirstOrDefaultAsync();
            lines = lines.Where(l => l.CounterAccountId == grni).ToList();
        }
        var result = new List<UninvoicedLine>();
        foreach (var l in lines)
        {
            var invoiced = await InvoicedQuantityAsync(l.Id, l.LineNo, null);
            if (invoiced >= l.Quantity) continue;
            var bdmt = await db.StockMeasurements.AsNoTracking().Where(m => m.DocumentId == l.Id && m.LineNo == l.LineNo && m.SupersededAt == null).Select(m => (decimal?)m.Bdmt).FirstOrDefaultAsync();
            result.Add(new UninvoicedLine(l.Id, l.Number, l.PostingDate, l.LineNo, l.ItemId, l.ItemCode, l.Quantity, invoiced, bdmt, l.PartnerId));
        }
        return result.OrderBy(r => r.PostingDate).ThenBy(r => r.StockDocumentNumber).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<CommercialInvoiceLine> BuildLineAsync(SessionContext ctx, CommercialInvoice inv, InvoiceLineInput l, int lineNo, int curDp, int funcDp)
    {
        if (l.UnitPrice < 0 || !MoneyMath.HasAtMostDecimals(l.UnitPrice, MoneyMath.StorageScale))
            throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: đơn giá không hợp lệ (≥ 0, tối đa 4 chữ số thập phân).");
        if (l.VatRate is { } vr && (vr < 0 || vr > 100))
            throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: thuế suất không hợp lệ.");
        var line = new CommercialInvoiceLine
        {
            Id = Ids.New(), InvoiceId = inv.Id, CompanyId = ctx.CompanyId, LineNo = lineNo, Basis = l.Basis, UnitPrice = l.UnitPrice,
            VatRate = l.VatRate, AccountId = l.AccountId, Description = l.Description, AnalysisCodeId = l.AnalysisCodeId,
        };

        if (l.StockDocumentId is { } sd)
        {
            var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == sd && d.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, $"Dòng {lineNo}: phiếu kho không tồn tại.");
            var expected = inv.Kind == InvoiceKind.Purchase ? StockDocumentType.Receipt : StockDocumentType.Issue;
            if (doc.Type != expected || doc.ReversesDocumentId is not null)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: hóa đơn {(inv.Kind == InvoiceKind.Purchase ? "mua phải gắn phiếu nhập" : "bán phải gắn phiếu xuất")}.");
            var okStatus = inv.Kind == InvoiceKind.Purchase ? doc.Status is StockDocumentStatus.Draft or StockDocumentStatus.Posted : doc.Status == StockDocumentStatus.Posted;
            if (!okStatus) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Dòng {lineNo}: phiếu {doc.Number} ở trạng thái {doc.Status}.");
            var sl = doc.Lines.FirstOrDefault(x => x.LineNo == l.StockLineNo) ?? throw new DomainException(ErrorCodes.NotFound, $"Dòng {lineNo}: phiếu {doc.Number} không có dòng {l.StockLineNo}.");
            line.StockDocumentId = doc.Id;
            line.StockLineNo = sl.LineNo;
            line.ItemId = sl.ItemId;
            line.OrderId = sl.OrderId;
            line.OrderLineNo = sl.OrderLineNo;
            if (sl.OrderId is { } so && (await db.TradeOrders.AsNoTracking().FirstAsync(o => o.Id == so)).PartnerId != inv.PartnerId)
                throw new DomainException(ErrorCodes.OrderMismatch, $"Dòng {lineNo}: đối tượng hóa đơn khác đối tượng của đơn hàng gắn với phiếu {doc.Number}.");
            line.StockQuantity = l.StockQuantity ?? sl.Quantity - await InvoicedQuantityAsync(doc.Id, sl.LineNo, null);
            if (line.StockQuantity <= 0) throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {lineNo}: phiếu {doc.Number} dòng {sl.LineNo} đã lập hóa đơn đủ.");
            await CheckRemainingAsync(doc.Id, doc.Number, sl.LineNo, sl.Quantity, line.StockQuantity.Value, null);

            if (l.Basis == PricingBasis.Bdmt)
            {
                var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == sl.ItemId);
                if (!item.TracksDryness) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: vật tư {item.Code} không theo dõi BDMT.");
                if (line.StockQuantity != sl.Quantity)
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: tính giá theo BDMT phải lập hóa đơn cho trọn dòng phiếu (BDMT đo cho cả dòng).");
                line.BasisQuantity = await ActiveBdmtAsync(doc.Id, doc.Number, sl.LineNo);
            }
            else line.BasisQuantity = line.StockQuantity.Value;
        }
        else
        {
            if (l.Basis == PricingBasis.Bdmt) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: giá theo BDMT phải gắn phiếu kho có kết quả đo.");
            if (l.AccountId is null || l.Quantity is not { } q || q <= 0)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {lineNo}: dòng không gắn phiếu kho phải có tài khoản hạch toán và số lượng > 0.");
            if (!await db.Accounts.AnyAsync(a => a.Id == l.AccountId && a.CompanyId == ctx.CompanyId && a.IsPostable && a.IsActive))
                throw new DomainException(ErrorCodes.AccountNotFound, $"Dòng {lineNo}: tài khoản không hợp lệ.");
            line.BasisQuantity = q;
            if (l.OrderId is { } oid)
            {
                var order = await db.TradeOrders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == oid && o.CompanyId == ctx.CompanyId)
                            ?? throw new DomainException(ErrorCodes.NotFound, $"Dòng {lineNo}: đơn hàng không tồn tại.");
                if (order.Kind != inv.Kind || order.PartnerId != inv.PartnerId || order.Status != OrderStatus.Confirmed || order.Lines.All(x => x.LineNo != l.OrderLineNo))
                    throw new DomainException(ErrorCodes.OrderMismatch, $"Dòng {lineNo}: đơn {order.Number} không khớp loại/đối tượng/trạng thái/dòng.");
                line.OrderId = oid;
                line.OrderLineNo = l.OrderLineNo;
            }
        }
        Price(line, inv.ExchangeRate, curDp, funcDp);
        return line;
    }

    private static void Price(CommercialInvoiceLine line, decimal? rate, int curDp, int funcDp)
    {
        line.Amount = MoneyMath.Round(line.BasisQuantity * line.UnitPrice, curDp);
        line.FunctionalAmount = rate is { } r ? MoneyMath.Convert(line.Amount, r, funcDp) : line.Amount;
        line.VatAmount = line.VatRate is { } v ? MoneyMath.Round(line.FunctionalAmount * v / 100m, funcDp) : 0;
    }

    /// <summary>At posting: quantities still available and the BDMT basis still equal to the active measurement.</summary>
    private async Task RecheckLineAsync(SessionContext ctx, CommercialInvoice inv, CommercialInvoiceLine l)
    {
        if (l.StockDocumentId is not { } sd) return;
        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == sd);
        if (inv.Kind == InvoiceKind.Sales && doc.Status != StockDocumentStatus.Posted || doc.Status is StockDocumentStatus.Reversed or StockDocumentStatus.Cancelled)
            throw new DomainException(ErrorCodes.InvalidStateTransition, $"Dòng {l.LineNo}: phiếu {doc.Number} ở trạng thái {doc.Status}.");
        var sl = doc.Lines.Single(x => x.LineNo == l.StockLineNo);
        await CheckRemainingAsync(doc.Id, doc.Number, sl.LineNo, sl.Quantity, l.StockQuantity!.Value, inv.Id);
        if (l.Basis == PricingBasis.Bdmt && await ActiveBdmtAsync(doc.Id, doc.Number, sl.LineNo) != l.BasisQuantity)
            throw new DomainException(ErrorCodes.MeasurementPending,
                $"Dòng {l.LineNo}: BDMT của phiếu {doc.Number} đã thay đổi so với lúc lập hóa đơn (kết quả đo/chứng thư mới); hủy hóa đơn nháp và lập lại.");
        _ = ctx;
    }

    private async Task CheckRemainingAsync(Guid docId, string docNumber, int lineNo, decimal lineQty, decimal wanted, Guid? excludeInvoice)
    {
        var used = await InvoicedQuantityAsync(docId, lineNo, excludeInvoice);
        if (used + wanted > lineQty)
            throw new DomainException(ErrorCodes.OverInvoiced, $"Phiếu {docNumber} dòng {lineNo}: đã lập hóa đơn {used}, thêm {wanted} vượt số lượng {lineQty}.");
    }

    /// <summary>Quantity of a stock line already on Draft or Posted invoices (cancelled/reversed invoices release it).</summary>
    private async Task<decimal> InvoicedQuantityAsync(Guid docId, int lineNo, Guid? excludeInvoice)
    {
        var rows = await (from l in db.CommercialInvoiceLines
                          join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                          where l.StockDocumentId == docId && l.StockLineNo == lineNo
                                && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Posted) && i.Id != excludeInvoice
                          select l.StockQuantity).ToListAsync();
        var pending = db.CommercialInvoiceLines.Local.Where(l => l.StockDocumentId == docId && l.StockLineNo == lineNo && db.Entry(l).State == EntityState.Added && l.InvoiceId != excludeInvoice)
            .Sum(l => l.StockQuantity ?? 0);
        return rows.Sum(x => x ?? 0) + pending;
    }

    private async Task<decimal> ActiveBdmtAsync(Guid docId, string docNumber, int lineNo)
        => await db.StockMeasurements.AsNoTracking().Where(m => m.DocumentId == docId && m.LineNo == lineNo && m.SupersededAt == null)
               .Select(m => (decimal?)m.Bdmt).FirstOrDefaultAsync()
           ?? throw new DomainException(ErrorCodes.MeasurementPending, $"Phiếu {docNumber} dòng {lineNo} chưa có kết quả đo độ khô / chứng thư; chưa tính được BDMT để lập hóa đơn.");

    private async Task CheckDuplicateAsync(Guid companyId, InvoiceKind kind, Guid partnerId, string series, string no, Guid? self)
    {
        var q = db.CommercialInvoices.Where(i => i.CompanyId == companyId && i.Kind == kind && i.InvoiceSeries == series && i.InvoiceNo == no
                                                 && i.Id != self && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Reversed);
        if (kind == InvoiceKind.Purchase) q = q.Where(i => i.PartnerId == partnerId); // supplier invoice identity includes the seller
        if (await q.AnyAsync())
            throw new DomainException(ErrorCodes.DuplicateInvoice, $"Hóa đơn {series}/{no} đã được ghi nhận.");
    }

    private async Task<(string? Currency, decimal? Rate, int CurrencyDp, int FunctionalDp)> CurrencyAsync(Company company, string? currency, decimal? rate)
    {
        var funcDp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
        if (currency is null || currency == company.FunctionalCurrency) return (null, null, funcDp, funcDp);
        var dp = await db.Currencies.Where(c => c.Code == currency).Select(c => (int?)c.DecimalPlaces).FirstOrDefaultAsync()
                 ?? throw new DomainException(ErrorCodes.ValidationFailed, $"Đồng tiền {currency} chưa khai báo.");
        if (rate is not { } r || r <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Hóa đơn ngoại tệ phải có tỷ giá > 0.");
        if (!MoneyMath.HasAtMostDecimals(r, MoneyMath.RateScale)) throw new DomainException(ErrorCodes.AmountPrecision, "Tỷ giá tối đa 6 chữ số thập phân.");
        return (currency, r, dp, funcDp);
    }

    private async Task<string> NextNumberAsync(Guid companyId, FiscalYear fy, string prefix)
    {
        var key = "INV-" + prefix;
        var seq = await db.VoucherNumberSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.FiscalYearId == fy.Id && s.VoucherType == key);
        if (seq is null) db.VoucherNumberSequences.Add(seq = new VoucherNumberSequence { Id = Ids.New(), CompanyId = companyId, FiscalYearId = fy.Id, VoucherType = key });
        seq.LastNumber++;
        seq.RowVersion++;
        return $"{prefix}{fy.Code}-{seq.LastNumber:D6}";
    }
}

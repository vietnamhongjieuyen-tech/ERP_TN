using Hak.Application.Accounting;
using Hak.Application.Common;
using Hak.Application.Inventory;
using Hak.Application.Security;
using Hak.Domain.Common;
using Hak.Domain.Inventory;
using Hak.Domain.Security;
using Hak.Domain.Trade;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Trade;

/// <summary>One line of a sales adjustment invoice. Return lines point at a return receipt; Discount/Increase lines carry an amount.</summary>
public sealed record AdjustmentLineInput(int OriginalLineNo, Guid? ReturnReceiptId = null, int? ReturnReceiptLineNo = null, decimal? Amount = null, string? Description = null);

public sealed record AdjustmentInput(Guid OriginalInvoiceId, InvoicePurpose Purpose, string InvoiceSeries, string InvoiceNo, DateOnly InvoiceDate, DateOnly PostingDate,
    string Description, IReadOnlyList<AdjustmentLineInput> Lines);

/// <summary>
/// Sales adjustments (master prompt §13): returns, discounts/price reductions and increases, each linked to a posted
/// Normal sales invoice. Additional account roles: SALES_RETURNS, SALES_DISCOUNT (may map to the same account).
/// Currency and exchange rate are those of the original invoice [CẦN XÁC MINH chính sách tỷ giá khi điều chỉnh].
/// </summary>
public sealed partial class InvoiceService
{
    /// <summary>
    /// Step 1 of a return: a Draft stock receipt of the returned quantity, valued at the original issue's cost
    /// (Dr inventory / Cr the issue's counter account, i.e. cost of goods sold is reduced by exactly what was booked).
    /// Dryness-tracked goods are then measured on this receipt like any other receipt.
    /// </summary>
    public async Task<StockDocument> CreateReturnReceiptAsync(SessionContext ctx, Guid originalInvoiceId, int originalLineNo, decimal quantity,
        DateOnly date, Guid? warehouseId = null, string? description = null)
    {
        ctx.Require(Permissions.InvoiceDocument);
        var (orig, line) = await OriginalLineAsync(ctx, originalInvoiceId, originalLineNo);
        if (line.StockDocumentId is not { } issueId) throw new DomainException(ErrorCodes.ValidationFailed, "Dòng hóa đơn gốc không gắn phiếu xuất; dùng điều chỉnh giảm giá thay vì trả hàng.");
        if (quantity <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Số lượng trả lại phải lớn hơn 0.");
        var returned = await ReturnedQuantityAsync(orig.Id, originalLineNo, null);
        if (returned + quantity > line.StockQuantity)
            throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {originalLineNo}: đã trả {returned}, thêm {quantity} vượt số đã bán {line.StockQuantity}.");

        var issue = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == issueId);
        var issueLine = issue.Lines.Single(l => l.LineNo == line.StockLineNo);
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == issueId && m.LineNo == issueLine.LineNo && m.Quantity < 0);
        var valuation = await db.StockValuations.AsNoTracking().Where(v => v.MovementId == movement.Id && v.VoidedAt == null).Select(v => (decimal?)v.Value).FirstOrDefaultAsync()
                        ?? throw new DomainException(ErrorCodes.CostingPending, $"Phiếu xuất {issue.Number} chưa được tính giá (bình quân cuối tháng); tính giá trước khi nhận hàng trả lại.");
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
        var dp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
        var value = quantity == issueLine.Quantity ? -valuation : MoneyMath.Round(-valuation * quantity / issueLine.Quantity, dp);

        return await stock.CreateDocumentAsync(ctx, new StockDocumentInput(StockDocumentType.Receipt, warehouseId ?? issue.WarehouseId, date, date,
            description ?? $"Hàng bán bị trả lại theo HĐ {orig.InvoiceSeries}/{orig.InvoiceNo}",
            [new StockLineInput(issueLine.ItemId, quantity, value, issueLine.CounterAccountId, $"Trả lại từ {issue.Number}")], PartnerId: orig.PartnerId));
    }

    /// <summary>
    /// Purchase return, step 1: a Draft stock issue of the returned quantity from the original receipt's warehouse, against the
    /// GRNI clearing account. The warehouse costing method values it when posted; the supplier's credit note then clears GRNI
    /// at that cost and the gap to the purchase price goes to COGS [CẦN XÁC MINH — chính sách chênh lệch khi trả hàng mua].
    /// </summary>
    public async Task<StockDocument> CreatePurchaseReturnIssueAsync(SessionContext ctx, Guid originalInvoiceId, int originalLineNo, decimal quantity,
        DateOnly date, string? description = null)
    {
        ctx.Require(Permissions.InvoiceDocument);
        var (orig, line) = await OriginalLineAsync(ctx, originalInvoiceId, originalLineNo, InvoiceKind.Purchase);
        if (line.StockDocumentId is not { } receiptId) throw new DomainException(ErrorCodes.ValidationFailed, "Dòng hóa đơn gốc không gắn phiếu nhập; dùng điều chỉnh giảm giá thay vì trả hàng.");
        if (quantity <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Số lượng trả lại phải lớn hơn 0.");
        var returned = await ReturnedQuantityAsync(orig.Id, originalLineNo, null);
        if (returned + quantity > line.StockQuantity)
            throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {originalLineNo}: đã trả {returned}, thêm {quantity} vượt số đã mua {line.StockQuantity}.");
        var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == receiptId);
        var receiptLine = receipt.Lines.Single(l => l.LineNo == line.StockLineNo);
        var grni = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.RoleCode == "GRNI").Select(m => (Guid?)m.AccountId).FirstOrDefaultAsync()
                   ?? throw new DomainException(ErrorCodes.RuleRoleUnmapped, "Chưa ánh xạ vai trò tài khoản GRNI.");
        return await stock.CreateDocumentAsync(ctx, new StockDocumentInput(StockDocumentType.Issue, receipt.WarehouseId, date, date,
            description ?? $"Xuất trả nhà cung cấp theo HĐ {orig.InvoiceSeries}/{orig.InvoiceNo}",
            [new StockLineInput(receiptLine.ItemId, quantity, null, grni, $"Trả lại hàng nhập {receipt.Number}")], PartnerId: orig.PartnerId));
    }

    /// <summary>Step 2: the adjustment invoice (Return / Discount / Increase) referencing the original invoice lines.</summary>
    public async Task<CommercialInvoice> CreateAdjustmentAsync(SessionContext ctx, AdjustmentInput input)
    {
        ctx.Require(Permissions.InvoiceDocument);
        if (input.Purpose == InvoicePurpose.Normal) throw new DomainException(ErrorCodes.ValidationFailed, "Dùng chức năng lập hóa đơn thường.");
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Hóa đơn điều chỉnh chưa có dòng.");
        if (string.IsNullOrWhiteSpace(input.InvoiceNo) || string.IsNullOrWhiteSpace(input.InvoiceSeries))
            throw new DomainException(ErrorCodes.ValidationFailed, "Phải nhập ký hiệu và số hóa đơn điều chỉnh.");
        return await db.InTransactionAsync(async () =>
        {
            var orig = await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == input.OriginalInvoiceId && i.CompanyId == ctx.CompanyId)
                       ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn gốc không tồn tại.");
            if (orig.Purpose != InvoicePurpose.Normal || orig.Status != InvoiceStatus.Posted)
                throw new DomainException(ErrorCodes.ValidationFailed, "Chỉ điều chỉnh hóa đơn thường đã ghi sổ.");
            var purchase = orig.Kind == InvoiceKind.Purchase;
            await CheckDuplicateAsync(ctx.CompanyId, orig.Kind, orig.PartnerId, input.InvoiceSeries.Trim(), input.InvoiceNo.Trim(), null);
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var (_, rate, curDp, funcDp) = await CurrencyAsync(company, orig.Currency, orig.ExchangeRate);
            var fy = await db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= input.PostingDate && f.EndDate >= input.PostingDate)
                     ?? throw new DomainException(ErrorCodes.FiscalYearNotFound, $"Không có năm tài chính chứa ngày {input.PostingDate}.");

            var inv = new CommercialInvoice
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Kind = orig.Kind, Purpose = input.Purpose, AdjustsInvoiceId = orig.Id,
                InvoiceSeries = input.InvoiceSeries.Trim(), InvoiceNo = input.InvoiceNo.Trim(), InvoiceDate = input.InvoiceDate, PostingDate = input.PostingDate,
                PartnerId = orig.PartnerId, Currency = orig.Currency, ExchangeRate = orig.ExchangeRate, Description = input.Description.Trim(),
                CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
                Number = await NextNumberAsync(ctx.CompanyId, fy, (purchase, input.Purpose) switch
                {
                    (false, InvoicePurpose.Return) => "HDTL", (false, InvoicePurpose.Discount) => "HDGG", (false, _) => "HDDT",
                    (true, InvoicePurpose.Return) => "HMTL", (true, InvoicePurpose.Discount) => "HMGG", (true, _) => "HMDT",
                }),
            };
            var n = 0;
            foreach (var l in input.Lines)
            {
                var ol = orig.Lines.FirstOrDefault(x => x.LineNo == l.OriginalLineNo)
                         ?? throw new DomainException(ErrorCodes.NotFound, $"Hóa đơn gốc không có dòng {l.OriginalLineNo}.");
                var line = new CommercialInvoiceLine
                {
                    Id = Ids.New(), InvoiceId = inv.Id, CompanyId = ctx.CompanyId, LineNo = ++n, AdjustsLineNo = ol.LineNo, ItemId = ol.ItemId,
                    AccountId = ol.AccountId, VatRate = ol.VatRate, Description = l.Description, AnalysisCodeId = ol.AnalysisCodeId,   // follows the original line
                };
                if (input.Purpose == InvoicePurpose.Return)
                {
                    if (l.ReturnReceiptId is not { } rid || l.ReturnReceiptLineNo is not { } rln)
                        throw new DomainException(ErrorCodes.ValidationFailed, purchase ? $"Dòng {n}: trả hàng mua phải gắn phiếu xuất trả nhà cung cấp." : $"Dòng {n}: trả hàng phải gắn phiếu nhập hàng trả lại.");
                    var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == rid && d.CompanyId == ctx.CompanyId)
                                  ?? throw new DomainException(ErrorCodes.NotFound, $"Dòng {n}: phiếu kho trả lại không tồn tại.");
                    var expectedType = purchase ? StockDocumentType.Issue : StockDocumentType.Receipt;
                    var statusOk = purchase ? receipt.Status is StockDocumentStatus.Draft or StockDocumentStatus.Posted : receipt.Status == StockDocumentStatus.Draft;
                    if (receipt.Type != expectedType || !statusOk || receipt.PartnerId != orig.PartnerId)
                        throw new DomainException(ErrorCodes.ValidationFailed, purchase
                            ? $"Dòng {n}: phải là phiếu xuất trả hàng (nháp hoặc đã ghi sổ) của cùng nhà cung cấp."
                            : $"Dòng {n}: phải là phiếu nhập hàng trả lại (nháp) của cùng khách hàng.");
                    var rl = receipt.Lines.Single(x => x.LineNo == rln);
                    if (rl.ItemId != ol.ItemId) throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: vật tư trả lại khác vật tư trên hóa đơn gốc.");
                    if (await db.CommercialInvoiceLines.AnyAsync(x => x.StockDocumentId == receipt.Id && x.StockLineNo == rl.LineNo
                            && db.CommercialInvoices.Any(i => i.Id == x.InvoiceId && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Posted))))
                        throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {n}: phiếu {receipt.Number} đã gắn với hóa đơn khác.");
                    var returned = await ReturnedQuantityAsync(orig.Id, ol.LineNo, null);
                    if (returned + rl.Quantity > ol.StockQuantity)
                        throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {n}: đã trả {returned}, thêm {rl.Quantity} vượt số trên hóa đơn gốc {ol.StockQuantity}.");
                    line.StockDocumentId = receipt.Id;
                    line.StockLineNo = rl.LineNo;
                    line.StockQuantity = rl.Quantity;
                    line.Basis = ol.Basis;
                    line.UnitPrice = ol.UnitPrice;
                    // Goods returned to a supplier are not re-measured: their BDMT is the original line's BDMT pro rata [CẦN XÁC MINH].
                    line.BasisQuantity = ol.Basis != PricingBasis.Bdmt ? rl.Quantity
                        : purchase ? (rl.Quantity == ol.StockQuantity ? ol.BasisQuantity : MoneyMath.Round(ol.BasisQuantity * rl.Quantity / ol.StockQuantity!.Value, 3))
                        : await ActiveBdmtAsync(receipt.Id, receipt.Number, rl.LineNo);
                    Price(line, rate, curDp, funcDp);
                }
                else
                {
                    if (l.Amount is not { } amt || amt <= 0 || !MoneyMath.HasAtMostDecimals(amt, curDp))
                        throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: số tiền điều chỉnh phải lớn hơn 0, tối đa {curDp} chữ số thập phân.");
                    line.Basis = PricingBasis.Quantity;
                    line.BasisQuantity = 1;
                    line.UnitPrice = amt;
                    Price(line, rate, curDp, funcDp);
                    if (input.Purpose == InvoicePurpose.Discount)
                    {
                        var already = await AdjustedAmountAsync(orig.Id, ol.LineNo, InvoicePurpose.Discount) + await AdjustedAmountAsync(orig.Id, ol.LineNo, InvoicePurpose.Return);
                        if (already + line.FunctionalAmount > ol.FunctionalAmount)
                            throw new DomainException(ErrorCodes.OverInvoiced, $"Dòng {n}: tổng giảm trừ {already + line.FunctionalAmount} vượt giá trị dòng hóa đơn gốc {ol.FunctionalAmount}.");
                    }
                }
                inv.Lines.Add(line);
            }
            db.CommercialInvoices.Add(inv);
            await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.adjustment.create", "invoice", inv.Id.ToString(),
                new { inv.Number, inv.Purpose, original = orig.Number, total = inv.Lines.Sum(x => x.FunctionalAmount + x.VatAmount) });
            await db.SaveChangesAsync();
            return inv;
        });
    }

    /// <summary>
    /// Posts a sales adjustment. Return: the return receipt is posted (Dr inventory / Cr COGS) and the credit note books
    /// Dr SALES_RETURNS + Dr VAT_OUTPUT / Cr AR. Discount: Dr SALES_DISCOUNT + Dr VAT_OUTPUT / Cr AR. Increase: Dr AR /
    /// Cr revenue + VAT. Credits to AR settle the original invoice's receivable while it is still open.
    /// </summary>
    private async Task<CommercialInvoice> PostAdjustmentCoreAsync(SessionContext ctx, CommercialInvoice inv)
    {
        var orig = await db.CommercialInvoices.AsNoTracking().FirstAsync(i => i.Id == inv.AdjustsInvoiceId);
        if (orig.Status != InvoiceStatus.Posted) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Hóa đơn gốc {orig.Number} không còn hiệu lực.");
        var roles = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).ToDictionaryAsync(m => m.RoleCode, m => m.AccountId);
        Guid Role(string code) => roles.TryGetValue(code, out var id) ? id : throw new DomainException(ErrorCodes.RuleRoleUnmapped, $"Chưa ánh xạ vai trò tài khoản {code}.");

        var label = $"HĐ {inv.InvoiceSeries}/{inv.InvoiceNo} điều chỉnh {orig.InvoiceSeries}/{orig.InvoiceNo}";
        var vat = inv.Lines.Sum(x => x.VatAmount);
        var total = inv.Lines.Sum(x => x.FunctionalAmount) + vat;
        var gl = inv.Kind == InvoiceKind.Purchase ? await PurchaseAdjustmentLinesAsync(ctx, inv, orig, Role, label) : new List<VoucherLineInput>();
        foreach (var l in inv.Lines.Where(_ => inv.Kind == InvoiceKind.Sales).OrderBy(x => x.LineNo))
        {
            if (inv.Purpose == InvoicePurpose.Return)
            {
                var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == l.StockDocumentId);
                if (l.Basis == PricingBasis.Bdmt && await ActiveBdmtAsync(receipt.Id, receipt.Number, l.StockLineNo!.Value) != l.BasisQuantity)
                    throw new DomainException(ErrorCodes.MeasurementPending, $"Dòng {l.LineNo}: BDMT của phiếu {receipt.Number} đã thay đổi; hủy và lập lại hóa đơn điều chỉnh.");
                if (receipt.Status == StockDocumentStatus.Draft) await stock.PostDocumentAsync(ctx, receipt.Id);
                gl.Add(new VoucherLineInput(Role("SALES_RETURNS"), Side.Debit, l.FunctionalAmount, Description: l.Description ?? "Hàng bán bị trả lại", AnalysisCodeId: l.AnalysisCodeId));
            }
            else if (inv.Purpose == InvoicePurpose.Discount)
                gl.Add(new VoucherLineInput(Role("SALES_DISCOUNT"), Side.Debit, l.FunctionalAmount, Description: l.Description ?? "Giảm giá hàng bán", AnalysisCodeId: l.AnalysisCodeId));
            else
                gl.Add(new VoucherLineInput(l.AccountId ?? Role("SALES_REVENUE"), Side.Credit, l.FunctionalAmount, Description: l.Description ?? "Điều chỉnh tăng", AnalysisCodeId: l.AnalysisCodeId));
        }
        if (inv.Kind == InvoiceKind.Sales && inv.Purpose == InvoicePurpose.Increase)
        {
            gl.InsertRange(0, await PartnerLinesAsync(ctx.CompanyId, inv, Role("AR_TRADE"), Side.Debit, label, orig.DueDate, null));
            if (vat > 0) gl.Add(new VoucherLineInput(Role("VAT_OUTPUT"), Side.Credit, vat, Description: "Thuế GTGT đầu ra"));
        }
        else if (inv.Kind == InvoiceKind.Sales)
        {
            if (vat > 0) gl.Add(new VoucherLineInput(Role("VAT_OUTPUT"), Side.Debit, vat, Description: "Giảm thuế GTGT đầu ra"));
            // Settle the original receivable when it is still open enough (and, in foreign currency, at its carrying amount);
            // otherwise the credit stays unallocated.
            gl.AddRange(await PartnerLinesAsync(ctx.CompanyId, inv, Role("AR_TRADE"), Side.Credit, label, null, orig.VoucherId));
        }
        gl.RemoveAll(x => x.Amount == 0);

        var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(inv.Kind == InvoiceKind.Purchase ? VoucherTypes.PurchaseInvoice : VoucherTypes.SalesInvoice, inv.InvoiceDate, inv.PostingDate,
            $"{inv.Description} ({label})", gl, SourceSystem: Source, SourceId: inv.Id.ToString()));
        inv.VoucherId = v.Id;
        inv.Status = InvoiceStatus.Posted;
        inv.PostedBy = ctx.UserId;
        inv.PostedAt = clock.UtcNow;
        inv.RowVersion++;
        await audit.AppendAsync(ctx.CompanyId, ctx, "invoice.adjustment.post", "invoice", inv.Id.ToString(), new { inv.Number, inv.Purpose, voucher = v.Number, total }, inv.RowVersion);
        await db.SaveChangesAsync();
        return inv;
    }

    /// <summary>
    /// Purchase adjustment lines. Return: the return issue is posted if still Draft and must be valued (monthly-average warehouses:
    /// post it and run the month's costing first); Dr AP / Cr GRNI at the issue's cost / Cr VAT input, and the gap between the
    /// purchase price and that cost to COGS [CẦN XÁC MINH]. Discount / Increase: the purchase value changes, so — as confirmed for
    /// purchase price differences — the part still in stock adjusts inventory value and the part already issued adjusts COGS;
    /// lines without stock adjust the original expense account. AP debits settle the original invoice while it is open enough.
    /// </summary>
    private async Task<List<VoucherLineInput>> PurchaseAdjustmentLinesAsync(SessionContext ctx, CommercialInvoice inv, CommercialInvoice orig,
        Func<string, Guid> role, string label)
    {
        var original = await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).FirstAsync(i => i.Id == orig.Id);
        var itemAccounts = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id, i => i.InventoryAccountId);
        var reduces = inv.Purpose != InvoicePurpose.Increase;
        var gl = new List<VoucherLineInput>();
        foreach (var l in inv.Lines.OrderBy(x => x.LineNo))
        {
            var ol = original.Lines.Single(x => x.LineNo == l.AdjustsLineNo);
            if (inv.Purpose == InvoicePurpose.Return)
            {
                var issue = await db.StockDocuments.AsNoTracking().FirstAsync(d => d.Id == l.StockDocumentId);
                if (issue.Status == StockDocumentStatus.Draft) await stock.PostDocumentAsync(ctx, issue.Id);
                else if (issue.Status != StockDocumentStatus.Posted)
                    throw new DomainException(ErrorCodes.InvalidStateTransition, $"Phiếu xuất trả {issue.Number} không còn hiệu lực.");
                var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == issue.Id && m.LineNo == l.StockLineNo && m.Quantity < 0);
                var cost = -(await db.StockValuations.AsNoTracking().Where(v => v.MovementId == movement.Id && v.VoidedAt == null).Select(v => (decimal?)v.Value).FirstOrDefaultAsync()
                             ?? throw new DomainException(ErrorCodes.CostingPending,
                                 $"Phiếu xuất trả {issue.Number} chưa có giá vốn (kho bình quân cuối tháng): ghi sổ phiếu xuất, tính giá tháng rồi mới ghi sổ hóa đơn trả hàng."));
                gl.Add(new VoucherLineInput(role("GRNI"), Side.Credit, cost, orig.PartnerId, Description: l.Description ?? $"Hàng mua trả lại – giá vốn phiếu {issue.Number}"));
                var gap = l.FunctionalAmount - cost;
                if (gap != 0)
                    gl.Add(new VoucherLineInput(role("COGS"), gap > 0 ? Side.Credit : Side.Debit, Math.Abs(gap), Description: $"Chênh lệch giá mua – giá vốn hàng trả lại {issue.Number}"));
                continue;
            }
            var change = reduces ? -l.FunctionalAmount : l.FunctionalAmount;   // change of the purchase value
            if (ol.StockDocumentId is { } receiptId)
            {
                var receipt = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstAsync(d => d.Id == receiptId);
                var rl = receipt.Lines.Single(x => x.LineNo == ol.StockLineNo);
                var split = await stock.AdjustReceiptValueAsync(ctx, receiptId, rl.LineNo, change, inv.PostingDate, inv.Id, $"{label}");
                if (split.InStockPart != 0)
                    gl.Add(new VoucherLineInput(itemAccounts[rl.ItemId], split.InStockPart > 0 ? Side.Debit : Side.Credit, Math.Abs(split.InStockPart),
                        Description: $"Điều chỉnh giá trị tồn kho theo {label}"));
                if (split.IssuedPart != 0)
                    gl.Add(new VoucherLineInput(role("COGS"), split.IssuedPart > 0 ? Side.Debit : Side.Credit, Math.Abs(split.IssuedPart),
                        Description: $"Điều chỉnh giá vốn hàng đã xuất theo {label}"));
            }
            else
                gl.Add(new VoucherLineInput(ol.AccountId!.Value, change > 0 ? Side.Debit : Side.Credit, Math.Abs(change), Description: l.Description ?? label));
        }
        var vat = inv.Lines.Sum(x => x.VatAmount);
        if (vat > 0) gl.Add(new VoucherLineInput(role("VAT_INPUT"), reduces ? Side.Credit : Side.Debit, vat, Description: reduces ? "Giảm thuế GTGT đầu vào" : "Thuế GTGT đầu vào"));
        gl.InsertRange(0, reduces
            ? await PartnerLinesAsync(ctx.CompanyId, inv, role("AP_TRADE"), Side.Debit, label, null, orig.VoucherId)
            : await PartnerLinesAsync(ctx.CompanyId, inv, role("AP_TRADE"), Side.Credit, label, orig.DueDate, null));
        return gl;
    }

    private async Task<(CommercialInvoice Invoice, CommercialInvoiceLine Line)> OriginalLineAsync(SessionContext ctx, Guid invoiceId, int lineNo,
        InvoiceKind kind = InvoiceKind.Sales)
    {
        var orig = await db.CommercialInvoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == ctx.CompanyId)
                   ?? throw new DomainException(ErrorCodes.NotFound, "Hóa đơn gốc không tồn tại.");
        if (orig.Kind != kind || orig.Purpose != InvoicePurpose.Normal || orig.Status != InvoiceStatus.Posted)
            throw new DomainException(ErrorCodes.ValidationFailed, kind == InvoiceKind.Sales
                ? "Chỉ nhận hàng trả lại cho hóa đơn bán thường đã ghi sổ."
                : "Chỉ xuất trả hàng cho hóa đơn mua thường đã ghi sổ.");
        return (orig, orig.Lines.FirstOrDefault(l => l.LineNo == lineNo) ?? throw new DomainException(ErrorCodes.NotFound, $"Hóa đơn gốc không có dòng {lineNo}."));
    }

    /// <summary>Stock quantity already returned against an original line (Draft or Posted return invoices).</summary>
    private async Task<decimal> ReturnedQuantityAsync(Guid originalInvoiceId, int originalLineNo, Guid? exclude)
        => (await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                   where i.AdjustsInvoiceId == originalInvoiceId && i.Purpose == InvoicePurpose.Return && l.AdjustsLineNo == originalLineNo
                         && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Posted) && i.Id != exclude
                   select l.StockQuantity).ToListAsync()).Sum(x => x ?? 0);

    private async Task<decimal> AdjustedAmountAsync(Guid originalInvoiceId, int originalLineNo, InvoicePurpose purpose)
        => (await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                   where i.AdjustsInvoiceId == originalInvoiceId && i.Purpose == purpose && l.AdjustsLineNo == originalLineNo
                         && (i.Status == InvoiceStatus.Draft || i.Status == InvoiceStatus.Posted)
                   select l.FunctionalAmount).ToListAsync()).Sum();
}

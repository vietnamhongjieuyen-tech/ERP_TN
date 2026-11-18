using Hak.Application.Inventory;
using Hak.Application.Trade;
using Hak.Domain.Common;
using Hak.Domain.Inventory;
using Hak.Domain.Trade;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

public class TradeTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;
    private static DateOnly D(int m, int d) => new(2026, m, d);

    private sealed class Fx : IDisposable
    {
        public required TestHost H;
        public required Scope S;
        public required InvoiceService Inv;
        public Guid Wh, Dam, Add;
        public void Dispose() { S.Dispose(); H.Dispose(); }
        public Guid A(string code) => H.Acc[code];
        public Task<decimal> Bal(string acc, Guid? partner = null) => S.Ledger.AccountBalanceAsync(H.Auditor, A(acc), D(12, 31), partner);
    }

    /// <summary>Test-fixture accounts (neutral codes) mapped to the roles the invoice module needs.</summary>
    private static async Task<Fx> SetupAsync()
    {
        var h = await TestHost.CreateAsync();
        var s = h.NewScope();
        foreach (var (code, name) in new[] { ("INV", "Hàng tồn kho"), ("GRNI", "Hàng về chưa có hóa đơn"), ("VATIN", "Thuế GTGT đầu vào"), ("VATOUT", "Thuế GTGT đầu ra"), ("SALESREV", "Doanh thu bán hàng"), ("COGS", "Giá vốn"), ("SALESRET", "Hàng bán bị trả lại"), ("SALESDISC", "Giảm giá hàng bán") })
            h.Acc[code] = (await s.Setup.CreateAccountAsync(h.Admin, code, name + " (thử)")).Id;
        // GRNI followed per supplier (company decision 16/10/2026): every GRNI line must carry the supplier.
        await s.Setup.UpdateAccountAttributesAsync(h.Admin, h.Acc["GRNI"], true, Hak.Domain.Catalog.OpenItemNature.None, Hak.Domain.Catalog.CashKind.None,
            Hak.Domain.Catalog.NegativeBalancePolicy.None);
        foreach (var (role, code) in new[] { ("AR_TRADE", "AR"), ("AP_TRADE", "AP"), ("GRNI", "GRNI"), ("VAT_INPUT", "VATIN"), ("VAT_OUTPUT", "VATOUT"), ("SALES_REVENUE", "SALESREV"), ("SALES_RETURNS", "SALESRET"), ("SALES_DISCOUNT", "SALESDISC"), ("COGS", "COGS") })
            await s.Rules.SetRoleMappingAsync(h.Admin, role, code);
        var fx = new Fx { H = h, S = s, Inv = new InvoiceService(s.Db, s.Engine, s.Stock, s.Audit, h.Clock) };
        fx.Wh = (await s.Stock.CreateWarehouseAsync(h.Admin, "BAI", "Bãi dăm")).Id;
        await s.Stock.SetCostingPolicyAsync(h.Admin, fx.Wh, CostingMethod.MovingAverage, D(1, 1), "QĐ test");
        fx.Dam = (await s.Stock.CreateItemAsync(h.Admin, "DAM", "Dăm gỗ", "tấn", "INV")).Id;
        fx.Add = (await s.Stock.CreateItemAsync(h.Admin, "PHU", "Phụ gia", "kg", "INV")).Id;
        await s.Dryness.SetItemTracksDrynessAsync(h.Admin, fx.Dam, true);
        await s.Dryness.CreateMethodAsync(h.Admin, "DO-KHO", "BDMT = MT × độ khô%", 2, 3, D(1, 1), "Doanh nghiệp xác nhận 14/10/2026");
        return fx;
    }

    private static async Task<StockDocument> DraftReceiptAsync(Fx fx, decimal mt, DateOnly date, Guid? item = null)
        => await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Receipt, fx.Wh, date, date, "Nhập dăm chờ hóa đơn",
            [new StockLineInput(item ?? fx.Dam, mt, 0, fx.A("GRNI"))], PartnerId: fx.H.Partner["NCC01"]));

    private static async Task<StockDocument> PostedReceiptAsync(Fx fx, decimal qty, decimal value, DateOnly date, Guid item)
    {
        var d = await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Receipt, fx.Wh, date, date, "Nhập",
            [new StockLineInput(item, qty, value, fx.A("GRNI"))], PartnerId: fx.H.Partner["NCC01"]));
        return await fx.S.Stock.PostDocumentAsync(fx.H.Approver, d.Id);
    }

    private static async Task<StockDocument> PostedIssueAsync(Fx fx, decimal qty, DateOnly date, Guid? item = null)
    {
        var d = await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Issue, fx.Wh, date, date, "Xuất bán",
            [new StockLineInput(item ?? fx.Dam, qty, CounterAccountId: fx.A("COGS"))]));
        return await fx.S.Stock.PostDocumentAsync(fx.H.Approver, d.Id);
    }

    private static InvoiceInput Purchase(Fx fx, string no, params InvoiceLineInput[] lines)
        => new(InvoiceKind.Purchase, fx.H.Partner["NCC01"], "1C26TAA", no, D(3, 3), D(3, 3), "Mua dăm gỗ", lines, DueDate: D(4, 2));

    /// <summary>Posted purchase invoice of 100 kg additive at 50,000/kg + VAT 10% (receipt valued 5,000,000 through GRNI).</summary>
    private static async Task<CommercialInvoice> PostedPurchaseOfAdditiveAsync(Fx fx, string no = "0000300")
    {
        var receipt = await DraftReceiptAsync(fx, 100, D(3, 2), fx.Add);
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, no, new InvoiceLineInput(50_000, receipt.Id, 1, VatRate: 10)));
        return await fx.Inv.PostAsync(fx.H.Approver, inv.Id);
    }

    [Fact]
    public async Task AnalysisCode_OnStockIssueGoesToCostLine_OnServiceInvoiceLineToRevenue()
    {
        using var fx = await SetupAsync();
        var code = await new Hak.Application.Setup.WorkflowSetupService(fx.S.Db, fx.S.Audit, fx.H.Clock).CreateAnalysisCodeAsync(fx.H.Preparer, "Chuyến tàu", "T01", "Tàu 01");
        await PostedReceiptAsync(fx, 100, 5_000_000, D(3, 2), fx.Add);
        var issue = await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Issue, fx.Wh, D(3, 5), D(3, 5), "Xuất bán",
            [new StockLineInput(fx.Add, 40, CounterAccountId: fx.A("COGS"), AnalysisCodeId: code.Id)]));
        issue = await fx.S.Stock.PostDocumentAsync(fx.H.Approver, issue.Id);
        var cost = await fx.S.Db.JournalLines.AsNoTracking().Where(l => l.AnalysisCodeId == code.Id).ToListAsync();
        Assert.Equal((fx.A("COGS"), 2_000_000m), (Assert.Single(cost).AccountId, cost[0].Amount));      // never on the inventory account

        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26TBB", "0000007", D(3, 6), D(3, 6), "Phí dịch vụ",
            [new InvoiceLineInput(3_000_000, Quantity: 1, AccountId: fx.A("SALESREV"), VatRate: 10, AnalysisCodeId: code.Id)]));
        await fx.Inv.PostAsync(fx.H.Approver, inv.Id);
        var tagged = await fx.S.Db.JournalLines.AsNoTracking().Where(l => l.AnalysisCodeId == code.Id).Select(l => new { l.AccountId, l.Amount }).ToListAsync();
        Assert.Contains(tagged, l => l.AccountId == fx.A("SALESREV") && l.Amount == 3_000_000m);
        Assert.Equal(2, tagged.Count);                                                                 // not on receivable or VAT lines
    }

    [Fact]
    public async Task PurchaseReturn_IssuedAtCost_CreditNoteClearsGrni_GapToCogs_AndSettlesAp()
    {
        using var fx = await SetupAsync();
        var invoice = await PostedPurchaseOfAdditiveAsync(fx);
        await PostedReceiptAsync(fx, 100, 7_000_000, D(3, 4), fx.Add);          // moving average now 60,000/kg

        var issue = await fx.Inv.CreatePurchaseReturnIssueAsync(fx.H.Preparer, invoice.Id, 1, 20, D(3, 10));
        Assert.Equal(StockDocumentStatus.Draft, issue.Status);
        var credit = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(invoice.Id, InvoicePurpose.Return, "1C26TAA", "0000301", D(3, 10), D(3, 10),
            "Trả 20 kg phụ gia kém chất lượng", [new AdjustmentLineInput(1, issue.Id, 1)]));
        Assert.Equal((1_000_000m, 100_000m), (credit.Lines[0].FunctionalAmount, credit.Lines[0].VatAmount));
        await fx.Inv.PostAsync(fx.H.Approver, credit.Id);

        // Issue at cost 20 × 60,000 = 1,200,000; supplier credits 1,000,000 + VAT → 200,000 to COGS.
        Assert.Equal(-4_400_000m, await fx.Bal("AP", fx.H.Partner["NCC01"]));
        Assert.Equal(400_000m, await fx.Bal("VATIN"));
        Assert.Equal(200_000m, await fx.Bal("COGS"));
        Assert.Equal(10_800_000m, await fx.Bal("INV"));
        Assert.Equal(-7_000_000m, await fx.Bal("GRNI"));                         // only the un-invoiced second receipt remains
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);
        var open = Assert.Single(await fx.S.Settlement.OpenItemsAsync(fx.H.Auditor, fx.A("AP"), fx.H.Partner["NCC01"], D(12, 31)));
        Assert.Equal(4_400_000m, open.Open);                                     // the credit note settled the original payable

        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreatePurchaseReturnIssueAsync(fx.H.Preparer, invoice.Id, 1, 81, D(3, 11))));
        var issue2 = await fx.Inv.CreatePurchaseReturnIssueAsync(fx.H.Preparer, invoice.Id, 1, 5, D(3, 11));
        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(invoice.Id, InvoicePurpose.Return,
            "1C26TAA", "0000302", D(3, 11), D(3, 11), "Lần 2", [new AdjustmentLineInput(1, issue.Id, 1)]))));       // issue already on a credit note
        await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(invoice.Id, InvoicePurpose.Return, "1C26TAA", "0000302", D(3, 11), D(3, 11),
            "Lần 2", [new AdjustmentLineInput(1, issue2.Id, 1)]));
    }

    [Fact]
    public async Task PurchaseDiscount_SplitsBetweenStockAndCogs_AndReversalKeepsStockLedgerInLine()
    {
        using var fx = await SetupAsync();
        var invoice = await PostedPurchaseOfAdditiveAsync(fx);
        await PostedIssueAsync(fx, 40, D(3, 5), fx.Add);                       // 40 kg used: COGS 2,000,000
        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(invoice.Id, InvoicePurpose.Discount,
            "1C26TAA", "0000310", D(3, 12), D(3, 12), "Giảm giá", [new AdjustmentLineInput(1, Amount: 5_000_001)]))));

        var discount = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(invoice.Id, InvoicePurpose.Discount, "1C26TAA", "0000310", D(3, 12), D(3, 12),
            "Giảm giá theo biên bản chất lượng", [new AdjustmentLineInput(1, Amount: 1_000_000)]));
        await fx.Inv.PostAsync(fx.H.Approver, discount.Id);
        // 60 kg still in stock → −600,000 inventory; 40 kg issued → −400,000 COGS; VAT −100,000; AP −1,100,000.
        Assert.Equal(2_400_000m, await fx.Bal("INV"));
        Assert.Equal(1_600_000m, await fx.Bal("COGS"));
        Assert.Equal(400_000m, await fx.Bal("VATIN"));
        Assert.Equal(-4_400_000m, await fx.Bal("AP", fx.H.Partner["NCC01"]));
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);

        await fx.Inv.ReverseAsync(fx.H.Approver, discount.Id, D(3, 13), "Nhà cung cấp hủy biên bản");
        Assert.Equal(3_000_000m, await fx.Bal("INV"));
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);
        var summary = Assert.Single(await fx.S.Stock.SummaryAsync(fx.H.Auditor, D(1, 1), D(12, 31)), r => r.ItemId == fx.Add);
        Assert.Equal((60m, 3_000_000m), (summary.ClosingQty, summary.ClosingValue));
    }

    [Fact]
    public async Task Purchase_PricedPerBdmt_ValuesTheDraftReceipt_AndBooksApWithVat()
    {
        using var fx = await SetupAsync();
        var receipt = await DraftReceiptAsync(fx, 100, D(3, 2));
        Assert.Equal(ErrorCodes.MeasurementPending, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer,
            Purchase(fx, "0000123", new InvoiceLineInput(2_000_000, receipt.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)))));

        await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(receipt.Id, 1, 52.35m, EvidenceType.InternalReport, "PĐ-1", D(3, 2)));
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "0000123", new InvoiceLineInput(2_000_000, receipt.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)));
        var line = Assert.Single(inv.Lines);
        Assert.Equal((52.35m, 104_700_000m, 8_376_000m), (line.BasisQuantity, line.FunctionalAmount, line.VatAmount)); // 52.35 BDMT × 2,000,000

        Assert.Equal(ErrorCodes.SodViolation, await CodeOf(() => fx.Inv.PostAsync(fx.H.Preparer with { Permissions = new HashSet<string>(fx.H.Preparer.Permissions) }, inv.Id)));
        await fx.Inv.PostAsync(fx.H.Approver, inv.Id);
        Assert.Equal(StockDocumentStatus.Posted, (await fx.S.Db.StockDocuments.AsNoTracking().SingleAsync(d => d.Id == receipt.Id)).Status);
        Assert.Equal(104_700_000m, await fx.Bal("INV"));       // stock carried at MT, valued from BDMT × price
        Assert.Equal(0m, await fx.Bal("GRNI"));
        Assert.Equal(8_376_000m, await fx.Bal("VATIN"));
        Assert.Equal(-113_076_000m, await fx.Bal("AP", fx.H.Partner["NCC01"]));
        var open = Assert.Single(await fx.S.Settlement.OpenItemsAsync(fx.H.Auditor, fx.A("AP"), null, D(12, 31)));
        Assert.Equal(D(4, 2), open.DueDate);
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);
        Assert.Empty(await fx.Inv.UninvoicedAsync(fx.H.Auditor, InvoiceKind.Purchase, D(12, 31)));
    }

    [Fact]
    public async Task Sales_ExportInUsdPerBdmt_FromShipCertificate_NoDoubleRevenue()
    {
        using var fx = await SetupAsync();
        await PostedReceiptAsync(fx, 100, 150_000_000, D(3, 1), fx.Dam);
        var issue = await PostedIssueAsync(fx, 60, D(3, 10));
        Assert.Equal(90_000_000m, await fx.Bal("COGS")); // cost by MT (moving average)

        var sale = new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26TXK", "0000045", D(3, 12), D(3, 12), "Xuất khẩu dăm gỗ",
            [new InvoiceLineInput(180, issue.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 0)], DueDate: D(4, 11), Currency: "USD", ExchangeRate: 25_000);
        Assert.Equal(ErrorCodes.MeasurementPending, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, sale)));
        var cert = await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(issue.Id, 1, 55.5m, EvidenceType.Certificate, "CT-0456", D(3, 11), MeasuredMt: 59.2m));

        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, sale);
        var line = Assert.Single(inv.Lines);
        Assert.Equal((32.856m, 5_914.08m, 147_852_000m), (line.BasisQuantity, line.Amount, line.FunctionalAmount)); // 32.856 × 180 USD × 25,000
        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, sale with { InvoiceNo = "0000046" })));

        await fx.Inv.PostAsync(fx.H.Approver, inv.Id);
        Assert.Equal(147_852_000m, await fx.Bal("AR", fx.H.Partner["KH01"]));
        Assert.Equal(-147_852_000m, await fx.Bal("SALESREV"));
        // The receivable is a foreign-currency open item, so it can be paid and revalued in USD.
        var receivable = Assert.Single(await fx.S.Settlement.OpenItemsAsync(fx.H.Auditor, fx.A("AR"), fx.H.Partner["KH01"], D(3, 31)));
        Assert.Equal(("USD", 5_914.08m, 5_914.08m, 147_852_000m), (receivable.ForeignCurrency, receivable.ForeignAmount, receivable.OpenForeign, receivable.Open));
        Assert.Empty(await fx.Inv.UninvoicedAsync(fx.H.Auditor, InvoiceKind.Sales, D(12, 31)));
        Assert.Equal(ErrorCodes.DuplicateInvoice, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, sale)));

        // Reversal releases the delivery for re-invoicing; the cost of goods sold stays.
        await fx.Inv.ReverseAsync(fx.H.Approver, inv.Id, D(3, 15), "Sai tỷ giá");
        Assert.Equal(0m, await fx.Bal("SALESREV"));
        Assert.Equal(90_000_000m, await fx.Bal("COGS"));
        Assert.Single(await fx.Inv.UninvoicedAsync(fx.H.Auditor, InvoiceKind.Sales, D(12, 31)));
        _ = cert;
    }

    [Fact]
    public async Task SupersededCertificate_AfterDraftInvoice_BlocksPostingAtStaleBdmt()
    {
        using var fx = await SetupAsync();
        await PostedReceiptAsync(fx, 100, 150_000_000, D(3, 1), fx.Dam);
        var issue = await PostedIssueAsync(fx, 60, D(3, 10));
        var cert = await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(issue.Id, 1, 55.5m, EvidenceType.Certificate, "CT-1", D(3, 11)));
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26T", "9", D(3, 12), D(3, 12), "Bán",
            [new InvoiceLineInput(4_000_000, issue.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)]));
        await fx.S.Dryness.SupersedeAsync(fx.H.Preparer, cert.Id, new MeasurementInput(issue.Id, 1, 54.9m, EvidenceType.Certificate, "CT-1-SUA", D(3, 13)), "Chứng thư điều chỉnh");
        Assert.Equal(ErrorCodes.MeasurementPending, await CodeOf(() => fx.Inv.PostAsync(fx.H.Approver, inv.Id)));
        await fx.Inv.CancelAsync(fx.H.Preparer, inv.Id, "Lập lại theo chứng thư điều chỉnh");
        var again = await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26T", "9", D(3, 13), D(3, 13), "Bán",
            [new InvoiceLineInput(4_000_000, issue.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)]));
        Assert.Equal(32.94m, again.Lines[0].BasisQuantity); // 60 × 54.9%
    }

    [Fact]
    public async Task PostedReceipt_MustMatchInvoiceValue_OtherwisePriceDifference()
    {
        using var fx = await SetupAsync();
        var r = await PostedReceiptAsync(fx, 10, 1_000_000, D(3, 1), fx.Add);
        Assert.Single(await fx.Inv.UninvoicedAsync(fx.H.Auditor, InvoiceKind.Purchase, D(12, 31))); // goods received, invoice pending
        var bad = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "777", new InvoiceLineInput(105_000, r.Id, 1, VatRate: 10)));
        Assert.Equal(ErrorCodes.PriceDifference, await CodeOf(() => fx.Inv.PostAsync(fx.H.Approver, bad.Id)));
        await fx.Inv.CancelAsync(fx.H.Preparer, bad.Id, "Sai giá");
        var ok = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "778", new InvoiceLineInput(100_000, r.Id, 1, VatRate: 10)));
        await fx.Inv.PostAsync(fx.H.Approver, ok.Id);
        Assert.Equal((0m, 100_000m, -1_100_000m), (await fx.Bal("GRNI"), await fx.Bal("VATIN"), await fx.Bal("AP")));
    }

    [Fact]
    public async Task ServiceLines_DuplicateSupplierInvoice_AndPartialSalesInvoicing()
    {
        using var fx = await SetupAsync();
        var svc = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "555", new InvoiceLineInput(250_000, Quantity: 4, AccountId: fx.A("EXP"), VatRate: 10, Description: "Vận chuyển")));
        await fx.Inv.PostAsync(fx.H.Approver, svc.Id);
        Assert.Equal((1_000_000m, 100_000m), (await fx.Bal("EXP"), await fx.Bal("VATIN")));
        Assert.Equal(ErrorCodes.DuplicateInvoice, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "555", new InvoiceLineInput(1, Quantity: 1, AccountId: fx.A("EXP"))))));
        await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "555", new InvoiceLineInput(1, Quantity: 1, AccountId: fx.A("EXP"))) with { PartnerId = fx.H.Partner["KH02"] }); // other seller, same number: allowed

        await PostedReceiptAsync(fx, 10, 1_000, D(3, 1), fx.Add);
        var issue = await PostedIssueAsync(fx, 10, D(3, 2), fx.Add);
        InvoiceInput SalesLine(decimal q) => new(InvoiceKind.Sales, fx.H.Partner["KH01"], "S", Guid.NewGuid().ToString("N")[..8], D(3, 3), D(3, 3), "Bán phụ gia",
            [new InvoiceLineInput(500, issue.Id, 1, StockQuantity: q, VatRate: 10)]);
        await fx.Inv.CreateAsync(fx.H.Preparer, SalesLine(4));
        await fx.Inv.CreateAsync(fx.H.Preparer, SalesLine(6));
        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, SalesLine(1))));
        // BDMT pricing is only for dryness-tracked items.
        await PostedReceiptAsync(fx, 5, 500, D(3, 4), fx.Add);
        var issue2 = await PostedIssueAsync(fx, 5, D(3, 5), fx.Add);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "S", "x", D(3, 5), D(3, 5), "x",
            [new InvoiceLineInput(1, issue2.Id, 1, Basis: PricingBasis.Bdmt)]))));
    }

    [Fact]
    public async Task InvoiceVoucher_CannotBeReversedOutsideTheInvoice_AndPostedInvoiceIsFrozen()
    {
        using var fx = await SetupAsync();
        var svc = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "1", new InvoiceLineInput(100, Quantity: 1, AccountId: fx.A("EXP"))));
        svc = await fx.Inv.PostAsync(fx.H.Approver, svc.Id);
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => fx.S.Engine.ReverseAsync(fx.H.Approver, svc.VoucherId!.Value, D(3, 5), "x")));
        var tracked = await fx.S.Db.CommercialInvoices.Include(i => i.Lines).SingleAsync(i => i.Id == svc.Id);
        tracked.Lines[0].UnitPrice = 1;
        Assert.Equal(ErrorCodes.LedgerImmutable, (await Assert.ThrowsAsync<DomainException>(() => fx.S.Db.SaveChangesAsync())).Code);
    }

    /// <summary>Posted BDMT sale used by the adjustment tests: 60 MT issued (cost 90,000,000), 33.3 BDMT × 4,000,000 + 8% VAT.</summary>
    private static async Task<(CommercialInvoice Invoice, StockDocument Issue)> PostedBdmtSaleAsync(Fx fx)
    {
        await PostedReceiptAsync(fx, 100, 150_000_000, D(3, 1), fx.Dam);
        var issue = await PostedIssueAsync(fx, 60, D(3, 10));
        await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(issue.Id, 1, 55.5m, EvidenceType.Certificate, "CT-9", D(3, 11)));
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26T", "100", D(3, 12), D(3, 12), "Bán dăm",
            [new InvoiceLineInput(4_000_000, issue.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)], DueDate: D(4, 30)));
        return (await fx.Inv.PostAsync(fx.H.Approver, inv.Id), issue);
    }

    [Fact]
    public async Task SalesReturn_RestocksAtOriginalCost_ReducesRevenueByReturnBdmt_AndSettlesTheInvoice()
    {
        using var fx = await SetupAsync();
        var (inv, _) = await PostedBdmtSaleAsync(fx);
        Assert.Equal(143_856_000m, await fx.Bal("AR")); // 33.3 BDMT × 4,000,000 = 133,200,000 + 10,656,000 VAT

        var ret = await fx.Inv.CreateReturnReceiptAsync(fx.H.Preparer, inv.Id, 1, 10, D(3, 15));
        Assert.Equal(15_000_000m, ret.Lines[0].Amount);   // 90,000,000 × 10 / 60: back at the cost that was booked
        var adj = new AdjustmentInput(inv.Id, InvoicePurpose.Return, "1C26T", "101", D(3, 16), D(3, 16), "Khách trả lại 10 MT",
            [new AdjustmentLineInput(1, ret.Id, 1)]);
        Assert.Equal(ErrorCodes.MeasurementPending, await CodeOf(() => fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, adj))); // measured at receipt, like any receipt
        await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(ret.Id, 1, 50m, EvidenceType.InternalReport, "PĐ-TL", D(3, 15)));
        var note = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, adj);
        Assert.Equal((5m, 20_000_000m, 1_600_000m), (note.Lines[0].BasisQuantity, note.Lines[0].FunctionalAmount, note.Lines[0].VatAmount));
        await fx.Inv.PostAsync(fx.H.Approver, note.Id);

        Assert.Equal(75_000_000m, await fx.Bal("COGS"));
        Assert.Equal(75_000_000m, await fx.Bal("INV"));
        Assert.Equal(20_000_000m, await fx.Bal("SALESRET"));
        Assert.Equal(-9_056_000m, await fx.Bal("VATOUT"));
        Assert.Equal(122_256_000m, await fx.Bal("AR"));
        var open = Assert.Single(await fx.S.Settlement.OpenItemsAsync(fx.H.Auditor, fx.A("AR"), null, D(12, 31)));
        Assert.Equal(122_256_000m, open.Open); // the credit note settled part of the original invoice
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);

        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateReturnReceiptAsync(fx.H.Preparer, inv.Id, 1, 51, D(3, 20))));
    }

    [Fact]
    public async Task Discount_And_Increase_AdjustRevenueAndReceivable_WithinLimits()
    {
        using var fx = await SetupAsync();
        var (inv, _) = await PostedBdmtSaleAsync(fx);
        var disc = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(inv.Id, InvoicePurpose.Discount, "1C26T", "102", D(3, 20), D(3, 20),
            "Giảm giá do độ khô thấp", [new AdjustmentLineInput(1, Amount: 5_000_000)]));
        await fx.Inv.PostAsync(fx.H.Approver, disc.Id);
        Assert.Equal(5_000_000m, await fx.Bal("SALESDISC"));
        Assert.Equal(143_856_000m - 5_400_000m, await fx.Bal("AR"));
        Assert.Equal(ErrorCodes.OverInvoiced, await CodeOf(() => fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(inv.Id, InvoicePurpose.Discount,
            "1C26T", "103", D(3, 21), D(3, 21), "x", [new AdjustmentLineInput(1, Amount: 128_200_001)]))));

        var inc = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(inv.Id, InvoicePurpose.Increase, "1C26T", "104", D(3, 22), D(3, 22),
            "Điều chỉnh tăng phí", [new AdjustmentLineInput(1, Amount: 1_000_000)]));
        await fx.Inv.PostAsync(fx.H.Approver, inc.Id);
        Assert.Equal(143_856_000m - 5_400_000m + 1_080_000m, await fx.Bal("AR"));
        Assert.Equal(-134_200_000m, await fx.Bal("SALESREV"));
        Assert.True((await fx.S.Settlement.ReconcileAsync(fx.H.Auditor, D(12, 31))).IsReconciled);
    }

    [Fact]
    public async Task Adjustments_OnlyForPostedNormalSalesInvoices()
    {
        using var fx = await SetupAsync();
        var p = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "9", new InvoiceLineInput(100, Quantity: 1, AccountId: fx.A("EXP"))));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(p.Id, InvoicePurpose.Discount,
            "X", "1", D(3, 3), D(3, 3), "x", [new AdjustmentLineInput(1, Amount: 1)]))));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => fx.Inv.CreateReturnReceiptAsync(fx.H.Preparer, p.Id, 1, 1, D(3, 3))));
    }

    private static OrderService Orders(Fx fx) => new(fx.S.Db, fx.S.Audit, fx.H.Clock);

    [Fact]
    public async Task SalesOrder_ControlsDeliveries_WithTolerance_AndTracksChainToPayment()
    {
        using var fx = await SetupAsync();
        var orders = Orders(fx);
        await PostedReceiptAsync(fx, 200, 300_000_000, D(3, 1), fx.Dam);
        var order = await orders.CreateAsync(fx.H.Preparer, new OrderInput(InvoiceKind.Sales, fx.H.Partner["KH01"], D(3, 2), "HĐ bán dăm số 01/2026",
            [new OrderLineInput(fx.Dam, 100, 4_000_000, PricingBasis.Bdmt, 8)], ContractNo: "01/2026/HĐMB", OverDeliveryTolerancePercent: 5));

        StockDocumentInput Issue(decimal q) => new(StockDocumentType.Issue, fx.Wh, D(3, 10), D(3, 10), "Giao theo đơn",
            [new StockLineInput(fx.Dam, q, CounterAccountId: fx.A("COGS"), OrderId: order.Id, OrderLineNo: 1)], PartnerId: fx.H.Partner["KH01"]);
        Assert.Equal(ErrorCodes.OrderMismatch, await CodeOf(() => fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(60)))); // not confirmed yet
        Assert.Equal(ErrorCodes.SodViolation, await CodeOf(() => orders.ConfirmAsync(fx.H.Preparer with { Permissions = new HashSet<string>(fx.H.Preparer.Permissions) { "voucher.approve" } }, order.Id)));
        await orders.ConfirmAsync(fx.H.Approver, order.Id);

        var d1 = await fx.S.Stock.PostDocumentAsync(fx.H.Approver, (await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(60))).Id);
        var d2draft = await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(40));
        Assert.Equal(ErrorCodes.OverDelivered, await CodeOf(() => fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(6)))); // 100 + 5% = 105 max
        await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(5));                                                        // exactly 105 allowed
        Assert.Equal(ErrorCodes.OrderMismatch, await CodeOf(() => fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(1) with { PartnerId = fx.H.Partner["KH02"] })));
        await fx.S.Stock.CancelDocumentAsync(fx.H.Preparer, d2draft.Id, "Khách lùi lịch");
        await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, Issue(35)); // cancelled draft no longer counts

        await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(d1.Id, 1, 55.5m, EvidenceType.Certificate, "CT-ORD", D(3, 11)));
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26T", "300", D(3, 12), D(3, 12), "Bán theo đơn",
            [new InvoiceLineInput(4_000_000, d1.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8)]));
        Assert.Equal(order.Id, inv.Lines[0].OrderId);
        await fx.Inv.PostAsync(fx.H.Approver, inv.Id);

        // Customer pays half by bank, settling the invoice.
        await Phase2Fixture.ConfigureAsync(fx.H, fx.S);
        var arLine = Phase2Fixture.LineOf(fx.S, inv.VoucherId!.Value, fx.A("AR"));
        await Phase2Fixture.PostDocAsync(fx.H, fx.S, Phase2Fixture.Receipt(fx.H, 71_928_000, D(3, 20), arLine, main: "BANK"));

        var f = await orders.FulfilmentAsync(fx.H.Auditor, order.Id);
        var line = Assert.Single(f.Lines);
        Assert.Equal((100m, 60m, 40m, 33.3m, 60m, 133_200_000m, 40m), (line.Ordered, line.StockPosted, line.StockDraft, line.Bdmt, line.Invoiced, line.InvoicedAmount, line.Outstanding));
        var iv = Assert.Single(f.Invoices);
        Assert.Equal((143_856_000m, 71_928_000m, 71_928_000m), (iv.Total, iv.Settled, iv.Open));

        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => orders.CancelAsync(fx.H.Preparer, order.Id, "x")));
        await orders.CloseAsync(fx.H.Preparer, order.Id, "Khách không lấy phần còn lại");
        var pending = (await fx.S.Db.StockDocuments.AsNoTracking().Where(d => d.Status == StockDocumentStatus.Draft).ToListAsync()).First();
        Assert.Equal(ErrorCodes.OrderMismatch, await CodeOf(() => fx.S.Stock.PostDocumentAsync(fx.H.Approver, pending.Id))); // closed order: drafts cannot post
    }

    [Fact]
    public async Task PurchaseOrder_LinksReceiptAndServiceLines_AndRejectsWrongItemOrKind()
    {
        using var fx = await SetupAsync();
        var orders = Orders(fx);
        var po = await orders.CreateAsync(fx.H.Preparer, new OrderInput(InvoiceKind.Purchase, fx.H.Partner["NCC01"], D(3, 1), "Mua dăm",
            [new OrderLineInput(fx.Dam, 50, 2_000_000, PricingBasis.Bdmt, 8)]));
        await orders.ConfirmAsync(fx.H.Approver, po.Id);
        Assert.Equal(ErrorCodes.OrderMismatch, await CodeOf(() => fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Receipt, fx.Wh,
            D(3, 2), D(3, 2), "x", [new StockLineInput(fx.Add, 1, 0, fx.A("GRNI"), OrderId: po.Id, OrderLineNo: 1)], PartnerId: fx.H.Partner["NCC01"]))));
        Assert.Equal(ErrorCodes.OrderMismatch, await CodeOf(() => fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Issue, fx.Wh,
            D(3, 2), D(3, 2), "x", [new StockLineInput(fx.Dam, 1, CounterAccountId: fx.A("COGS"), OrderId: po.Id, OrderLineNo: 1)], PartnerId: fx.H.Partner["NCC01"]))));

        var receipt = await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer, new StockDocumentInput(StockDocumentType.Receipt, fx.Wh, D(3, 2), D(3, 2), "Nhập theo đơn",
            [new StockLineInput(fx.Dam, 50, 0, fx.A("GRNI"), OrderId: po.Id, OrderLineNo: 1)], PartnerId: fx.H.Partner["NCC01"]));
        await fx.S.Dryness.RecordAsync(fx.H.Preparer, new MeasurementInput(receipt.Id, 1, 50m, EvidenceType.InternalReport, "PĐ", D(3, 2)));
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "800",
            new InvoiceLineInput(2_000_000, receipt.Id, 1, Basis: PricingBasis.Bdmt, VatRate: 8),
            new InvoiceLineInput(500_000, Quantity: 1, AccountId: fx.A("EXP"), VatRate: 10, Description: "Cước vận chuyển", OrderId: po.Id, OrderLineNo: 1)));
        await fx.Inv.PostAsync(fx.H.Approver, inv.Id);
        var f = await orders.FulfilmentAsync(fx.H.Auditor, po.Id);
        Assert.Equal((50m, 25m, 50m, 0m), (f.Lines[0].StockPosted, f.Lines[0].Bdmt, f.Lines[0].Invoiced, f.Lines[0].Outstanding));
        Assert.Equal(50_000_000m + 500_000m, f.Lines[0].InvoicedAmount);
    }

    private static async Task<decimal> IssueValue(Fx fx, StockDocument doc)
    {
        var m = await fx.S.Db.StockMovements.AsNoTracking().SingleAsync(x => x.DocumentId == doc.Id);
        return -(await fx.S.Db.StockValuations.AsNoTracking().SingleAsync(v => v.MovementId == m.Id && v.VoidedAt == null)).Value;
    }

    [Fact]
    public async Task PriceDifference_MovingAverage_SplitsBetweenStockAndCogs_AfterAcknowledgedWarning()
    {
        using var fx = await SetupAsync();
        var r = await PostedReceiptAsync(fx, 10, 1_000_000, D(3, 1), fx.Add);          // provisional value on GRNI
        await PostedIssueAsync(fx, 4, D(3, 2), fx.Add);                                 // 400,000 to COGS
        var inv = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "901", new InvoiceLineInput(110_000, r.Id, 1, VatRate: 10)));  // invoice 1,100,000

        var warn = Assert.Single(await fx.Inv.PriceDifferencesAsync(fx.H.Approver, inv.Id));
        Assert.Equal((100_000m, 6m, 60_000m, 40_000m), (warn.Difference, warn.QuantityInStock, warn.InventoryPart, warn.CogsPart));
        var ex = await Assert.ThrowsAsync<DomainException>(() => fx.Inv.PostAsync(fx.H.Approver, inv.Id));
        Assert.Equal(ErrorCodes.PriceDifference, ex.Code);  // warning: nothing posted until acknowledged
        Assert.Equal(0m, await fx.Bal("AP"));

        await fx.Inv.PostAsync(fx.H.Approver, inv.Id, acknowledgePriceDifferences: true);
        Assert.Equal((0m, 660_000m, 440_000m, -1_210_000m), (await fx.Bal("GRNI"), await fx.Bal("INV"), await fx.Bal("COGS"), await fx.Bal("AP")));
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);

        var last = await PostedIssueAsync(fx, 6, D(3, 5), fx.Add);
        Assert.Equal(660_000m, await IssueValue(fx, last));    // the adjusted value flows out with the remaining stock
        Assert.Equal(0m, await fx.Bal("INV"));
        Assert.Equal(ErrorCodes.StockBackdated, await CodeOf(() => fx.S.Stock.ReverseDocumentAsync(fx.H.Approver, r.Id, D(3, 6), "x")));
    }

    [Fact]
    public async Task PriceDifference_Fifo_UsesTheReceiptsOwnRemainingLayer()
    {
        using var fx = await SetupAsync();
        var fifo = (await fx.S.Stock.CreateWarehouseAsync(fx.H.Admin, "K-FIFO", "Kho FIFO")).Id;
        await fx.S.Stock.SetCostingPolicyAsync(fx.H.Admin, fifo, CostingMethod.Fifo, D(1, 1), "QĐ test");
        async Task<StockDocument> Rcv(decimal v, DateOnly d) => await fx.S.Stock.PostDocumentAsync(fx.H.Approver, (await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer,
            new StockDocumentInput(StockDocumentType.Receipt, fifo, d, d, "Nhập", [new StockLineInput(fx.Add, 10, v, fx.A("GRNI"))], PartnerId: fx.H.Partner["NCC01"]))).Id);
        async Task<StockDocument> Iss(decimal q, DateOnly d) => await fx.S.Stock.PostDocumentAsync(fx.H.Approver, (await fx.S.Stock.CreateDocumentAsync(fx.H.Preparer,
            new StockDocumentInput(StockDocumentType.Issue, fifo, d, d, "Xuất", [new StockLineInput(fx.Add, q, CounterAccountId: fx.A("COGS"))]))).Id);
        var a = await Rcv(1_000_000, D(3, 1));
        var b = await Rcv(1_200_000, D(3, 2));
        Assert.Equal(1_240_000m, await IssueValue(fx, await Iss(12, D(3, 3))));        // all of A + 2/10 of B

        var invA = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "A1", new InvoiceLineInput(105_000, a.Id, 1)));
        Assert.Equal((0m, 50_000m), (Assert.Single(await fx.Inv.PriceDifferencesAsync(fx.H.Approver, invA.Id)).InventoryPart, (await fx.Inv.PriceDifferencesAsync(fx.H.Approver, invA.Id))[0].CogsPart));
        await fx.Inv.PostAsync(fx.H.Approver, invA.Id, acknowledgePriceDifferences: true);   // layer A fully issued → all to COGS

        var invB = await fx.Inv.CreateAsync(fx.H.Preparer, Purchase(fx, "B1", new InvoiceLineInput(115_000, b.Id, 1)));
        var wb = Assert.Single(await fx.Inv.PriceDifferencesAsync(fx.H.Approver, invB.Id));
        Assert.Equal((-50_000m, 8m, -40_000m, -10_000m), (wb.Difference, wb.QuantityInStock, wb.InventoryPart, wb.CogsPart));
        await fx.Inv.PostAsync(fx.H.Approver, invB.Id, acknowledgePriceDifferences: true);

        Assert.Equal(920_000m, await IssueValue(fx, await Iss(8, D(3, 6))));          // 960,000 − 40,000 left on layer B
        Assert.Equal(0m, await fx.Bal("INV"));
        Assert.Equal(1_240_000m + 50_000m - 10_000m + 920_000m, await fx.Bal("COGS"));
        Assert.Equal(0m, Assert.Single(await fx.S.Stock.ReconcileAsync(fx.H.Auditor, D(12, 31))).Difference);
    }

    [Fact]
    public async Task SalesBook_RevenueQuantityPrice_DiscountAsDeduction_CostFromCoveredIssues()
    {
        using var fx = await SetupAsync();
        await PostedReceiptAsync(fx, 100, 5_000_000, D(3, 1), fx.Add);                 // 50,000 / kg
        var issue = await PostedIssueAsync(fx, 40, D(3, 2), fx.Add);                     // cost 2,000,000
        InvoiceInput Sale(string no, decimal q, decimal price) => new(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26TBB", no, D(3, 3), D(3, 4), "Bán phụ gia",
            [new InvoiceLineInput(price, issue.Id, 1, StockQuantity: q, VatRate: 10)]);
        var a = await fx.Inv.PostAsync(fx.H.Approver, (await fx.Inv.CreateAsync(fx.H.Preparer, Sale("0000101", 30, 80_000))).Id);
        await fx.Inv.PostAsync(fx.H.Approver, (await fx.Inv.CreateAsync(fx.H.Preparer, Sale("0000102", 10, 90_000))).Id);
        var disc = await fx.Inv.CreateAdjustmentAsync(fx.H.Preparer, new AdjustmentInput(a.Id, InvoicePurpose.Discount, "1C26TBB", "0000103", D(3, 20), D(3, 20),
            "Chiết khấu", [new AdjustmentLineInput(1, Amount: 100_000)]));
        await fx.Inv.PostAsync(fx.H.Approver, disc.Id);
        var issue2 = await PostedIssueAsync(fx, 5, D(3, 6), fx.Add);
        await fx.Inv.CreateAsync(fx.H.Preparer, new InvoiceInput(InvoiceKind.Sales, fx.H.Partner["KH01"], "1C26TBB", "0000104", D(3, 6), D(3, 6), "Nháp",
            [new InvoiceLineInput(1, issue2.Id, 1, VatRate: 10)]));                       // draft: not in the book

        var book = await fx.Inv.SalesBookAsync(fx.H.Auditor, fx.Add, D(3, 1), D(3, 31));
        Assert.Equal(3, book.Rows.Count);
        var first = book.Rows[0];
        Assert.Equal((30m, 80_000m, 2_400_000m, "1C26TBB0000101", "AR"), (first.Quantity!.Value, first.UnitPrice!.Value, first.Revenue, first.InvoiceRef, first.CounterAccount));
        Assert.Equal(100_000m, book.Rows[2].Deduction);
        Assert.Equal((40m, 3_300_000m, 100_000m, 3_200_000m), (book.TotalQuantity, book.TotalRevenue, book.TotalDeduction, book.NetRevenue));
        Assert.True(book.CostComplete);
        Assert.Equal((2_000_000m, 1_200_000m), (book.CostOfSales, book.GrossProfit));    // 30 + 10 kg of the 40 kg issue
        Assert.Empty((await fx.Inv.SalesBookAsync(fx.H.Auditor, fx.Dam, D(3, 1), D(3, 31))).Rows);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => fx.Inv.SalesBookAsync(fx.H.Preparer with { Permissions = new HashSet<string>() }, fx.Add, D(3, 1), D(3, 31))));
    }
}

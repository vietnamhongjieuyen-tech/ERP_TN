using Hak.Application.Setup;
using Hak.Domain.Assets;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

public class ImportTests
{
    [Fact]
    public async Task Accounts_DryRunListsEveryProblem_AndImportIsAllOrNothing_ParentsFirst()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        AccountImportRow[] bad =
        [
            new(2, "LIAB_DT", "Phải trả chi tiết", "LIAB", RequiresPartner: "x", OpenItem: "phải trả"),   // child listed before its parent
            new(3, "LIAB", "Nợ phải trả (tổng hợp)", IsPostable: "không"),
            new(4, "CASH", "Tiền mặt (tên khác)"),                                                      // existing → skipped + note
            new(5, "BAD CODE", "x"),
            new(6, "LIAB", "trùng"),
            new(7, "AR2", "Phải thu 2", OpenItem: "phải thu"),                                         // open item without partner
            new(8, "X1", "x", "NOPE"),
            new(9, "X2", "x", "CASH"),                                                                 // parent is a detail account
            new(10, "X3", "x", CashKind: "vàng"),
        ];
        var dry = await s.CatalogImport.ImportAccountsAsync(h.Admin, bad, commit: true);
        Assert.False(dry.Committed);
        Assert.Equal([5, 6, 7, 8, 9, 10], dry.Issues.Select(i => i.Line));
        Assert.Equal(1, dry.Skipped);
        Assert.Contains(dry.Notes, n => n.Contains("CASH"));
        Assert.False(await s.Db.Accounts.AnyAsync(a => a.Code == "LIAB"));                          // nothing written

        var ok = await s.CatalogImport.ImportAccountsAsync(h.Admin, bad[..3], commit: true);
        Assert.True(ok.Committed);
        Assert.Equal((2, 1), (ok.Valid, ok.Skipped));
        var liab = await s.Db.Accounts.AsNoTracking().SingleAsync(a => a.Code == "LIAB");
        var child = await s.Db.Accounts.AsNoTracking().SingleAsync(a => a.Code == "LIAB_DT");
        Assert.False(liab.IsPostable);
        Assert.Equal((liab.Id, true, OpenItemNature.Payable), (child.ParentId!.Value, child.RequiresPartner, child.OpenItemNature));
        Assert.Equal("Tiền mặt", (await s.Db.Accounts.AsNoTracking().SingleAsync(a => a.Code == "CASH")).Name);   // never overwritten
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.Action == "import.accounts"));
    }

    [Fact]
    public async Task Partners_RolesAliases_ExistingSkipped()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        PartnerImportRow[] rows = [new(2, "ncc02", "Nhà cung cấp 2", "NCC, KH", "0101234567"), new(3, "KH01", "Khác tên"), new(4, "NV01", "Nhân viên 1", "nhân viên")];
        var r = await s.CatalogImport.ImportPartnersAsync(h.Admin, rows, commit: true);
        Assert.True(r.Committed);
        Assert.Equal((2, 1), (r.Valid, r.Skipped));
        var p = await s.Db.BusinessPartners.AsNoTracking().SingleAsync(x => x.Code == "NCC02");
        Assert.Equal((PartnerRoles.Customer | PartnerRoles.Supplier, "0101234567"), (p.Roles, p.TaxCode));

        var bad = await s.CatalogImport.ImportPartnersAsync(h.Admin, [new(2, "Z1", "z", "đối tác")], commit: true);
        Assert.False(bad.Committed);
        Assert.Single(bad.Issues);
        Assert.Equal("FORBIDDEN", (await Assert.ThrowsAsync<Hak.Domain.Common.DomainException>(() =>
            s.CatalogImport.ImportPartnersAsync(h.Auditor, rows, false))).Code);
    }

    [Fact]
    public async Task OpeningBalances_BecomeOneBalancedDraft_WithOpenItemsAfterPosting()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        OpeningImportRow[] rows =
        [
            new(2, "CASH", Debit: "1.500.000"),
            new(3, "AR", "KH01", Debit: "2,000,000.00", DueDate: "31/01/2026", Description: "HĐ 0001 năm trước"),
            new(4, "AR", "KH02", Debit: "500000"),
            new(5, "EQUITY", Credit: "4000000"),
        ];
        var dry = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), null, rows, commit: false);
        Assert.Empty(dry.Issues);
        Assert.Equal((4_000_000m, 4_000_000m, false), (dry.TotalDebit, dry.TotalCredit, dry.Committed));

        var bad = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), null,
            [new(2, "AR", Debit: "100"), new(3, "ASSETS", Debit: "1"), new(4, "EXP", Debit: "1,500"), new(5, "EXP", Debit: "1", Credit: "1"),
             new(6, "REV", Credit: "100", Currency: "XYZ", ForeignAmount: "5")], commit: true);
        Assert.False(bad.Committed);
        Assert.Equal([2, 3, 4, 5, 6], bad.Issues.Select(i => i.Line));

        var unbalanced = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), null, rows[..3], commit: true);
        Assert.Equal(0, Assert.Single(unbalanced.Issues).Line);

        var ok = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), "Số dư chuyển sang", rows, commit: true);
        Assert.True(ok.Committed);
        var v = await s.Db.Vouchers.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == ok.VoucherId);
        Assert.Equal((VoucherTypes.OpeningBalance, VoucherStatus.Draft, 4), (v.VoucherType, v.Status, v.Lines.Count));
        Assert.Equal(new DateOnly(2026, 1, 31), v.Lines.Single(l => l.PartnerId == h.Partner["KH01"]).DueDate);

        await s.Engine.SubmitAsync(h.Preparer, v.Id);
        await s.Engine.ApproveAsync(h.Approver, v.Id);
        await s.Engine.PostAsync(h.Approver, v.Id);
        var open = await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], new(2026, 12, 31));
        Assert.Equal(2_000_000m, Assert.Single(open).Open);

        // The same file a second time is refused (even on another date); a different, partial file only gets a warning.
        var again = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 2, 1), null, rows, commit: true);
        Assert.False(again.Committed);
        Assert.Contains(again.Issues, i => i.Field == "Trùng" && i.Message.Contains(ok.VoucherNumber!));
        var partial = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), null, rows[..3], commit: false);
        Assert.DoesNotContain(partial.Issues, i => i.Field == "Trùng");
        Assert.Contains(partial.Notes, n => n.Contains(ok.VoucherNumber!));

        // The OPB voucher of 01/01 is the opening balance of January, not a movement — trial balance and account ledger.
        var tb = await s.Ledger.TrialBalanceAsync(h.Auditor, new(2026, 1, 1), new(2026, 1, 31));
        var cash = tb.Rows.Single(r => r.AccountCode == "CASH");
        Assert.Equal((1_500_000m, 0m, 0m, 1_500_000m), (cash.OpeningDebit, cash.PeriodDebit, cash.PeriodCredit, cash.ClosingDebit));
        var book = await s.Ledger.AccountLedgerAsync(h.Auditor, h.Acc["CASH"], new(2026, 1, 1), new(2026, 1, 31));
        Assert.Equal((1_500_000m, 0), (book.OpeningBalance, book.Rows.Count));
    }

    [Fact]
    public async Task Vouchers_ImportAsDrafts_PerKey_AllOrNothing_ThenBulkSubmitAndPost_WithMakerChecker()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        VoucherImportRow[] rows =
        [
            new(2, "A0001", "2026-01-05", "PC1 80", "Chi phí văn phòng", "EXP", Debit: "100000"),
            new(3, "A0001", "2026-01-05", "PC1 80", null, "EQUITY", Credit: "100000"),
            new(4, "A0002", "10/01/2026", "BC1 3", "Khách trả tiền", "BANK", Debit: "250000"),
            new(5, "A0002", "10/01/2026", null, null, "AR", "KH01", Credit: "250000"),
        ];
        var bad = await s.CatalogImport.ImportVouchersAsync(h.Preparer,
            [rows[0], rows[1] with { Credit = "90000" }, rows[2], rows[3] with { PartnerCode = null }, new(6, "", "2026-01-05", AccountCode: "EXP", Debit: "1")], commit: true);
        Assert.False(bad.Committed);
        Assert.True(bad.Issues.Select(i => i.Line).SequenceEqual([2, 5, 6]), string.Join(" | ", bad.Issues.Select(i => $"{i.Line} {i.Field}: {i.Message}")));
        Assert.Empty(await s.Db.Vouchers.Where(v => v.SourceSystem == CatalogImportService.ImportSource).ToListAsync());

        var ok = await s.CatalogImport.ImportVouchersAsync(h.Preparer, rows, commit: true);
        Assert.True(ok.Committed);
        Assert.Equal((2, 350_000m), (ok.Documents!.Count, ok.TotalDebit));
        var drafts = await s.Db.Vouchers.AsNoTracking().Where(v => v.SourceSystem == CatalogImportService.ImportSource).OrderBy(v => v.PostingDate).ToListAsync();
        Assert.Equal(["[PC1 80] Chi phí văn phòng", "[BC1 3] Khách trả tiền"], drafts.Select(v => v.Description));
        Assert.Equal("A0001", (await s.CatalogImport.ImportVouchersAsync(h.Preparer, rows[..2], commit: false)).Issues.Single().Message.Split(' ')[2]);

        var submitted = await s.CatalogImport.SubmitImportedAsync(h.Preparer);
        Assert.True(submitted.Done == 2, string.Join(" | ", submitted.Failures));
        var selfApprove = await s.CatalogImport.ApproveAndPostImportedAsync(h.Preparer with { Permissions = new HashSet<string>(h.Preparer.Permissions) { "voucher.approve", "voucher.post" } });
        Assert.Equal((0, 2), (selfApprove.Done, selfApprove.Failures.Count));      // maker ≠ checker on every voucher
        var posted = await s.CatalogImport.ApproveAndPostImportedAsync(h.Approver);
        Assert.Equal((2, 0), (posted.Done, posted.Failures.Count));
        Assert.Equal(250_000m, await s.Ledger.AccountBalanceAsync(h.Auditor, h.Acc["BANK"], new(2026, 1, 31)));
    }

    [Fact]
    public async Task Items_ThenOpeningStock_OneDraftReceiptPerWarehouse_PostedAgainstTheClearingAccount()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var inv = (await s.Setup.CreateAccountAsync(h.Admin, "INV", "Hàng tồn kho (thử)")).Id;
        var clearing = (await s.Setup.CreateAccountAsync(h.Admin, "OPEN", "Trung gian số dư đầu kỳ (thử)")).Id;
        var w1 = await s.Stock.CreateWarehouseAsync(h.Admin, "K1", "Kho 1");
        var w2 = await s.Stock.CreateWarehouseAsync(h.Admin, "K2", "Kho 2");
        foreach (var w in new[] { w1, w2 })
            await s.Stock.SetCostingPolicyAsync(h.Admin, w.Id, Domain.Inventory.CostingMethod.Fifo, new(2026, 1, 1), "Quyết định thử");

        var items = await s.CatalogImport.ImportItemsAsync(h.Admin,
            [new(2, "vt01", "Dăm gỗ", "tấn", "INV"), new(3, "VT02", "Dầu", "lít", "INV"), new(4, "VT03", "Sai TK", "cái", "AR")], commit: true);
        Assert.False(items.Committed);
        Assert.Equal(4, Assert.Single(items.Issues).Line);                                   // AR is a receivable account
        items = await s.CatalogImport.ImportItemsAsync(h.Admin, [new(2, "vt01", "Dăm gỗ", "tấn", "INV"), new(3, "VT02", "Dầu", "lít", "INV")], commit: true);
        Assert.True(items.Committed);

        OpeningStockRow[] rows = [new(2, "K1", "VT01", "120,5", "60.250.000"), new(3, "K1", "VT02", "1000", "25000000"), new(4, "K2", "VT01", "10", "5000000")];
        var bad = await s.CatalogImport.ImportOpeningStockAsync(h.Preparer, new(2026, 1, 1), "OPEN", null,
            [.. rows, new(5, "K1", "VT01", "1", "1"), new(6, "K9", "VT02", "-1", "x")], commit: true);
        Assert.False(bad.Committed);
        Assert.Equal([5, 6, 6], bad.Issues.Select(i => i.Line));                       // duplicate; unknown warehouse + bad value
        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<DomainException>(() =>
            s.CatalogImport.ImportOpeningStockAsync(h.Auditor, new(2026, 1, 1), "OPEN", null, rows, false))).Code);

        var ok = await s.CatalogImport.ImportOpeningStockAsync(h.Preparer, new(2026, 1, 1), "OPEN", null, rows, commit: true);
        Assert.True(ok.Committed);
        Assert.Equal(90_250_000m, ok.TotalDebit);
        Assert.Equal(2, ok.Documents!.Count);
        var docs = await s.Db.StockDocuments.AsNoTracking().Include(d => d.Lines).Where(d => ok.Documents.Contains(d.Number)).ToListAsync();
        Assert.All(docs, d => Assert.Equal(Domain.Inventory.StockDocumentStatus.Draft, d.Status));
        foreach (var d in docs) await s.Stock.PostDocumentAsync(h.Approver, d.Id);

        Assert.Equal(90_250_000m, await s.Ledger.AccountBalanceAsync(h.Auditor, inv, new(2026, 1, 31)));
        Assert.Equal(-90_250_000m, await s.Ledger.AccountBalanceAsync(h.Auditor, clearing, new(2026, 1, 31)));
        var again = await s.CatalogImport.ImportOpeningStockAsync(h.Preparer, new(2026, 1, 1), "OPEN", null, rows, commit: false);
        Assert.Contains(again.Notes, n => n.Contains("K1") && n.Contains("phát sinh"));
    }

    [Fact]
    public async Task Assets_TakeTheirCostFromThePostedOpeningVoucher_AndContinueOverTheRemainingMonths()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var fa = (await s.Setup.CreateAccountAsync(h.Admin, "FA", "TSCĐ (thử)")).Id;
        var faAcc = (await s.Setup.CreateAccountAsync(h.Admin, "FA_ACC", "Hao mòn (thử)")).Id;
        await s.Setup.CreateAccountAsync(h.Admin, "TOOLS", "CCDC chờ phân bổ (thử)");
        OpeningImportRow[] opening =
        [
            new(2, "FA", Debit: "300000000"), new(3, "TOOLS", Debit: "30000000"),
            new(4, "FA_ACC", Credit: "100000000"), new(5, "EQUITY", Credit: "230000000"),
        ];
        var opb = await s.CatalogImport.ImportOpeningBalancesAsync(h.Preparer, new(2026, 1, 1), null, opening, commit: true);
        AssetImportRow[] rows =
        [
            new(2, "TS01", "Máy xúc", "TSCĐ", "FA", "FA_ACC", "EXP", "200000000", "80000000", "60", "36", "15/03/2023"),
            new(3, "TS02", "Xe tải", "tscd", "FA", "FA_ACC", "EXP", "100000000", "20000000", "60", "48", "2025-01-01", "theo ngày"),
            new(4, "CC01", "Dụng cụ", "CCDC", "TOOLS", null, "EXP", "30000000", "0", "12", "6", "2025-07-01"),
        ];
        var notPosted = await s.CatalogImport.ImportAssetsAsync(h.Preparer, opb.VoucherNumber!, rows, commit: false);
        Assert.Equal(0, Assert.Single(notPosted.Issues).Line);                              // the OPB voucher is still a draft
        await s.Engine.SubmitAsync(h.Preparer, opb.VoucherId!.Value);
        await s.Engine.ApproveAsync(h.Approver, opb.VoucherId.Value);
        await s.Engine.PostAsync(h.Approver, opb.VoucherId.Value);

        var bad = await s.CatalogImport.ImportAssetsAsync(h.Preparer, opb.VoucherNumber!,
            [.. rows, new(5, "TS03", "Thừa", "TSCĐ", "FA", "FA_ACC", "EXP", "1", "0", "12", "12", "2025-01-01"),
             new(6, "CC02", "Sai", "CCDC", "TOOLS", null, "EXP", "1", "1", "12", "6", "2025-01-01")], commit: true);
        Assert.False(bad.Committed);
        Assert.Equal([5, 6], bad.Issues.Select(i => i.Line));                               // FA fully allocated; tool with accumulated

        var ok = await s.CatalogImport.ImportAssetsAsync(h.Preparer, opb.VoucherNumber!, rows, commit: true);
        Assert.True(ok.Committed);
        Assert.Empty(ok.Notes);                                                              // FA_ACC 100m = 80m + 20m, nothing left unassigned
        var ts01 = await s.Db.LongTermAssets.AsNoTracking().SingleAsync(a => a.Code == "TS01");
        Assert.Equal((200_000_000m, 80_000_000m, 36, new DateOnly(2026, 1, 1), AssetStatus.Draft),
            (ts01.OriginalCost, ts01.OpeningAccumulated, ts01.RemainingLifeMonths!.Value, ts01.ScheduleStartDate, ts01.Status));
        await s.Assets.ActivateAsync(h.Approver, ts01.Id);                                   // cost sources check out against the ledger
        var again = await s.CatalogImport.ImportAssetsAsync(h.Preparer, opb.VoucherNumber!, [rows[0] with { Code = "TS09" }], commit: false);
        Assert.Contains("không còn đủ", Assert.Single(again.Issues).Message);
    }

    /// <summary>The TT 99/2025 chart extracted from the original text (legal/TT99-2025) imports cleanly with the suggested attributes.</summary>
    [Fact]
    public async Task Tt99ChartOfAccounts_ImportsCleanly_WithItsHierarchy()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "legal", "TT99-2025"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "legal", "TT99-2025", "nhap-he-thong-tk-de-xuat.csv"));
        var rows = lines.Skip(1).Select((l, i) => l.Split(';')).Select((c, i) => new AccountImportRow(i + 2, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8])).ToList();

        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var r = await s.CatalogImport.ImportAccountsAsync(h.Admin, rows, commit: true);
        Assert.Empty(r.Issues);
        Assert.True(r.Committed);
        Assert.Equal(184, r.Valid);

        var accounts = await s.Db.Accounts.AsNoTracking().ToListAsync();
        Account A(string code) => accounts.Single(a => a.Code == code);
        Assert.Equal(71, rows.Count(x => x.ParentCode == ""));
        Assert.Equal((A("21512").Id, true), (A("215121").ParentId!.Value, A("215121").IsPostable));
        Assert.Equal((A("3331").Id, false), (A("33311").ParentId!.Value, A("3331").IsPostable));
        Assert.Equal((CashKind.Cash, NegativeBalancePolicy.Block), (A("111").CashKind, A("111").NegativeBalancePolicy));
        Assert.Equal((OpenItemNature.Payable, true, BalanceDisplay.GrossByPartner), (A("331").OpenItemNature, A("331").RequiresPartner, A("331").BalanceDisplay));
        Assert.False(A("911").RequiresPartner);
    }

    [Theory]
    [InlineData("1234567", "1234567")]
    [InlineData("1.234.567", "1234567")]
    [InlineData("1,234,567.89", "1234567.89")]
    [InlineData("1.234.567,89", "1234567.89")]
    [InlineData("(1.000.000)", "-1000000")]
    [InlineData("12.5", "12.5")]
    [InlineData("1,5", "1.5")]
    [InlineData("1000.12", "1000.12")]
    public void Amounts_AcceptOnlyUnambiguousSeparators(string text, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), CatalogImportService.ParseAmount(text, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("1.000")]
    [InlineData("1,500")]
    [InlineData("1.234,5.6")]
    [InlineData("abc")]
    public void Amounts_RefuseAmbiguousOrInvalidText(string text)
    {
        CatalogImportService.ParseAmount(text, out var error);
        Assert.NotNull(error);
    }
}

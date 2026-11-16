using Hak.Application.Accounting;
using Hak.Application.Fx;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Fx;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

/// <summary>Foreign-currency balances per (account, partner, currency) and period-end revaluation.</summary>
public class FxTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    private static async Task<TestHost> HostAsync()
    {
        var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        h.Acc["BANKUSD"] = (await s.Setup.CreateAccountAsync(h.Admin, "BANKUSD", "Tiền gửi USD (mã thử)", cashKind: CashKind.Bank)).Id;
        h.Acc["LOANUSD"] = (await s.Setup.CreateAccountAsync(h.Admin, "LOANUSD", "Vay USD (mã thử)")).Id;
        h.Acc["FXDIFF"] = (await s.Setup.CreateAccountAsync(h.Admin, "FXDIFF", "Chênh lệch đánh giá lại (mã thử)")).Id;
        await s.Rules.SetRoleMappingAsync(h.Admin, "FX_REVALUATION", "FXDIFF");
        return h;
    }

    private static VoucherLineInput Usd(Guid account, Side side, decimal usd, decimal rate, Guid? partner = null)
        => new(account, side, MoneyMath.Convert(usd, rate, 0), partner, "USD", usd, rate);

    private static Task<Voucher> PostAsync(TestHost h, Scope s, DateOnly d, string text, params VoucherLineInput[] lines)
        => h.PostedAsync(s, new VoucherInput(VoucherTypes.General, d, d, text, lines));

    private static async Task<decimal> BalanceAsync(Scope s, Guid account)
    {
        var lines = await s.Db.JournalLines.AsNoTracking().Where(l => l.AccountId == account).Select(l => new { l.Side, l.Amount }).ToListAsync();
        return lines.Sum(l => l.Side == Side.Debit ? l.Amount : -l.Amount);
    }

    private static FxRate UsdRate(decimal debit, decimal credit) => new("USD", debit, credit);

    [Fact]
    public async Task Revaluation_AssetsAtDebitRate_LiabilitiesAtCreditRate_DifferenceToMappedRole()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        await PostAsync(h, s, new(2026, 3, 5), "Nhận USD", Usd(h.Acc["BANKUSD"], Side.Debit, 1_000, 25_000), new(h.Acc["EQUITY"], Side.Credit, 25_000_000));
        await PostAsync(h, s, new(2026, 3, 20), "Nhận USD", Usd(h.Acc["BANKUSD"], Side.Debit, 500, 25_400), new(h.Acc["EQUITY"], Side.Credit, 12_700_000));
        await PostAsync(h, s, new(2026, 3, 10), "Vay USD", new(h.Acc["BANK"], Side.Debit, 50_200_000), Usd(h.Acc["LOANUSD"], Side.Credit, 2_000, 25_100));

        var before = await s.Fx.BalancesAsync(h.Auditor, new(2026, 3, 31));
        var bank = before.Single(b => b.AccountId == h.Acc["BANKUSD"]);
        Assert.Equal(1_500m, bank.ForeignBalance);
        Assert.Equal(37_700_000m, bank.BookBalance);
        Assert.Equal(25_133.333333m, bank.BookRate);

        var run = await s.Fx.RevalueAsync(h.Approver, new(2026, 3, 31), [UsdRate(25_300, 25_500)], "Vietcombank 31/03/2026 16:00");
        Assert.Equal(37_950_000m, await BalanceAsync(s, h.Acc["BANKUSD"]));      // +250,000
        Assert.Equal(-51_000_000m, await BalanceAsync(s, h.Acc["LOANUSD"]));     // −800,000
        Assert.Equal(550_000m, await BalanceAsync(s, h.Acc["FXDIFF"]));          // net loss 550,000 on the mapped account
        var after = await s.Fx.BalancesAsync(h.Auditor, new(2026, 3, 31));
        Assert.Equal(25_300m, after.Single(b => b.AccountId == h.Acc["BANKUSD"]).BookRate);
        Assert.Equal(1_500m, after.Single(b => b.AccountId == h.Acc["BANKUSD"]).ForeignBalance);

        // Run vouchers belong to the FX module; a second run must be later than the last one.
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Engine.ReverseAsync(h.Approver, run.VoucherId!.Value, new(2026, 3, 31), "x")));
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Fx.RevalueAsync(h.Approver, new(2026, 3, 31), [UsdRate(1, 1)], "x")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Fx.RevalueAsync(h.Approver, new(2026, 4, 30), [], "x")));
    }

    [Fact]
    public async Task ZeroForeignBalance_ResidueIsCleared_AndRunsReverseLatestFirst()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        await PostAsync(h, s, new(2026, 3, 5), "Nhận USD", Usd(h.Acc["BANKUSD"], Side.Debit, 1_000, 25_000), new(h.Acc["EQUITY"], Side.Credit, 25_000_000));
        var march = await s.Fx.RevalueAsync(h.Approver, new(2026, 3, 31), [UsdRate(25_300, 25_300)], "NH 31/03");
        Assert.Equal(25_300_000m, await BalanceAsync(s, h.Acc["BANKUSD"]));

        // All USD spent, but booked at the transaction rate instead of the book rate: 300,000 VND is left on a zero USD balance.
        await PostAsync(h, s, new(2026, 4, 10), "Chi USD", new(h.Acc["EXP"], Side.Debit, 25_000_000), Usd(h.Acc["BANKUSD"], Side.Credit, 1_000, 25_000));
        var residue = (await s.Fx.BalancesAsync(h.Auditor, new(2026, 4, 30))).Single();
        Assert.Equal(0m, residue.ForeignBalance);
        Assert.Equal(300_000m, residue.BookBalance);
        Assert.Null(residue.BookRate);

        var april = await s.Fx.RevalueAsync(h.Approver, new(2026, 4, 30), [], "Không còn số dư ngoại tệ");
        Assert.Equal(0m, await BalanceAsync(s, h.Acc["BANKUSD"]));
        Assert.Equal(-300_000m + 300_000m, await BalanceAsync(s, h.Acc["FXDIFF"]));  // −300,000 gain in March, +300,000 loss in April

        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Fx.ReverseAsync(h.Approver, march.Id, "x")));
        await s.Fx.ReverseAsync(h.Approver, april.Id, "Tỷ giá sai");
        Assert.Equal(300_000m, await BalanceAsync(s, h.Acc["BANKUSD"]));
        await s.Fx.ReverseAsync(h.Approver, march.Id, "Tỷ giá sai");
        Assert.Equal(0m, await BalanceAsync(s, h.Acc["FXDIFF"]));
        Assert.All(await s.Fx.RunsAsync(h.Auditor), r => Assert.Equal(FxRunStatus.Reversed, r.Status));
    }

    private static async Task<TestHost> HostWithRealisedAsync()
    {
        var h = await HostAsync();
        using var s = h.NewScope();
        h.Acc["FXGAIN"] = (await s.Setup.CreateAccountAsync(h.Admin, "FXGAIN", "Lãi tỷ giá đã thực hiện (mã thử)")).Id;
        h.Acc["FXLOSS"] = (await s.Setup.CreateAccountAsync(h.Admin, "FXLOSS", "Lỗ tỷ giá đã thực hiện (mã thử)")).Id;
        await s.Rules.SetRoleMappingAsync(h.Admin, "FX_REALIZED_GAIN", "FXGAIN");
        await s.Rules.SetRoleMappingAsync(h.Admin, "FX_REALIZED_LOSS", "FXLOSS");
        return h;
    }

    private static async Task<Hak.Application.Settlement.OpenItem> InvoiceItemAsync(TestHost h, Scope s, DateOnly asOf)
        => Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], asOf));

    /// <summary>Draft (preparer) → submit → approve and post (approver).</summary>
    private static async Task PostDraftAsync(TestHost h, Scope s, Voucher draft)
    {
        await s.Engine.SubmitAsync(h.Preparer, draft.Id);
        await s.Engine.ApproveAsync(h.Approver, draft.Id);
        await s.Engine.PostAsync(h.Approver, draft.Id);
    }

    [Fact]
    public async Task ForeignReceivable_PartialPayment_Revaluation_FinalPayment_RealisedAndUnrealisedDifferences()
    {
        using var h = await HostWithRealisedAsync();
        using var s = h.NewScope();
        await PostAsync(h, s, new(2026, 3, 5), "Bán hàng USD", Usd(h.Acc["AR"], Side.Debit, 1_000, 25_000, h.Partner["KH01"]) with { DueDate = new DateOnly(2026, 5, 5) },
            new(h.Acc["REV"], Side.Credit, 25_000_000));
        var invoice = await InvoiceItemAsync(h, s, new(2026, 3, 31));

        // 400 USD received at 25,200: AR is credited at the invoice's book rate, the 80,000 gap is a realised gain.
        var input = new FxPaymentInput(new(2026, 3, 20), h.Acc["BANKUSD"], 25_200, "Thu tiền KH01 400 USD", [new FxSettleItem(invoice.JournalLineId, 400)]);
        var preview = await s.FxPayments.PreviewAsync(h.Preparer, input);
        Assert.Equal((10_080_000m, 10_000_000m, 80_000m), (preview.CashAmount, preview.BookAmount, preview.Difference));
        await PostDraftAsync(h, s, await s.FxPayments.CreateDraftAsync(h.Preparer, input));
        Assert.Equal(-80_000m, await BalanceAsync(s, h.Acc["FXGAIN"]));
        var partly = await InvoiceItemAsync(h, s, new(2026, 3, 31));
        Assert.Equal((600m, 15_000_000m), (partly.OpenForeign!.Value, partly.Open));
        // The partner ledger (used by the balance confirmation) carries the foreign amounts and the foreign closing balance.
        var book = await s.Ledger.AccountLedgerAsync(h.Auditor, h.Acc["AR"], new(2026, 3, 1), new(2026, 3, 25), h.Partner["KH01"]);
        Assert.Equal(("USD", 600m), (Assert.Single(book.ForeignClosing).Currency, book.ForeignClosing[0].Closing));
        Assert.Equal([1_000m, 400m], book.Rows.Select(r => r.ForeignAmount!.Value).ToArray());

        // Period end at 25,500: the invoice is closed at book and re-opened at the closing rate (+300,000), the USD cash +120,000.
        await s.Fx.RevalueAsync(h.Approver, new(2026, 3, 31), [UsdRate(25_500, 25_500)], "NH 31/03");
        Assert.Equal(-420_000m, await BalanceAsync(s, h.Acc["FXDIFF"]));
        var revalued = await InvoiceItemAsync(h, s, new(2026, 3, 31));
        Assert.NotEqual(invoice.JournalLineId, revalued.JournalLineId);
        Assert.Equal((600m, 15_300_000m, new DateOnly(2026, 5, 5)), (revalued.OpenForeign!.Value, revalued.Open, revalued.DueDate));

        // The remaining 600 USD at 25,100: book 15,300,000, cash 15,060,000 → realised loss 240,000; the receivable is closed.
        var last = new FxPaymentInput(new(2026, 4, 15), h.Acc["BANKUSD"], 25_100, "Thu nốt KH01", [new FxSettleItem(revalued.JournalLineId, 600)]);
        Assert.Equal(-240_000m, (await s.FxPayments.PreviewAsync(h.Preparer, last)).Difference);
        await PostDraftAsync(h, s, await s.FxPayments.CreateDraftAsync(h.Preparer, last));
        Assert.Equal(240_000m, await BalanceAsync(s, h.Acc["FXLOSS"]));
        Assert.Equal(0m, await BalanceAsync(s, h.Acc["AR"]));
        Assert.Equal(0m, Assert.Single((await s.Ledger.AccountLedgerAsync(h.Auditor, h.Acc["AR"], new(2026, 3, 1), new(2026, 4, 30), h.Partner["KH01"])).ForeignClosing).Closing);
        Assert.Empty(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, new(2026, 4, 30)));
        Assert.True((await s.Settlement.ReconcileAsync(h.Auditor, new(2026, 4, 30))).IsReconciled);

        // The revaluation cannot be undone underneath a payment of the re-opened item.
        var run = Assert.Single(await s.Fx.RunsAsync(h.Auditor));
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Fx.ReverseAsync(h.Approver, run.Id, "x")));
    }

    [Fact]
    public async Task SupplierPayment_FromForeignCash_UsesMovingAverageBookRate()
    {
        using var h = await HostWithRealisedAsync();
        using var s = h.NewScope();
        // USD in: 1,000 at 25,000 and 500 at 25,600 → moving average 25,200 (37,800,000 / 1,500).
        await PostAsync(h, s, new(2026, 3, 2), "Nhận USD", Usd(h.Acc["BANKUSD"], Side.Debit, 1_000, 25_000), new(h.Acc["EQUITY"], Side.Credit, 25_000_000));
        await PostAsync(h, s, new(2026, 3, 3), "Nhận USD", Usd(h.Acc["BANKUSD"], Side.Debit, 500, 25_600), new(h.Acc["EQUITY"], Side.Credit, 12_800_000));
        await PostAsync(h, s, new(2026, 3, 5), "Mua dịch vụ USD", new(h.Acc["EXP"], Side.Debit, 15_060_000), Usd(h.Acc["AP"], Side.Credit, 600, 25_100, h.Partner["NCC01"]));
        var bill = Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AP"], h.Partner["NCC01"], new(2026, 3, 31)));

        // Paying 600 USD: the rate typed (25,500) is not used for an outflow; cash is credited 600 × 25,200 = 15,120,000.
        var pay = new FxPaymentInput(new(2026, 3, 10), h.Acc["BANKUSD"], 25_500, "Trả NCC01 600 USD", [new FxSettleItem(bill.JournalLineId, 600)]);
        var preview = await s.FxPayments.PreviewAsync(h.Preparer, pay);
        Assert.Equal((15_120_000m, 15_060_000m, -60_000m), (preview.CashAmount, preview.BookAmount, preview.Difference));
        Assert.Equal(25_200m, preview.Lines[0].ExchangeRate);
        await PostDraftAsync(h, s, await s.FxPayments.CreateDraftAsync(h.Preparer, pay));
        Assert.Equal(60_000m, await BalanceAsync(s, h.Acc["FXLOSS"]));
        Assert.Equal(22_680_000m, await BalanceAsync(s, h.Acc["BANKUSD"]));     // 900 USD still at 25,200

        // More than the USD balance cannot be paid out.
        await PostAsync(h, s, new(2026, 3, 12), "Mua dịch vụ USD", new(h.Acc["EXP"], Side.Debit, 25_100_000), Usd(h.Acc["AP"], Side.Credit, 1_000, 25_100, h.Partner["NCC01"]));
        var big = Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AP"], h.Partner["NCC01"], new(2026, 3, 31)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.FxPayments.PreviewAsync(h.Preparer,
            new FxPaymentInput(new(2026, 3, 15), h.Acc["BANKUSD"], 25_200, "Trả", [new FxSettleItem(big.JournalLineId, 1_000)]))));
    }

    [Fact]
    public async Task ForeignItems_CannotBeSettledInFunctionalCurrency_OrOffBookValue_OrAutoMatched()
    {
        using var h = await HostWithRealisedAsync();
        using var s = h.NewScope();
        await PostAsync(h, s, new(2026, 3, 5), "Bán hàng USD", Usd(h.Acc["AR"], Side.Debit, 1_000, 25_000, h.Partner["KH01"]), new(h.Acc["REV"], Side.Credit, 25_000_000));
        var invoice = await InvoiceItemAsync(h, s, new(2026, 3, 31));

        async Task<string> TryPay(VoucherLineInput settleLine, VoucherLineInput other) => await CodeOf(async () =>
        {
            var v = await s.Engine.CreateDraftAsync(h.Preparer, new VoucherInput(VoucherTypes.General, new(2026, 3, 20), new(2026, 3, 20), "Thu tiền", [other, settleLine]));
            await PostDraftAsync(h, s, v);
        });
        // VND receipt against a USD invoice.
        Assert.Equal(ErrorCodes.SettlementMismatch, await TryPay(
            new VoucherLineInput(h.Acc["AR"], Side.Credit, 25_000_000, h.Partner["KH01"], SettleJournalLineId: invoice.JournalLineId),
            new VoucherLineInput(h.Acc["BANK"], Side.Debit, 25_000_000)));
        // USD receipt booked on AR at the payment rate instead of the invoice's book rate.
        Assert.Equal(ErrorCodes.FxBookValue, await TryPay(
            Usd(h.Acc["AR"], Side.Credit, 1_000, 25_300, h.Partner["KH01"]) with { SettleJournalLineId = invoice.JournalLineId },
            Usd(h.Acc["BANKUSD"], Side.Debit, 1_000, 25_300)));

        // An advance received in USD at another rate: manual matching needs a voucher for the difference; auto-matching skips foreign items.
        await PostAsync(h, s, new(2026, 3, 25), "KH ứng trước USD", Usd(h.Acc["BANKUSD"], Side.Debit, 1_000, 25_300), Usd(h.Acc["AR"], Side.Credit, 1_000, 25_300, h.Partner["KH01"]));
        var advance = (await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], new(2026, 3, 31))).Single(i => i.Side == Side.Credit);
        Assert.Equal(ErrorCodes.FxBookValue, await CodeOf(() => s.Settlement.AllocateAsync(h.Preparer, invoice.JournalLineId, advance.JournalLineId, 25_000_000, foreignAmount: 1_000)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Settlement.AllocateAsync(h.Preparer, invoice.JournalLineId, advance.JournalLineId, 25_000_000)));
        Assert.Empty(await s.Settlement.AutoAllocateAsync(h.Preparer, h.Acc["AR"], h.Partner["KH01"], new(2026, 3, 31)));
    }

    [Fact]
    public async Task CurrencyTagOnlyLines_AreRevaluationOnly()
    {
        using var h = await HostAsync();
        using var s = h.NewScope();
        // A currency tag without foreign amount and rate is refused outside revaluation vouchers.
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(async () =>
        {
            var v = await s.Engine.CreateDraftAsync(h.Preparer, new VoucherInput(VoucherTypes.General, new(2026, 3, 31), new(2026, 3, 31), "x",
                [new VoucherLineInput(h.Acc["BANKUSD"], Side.Debit, 1, ForeignCurrency: "USD"), new VoucherLineInput(h.Acc["EXP"], Side.Credit, 1)]));
            await s.Engine.SubmitAsync(h.Preparer, v.Id);
        }));
    }

    [Fact]
    public async Task RevaluationRole_MustBeMapped()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        Assert.Equal(ErrorCodes.RuleRoleUnmapped, await CodeOf(() => s.Fx.RevalueAsync(h.Approver, new(2026, 3, 31), [], "NH")));
    }
}

using Hak.Application.Accounting;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using static Hak.Integration.Tests.Phase2Fixture;

namespace Hak.Integration.Tests;

public class SettlementTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;
    private static readonly DateOnly Mar10 = new(2026, 3, 10);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private static async Task<(TestHost, Scope)> SetupAsync()
    {
        var h = await TestHost.CreateAsync();
        var s = h.NewScope();
        await ConfigureAsync(h, s);
        return (h, s);
    }

    private static VoucherInput Invoice(TestHost h, decimal amount, DateOnly date, DateOnly? due = null, string partner = "KH01")
        => new(VoucherTypes.General, date, date, "Hóa đơn bán hàng",
        [
            new VoucherLineInput(h.Acc["AR"], Side.Debit, amount, h.Partner[partner], DueDate: due),
            new VoucherLineInput(h.Acc["REV"], Side.Credit, amount),
        ]);

    [Fact]
    public async Task PartialPayment_ThenOverpaymentRefused_ThenRemainderPlusAdvance()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        var inv = await h.PostedAsync(s, Invoice(h, 10_000_000, new(2026, 3, 1), due: new(2026, 3, 15)));
        var invLine = LineOf(s, inv.Id, h.Acc["AR"]);

        await PostDocAsync(h, s, Receipt(h, 6_000_000, Mar10, invLine));
        var open = await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], Mar31);
        Assert.Equal(4_000_000m, Assert.Single(open).Open);

        var over = await s.Documents.CreateAsync(h.Preparer, Receipt(h, 5_000_000, Mar10, invLine));
        Assert.Equal(ErrorCodes.SettlementExceedsOpen, await CodeOf(() => s.Engine.SubmitAsync(h.Preparer, over.Id)));

        // Remainder settles the invoice; the extra 1,000,000 is a separate, unallocated advance line.
        var doc = Receipt(h, 4_000_000, new(2026, 3, 20), invLine) with
        {
            Lines = [new(4_000_000, SettleJournalLineId: invLine), new(1_000_000, Description: "Ứng trước")],
        };
        await PostDocAsync(h, s, doc);

        var items = await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], Mar31);
        var advance = Assert.Single(items);
        Assert.Equal((Side.Credit, 1_000_000m), (advance.Side, advance.Open));

        var aging = await s.Settlement.AgingAsync(h.Auditor, h.Acc["AR"], Mar31);
        var row = Assert.Single(aging.Rows);
        Assert.Equal((0m, 1_000_000m, -1_000_000m), (row.OpenObligations, row.UnappliedCredits, row.Net));
        Assert.Equal(-1_000_000m, await s.Ledger.AccountBalanceAsync(h.Auditor, h.Acc["AR"], Mar31, h.Partner["KH01"]));
        Assert.True((await s.Settlement.ReconcileAsync(h.Auditor, Mar31)).IsReconciled);
    }

    [Fact]
    public async Task OnePayment_SettlesSeveralInvoices()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        var a = LineOf(s, (await h.PostedAsync(s, Invoice(h, 3_000_000, new(2026, 3, 1)))).Id, h.Acc["AR"]);
        var b = LineOf(s, (await h.PostedAsync(s, Invoice(h, 2_000_000, new(2026, 3, 2)))).Id, h.Acc["AR"]);
        await PostDocAsync(h, s, Receipt(h, 0, Mar10) with { Lines = [new(3_000_000, SettleJournalLineId: a), new(2_000_000, SettleJournalLineId: b)] });
        Assert.Empty(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31));
        Assert.Equal(2, await s.Db.SettlementAllocations.CountAsync());
    }

    [Fact]
    public async Task ReversingPayment_ReopensInvoice_AndKeepsSubledgerReconciled()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        var invLine = LineOf(s, (await h.PostedAsync(s, Invoice(h, 5_000_000, new(2026, 3, 1)))).Id, h.Acc["AR"]);
        var pay = await PostDocAsync(h, s, Receipt(h, 5_000_000, Mar10, invLine));
        Assert.Empty(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31));

        await s.Engine.ReverseAsync(h.Approver, pay.Id, new(2026, 3, 12), "Séc bị trả lại");
        var open = Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31));
        Assert.Equal((invLine, 5_000_000m), (open.JournalLineId, open.Open));
        Assert.Equal(1, await s.Db.SettlementAllocations.CountAsync(x => x.VoidedAt != null));
        Assert.True((await s.Settlement.ReconcileAsync(h.Auditor, Mar31)).IsReconciled);
    }

    [Fact]
    public async Task ReversingInvoice_LeavesPaymentAsAdvance()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        var inv = await h.PostedAsync(s, Invoice(h, 5_000_000, new(2026, 3, 1)));
        await PostDocAsync(h, s, Receipt(h, 5_000_000, Mar10, LineOf(s, inv.Id, h.Acc["AR"])));
        await s.Engine.ReverseAsync(h.Approver, inv.Id, new(2026, 3, 15), "Hóa đơn lập sai");
        var open = Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31));
        Assert.Equal((Side.Credit, 5_000_000m, VoucherTypes.CashReceipt), (open.Side, open.Open, open.VoucherType));
        Assert.True((await s.Settlement.ReconcileAsync(h.Auditor, Mar31)).IsReconciled);
    }

    [Fact]
    public async Task AutoFifo_ManualVoid_AndGuards()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        await h.PostedAsync(s, Invoice(h, 1_000_000, new(2026, 2, 10)));
        await h.PostedAsync(s, Invoice(h, 2_000_000, new(2026, 3, 1)));
        await PostDocAsync(h, s, Receipt(h, 2_500_000, Mar10)); // unallocated receipt

        var created = await s.Settlement.AutoAllocateAsync(h.Approver, h.Acc["AR"], h.Partner["KH01"], Mar31);
        Assert.Equal([1_000_000m, 1_500_000m], created.Select(a => a.Amount).ToArray());
        var remaining = Assert.Single(await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31));
        Assert.Equal((new DateOnly(2026, 3, 1), 500_000m), (remaining.PostingDate, remaining.Open));

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Settlement.VoidAsync(h.Auditor, created[0].Id, "x")));
        Assert.Equal(ErrorCodes.ReasonRequired, await CodeOf(() => s.Settlement.VoidAsync(h.Approver, created[0].Id, "")));
        await s.Settlement.VoidAsync(h.Approver, created[0].Id, "Ghép nhầm");
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => s.Settlement.VoidAsync(h.Approver, created[0].Id, "lần 2")));
        Assert.Equal(2, (await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], null, Mar31)).Count(i => i.Side == Side.Debit));

        // History for the manual-matching screen: voided rows stay listed with their reason.
        var history = await s.Settlement.AllocationsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"]);
        Assert.Equal(2, history.Count);
        var voided = Assert.Single(history, a => !a.IsActive);
        Assert.Equal((created[0].Id, 1_000_000m, "Ghép nhầm", Hak.Domain.Settlement.SettlementOrigins.Auto), (voided.Id, voided.Amount, voided.VoidReason, voided.Origin));
        Assert.All(history, a => Assert.NotEqual("?", a.DebitVoucherNumber));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync("DELETE FROM settlement_allocations"));
        Assert.Contains("LEDGER_IMMUTABLE", ex.Message);
        var ex2 = await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync("UPDATE settlement_allocations SET amount = 1"));
        Assert.Contains("LEDGER_IMMUTABLE", ex2.Message);
        Assert.True((await s.Settlement.ReconcileAsync(h.Auditor, Mar31)).IsReconciled);
    }

    [Fact]
    public async Task Allocation_MustMatchAccountPartnerAndSides_AndNotExceedOpen()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        var kh1 = LineOf(s, (await h.PostedAsync(s, Invoice(h, 1_000, new(2026, 3, 1)))).Id, h.Acc["AR"]);
        var kh1b = LineOf(s, (await h.PostedAsync(s, Invoice(h, 1_000, new(2026, 3, 1)))).Id, h.Acc["AR"]);
        var kh2Pay = LineOf(s, (await PostDocAsync(h, s, Receipt(h, 1_000, Mar10, partner: "KH02"))).Id, h.Acc["AR"]);
        var kh1Pay = LineOf(s, (await PostDocAsync(h, s, Receipt(h, 500, Mar10))).Id, h.Acc["AR"]);

        Assert.Equal(ErrorCodes.SettlementMismatch, await CodeOf(() => s.Settlement.AllocateAsync(h.Approver, kh1, kh2Pay, 100)));
        Assert.Equal(ErrorCodes.SettlementMismatch, await CodeOf(() => s.Settlement.AllocateAsync(h.Approver, kh1, kh1b, 100)));
        Assert.Equal(ErrorCodes.SettlementExceedsOpen, await CodeOf(() => s.Settlement.AllocateAsync(h.Approver, kh1, kh1Pay, 600)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Settlement.AllocateAsync(h.Auditor, kh1, kh1Pay, 100)));
        await s.Settlement.AllocateAsync(h.Approver, kh1Pay, kh1, 500); // argument order does not matter
        Assert.Equal(500m, (await s.Settlement.OpenItemsAsync(h.Auditor, h.Acc["AR"], h.Partner["KH01"], Mar31)).Single(i => i.JournalLineId == kh1).Open);
    }

    [Fact]
    public async Task Aging_BucketsByDaysPastDue()
    {
        var (h, s) = await SetupAsync();
        using var _h = h; using var _s = s;
        await h.PostedAsync(s, Invoice(h, 100, new(2026, 1, 5), due: new(2026, 1, 5)));   // 85 days overdue at 31/03
        await h.PostedAsync(s, Invoice(h, 200, new(2026, 3, 1), due: new(2026, 3, 16)));  // 15 days
        await h.PostedAsync(s, Invoice(h, 400, new(2026, 3, 20), due: new(2026, 4, 19))); // not due
        var r = Assert.Single((await s.Settlement.AgingAsync(h.Auditor, h.Acc["AR"], Mar31)).Rows);
        Assert.Equal(400m, r.NotDue);
        Assert.Equal([200m, 0m, 100m, 0m], r.Buckets.ToArray());
        Assert.Equal(700m, r.OpenObligations);
    }
}

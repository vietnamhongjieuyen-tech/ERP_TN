using Hak.Application.Accounting;
using Hak.Application.Overview;
using Hak.Application.Security;
using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hak.Integration.Tests;

public class OverviewTests
{
    private static async Task<string> CodeOf(Func<Task> f) => (await Assert.ThrowsAsync<DomainException>(f)).Code;

    [Fact]
    public async Task Dashboard_CountsWaitingWork_CashAndOverdue_AndHidesFiguresWithoutReportPermission()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var overview = new OverviewService(s.Db, s.Ledger, s.Settlement, new IntegrityService(s.Db));

        await h.PostedAsync(s, h.Sale(5_000_000, new(2026, 3, 10)));                                   // receivable, due 10/03
        await h.PostedAsync(s, new VoucherInput(VoucherTypes.General, new(2026, 3, 12), new(2026, 3, 12), "Góp vốn",
            [new VoucherLineInput(h.Acc["CASH"], Side.Debit, 2_000_000), new VoucherLineInput(h.Acc["EQUITY"], Side.Credit, 2_000_000)]));
        await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(100, new(2026, 3, 15)));                    // stays a draft
        var waiting = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(200, new(2026, 3, 16)));
        await s.Engine.SubmitAsync(h.Preparer, waiting.Id);                                            // waits for approval

        var d = await overview.DashboardAsync(h.Preparer, new(2026, 4, 20));
        Assert.Equal((1, 1, 1, 0), (d.MyDrafts, d.Drafts, d.AwaitingApproval, d.AwaitingPosting));
        Assert.Equal(2_000_000m, d.Cash!.Single(c => c.AccountCode == "CASH").Balance);
        Assert.Equal(0m, d.Cash!.Single(c => c.AccountCode == "BANK").Balance);
        Assert.Equal((5_000_000m, 0m), (d.OverdueReceivables, d.OverduePayables));                    // drafts are not receivables yet
        Assert.Equal((4, "Open"), (d.CurrentPeriod!.PeriodNo, d.CurrentPeriod.Status));
        Assert.Equal(1, d.OldestOpenPeriod!.PeriodNo);

        Assert.Equal(0, (await overview.DashboardAsync(h.Approver, new(2026, 3, 9))).MyDrafts);       // someone else's drafts
        Assert.Equal(0m, (await overview.DashboardAsync(h.Approver, new(2026, 3, 9))).OverdueReceivables);

        var viewOnly = new SessionContext(h.PreparerId, h.CompanyId, new HashSet<string> { Permissions.VoucherView });
        var limited = await overview.DashboardAsync(viewOnly, new(2026, 4, 20));
        Assert.Null(limited.Cash);
        Assert.Null(limited.OverdueReceivables);
        Assert.Equal(1, limited.AwaitingApproval);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => overview.DashboardAsync(new SessionContext(h.PreparerId, h.CompanyId, new HashSet<string>()), new(2026, 4, 20))));
    }

    [Fact]
    public async Task ClosingChecklist_ListsWhatBlocksThePeriod_AndWhatDoesNotApply()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var overview = new OverviewService(s.Db, s.Ledger, s.Settlement, new IntegrityService(s.Db));
        var march = await s.Db.AccountingPeriods.AsNoTracking().SingleAsync(p => p.CompanyId == h.CompanyId && p.PeriodNo == 3);
        ChecklistItem Item(ClosingChecklist c, string key) => c.Items.Single(i => i.Key == key);

        await h.PostedAsync(s, h.Sale(5_000_000, new(2026, 3, 10)));
        var clean = await overview.ClosingChecklistAsync(h.Auditor, march.Id);
        Assert.True(clean.ReadyToLock);
        Assert.Equal(CheckStatus.Done, Item(clean, "vouchers").Status);
        Assert.Equal(CheckStatus.Done, Item(clean, "trial_balance").Status);
        Assert.Equal(CheckStatus.Done, Item(clean, "subledger").Status);
        Assert.Equal(CheckStatus.Optional, Item(clean, "lock").Status);
        Assert.Equal(CheckStatus.Optional, Item(clean, "bank").Status);                               // bank accounts exist, no statement imported
        foreach (var key in new[] { "stock_documents", "costing", "production", "depreciation", "loan_interest", "payroll", "fx" })
            Assert.Equal(CheckStatus.NotApplicable, Item(clean, key).Status);
        Assert.DoesNotContain(clean.Items, i => i.Key == "advances");                                // no advance account mapped

        // Once an account is mapped to EMPLOYEE_ADVANCE, open balances per person are reported (information only, does not block).
        s.Db.AccountRoleMappings.Add(new() { Id = Guid.NewGuid(), CompanyId = h.CompanyId, RoleCode = "EMPLOYEE_ADVANCE", AccountId = h.Acc["AR"], CreatedAt = DateTime.UtcNow, CreatedBy = h.AdminId });
        await s.Db.SaveChangesAsync();
        var advances = Item(await overview.ClosingChecklistAsync(h.Auditor, march.Id), "advances");
        Assert.Equal(CheckStatus.Optional, advances.Status);
        Assert.Contains("1 người", advances.Detail);
        s.Db.AccountRoleMappings.RemoveRange(s.Db.AccountRoleMappings.Where(m => m.RoleCode == "EMPLOYEE_ADVANCE"));
        await s.Db.SaveChangesAsync();

        // A draft dated in the period blocks the close; one dated in April does not.
        await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(100, new(2026, 3, 31)));
        await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(100, new(2026, 4, 1)));
        var blocked = await overview.ClosingChecklistAsync(h.Auditor, march.Id);
        Assert.False(blocked.ReadyToLock);
        Assert.Equal(CheckStatus.Todo, Item(blocked, "vouchers").Status);
        Assert.Contains("1 chứng từ", Item(blocked, "vouchers").Detail);

        await s.Setup.LockPeriodAsync(h.Approver, (await s.Db.AccountingPeriods.AsNoTracking().SingleAsync(p => p.CompanyId == h.CompanyId && p.PeriodNo == 1)).Id);
        var january = await overview.ClosingChecklistAsync(h.Auditor, (await s.Db.AccountingPeriods.AsNoTracking().SingleAsync(p => p.CompanyId == h.CompanyId && p.PeriodNo == 1)).Id);
        Assert.Equal(CheckStatus.Done, Item(january, "lock").Status);

        Assert.Equal(ErrorCodes.PeriodNotFound, await CodeOf(() => overview.ClosingChecklistAsync(h.Auditor, Guid.NewGuid())));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => overview.ClosingChecklistAsync(new SessionContext(h.PreparerId, h.CompanyId, new HashSet<string> { Permissions.VoucherView }), march.Id)));
    }
}

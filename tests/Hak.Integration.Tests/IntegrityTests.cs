using Hak.Application.Overview;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

/// <summary>Kiểm soát toàn vẹn: the ledger invariants, run read-only on a company's data.</summary>
public class IntegrityTests
{
    [Fact]
    public async Task CleanBooks_HaveNoErrors_AndATamperedVoucherIsReported_WithoutChangingAnything()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var svc = new IntegrityService(s.Db);
        var asOf = new DateOnly(2026, 12, 31);
        await h.PostedAsync(s, h.Sale(1_250_000));

        var clean = await svc.RunAsync(h.Auditor, asOf);
        Assert.Equal(0, clean.Errors);
        Assert.Contains(clean.Checks, c => c.Code == "unbalanced_entry" && c.Count == 0);

        // A draft whose status was changed outside the program (no journal entry behind it).
        var draft = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(300_000));
        await s.Db.Vouchers.Where(v => v.Id == draft.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.Status, VoucherStatus.Posted));
        var journalCount = await s.Db.JournalEntries.CountAsync();

        var report = await svc.RunAsync(h.Auditor, asOf);
        var finding = Assert.Single(report.Checks.Single(c => c.Code == "posted_without_entry").Findings);
        Assert.Equal((draft.Number, draft.Id), (finding.Reference, finding.VoucherId));
        Assert.Equal(1, report.Errors);
        Assert.Equal(journalCount, await s.Db.JournalEntries.CountAsync());   // read-only

        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => svc.RunAsync(h.Preparer with { Permissions = new HashSet<string>() }, asOf))).Code);
    }
}

using Hak.Application.Accounting;
using Hak.Application.Security;
using Hak.Application.Setup;
using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hak.Integration.Tests;

public class WorkflowSetupTests
{
    private static async Task<string> CodeOf(Func<Task> f) => (await Assert.ThrowsAsync<DomainException>(f)).Code;

    [Fact]
    public async Task VoucherTemplates_SaveReplaceListDelete_ValidatedAndAudited()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var setup = new WorkflowSetupService(s.Db, s.Audit, h.Clock);
        TemplateLine[] lines = [new(h.Acc["EXP"], "D", 5_000_000), new(h.Acc["BANK"], "C", null, Description: "Chuyển khoản")];

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.SaveTemplateAsync(h.Auditor, "Tiền thuê", "GJ", null, lines)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SaveTemplateAsync(h.Preparer, " ", "GJ", null, lines)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SaveTemplateAsync(h.Preparer, "x", "CR", null, lines)));           // cash documents have their own form
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SaveTemplateAsync(h.Preparer, "x", "GJ", null, [new(h.Acc["EXP"], "X", 1)])));
        Assert.Equal(ErrorCodes.AccountNotFound, await CodeOf(() => setup.SaveTemplateAsync(h.Preparer, "x", "GJ", null, [new(Guid.NewGuid(), "D", 1)])));
        Assert.Equal(ErrorCodes.AccountNotFound, await CodeOf(() => setup.SaveTemplateAsync(h.Preparer, "x", "GJ", null, [new(h.Acc["ASSETS"], "D", 1)])));   // summary account

        var saved = await setup.SaveTemplateAsync(h.Preparer, "Tiền thuê văn phòng", "GJ", "Hằng tháng", lines);
        Assert.Equal((5_000_000m, (decimal?)null, "Chuyển khoản"), (saved.Lines[0].Amount!.Value, saved.Lines[1].Amount, saved.Lines[1].Description));

        // Same name replaces; the list is by name.
        var replaced = await setup.SaveTemplateAsync(h.Approver, "Tiền thuê văn phòng", "GJ", "Từ 2026", [new(h.Acc["EXP"], "D", 6_000_000), new(h.Acc["BANK"], "C", 6_000_000)]);
        Assert.Equal(saved.Id, replaced.Id);
        await setup.SaveTemplateAsync(h.Preparer, "Phí ngân hàng", "GJ", null, lines);
        var list = await setup.TemplatesAsync(h.Auditor);
        Assert.Equal(["Phí ngân hàng", "Tiền thuê văn phòng"], list.Select(t => t.Name).ToArray());
        Assert.Equal(6_000_000m, list[1].Lines[0].Amount);

        await setup.DeleteTemplateAsync(h.Preparer, saved.Id);
        Assert.Single(await setup.TemplatesAsync(h.Auditor));
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => setup.DeleteTemplateAsync(h.Preparer, saved.Id)));
        Assert.Contains(await s.Db.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "voucher_template.delete");
    }

    [Fact]
    public async Task AnalysisCodes_FollowTheLineIntoTheLedger_AndReportPerCode()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var setup = new WorkflowSetupService(s.Db, s.Audit, h.Clock);

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.CreateAnalysisCodeAsync(h.Auditor, "Chuyến tàu", "T01", "Tàu 01")));
        var t01 = await setup.CreateAnalysisCodeAsync(h.Preparer, "Chuyến tàu", "t01", "Tàu tháng 3");
        var t02 = await setup.CreateAnalysisCodeAsync(h.Preparer, "Chuyến tàu", "T02", "Tàu tháng 4");
        Assert.Equal("T01", t01.Code);
        Assert.Equal(ErrorCodes.DuplicateCode, await CodeOf(() => setup.CreateAnalysisCodeAsync(h.Preparer, "Chuyến tàu", "T01", "x")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.CreateAnalysisCodeAsync(h.Preparer, "", "T03", "x")));

        VoucherInput Entry(DateOnly d, Guid debit, Guid credit, decimal amount, Guid? code, Guid? partner = null) => new(VoucherTypes.General, d, d, "Phân tích",
            [new VoucherLineInput(debit, Side.Debit, amount, partner, AnalysisCodeId: debit == h.Acc["EXP"] ? code : null),
             new VoucherLineInput(credit, Side.Credit, amount, AnalysisCodeId: credit == h.Acc["REV"] ? code : null)]);
        var sale = await h.PostedAsync(s, Entry(new(2026, 3, 10), h.Acc["AR"], h.Acc["REV"], 100_000_000, t01.Id, h.Partner["KH01"]));
        await h.PostedAsync(s, Entry(new(2026, 3, 12), h.Acc["EXP"], h.Acc["BANK2"], 70_000_000, t01.Id));
        await h.PostedAsync(s, Entry(new(2026, 4, 2), h.Acc["EXP"], h.Acc["BANK2"], 5_000_000, t02.Id));

        // The posted line carries the code; a reversal mirrors it.
        var posted = await s.Db.JournalLines.AsNoTracking().Where(l => l.AnalysisCodeId == t01.Id).ToListAsync();
        Assert.Equal(2, posted.Count);
        var rev = await s.Engine.ReverseAsync(h.Approver, sale.Id, new(2026, 3, 20), "Sai giá");
        Assert.Equal(t01.Id, (await s.Db.JournalEntries.AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.VoucherId == rev.Id)).Lines.Single(l => l.AccountId == h.Acc["REV"]).AnalysisCodeId);

        var march = await setup.AnalysisReportAsync(h.Auditor, new(2026, 3, 1), new(2026, 3, 31));
        Assert.Equal([("T01", "EXP", 70_000_000m, 0m), ("T01", "REV", 100_000_000m, 100_000_000m)], march.Select(r => (r.Code, r.AccountCode, r.Debit, r.Credit)).ToArray());
        Assert.Equal(["T01", "T02"], (await setup.AnalysisReportAsync(h.Auditor, new(2026, 1, 1), new(2026, 12, 31))).Select(r => r.Code).Distinct().ToArray());

        // Unknown, foreign-company or retired codes are refused when the voucher is checked.
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(async () =>
        {
            var v = await s.Engine.CreateDraftAsync(h.Preparer, Entry(new(2026, 4, 3), h.Acc["EXP"], h.Acc["BANK2"], 1, Guid.NewGuid()));
            await s.Engine.SubmitAsync(h.Preparer, v.Id);
        }));
        await setup.UpdateAnalysisCodeAsync(h.Preparer, t02.Id, "Tàu tháng 4 (đã xong)", false);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(async () =>
        {
            var v = await s.Engine.CreateDraftAsync(h.Preparer, Entry(new(2026, 4, 4), h.Acc["EXP"], h.Acc["BANK2"], 1, t02.Id));
            await s.Engine.SubmitAsync(h.Preparer, v.Id);
        }));
        Assert.Contains(await setup.AnalysisCodesAsync(h.Auditor), c => c.Code == "T02" && !c.IsActive && c.Name.EndsWith("(đã xong)"));
    }

    [Fact]
    public async Task PrintDetails_CompanyAddressAndBankAccountInfo_AreStoredAndAudited()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var setup = new WorkflowSetupService(s.Db, s.Audit, h.Clock);

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.SetCompanyAddressAsync(h.Preparer, "Số 1 đường thử")));     // config.manage
        await setup.SetCompanyAddressAsync(h.Admin, "  Số 1 đường thử, phường Thử  ");
        Assert.Equal("Số 1 đường thử, phường Thử", (await s.Db.Companies.AsNoTracking().SingleAsync(c => c.Id == h.CompanyId)).Address);
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SetCompanyAddressAsync(h.Admin, new string('x', 301))));

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.SetBankInfoAsync(h.Auditor, h.Acc["BANK"], "001", "NH thử")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SetBankInfoAsync(h.Preparer, h.Acc["CASH"], "001", "NH thử")));   // not a bank account
        Assert.Equal(ErrorCodes.AccountNotFound, await CodeOf(() => setup.SetBankInfoAsync(h.Preparer, Guid.NewGuid(), "001", "NH thử")));
        await setup.SetBankInfoAsync(h.Preparer, h.Acc["BANK"], " 0011223344 ", "Ngân hàng thử – CN Thử");
        var bank = await s.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == h.Acc["BANK"]);
        Assert.Equal(("0011223344", "Ngân hàng thử – CN Thử"), (bank.BankAccountNo, bank.BankName));
        await setup.SetBankInfoAsync(h.Preparer, h.Acc["BANK"], "", null);                                                      // cleared
        Assert.Null((await s.Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == h.Acc["BANK"])).BankAccountNo);

        var actions = await s.Db.AuditLogs.Select(a => a.Action).ToListAsync();
        Assert.Contains("company.address", actions);
        Assert.Contains("account.bank_info", actions);
    }

    [Fact]
    public async Task ApprovalLimit_AboveTheLimitNeedsTheHigherPermission()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var setup = new WorkflowSetupService(s.Db, s.Audit, h.Clock);

        Assert.Null(await setup.ApprovalLimitAsync(h.Auditor));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.SetApprovalLimitAsync(h.Approver, 1_000_000)));     // config.manage only
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => setup.SetApprovalLimitAsync(h.Admin, 0)));
        await setup.SetApprovalLimitAsync(h.Admin, 50_000_000);
        Assert.Equal(50_000_000m, await setup.ApprovalLimitAsync(h.Auditor));

        // An approver without the higher permission: fine up to the limit, refused above it. The chief accountant holds it by default.
        var plainApprover = new SessionContext(h.ApproverId, h.CompanyId, h.Approver.Permissions.Where(p => p != Permissions.VoucherApproveHigh).ToHashSet());
        Assert.Contains(Permissions.VoucherApproveHigh, h.Approver.Permissions);
        async Task<Guid> Submitted(decimal amount)
        {
            var v = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(amount));
            await s.Engine.SubmitAsync(h.Preparer, v.Id);
            return v.Id;
        }
        await s.Engine.ApproveAsync(plainApprover, await Submitted(50_000_000));
        var big = await Submitted(50_000_001);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Engine.ApproveAsync(plainApprover, big)));
        Assert.Equal(VoucherStatus.Submitted, (await s.Db.Vouchers.AsNoTracking().SingleAsync(v => v.Id == big)).Status);
        await s.Engine.ApproveAsync(h.Approver, big);

        await setup.SetApprovalLimitAsync(h.Admin, null);                                                               // limit removed
        await s.Engine.ApproveAsync(plainApprover, await Submitted(900_000_000));
        Assert.Contains(await s.Db.AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "company.approval_limit");
    }
}

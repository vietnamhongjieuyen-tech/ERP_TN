using Hak.Application.Banking;
using Hak.Application.Security;
using Hak.Application.Setup;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using static Hak.Integration.Tests.Phase2Fixture;

namespace Hak.Integration.Tests;

/// <summary>Chế độ phân quyền: full segregation (default) or one person keeping the whole set of books.</summary>
public class SegregationTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    [Fact]
    public async Task AllInOneRole_HasEveryCompanyPermission()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var role = await s.Db.Roles.Include(r => r.Permissions).SingleAsync(r => r.Code == "ALL_IN_ONE");
        var all = typeof(Permissions).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Equal(all.Order(), role.Permissions.Select(p => p.Permission).Order());
        Assert.Contains(Permissions.VoucherPost, all);
        Assert.Contains(Permissions.PaymentOrderApprove, all);
    }

    [Fact]
    public async Task OnePersonMode_LetsTheCreatorApproveAndPost_SwitchNeedsReasonAndIsLogged()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await ConfigureAsync(h, s);
        var setup = new WorkflowSetupService(s.Db, s.Audit, h.Clock);
        var solo = h.Approver with { Permissions = new HashSet<string>(RoleCatalog.AllPermissions()) };

        // Default: full segregation.
        Assert.True(await setup.RequireDistinctApproverAsync(solo));
        var v1 = await s.Engine.CreateDraftAsync(solo, h.Sale(1_000_000));
        await s.Engine.SubmitAsync(solo, v1.Id);
        Assert.Equal(ErrorCodes.SodViolation, await CodeOf(() => s.Engine.ApproveAsync(solo, v1.Id)));

        // Switching needs config.manage and, to drop segregation, a reason.
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => setup.SetRequireDistinctApproverAsync(h.Preparer, false, "x")));
        Assert.Equal(ErrorCodes.ReasonRequired, await CodeOf(() => setup.SetRequireDistinctApproverAsync(solo, false, " ")));
        await setup.SetRequireDistinctApproverAsync(solo, false, "Công ty chỉ có một kế toán");
        Assert.False(await setup.RequireDistinctApproverAsync(solo));
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.CompanyId == h.CompanyId && a.Action == "company.segregation"));

        // Now the same person approves and posts their own voucher and payment order.
        await s.Engine.ApproveAsync(solo, v1.Id);
        await s.Engine.PostAsync(solo, v1.Id);
        Assert.Equal(VoucherStatus.Posted, (await s.Db.Vouchers.AsNoTracking().SingleAsync(x => x.Id == v1.Id)).Status);
        await PostDocAsync(h, s, Receipt(h, 50_000_000, new(2026, 3, 1), main: "BANK"));
        var po = await s.PaymentOrders.CreateAsync(solo, new PaymentOrderInput(h.Acc["BANK"], h.Partner["NCC01"], 2_000_000, new(2026, 3, 5), "Chi thử", "0123456789", "NH TMCP A", null));
        await s.PaymentOrders.ApproveAsync(solo, po.Id);

        // Back to full segregation (no reason needed): the creator is blocked again.
        await setup.SetRequireDistinctApproverAsync(solo, true, null);
        var v2 = await s.Engine.CreateDraftAsync(solo, h.Sale(500_000));
        await s.Engine.SubmitAsync(solo, v2.Id);
        Assert.Equal(ErrorCodes.SodViolation, await CodeOf(() => s.Engine.ApproveAsync(solo, v2.Id)));
        // Permissions still apply in one-person mode: someone without voucher.post cannot post.
        await setup.SetRequireDistinctApproverAsync(solo, false, "Một kế toán");
        var noPost = solo with { Permissions = new HashSet<string>(RoleCatalog.AllPermissions().Where(p => p != Permissions.VoucherPost)) };
        await s.Engine.ApproveAsync(noPost, v2.Id);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Engine.PostAsync(noPost, v2.Id)));
    }
}

using Hak.Application.Accounting;
using Hak.Application.Setup;
using Hak.Domain.Common;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

public class SecurityAndIsolationTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    private static async Task<(Guid CompanyB, Guid AccountB)> AddSecondCompanyAsync(TestHost h, Scope s)
    {
        var b = await s.Setup.CreateCompanyAsync(h.AdminId, "TEST02", "Công ty B", null, "VND", h.RegimeVersionId);
        await s.Setup.GrantRoleAsync(h.AdminId, h.AdminId, b.Id, "ADMIN");
        var adminB = await s.Auth.ResolveContextAsync(h.AdminId, b.Id);
        await s.Setup.CreateFiscalYearAsync(adminB, "2026", new(2026, 1, 1), new(2026, 12, 31));
        var acc = await s.Setup.CreateAccountAsync(adminB, "CASH", "Tiền mặt B");
        return (b.Id, acc.Id);
    }

    [Fact]
    public async Task UserWithoutRoleInCompany_IsRefused()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var (companyB, _) = await AddSecondCompanyAsync(h, s);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Auth.ResolveContextAsync(h.PreparerId, companyB)));
    }

    [Fact]
    public async Task AccountOfAnotherCompany_CannotBeUsed()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var (_, accB) = await AddSecondCompanyAsync(h, s);
        var v = await s.Engine.CreateDraftAsync(h.Preparer, new VoucherInput(VoucherTypes.General, new(2026, 3, 1), new(2026, 3, 1), "x",
            [new(accB, Side.Debit, 10), new(h.Acc["REV"], Side.Credit, 10)]));
        Assert.Equal(ErrorCodes.AccountWrongCompany, await CodeOf(() => s.Engine.SubmitAsync(h.Preparer, v.Id)));
    }

    [Fact]
    public async Task VoucherOfAnotherCompany_IsInvisible()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var (companyB, _) = await AddSecondCompanyAsync(h, s);
        var v = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(1000));
        await s.Setup.GrantRoleAsync(h.AdminId, h.ApproverId, companyB, "CHIEF_ACCOUNTANT");
        var approverInB = await s.Auth.ResolveContextAsync(h.ApproverId, companyB);
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => s.Engine.ValidateAsync(approverInB, v.Id)));
        Assert.Empty((await s.Ledger.TrialBalanceAsync(approverInB, new(2026, 1, 1), new(2026, 12, 31))).Rows);
    }

    [Fact]
    public async Task AuditChain_VerifiesAndDetectsTampering()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await h.PostedAsync(s, h.Sale(1000));
        Assert.Null(await s.Audit.VerifyChainAsync(h.CompanyId));
        Assert.Null(await s.Audit.VerifyChainAsync(Guid.Empty));

        // The triggers block tampering through the application. Simulate an attacker with raw file access
        // who drops the trigger and edits a record: the hash chain must still expose it.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync("UPDATE audit_logs SET action='x'"));
        Assert.Contains("LEDGER_IMMUTABLE", ex.Message);
        await s.Db.Database.ExecuteSqlRawAsync(h.DisableAuditUpdateTriggerSql);
        await s.Db.Database.ExecuteSqlRawAsync($"UPDATE audit_logs SET result = 'DENIED' WHERE seq = 3 AND company_id = {h.SqlGuidLiteral(h.CompanyId)}");
        Assert.Equal(3, await s.Audit.VerifyChainAsync(h.CompanyId));
    }

    [Fact]
    public async Task Production_RefusesUnverifiedRegime_UntilVerifiedWithSources()
    {
        using var h = await TestHost.CreateAsync("Production");
        using var s = h.NewScope();
        var v = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(1000));
        await s.Engine.SubmitAsync(h.Preparer, v.Id);
        await s.Engine.ApproveAsync(h.Approver, v.Id);
        Assert.Equal(ErrorCodes.RegimeNotVerified, await CodeOf(() => s.Engine.PostAsync(h.Approver, v.Id)));

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Setup.VerifyRegimeVersionAsync(h.PreparerId, h.RegimeVersionId, "x")));
        Assert.Equal(ErrorCodes.ReasonRequired, await CodeOf(() => s.Setup.VerifyRegimeVersionAsync(h.AdminId, h.RegimeVersionId, "")));
        await s.Setup.VerifyRegimeVersionAsync(h.AdminId, h.RegimeVersionId, "Fixture kiểm thử – KTT đã đối chiếu");
        await s.Engine.PostAsync(h.Approver, v.Id);
    }

    [Fact]
    public async Task RegimeVersion_WithoutLegalSources_CannotBeVerified()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        // Refused at creation; the verify-time guard still protects versions stored before that rule (sources removed below).
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Setup.CreateRegimeVersionAsync(h.AdminId, "TEST-REGIME", "x", "TEST-2", new(2027, 1, 1), null, [])));
        await s.Db.Set<Hak.Domain.Regime.LegalSource>().Where(x => x.RegimeVersionId == h.RegimeVersionId).ExecuteDeleteAsync();
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Setup.VerifyRegimeVersionAsync(h.AdminId, h.RegimeVersionId, "ok")));
    }

    [Fact]
    public async Task Login_Session_AndRevocation()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        Assert.Equal(ErrorCodes.Unauthenticated, await CodeOf(() => s.Auth.LoginAsync("lap", "wrong-password", "PC1")));
        var login = await s.Auth.LoginAsync("LAP", TestHost.Password, "PC1"); // user name is case-insensitive
        var (user, _) = await s.Auth.AuthenticateAsync(login.Token);
        Assert.Equal(h.PreparerId, user.Id);
        await s.Auth.LogoutAsync(login.SessionId);
        Assert.Equal(ErrorCodes.Unauthenticated, await CodeOf(() => s.Auth.AuthenticateAsync(login.Token)));
        Assert.Equal(ErrorCodes.Unauthenticated, await CodeOf(() => s.Auth.AuthenticateAsync("forged")));
        Assert.True(await s.Db.AuditLogs.AnyAsync(a => a.Action == "auth.login" && a.Result == "DENIED"));
        // Text typed as an unknown user name (it may be a password) never reaches the audit log.
        Assert.Equal(ErrorCodes.Unauthenticated, await CodeOf(() => s.Auth.LoginAsync("Go-nham-mat-khau-2026", "x", "PC1")));
        Assert.DoesNotContain("go-nham-mat-khau-2026", string.Join("|", await s.Db.AuditLogs.Select(a => a.EntityId + "|" + (a.DataJson ?? "")).ToListAsync()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestHost.Password, string.Join("|", await s.Db.Users.Select(u => u.PasswordHash).ToListAsync()));
    }

    [Fact]
    public async Task Bootstrap_OnlyOnce_AndWeakPasswordRejected()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Setup.BootstrapAdminAsync("root", "x", TestHost.Password)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Setup.CreateUserAsync(h.AdminId, "u2", "x", "short")));
    }

    /// <summary>
    /// LAN concurrency: eight users post at the same time through separate sessions. Every voucher must post (serialization
    /// failures are retried transparently), numbers stay unique and gap-free, the ledger balances and the audit chain is intact.
    /// </summary>
    [Fact]
    public async Task ConcurrentPosting_FromSeparateSessions_AllSucceed_WithUniqueNumbers_AndIntactAuditChain()
    {
        using var h = await TestHost.CreateAsync();
        var tasks = Enumerable.Range(1, 8).Select(i => Task.Run(async () =>
        {
            using var s = h.NewScope();
            var v = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(1_000 * i));
            await s.Engine.SubmitAsync(h.Preparer, v.Id);
            await s.Engine.ApproveAsync(h.Approver, v.Id);
            return (await s.Engine.PostAsync(h.Approver, v.Id)).VoucherNumber;
        })).ToList();
        var numbers = await Task.WhenAll(tasks);
        Assert.Equal(8, numbers.Distinct().Count());

        using var check = h.NewScope();
        Assert.Equal(8, await check.Db.JournalEntries.CountAsync());
        var tb = await check.Ledger.TrialBalanceAsync(h.Auditor, new(2026, 1, 1), new(2026, 12, 31));
        Assert.Equal(tb.Rows.Sum(r => r.ClosingDebit), tb.Rows.Sum(r => r.ClosingCredit));
        Assert.Null(await check.Audit.VerifyChainAsync(h.CompanyId));
    }

    [Fact]
    public async Task AuditViewer_FiltersAndPages_WithinCompany_ForAuditViewOnly()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await h.PostedAsync(s, h.Sale(1000));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Audit.QueryAsync(h.Preparer, new Hak.Application.Audit.AuditFilter())));
        var all = await s.Audit.QueryAsync(h.Auditor, new Hak.Application.Audit.AuditFilter(Take: 2));
        Assert.Equal(2, all.Rows.Count);
        Assert.True(all.Total > 2);
        Assert.True(all.Rows[0].Seq > all.Rows[1].Seq);                                     // newest first
        var posts = await s.Audit.QueryAsync(h.Auditor, new Hak.Application.Audit.AuditFilter(Action: "voucher.post"));
        Assert.Equal("duyet", Assert.Single(posts.Rows).UserName);
        Assert.Empty((await s.Audit.QueryAsync(h.Auditor, new Hak.Application.Audit.AuditFilter(From: new(2027, 1, 1)))).Rows);
    }

    [Fact]
    public async Task FiscalYears_CannotOverlap_AndCatalogCodesAreUnique()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        Assert.Equal(ErrorCodes.FiscalYearOverlap, await CodeOf(() => s.Setup.CreateFiscalYearAsync(h.Admin, "2026B", new(2026, 6, 1), new(2027, 5, 31))));
        Assert.Equal(ErrorCodes.DuplicateCode, await CodeOf(() => s.Setup.CreateAccountAsync(h.Admin, "CASH", "trùng")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Setup.CreateAccountAsync(h.Admin, "CASH-1", "x", parentCode: "CASH")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Setup.CreateAccountAsync(h.Admin, "bad code", "x")));
    }
}

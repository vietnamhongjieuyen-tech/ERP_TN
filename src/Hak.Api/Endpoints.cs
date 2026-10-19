using Hak.Application.Accounting;
using Hak.Application.Common;
using Hak.Application.Audit;
using Hak.Application.Exchange;
using Hak.Application.Security;
using Hak.Application.Setup;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

public sealed record LoginRequest(string UserName, string Password, string? Device);
public sealed record BootstrapRequest(string UserName, string FullName, string Password);
public sealed record CreateUserRequest(string UserName, string FullName, string Password, bool IsSystemAdmin = false);
public sealed record GrantRequest(Guid UserId, Guid CompanyId, string RoleCode);
public sealed record RegimeVersionRequest(string RegimeCode, string RegimeName, string VersionCode, DateOnly EffectiveFrom, DateOnly? EffectiveTo, List<LegalSourceInput> Sources);
public sealed record VerifyRequest(string Note);
public sealed record CompanyRequest(string Code, string Name, string? TaxCode, string FunctionalCurrency, Guid RegimeVersionId);
public sealed record FiscalYearRequest(string Code, DateOnly StartDate, DateOnly EndDate);
public sealed record AccountRequest(string Code, string Name, string? ParentCode, bool IsPostable = true, bool RequiresPartner = false, BalanceDisplay BalanceDisplay = BalanceDisplay.Net,
    OpenItemNature OpenItemNature = OpenItemNature.None, CashKind CashKind = CashKind.None, NegativeBalancePolicy NegativeBalancePolicy = NegativeBalancePolicy.None);
public sealed record PartnerRequest(string Code, string Name, PartnerRoles Roles, string? TaxCode);
public sealed record LineRequest(Guid AccountId, string Side, decimal Amount, Guid? PartnerId, string? ForeignCurrency, decimal? ForeignAmount, decimal? ExchangeRate, string? Description, string? CashFlowCode = null);
public sealed record VoucherRequest(string VoucherType, DateOnly VoucherDate, DateOnly PostingDate, string Description, List<LineRequest> Lines, Guid? BranchId = null);
public sealed record ReasonRequest(string Reason);
public sealed record ReverseRequest(DateOnly ReversalDate, string Reason);

public sealed record LineDto(int LineNo, Guid AccountId, string Side, decimal Amount, Guid? PartnerId, string? ForeignCurrency, decimal? ForeignAmount, decimal? ExchangeRate, string? Description, string? CashFlowCode = null);
public sealed record VoucherDto(Guid Id, string Number, string VoucherType, string Status, DateOnly VoucherDate, DateOnly PostingDate, string Description,
    decimal TotalDebit, decimal TotalCredit, long RowVersion, Guid? ReversesVoucherId, Guid? AdjustsVoucherId, string? SourceSystem, string? SourceId, List<LineDto> Lines)
{
    public static VoucherDto From(Voucher v) => new(v.Id, v.Number, v.VoucherType, v.Status.ToString(), v.VoucherDate, v.PostingDate, v.Description,
        v.TotalDebit, v.TotalCredit, v.RowVersion, v.ReversesVoucherId, v.AdjustsVoucherId, v.SourceSystem, v.SourceId,
        v.Lines.OrderBy(l => l.LineNo).Select(l => new LineDto(l.LineNo, l.AccountId, l.Side == Side.Debit ? "D" : "C", l.Amount, l.PartnerId,
            l.ForeignCurrency, l.ForeignAmount, l.ExchangeRate, l.Description, l.CashFlowCode)).ToList());
}

public static class Endpoints
{
    private static VoucherInput ToInput(VoucherRequest r) => new(r.VoucherType, r.VoucherDate, r.PostingDate, r.Description,
        r.Lines.Select(l => new VoucherLineInput(l.AccountId, l.Side switch
        {
            "D" => Side.Debit,
            "C" => Side.Credit,
            _ => throw new DomainException(ErrorCodes.ValidationFailed, $"Chiều '{l.Side}' không hợp lệ (D hoặc C)."),
        }, l.Amount, l.PartnerId, l.ForeignCurrency, l.ForeignAmount, l.ExchangeRate, l.Description, CashFlowCode: l.CashFlowCode)).ToList(), r.BranchId);

    private static async Task<Guid> AdminAsync(HttpContext http)
    {
        var (user, _) = await http.AuthAsync();
        if (!user.IsSystemAdmin) throw new DomainException(ErrorCodes.Forbidden, "Chỉ quản trị hệ thống.");
        return user.Id;
    }

    public static void MapHakEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (HakRuntimeOptions o) => Results.Ok(new { status = "ok", environment = o.Environment }));

        // ---- authentication & administration ----
        api.MapPost("/setup/bootstrap", async (BootstrapRequest r, SetupService s) =>
            Results.Ok(new { (await s.BootstrapAdminAsync(r.UserName, r.FullName, r.Password)).Id }));
        api.MapPost("/auth/login", async (LoginRequest r, AuthService a) => Results.Ok(await a.LoginAsync(r.UserName, r.Password, r.Device)));
        api.MapPost("/auth/logout", async (HttpContext http, AuthService a) => { var (_, s) = await http.AuthAsync(); await a.LogoutAsync(s.Id); return Results.NoContent(); });
        api.MapGet("/me/companies", async (HttpContext http, HakDbContext db) =>
        {
            var (user, _) = await http.AuthAsync();
            // Each company with the user's role names and whether they can open the accounting screens (voucher.view):
            // an "Administrator" grant alone only manages settings, so the screen says so instead of failing with FORBIDDEN.
            var rows = await (from ucr in db.UserCompanyRoles
                              join c in db.Companies on ucr.CompanyId equals c.Id
                              join r in db.Roles on ucr.RoleId equals r.Id
                              where ucr.UserId == user.Id
                              select new { c.Id, c.Code, c.Name, c.TaxCode, RoleId = r.Id, RoleName = r.Name }).ToListAsync();
            var roleIds = rows.Select(r => r.RoleId).Distinct().ToList();
            var perms = await db.RolePermissions.Where(p => roleIds.Contains(p.RoleId)).Select(p => new { p.RoleId, p.Permission }).ToListAsync();
            return Results.Ok(rows.GroupBy(r => new { r.Id, r.Code, r.Name, r.TaxCode }).Select(g => new
            {
                g.Key.Id, g.Key.Code, g.Key.Name, g.Key.TaxCode,
                roles = g.Select(r => r.RoleName).Distinct().ToList(),
                permissions = perms.Where(p => g.Any(r => r.RoleId == p.RoleId)).Select(p => p.Permission).Distinct().ToList(),
            }).OrderBy(c => c.Code).ToList());
        });

        api.MapPost("/admin/users", async (HttpContext http, CreateUserRequest r, SetupService s) =>
            Results.Ok(new { (await s.CreateUserAsync(await AdminAsync(http), r.UserName, r.FullName, r.Password, r.IsSystemAdmin)).Id }));
        api.MapPost("/admin/grants", async (HttpContext http, GrantRequest r, SetupService s) =>
        { await s.GrantRoleAsync(await AdminAsync(http), r.UserId, r.CompanyId, r.RoleCode); return Results.NoContent(); });
        api.MapPost("/admin/regime-versions", async (HttpContext http, RegimeVersionRequest r, SetupService s) =>
            Results.Ok(new { (await s.CreateRegimeVersionAsync(await AdminAsync(http), r.RegimeCode, r.RegimeName, r.VersionCode, r.EffectiveFrom, r.EffectiveTo, r.Sources)).Id }));
        api.MapPost("/admin/regime-versions/{id:guid}/verify", async (HttpContext http, Guid id, VerifyRequest r, SetupService s) =>
        { await s.VerifyRegimeVersionAsync(await AdminAsync(http), id, r.Note); return Results.NoContent(); });
        api.MapPost("/admin/companies", async (HttpContext http, CompanyRequest r, SetupService s) =>
            Results.Ok(new { (await s.CreateCompanyAsync(await AdminAsync(http), r.Code, r.Name, r.TaxCode, r.FunctionalCurrency, r.RegimeVersionId)).Id }));

        // ---- company-scoped configuration & catalog ----
        api.MapPost("/fiscal-years", async (HttpContext http, FiscalYearRequest r, SetupService s) =>
        {
            var fy = await s.CreateFiscalYearAsync(await http.SessionAsync(), r.Code, r.StartDate, r.EndDate);
            return Results.Ok(new { fy.Id, periods = fy.Periods.OrderBy(p => p.PeriodNo).Select(p => new { p.Id, p.PeriodNo, p.StartDate, p.EndDate, status = p.Status.ToString() }) });
        });
        api.MapGet("/periods", async (HttpContext http, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await db.AccountingPeriods.Where(p => p.CompanyId == ctx.CompanyId).OrderBy(p => p.StartDate)
                .Select(p => new { p.Id, p.FiscalYearId, p.PeriodNo, p.StartDate, p.EndDate, status = p.Status.ToString() }).ToListAsync());
        });
        api.MapPost("/periods/{id:guid}/lock", async (HttpContext http, Guid id, SetupService s) => { await s.LockPeriodAsync(await http.SessionAsync(), id); return Results.NoContent(); });
        api.MapPost("/periods/{id:guid}/reopen", async (HttpContext http, Guid id, ReasonRequest r, SetupService s) => { await s.ReopenPeriodAsync(await http.SessionAsync(), id, r.Reason); return Results.NoContent(); });

        api.MapGet("/accounts", async (HttpContext http, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await db.Accounts.Where(a => a.CompanyId == ctx.CompanyId).OrderBy(a => a.Code)
                .Select(a => new { a.Id, a.Code, a.Name, a.ParentId, a.IsPostable, a.RequiresPartner, a.IsActive, a.ReportTags, balanceDisplay = a.BalanceDisplay.ToString(),
                    openItemNature = a.OpenItemNature.ToString(), cashKind = a.CashKind.ToString(), negativeBalancePolicy = a.NegativeBalancePolicy.ToString() }).ToListAsync());
        });
        api.MapPost("/accounts", async (HttpContext http, AccountRequest r, SetupService s) =>
            Results.Ok(new { (await s.CreateAccountAsync(await http.SessionAsync(), r.Code, r.Name, r.ParentCode, r.IsPostable, r.RequiresPartner, r.BalanceDisplay,
                r.OpenItemNature, r.CashKind, r.NegativeBalancePolicy)).Id }));
        api.MapGet("/partners", async (HttpContext http, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await db.BusinessPartners.Where(p => p.CompanyId == ctx.CompanyId).OrderBy(p => p.Code)
                .Select(p => new { p.Id, p.Code, p.Name, p.TaxCode, p.IsActive }).ToListAsync());
        });
        api.MapPost("/partners", async (HttpContext http, PartnerRequest r, SetupService s) =>
            Results.Ok(new { (await s.CreatePartnerAsync(await http.SessionAsync(), r.Code, r.Name, r.Roles, r.TaxCode)).Id }));

        // ---- vouchers (Accounting Engine) ----
        // Paged list: filters by date, status, type, account and text (number / description); total is the full match count.
        api.MapGet("/vouchers", async (HttpContext http, HakDbContext db, DateOnly? from, DateOnly? to, VoucherStatus? status, string? type,
            Guid? accountId, string? text, int? skip, int? take) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            var q = db.Vouchers.AsNoTracking().Where(v => v.CompanyId == ctx.CompanyId);
            if (from is not null) q = q.Where(v => v.PostingDate >= from);
            if (to is not null) q = q.Where(v => v.PostingDate <= to);
            if (status is not null) q = q.Where(v => v.Status == status);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(v => v.VoucherType == type);
            if (accountId is not null) q = q.Where(v => v.Lines.Any(l => l.AccountId == accountId));
            if (!string.IsNullOrWhiteSpace(text))
            {
                var t = text.Trim().ToLower();
                q = q.Where(v => v.Number.ToLower().Contains(t) || v.Description.ToLower().Contains(t));
            }
            var size = Math.Clamp(take ?? 200, 1, 1000);
            var offset = Math.Max(skip ?? 0, 0);
            var total = await q.CountAsync();
            var page = await q.Include(v => v.Lines).OrderBy(v => v.PostingDate).ThenBy(v => v.Number).Skip(offset).Take(size).ToListAsync();
            return Results.Ok(new { items = page.Select(VoucherDto.From), total, skip = offset, take = size });
        });
        api.MapGet("/vouchers/{id:guid}", async (HttpContext http, Guid id, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            var v = await db.Vouchers.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Chứng từ không tồn tại.");
            return Results.Ok(VoucherDto.From(v));
        });
        api.MapGet("/vouchers/{id:guid}/history", async (HttpContext http, Guid id, HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.VoucherView);
            return Results.Ok(await db.VoucherHistory.Where(h => h.CompanyId == ctx.CompanyId && h.VoucherId == id).OrderBy(h => h.VoucherRowVersion)
                .Select(h => new { h.Action, from = h.FromStatus.ToString(), to = h.ToStatus.ToString(), h.Note, h.UserId, h.At, h.VoucherRowVersion }).ToListAsync());
        });
        api.MapPost("/vouchers", async (HttpContext http, VoucherRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.CreateDraftAsync(await http.SessionAsync(), ToInput(r)))));
        api.MapPut("/vouchers/{id:guid}", async (HttpContext http, Guid id, long rowVersion, VoucherRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.UpdateDraftAsync(await http.SessionAsync(), id, rowVersion, ToInput(r)))));
        api.MapPost("/vouchers/{id:guid}/adjust", async (HttpContext http, Guid id, VoucherRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.CreateAdjustmentAsync(await http.SessionAsync(), id, ToInput(r)))));
        api.MapPost("/vouchers/{id:guid}/validate", async (HttpContext http, Guid id, AccountingEngine e) =>
            Results.Ok(await e.ValidateAsync(await http.SessionAsync(), id)));
        api.MapPost("/vouchers/{id:guid}/submit", async (HttpContext http, Guid id, long? rowVersion, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.SubmitAsync(await http.SessionAsync(), id, rowVersion))));
        api.MapPost("/vouchers/{id:guid}/approve", async (HttpContext http, Guid id, long? rowVersion, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.ApproveAsync(await http.SessionAsync(), id, rowVersion))));
        api.MapPost("/vouchers/{id:guid}/reject", async (HttpContext http, Guid id, long? rowVersion, ReasonRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.RejectAsync(await http.SessionAsync(), id, r.Reason, rowVersion))));
        api.MapPost("/vouchers/{id:guid}/cancel", async (HttpContext http, Guid id, long? rowVersion, ReasonRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.CancelAsync(await http.SessionAsync(), id, r.Reason, rowVersion))));
        api.MapPost("/vouchers/{id:guid}/post", async (HttpContext http, Guid id, long? rowVersion, AccountingEngine e) =>
        {
            var j = await e.PostAsync(await http.SessionAsync(), id, rowVersion);
            return Results.Ok(new { journalEntryId = j.Id, j.PostingDate, j.VoucherNumber });
        });
        api.MapPost("/vouchers/{id:guid}/reverse", async (HttpContext http, Guid id, ReverseRequest r, AccountingEngine e) =>
            Results.Ok(VoucherDto.From(await e.ReverseAsync(await http.SessionAsync(), id, r.ReversalDate, r.Reason))));

        // ---- reports ----
        api.MapGet("/reports/trial-balance", async (HttpContext http, DateOnly from, DateOnly to, bool? rollup, LedgerQueryService q) =>
        {
            var tb = await q.TrialBalanceAsync(await http.SessionAsync(), from, to, rollup ?? false);
            return Results.Ok(new
            {
                tb.From, tb.To, tb.IsBalanced, rows = tb.Rows, groups = tb.Groups,
                totals = new { tb.TotalOpeningDebit, tb.TotalOpeningCredit, tb.TotalPeriodDebit, tb.TotalPeriodCredit, tb.TotalClosingDebit, tb.TotalClosingCredit },
            });
        });
        api.MapGet("/reports/general-journal", async (HttpContext http, DateOnly from, DateOnly to, LedgerQueryService q) =>
            Results.Ok(await q.GeneralJournalAsync(await http.SessionAsync(), from, to)));

        // ---- JSON exchange ----
        api.MapPost("/exchange/journal-vouchers", async (HttpContext http, bool commit, JournalVoucherPackageService svc) =>
        {
            var ctx = await http.SessionAsync();
            using var reader = new StreamReader(http.Request.Body);
            var result = await svc.ProcessAsync(ctx, await reader.ReadToEndAsync(), commit);
            return Results.Text(result.ToJson(), "application/json");
        });

        // ---- audit ----
        api.MapGet("/audit/verify", async (HttpContext http, AuditService a) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Permissions.AuditView);
            var broken = await a.VerifyChainAsync(ctx.CompanyId);
            return Results.Ok(new { intact = broken is null, firstBrokenSeq = broken });
        });
    }
}

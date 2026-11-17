using Hak.Application.Backup;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

public sealed record BackupRequest(string Passphrase, string? Note);
public sealed record BackupCheckRequest(string FileName, string Passphrase);
public sealed record BackupScheduleRequest(bool Enabled, int Hour, int Keep, string? CopyTo, string? Passphrase);
public sealed record AccountUpdateRequest(string Name, bool IsActive);
public sealed record MoveAccountRequest(string? ParentCode);
public sealed record AccountTagsRequest(string? Tags);
public sealed record ChildAccountRequest(string Code, string Name, bool? RequiresPartner = null, Hak.Domain.Catalog.OpenItemNature? OpenItemNature = null,
    Hak.Domain.Catalog.CashKind? CashKind = null, Hak.Domain.Catalog.NegativeBalancePolicy? NegativeBalancePolicy = null, string? ReportTags = null);
public sealed record AccountAttributesRequest(bool RequiresPartner, Hak.Domain.Catalog.OpenItemNature OpenItemNature, Hak.Domain.Catalog.CashKind CashKind,
    Hak.Domain.Catalog.NegativeBalancePolicy NegativeBalancePolicy);
public sealed record RevokeRequest(Guid CompanyId, string RoleCode, string Reason);
public sealed record ActiveRequest(bool Active, string Reason);
public sealed record ResetPasswordRequest(string NewPassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ImportRequest<T>(IReadOnlyList<T> Rows, bool Commit);
public sealed record AssetImportRequest(string OpeningVoucherNumber, IReadOnlyList<Hak.Application.Setup.AssetImportRow> Rows, bool Commit);
public sealed record OpeningStockRequest(DateOnly Date, string CounterAccountCode, string? Description, IReadOnlyList<Hak.Application.Setup.OpeningStockRow> Rows, bool Commit);
public sealed record OpeningImportRequest(DateOnly Date, string? Description, IReadOnlyList<Hak.Application.Setup.OpeningImportRow> Rows, bool Commit);

/// <summary>System-administrator endpoints (no company context): backups contain every company's data.</summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        // Audit Viewer (company scope, audit.view).
        app.MapGet("/api/audit", async (HttpContext http, Hak.Application.Audit.AuditService a, DateOnly? from, DateOnly? to, string? action, string? entityType,
                string? entityId, string? result, int? skip, int? take) =>
            Results.Ok(await a.QueryAsync(await http.SessionAsync(), new Hak.Application.Audit.AuditFilter(from, to, action, entityType, entityId, null, result, skip ?? 0, take ?? 100))));

        // User administration (system administrators) and own password change.
        var admin = app.MapGroup("/api/admin");
        admin.MapGet("/users", async (HttpContext http, Hak.Application.Setup.SetupService s) => Results.Ok(await s.ListUsersAsync((await http.AuthAsync()).User.Id)));
        admin.MapGet("/companies", async (HttpContext http, Hak.Application.Setup.SetupService s) =>
            Results.Ok((await s.ListCompaniesAsync((await http.AuthAsync()).User.Id)).Select(c => new { id = c.Id, code = c.Code, name = c.Name })));
        admin.MapGet("/regime-versions", async (HttpContext http, Hak.Application.Setup.SetupService s) =>
            Results.Ok(await s.ListRegimeVersionsAsync((await http.AuthAsync()).User.Id)));
        // First run: the login screen offers to create the first administrator while no user exists.
        app.MapGet("/api/setup/status", async (Hak.Application.Setup.SetupService s, Hak.Application.Common.HakRuntimeOptions o) =>
            Results.Ok(new { needsBootstrap = await s.NeedsBootstrapAsync(), environment = o.Environment }));
        app.MapPut("/api/accounts/{id:guid}", async (HttpContext http, Guid id, AccountUpdateRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.UpdateAccountAsync(await http.SessionAsync(), id, r.Name, r.IsActive); return Results.NoContent(); });
        app.MapPost("/api/accounts/{id:guid}/parent", async (HttpContext http, Guid id, MoveAccountRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.MoveAccountAsync(await http.SessionAsync(), id, r.ParentCode); return Results.NoContent(); });
        app.MapPost("/api/accounts/{id:guid}/tags", async (HttpContext http, Guid id, AccountTagsRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.SetAccountTagsAsync(await http.SessionAsync(), id, r.Tags); return Results.NoContent(); });
        app.MapPost("/api/accounts/{id:guid}/children", async (HttpContext http, Guid id, ChildAccountRequest r, Hak.Application.Setup.SetupService s) =>
        {
            var a = await s.AddChildAccountAsync(await http.SessionAsync(), id, r.Code, r.Name,
                new Hak.Application.Setup.AccountAttributes(r.RequiresPartner, r.OpenItemNature, r.CashKind, r.NegativeBalancePolicy, r.ReportTags));
            return Results.Ok(new { a.Id, a.Code });
        });
        app.MapPost("/api/accounts/{id:guid}/attributes", async (HttpContext http, Guid id, AccountAttributesRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.UpdateAccountAttributesAsync(await http.SessionAsync(), id, r.RequiresPartner, r.OpenItemNature, r.CashKind, r.NegativeBalancePolicy); return Results.NoContent(); });
        app.MapGet("/api/rules/account-roles", async (HttpContext http, Hak.Data.HakDbContext db) =>
        {
            var ctx = await http.SessionAsync(); ctx.Require(Hak.Domain.Security.Permissions.VoucherView);
            var mapped = await (from m in db.AccountRoleMappings join a in db.Accounts on m.AccountId equals a.Id
                                where m.CompanyId == ctx.CompanyId select new { m.RoleCode, a.Code, a.Name }).ToListAsync();
            var known = Hak.Application.Rules.AccountRoles.Known.Select(k => k.Code).ToHashSet();
            return Results.Ok(Hak.Application.Rules.AccountRoles.Known
                .Select(k => new { k.Code, k.Module, k.Description, k.Optional, account = mapped.Where(m => m.RoleCode == k.Code).Select(m => m.Code + " – " + m.Name).FirstOrDefault() })
                .Concat(mapped.Where(m => !known.Contains(m.RoleCode)).Select(m => new { Code = m.RoleCode, Module = "Khác", Description = "(vai trò do quy tắc riêng sử dụng)", Optional = true, account = (string?)(m.Code + " – " + m.Name) })));
        });
        // Bulk import (chart of accounts, partners, opening balances): commit=false is a dry run.
        app.MapPost("/api/import/accounts", async (HttpContext http, ImportRequest<Hak.Application.Setup.AccountImportRow> r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportAccountsAsync(await http.SessionAsync(), r.Rows, r.Commit)));
        app.MapPost("/api/import/partners", async (HttpContext http, ImportRequest<Hak.Application.Setup.PartnerImportRow> r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportPartnersAsync(await http.SessionAsync(), r.Rows, r.Commit)));
        app.MapPost("/api/import/items", async (HttpContext http, ImportRequest<Hak.Application.Setup.ItemImportRow> r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportItemsAsync(await http.SessionAsync(), r.Rows, r.Commit)));
        app.MapPost("/api/import/opening-stock", async (HttpContext http, OpeningStockRequest r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportOpeningStockAsync(await http.SessionAsync(), r.Date, r.CounterAccountCode, r.Description, r.Rows, r.Commit)));
        app.MapPost("/api/import/assets", async (HttpContext http, AssetImportRequest r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportAssetsAsync(await http.SessionAsync(), r.OpeningVoucherNumber, r.Rows, r.Commit)));
        app.MapPost("/api/import/opening-balances", async (HttpContext http, OpeningImportRequest r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportOpeningBalancesAsync(await http.SessionAsync(), r.Date, r.Description, r.Rows, r.Commit)));
        app.MapPost("/api/import/vouchers", async (HttpContext http, ImportRequest<Hak.Application.Setup.VoucherImportRow> r, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ImportVouchersAsync(await http.SessionAsync(), r.Rows, r.Commit)));
        app.MapPost("/api/import/vouchers/submit", async (HttpContext http, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.SubmitImportedAsync(await http.SessionAsync())));
        app.MapPost("/api/import/vouchers/approve-post", async (HttpContext http, Hak.Application.Setup.CatalogImportService s) =>
            Results.Ok(await s.ApproveAndPostImportedAsync(await http.SessionAsync())));

        admin.MapGet("/roles",async (HttpContext http, Hak.Application.Setup.SetupService s) => Results.Ok(await s.ListRolesAsync((await http.AuthAsync()).User.Id)));
        admin.MapPost("/users/{id:guid}/revoke", async (HttpContext http, Guid id, RevokeRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.RevokeRoleAsync((await http.AuthAsync()).User.Id, id, r.CompanyId, r.RoleCode, r.Reason); return Results.NoContent(); });
        admin.MapPost("/users/{id:guid}/active", async (HttpContext http, Guid id, ActiveRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.SetUserActiveAsync((await http.AuthAsync()).User.Id, id, r.Active, r.Reason); return Results.NoContent(); });
        admin.MapPost("/users/{id:guid}/reset-password", async (HttpContext http, Guid id, ResetPasswordRequest r, Hak.Application.Setup.SetupService s) =>
        { await s.ResetPasswordAsync((await http.AuthAsync()).User.Id, id, r.NewPassword); return Results.NoContent(); });
        app.MapPost("/api/me/password", async (HttpContext http, ChangePasswordRequest r, Hak.Application.Setup.SetupService s) =>
        {
            var (user, session) = await http.AuthAsync();
            await s.ChangeOwnPasswordAsync(user.Id, session.Id, r.CurrentPassword, r.NewPassword);
            return Results.NoContent();
        });
        app.MapGet("/api/me", async (HttpContext http) =>
        {
            var (user, _) = await http.AuthAsync();
            return Results.Ok(new { user.Id, user.UserName, user.FullName, user.IsSystemAdmin });
        });

        var api = app.MapGroup("/api/admin/backups");
        api.MapGet("", async (HttpContext http, BackupService s) => Results.Ok(await s.ListAsync((await http.AuthAsync()).User.Id)));
        api.MapPost("", async (HttpContext http, BackupRequest r, BackupService s) => Results.Ok(await s.CreateAsync((await http.AuthAsync()).User.Id, r.Passphrase, r.Note)));
        api.MapPost("/verify", async (HttpContext http, BackupCheckRequest r, BackupService s) =>
            Results.Ok(await s.VerifyAsync((await http.AuthAsync()).User.Id, r.FileName, r.Passphrase)));
        api.MapPost("/restore", async (HttpContext http, BackupCheckRequest r, BackupService s) =>
            Results.Ok(await s.VerifyAsync((await http.AuthAsync()).User.Id, r.FileName, r.Passphrase, restore: true)));
        api.MapGet("/schedule", async (HttpContext http, BackupScheduleService s) => Results.Ok(await s.GetAsync((await http.AuthAsync()).User.Id)));
        api.MapPost("/schedule", async (HttpContext http, BackupScheduleRequest r, BackupScheduleService s) =>
            Results.Ok(await s.SetAsync((await http.AuthAsync()).User.Id, r.Enabled, r.Hour, r.Keep, r.CopyTo, r.Passphrase)));
        api.MapPost("/schedule/run", async (HttpContext http, BackupScheduleService s) => Results.Ok(await s.RunNowAsync((await http.AuthAsync()).User.Id)));
    }
}

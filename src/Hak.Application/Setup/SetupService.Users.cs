using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Domain.Common;
using Hak.Domain.Regime;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Setup;

public sealed record UserGrant(Guid CompanyId, string CompanyCode, string RoleCode, string RoleName);

public sealed record UserRow(Guid Id, string UserName, string FullName, bool IsActive, bool IsSystemAdmin, DateTime? LockedUntil, IReadOnlyList<UserGrant> Grants);

public sealed record RoleRow(string Code, string Name, IReadOnlyList<string> Permissions);

public sealed record RegimeVersionInfo(Guid Id, string Regime, string RegimeName, string Version, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
    string? VerificationNote, DateTime? VerifiedAt, IReadOnlyList<LegalSourceInput> Sources);

/// <summary>User administration (system administrators only). Passwords are never logged; deactivation revokes every session.</summary>
public sealed partial class SetupService
{
    public async Task<IReadOnlyList<UserRow>> ListUsersAsync(Guid adminId)
    {
        await RequireSystemAdminAsync(adminId);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        var grants = await (from g in db.UserCompanyRoles
                            join c in db.Companies on g.CompanyId equals c.Id
                            join r in db.Roles on g.RoleId equals r.Id
                            select new { g.UserId, g.CompanyId, CompanyCode = c.Code, RoleCode = r.Code, RoleName = r.Name }).AsNoTracking().ToListAsync();
        var now = clock.UtcNow;
        return users.Select(u => new UserRow(u.Id, u.UserName, u.FullName, u.IsActive, u.IsSystemAdmin, u.LockedUntil > now ? u.LockedUntil : null,
            grants.Where(g => g.UserId == u.Id).OrderBy(g => g.CompanyCode).ThenBy(g => g.RoleCode)
                .Select(g => new UserGrant(g.CompanyId, g.CompanyCode, g.RoleCode, g.RoleName)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<(Guid Id, string Code, string Name)>> ListCompaniesAsync(Guid adminId)
    {
        await RequireSystemAdminAsync(adminId);
        return (await db.Companies.AsNoTracking().OrderBy(c => c.Code).Select(c => new { c.Id, c.Code, c.Name }).ToListAsync())
            .Select(c => (c.Id, c.Code, c.Name)).ToList();
    }

    public async Task<IReadOnlyList<RegimeVersionInfo>> ListRegimeVersionsAsync(Guid adminId)
    {
        await RequireSystemAdminAsync(adminId);
        var rows = await (from v in db.AccountingRegimeVersions join r in db.AccountingRegimes on v.RegimeId equals r.Id
                          orderby r.Code, v.EffectiveFrom
                          select new { v.Id, r.Code, r.Name, v.VersionCode, v.EffectiveFrom, v.EffectiveTo, v.VerificationStatus, v.VerificationNote, v.VerifiedAt }).AsNoTracking().ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        var sources = (await db.Set<LegalSource>().AsNoTracking().Where(s => ids.Contains(s.RegimeVersionId)).ToListAsync()).ToLookup(s => s.RegimeVersionId);
        return rows.Select(x => new RegimeVersionInfo(x.Id, x.Code, x.Name, x.VersionCode, x.EffectiveFrom, x.EffectiveTo, x.VerificationStatus.ToString(),
            x.VerificationNote, x.VerifiedAt, sources[x.Id].Select(s => new LegalSourceInput(s.RuleGroup, s.DocumentNo, s.IssuedDate, s.Article, s.Url)).ToList())).ToList();
    }

    /// <summary>
    /// Renames an account or takes it out of use. An account is deactivated only when its balance is zero and no account role
    /// points at it, so nothing the modules post to disappears silently; postings already made stay untouched.
    /// </summary>
    public async Task UpdateAccountAsync(SessionContext ctx, Guid accountId, string name, bool isActive)
    {
        ctx.Require(Permissions.CatalogManage);
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException(ErrorCodes.ValidationFailed, "Tên tài khoản bắt buộc.");
        await db.InTransactionAsync(async () =>
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
            if (acc.IsActive && !isActive)
            {
                var sums = await db.JournalLines.Where(l => l.AccountId == acc.Id).GroupBy(l => l.Side)
                    .Select(g => new { g.Key, Total = g.Sum(x => x.Amount) }).ToListAsync();
                var balance = sums.Sum(s => (int)s.Key * s.Total);
                if (balance != 0) throw new DomainException(ErrorCodes.ValidationFailed, $"TK {acc.Code} còn số dư {balance}; không ngừng sử dụng được.");
                var roles = await db.AccountRoleMappings.Where(m => m.CompanyId == ctx.CompanyId && m.AccountId == acc.Id).Select(m => m.RoleCode).ToListAsync();
                if (roles.Count > 0) throw new DomainException(ErrorCodes.ValidationFailed, $"TK {acc.Code} đang được ánh xạ vai trò {string.Join(", ", roles)}; đổi ánh xạ trước.");
                if (await db.Accounts.AnyAsync(a => a.ParentId == acc.Id && a.IsActive))
                    throw new DomainException(ErrorCodes.ValidationFailed, $"TK {acc.Code} còn tài khoản chi tiết đang dùng.");
            }
            var before = new { acc.Name, acc.IsActive };
            acc.Name = name.Trim();
            acc.IsActive = isActive;
            acc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "account.update", "account", acc.Id.ToString(), new { before, after = new { acc.Name, acc.IsActive } }, acc.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Sets the report tags of an account (DH, TDT, HCSD, LV, BDSDT, DGL, …) used by financial-statement formulas such as
    /// 131*[DH]. Only the presentation changes, never a posting.
    /// </summary>
    public async Task SetAccountTagsAsync(SessionContext ctx, Guid accountId, string? tags)
    {
        ctx.Require(Permissions.CatalogManage);
        var normalized = NormalizeTags(tags);
        await db.InTransactionAsync(async () =>
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
            if (acc.ReportTags == normalized) return;
            var before = acc.ReportTags;
            acc.ReportTags = normalized;
            acc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "account.tags", "account", acc.Id.ToString(), new { acc.Code, before, after = normalized }, acc.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>"dh, tdt" → "DH,TDT"; null when empty. Tags are A–Z, 0–9, _ (max 20 chars, 10 tags).</summary>
    public static string? NormalizeTags(string? tags)
    {
        var list = (tags ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        foreach (var t in list)
            if (t.Length > 20 || !t.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                throw new DomainException(ErrorCodes.ValidationFailed, $"Nhãn '{t}' không hợp lệ (chỉ A–Z, 0–9, _; tối đa 20 ký tự).");
        if (list.Count > 10) throw new DomainException(ErrorCodes.ValidationFailed, "Tối đa 10 nhãn cho một tài khoản.");
        return list.Count == 0 ? null : string.Join(",", list);
    }

    /// <summary>
    /// Moves an account under another summary account (or to the top level). Only the reporting hierarchy changes — postings
    /// stay on the account itself — so this is allowed at any time; loops and postable parents are refused.
    /// </summary>
    public async Task MoveAccountAsync(SessionContext ctx, Guid accountId, string? parentCode)
    {
        ctx.Require(Permissions.CatalogManage);
        await db.InTransactionAsync(async () =>
        {
            var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
            Guid? parentId = null;
            if (!string.IsNullOrWhiteSpace(parentCode))
            {
                var code = parentCode.Trim().ToUpperInvariant();
                var all = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Id);
                var parent = all.Values.FirstOrDefault(a => a.Code == code) ?? throw new DomainException(ErrorCodes.AccountNotFound, $"TK cấp trên {code} không tồn tại.");
                if (parent.IsPostable) throw new DomainException(ErrorCodes.ValidationFailed, $"TK {code} là TK chi tiết; chỉ TK tổng hợp mới làm cấp trên.");
                if (!parent.IsActive) throw new DomainException(ErrorCodes.ValidationFailed, $"TK {code} đã ngừng sử dụng.");
                for (Guid? p = parent.Id; p is { } id; p = all[id].ParentId)
                    if (id == acc.Id) throw new DomainException(ErrorCodes.ValidationFailed, $"Không đặt {acc.Code} dưới {code}: {code} đang là cấp dưới của {acc.Code} (vòng lặp).");
                parentId = parent.Id;
            }
            if (acc.ParentId == parentId) return;
            var before = acc.ParentId;
            acc.ParentId = parentId;
            acc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "account.move", "account", acc.Id.ToString(), new { acc.Code, before, after = parentId, parentCode }, acc.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    public async Task<IReadOnlyList<RoleRow>> ListRolesAsync(Guid adminId)
    {
        await RequireSystemAdminAsync(adminId);
        return (await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Code).ToListAsync())
            .Select(r => new RoleRow(r.Code, r.Name, r.Permissions.Select(p => p.Permission).OrderBy(p => p, StringComparer.Ordinal).ToList())).ToList();
    }

    public async Task RevokeRoleAsync(Guid adminId, Guid userId, Guid companyId, string roleCode, string reason)
    {
        await RequireSystemAdminAsync(adminId);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do thu hồi quyền.");
        await db.InTransactionAsync(async () =>
        {
            var grant = await (from g in db.UserCompanyRoles join r in db.Roles on g.RoleId equals r.Id
                               where g.UserId == userId && g.CompanyId == companyId && r.Code == roleCode select g).FirstOrDefaultAsync()
                        ?? throw new DomainException(ErrorCodes.NotFound, $"Người dùng không có vai trò {roleCode} tại công ty này.");
            if (roleCode == "ADMIN" && userId == adminId)
                throw new DomainException(ErrorCodes.ValidationFailed, "Không tự thu hồi vai trò quản trị của chính mình.");
            db.UserCompanyRoles.Remove(grant);
            await audit.AppendAsync(companyId, null, "role.revoke", "user", userId.ToString(), new { roleCode, reason }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Locks or unlocks an account. Locking revokes every open session at once.</summary>
    public async Task SetUserActiveAsync(Guid adminId, Guid userId, bool active, string reason)
    {
        await RequireSystemAdminAsync(adminId);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do.");
        if (!active && userId == adminId) throw new DomainException(ErrorCodes.ValidationFailed, "Không tự khóa tài khoản của chính mình.");
        await db.InTransactionAsync(async () =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId) ?? throw new DomainException(ErrorCodes.NotFound, "Người dùng không tồn tại.");
            if (!active && user.IsSystemAdmin && !await db.Users.AnyAsync(u => u.Id != userId && u.IsActive && u.IsSystemAdmin))
                throw new DomainException(ErrorCodes.ValidationFailed, "Không khóa quản trị hệ thống cuối cùng.");
            user.IsActive = active;
            if (active) { user.FailedLoginCount = 0; user.LockedUntil = null; }
            user.RowVersion++;
            var revoked = active ? 0 : await RevokeSessionsAsync(userId, null);
            await audit.AppendAsync(Guid.Empty, null, active ? "user.activate" : "user.deactivate", "user", userId.ToString(), new { reason, revokedSessions = revoked }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Sets a new password chosen by the administrator (to be changed by the user); clears the lockout and ends sessions.</summary>
    public async Task ResetPasswordAsync(Guid adminId, Guid userId, string newPassword)
    {
        await RequireSystemAdminAsync(adminId);
        RequirePasswordPolicy(newPassword);
        await db.InTransactionAsync(async () =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId) ?? throw new DomainException(ErrorCodes.NotFound, "Người dùng không tồn tại.");
            user.PasswordHash = PasswordHasher.Hash(newPassword, options.PasswordIterations);
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.RowVersion++;
            var revoked = await RevokeSessionsAsync(userId, null);
            await audit.AppendAsync(Guid.Empty, null, "user.password_reset", "user", userId.ToString(), new { revokedSessions = revoked }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Local recovery when no administrator can sign in: run on the machine that holds the database (command line
    /// <c>Hak.Api.exe --reset-admin-password &lt;user&gt;</c>), so it needs operating-system access, not an API session.
    /// Only an active system administrator can be recovered; the reset is audited and every session of that user ends.
    /// </summary>
    public async Task RecoverAdminPasswordAsync(string userName, string newPassword)
    {
        RequirePasswordPolicy(newPassword);
        var norm = userName.Trim().ToLowerInvariant();
        await db.InTransactionAsync(async () =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == norm)
                       ?? throw new DomainException(ErrorCodes.NotFound, $"Không có người dùng '{userName}'.");
            if (!user.IsSystemAdmin || !user.IsActive)
                throw new DomainException(ErrorCodes.Forbidden, "Chỉ khôi phục tại máy cho quản trị hệ thống đang hoạt động; người dùng khác do quản trị đặt lại.");
            user.PasswordHash = PasswordHasher.Hash(newPassword, options.PasswordIterations);
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.RowVersion++;
            var revoked = await RevokeSessionsAsync(user.Id, null);
            await audit.AppendAsync(Guid.Empty, null, "user.password_recover_local", "user", user.Id.ToString(),
                new { machine = Environment.MachineName, osUser = Environment.UserName, revokedSessions = revoked }, userIdOverride: user.Id);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>The user changes their own password; other sessions are ended, the current one stays.</summary>
    public async Task ChangeOwnPasswordAsync(Guid userId, Guid currentSessionId, string currentPassword, string newPassword)
    {
        RequirePasswordPolicy(newPassword);
        if (newPassword == currentPassword) throw new DomainException(ErrorCodes.ValidationFailed, "Mật khẩu mới phải khác mật khẩu hiện tại.");
        // A wrong current password counts like a failed login (committed, then reported): after MaxFailedLogins the account is
        // locked and every session ends, so a borrowed session cannot be used to guess the password.
        var failure = await db.InTransactionAsync(async () =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive) ?? throw new DomainException(ErrorCodes.Unauthenticated, "Tài khoản không hợp lệ.");
            if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            {
                user.FailedLoginCount++;
                var locked = user.FailedLoginCount >= options.MaxFailedLogins;
                if (locked)
                {
                    user.LockedUntil = clock.UtcNow.Add(options.LockoutDuration);
                    user.FailedLoginCount = 0;
                    var ended = await RevokeSessionsAsync(userId, null);
                    await audit.AppendAsync(Guid.Empty, null, "auth.lockout", "user", userId.ToString(), new { until = user.LockedUntil, via = "password_change", revokedSessions = ended }, result: "LOCKED");
                }
                user.RowVersion++;
                await audit.AppendAsync(Guid.Empty, null, "user.password_change", "user", userId.ToString(), null, result: "DENIED", userIdOverride: userId);
                await db.SaveChangesAsync();
                return locked
                    ? "Nhập sai mật khẩu hiện tại quá nhiều lần: tài khoản tạm khóa và mọi phiên làm việc đã kết thúc."
                    : "Mật khẩu hiện tại không đúng.";
            }
            user.PasswordHash = PasswordHasher.Hash(newPassword, options.PasswordIterations);
            user.FailedLoginCount = 0;
            user.RowVersion++;
            var revoked = await RevokeSessionsAsync(userId, currentSessionId);
            await audit.AppendAsync(Guid.Empty, null, "user.password_change", "user", userId.ToString(), new { revokedSessions = revoked }, userIdOverride: userId);
            await db.SaveChangesAsync();
            return (string?)null;
        });
        if (failure is not null) throw new DomainException(ErrorCodes.Unauthenticated, failure);
    }

    private static void RequirePasswordPolicy(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < PasswordHasher.MinPasswordLength)
            throw new DomainException(ErrorCodes.ValidationFailed, $"Mật khẩu phải có ít nhất {PasswordHasher.MinPasswordLength} ký tự.");
    }

    private async Task<int> RevokeSessionsAsync(Guid userId, Guid? keep)
    {
        var sessions = await db.LoginSessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != keep).ToListAsync();
        foreach (var s in sessions) s.RevokedAt = clock.UtcNow;
        return sessions.Count;
    }
}

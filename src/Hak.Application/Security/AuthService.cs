using System.Security.Cryptography;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Security;

public sealed record LoginResult(string Token, Guid SessionId, Guid UserId, DateTime ExpiresAt);

public sealed class AuthService(HakDbContext db, AuditService audit, IClock clock, HakRuntimeOptions options)
{
    public async Task<LoginResult> LoginAsync(string userName, string password, string? device)
    {
        var norm = userName.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == norm);
        if (user is { LockedUntil: { } until } && until > clock.UtcNow)
        {
            await db.InTransactionAsync(async () =>
            {
                await audit.AppendAsync(Guid.Empty, null, "auth.login", "user", user.Id.ToString(), new { device, lockedUntil = until }, result: "LOCKED");
                await db.SaveChangesAsync();
            });
            // Same answer as a wrong password while locked, so the lock does not reveal whether the password was right.
            throw new DomainException(ErrorCodes.Unauthenticated, $"Tài khoản tạm khóa do đăng nhập sai nhiều lần; thử lại sau {until.ToLocalTime():HH:mm} hoặc nhờ quản trị đặt lại mật khẩu.");
        }
        if (user is null || !user.IsActive || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            await db.InTransactionAsync(async () =>
            {
                if (user is not null)
                {
                    var tracked = await db.Users.FirstAsync(u => u.Id == user.Id);
                    tracked.FailedLoginCount++;
                    if (tracked.FailedLoginCount >= options.MaxFailedLogins)
                    {
                        tracked.LockedUntil = clock.UtcNow.Add(options.LockoutDuration);
                        tracked.FailedLoginCount = 0;
                        await audit.AppendAsync(Guid.Empty, null, "auth.lockout", "user", user.Id.ToString(), new { until = tracked.LockedUntil }, result: "LOCKED");
                    }
                    tracked.RowVersion++;
                }
                // An unknown name is not stored: people sometimes type their password into the user-name box.
                await audit.AppendAsync(Guid.Empty, null, "auth.login", "user", user?.Id.ToString() ?? "(tên không tồn tại)", new { device }, result: "DENIED");
                await db.SaveChangesAsync();
            });
            throw new DomainException(ErrorCodes.Unauthenticated, "Tên đăng nhập hoặc mật khẩu không đúng.");
        }
        if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.RowVersion++;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new LoginSession
        {
            Id = Ids.New(), UserId = user.Id, TokenHash = PasswordHasher.Sha256Hex(token), Device = device,
            CreatedAt = clock.UtcNow, ExpiresAt = clock.UtcNow.Add(options.SessionLifetime),
        };
        await db.InTransactionAsync(async () =>
        {
            db.LoginSessions.Add(session);
            await audit.AppendAsync(Guid.Empty, null, "auth.login", "user", user.Id.ToString(), new { device, session = session.Id }, userIdOverride: user.Id);
            await db.SaveChangesAsync();
        });
        return new LoginResult(token, session.Id, user.Id, session.ExpiresAt);
    }

    /// <summary>Validates the bearer token and returns the user (no company scope yet).</summary>
    public async Task<(User User, LoginSession Session)> AuthenticateAsync(string token)
    {
        var hash = PasswordHasher.Sha256Hex(token);
        var session = await db.LoginSessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == hash);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= clock.UtcNow)
            throw new DomainException(ErrorCodes.Unauthenticated, "Phiên làm việc không hợp lệ hoặc đã hết hạn.");
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == session.UserId);
        if (!user.IsActive) throw new DomainException(ErrorCodes.Unauthenticated, "Tài khoản đã bị khóa.");
        return (user, session);
    }

    /// <summary>Builds the company-scoped context. Users without a role in the company are refused.</summary>
    public async Task<SessionContext> ResolveContextAsync(Guid userId, Guid companyId, Guid? sessionId = null, string? device = null)
    {
        var perms = await (from ucr in db.UserCompanyRoles
                           join rp in db.RolePermissions on ucr.RoleId equals rp.RoleId
                           where ucr.UserId == userId && ucr.CompanyId == companyId
                           select rp.Permission).Distinct().ToListAsync();
        var hasAnyRole = await db.UserCompanyRoles.AnyAsync(x => x.UserId == userId && x.CompanyId == companyId);
        if (!hasAnyRole) throw new DomainException(ErrorCodes.Forbidden, "Người dùng chưa được cấp quyền tại công ty này.");
        return new SessionContext(userId, companyId, perms.ToHashSet(), sessionId, device);
    }

    public async Task LogoutAsync(Guid sessionId)
    {
        var s = await db.LoginSessions.FirstOrDefaultAsync(x => x.Id == sessionId);
        if (s is null || s.RevokedAt is not null) return;
        s.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync();
    }
}

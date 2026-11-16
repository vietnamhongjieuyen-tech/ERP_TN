using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Integration;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Exchange;

public sealed record ExchangeKeyRow(Guid Id, string SourceSystem, string KeyId, bool IsActive);

/// <summary>A newly issued key. The secret is shown once, to be configured in the sending system, and never returned again.</summary>
public sealed record IssuedExchangeKey(Guid Id, string SourceSystem, string KeyId, string SecretBase64);

/// <summary>
/// HMAC keys of external systems allowed to send JSON packages (journal vouchers, payroll). Issuing generates a random 256-bit
/// secret server-side; keys are deactivated, never deleted, so packages already received stay attributable.
/// </summary>
public sealed partial class ExchangeKeyService(HakDbContext db, AuditService audit)
{
    [GeneratedRegex("^[A-Z0-9_.-]{1,30}$")]
    private static partial Regex Code();

    public async Task<IssuedExchangeKey> IssueAsync(SessionContext ctx, string sourceSystem, string keyId)
    {
        ctx.Require(Permissions.ConfigManage);
        sourceSystem = sourceSystem.Trim().ToUpperInvariant();
        keyId = keyId.Trim();
        if (!Code().IsMatch(sourceSystem)) throw new DomainException(ErrorCodes.ValidationFailed, "Mã hệ thống nguồn chỉ gồm A-Z, 0-9, _ . - (tối đa 30 ký tự).");
        if (keyId.Length is 0 or > 50) throw new DomainException(ErrorCodes.ValidationFailed, "Mã khóa bắt buộc, tối đa 50 ký tự.");
        return await db.InTransactionAsync(async () =>
        {
            if (await db.ExchangePartnerKeys.AnyAsync(k => k.CompanyId == ctx.CompanyId && k.SourceSystem == sourceSystem && k.KeyId == keyId))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Khóa {sourceSystem}/{keyId} đã tồn tại; dùng mã khóa mới khi đổi khóa.");
            var secret = RandomNumberGenerator.GetBytes(32);
            var key = new ExchangePartnerKey
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, SourceSystem = sourceSystem, KeyId = keyId,
                SecretBase64 = SecretProtector.Protect(secret),
            };
            db.ExchangePartnerKeys.Add(key);
            await audit.AppendAsync(ctx.CompanyId, ctx, "exchange_key.issue", "exchange_key", key.Id.ToString(), new { sourceSystem, keyId });  // never the secret
            await db.SaveChangesAsync();
            return new IssuedExchangeKey(key.Id, sourceSystem, keyId, Convert.ToBase64String(secret));
        });
    }

    /// <summary>Start-up upgrade: wraps secrets stored as plain Base64 by earlier versions. Returns how many were wrapped.</summary>
    public async Task<int> ProtectStoredSecretsAsync()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        return await db.InTransactionAsync(async () =>
        {
            var plain = (await db.ExchangePartnerKeys.ToListAsync()).Where(k => !SecretProtector.IsProtected(k.SecretBase64)).ToList();
            foreach (var k in plain) k.SecretBase64 = SecretProtector.Protect(Convert.FromBase64String(k.SecretBase64));
            if (plain.Count > 0) await db.SaveChangesAsync();
            return plain.Count;
        });
    }

    public async Task<IReadOnlyList<ExchangeKeyRow>> ListAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.ConfigManage);
        return await db.ExchangePartnerKeys.AsNoTracking().Where(k => k.CompanyId == ctx.CompanyId)
            .OrderBy(k => k.SourceSystem).ThenBy(k => k.KeyId).Select(k => new ExchangeKeyRow(k.Id, k.SourceSystem, k.KeyId, k.IsActive)).ToListAsync();
    }

    public async Task DeactivateAsync(SessionContext ctx, Guid id, string reason)
    {
        ctx.Require(Permissions.ConfigManage);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do ngừng dùng khóa.");
        await db.InTransactionAsync(async () =>
        {
            var key = await db.ExchangePartnerKeys.FirstOrDefaultAsync(k => k.Id == id && k.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Khóa không tồn tại.");
            if (!key.IsActive) throw new DomainException(ErrorCodes.InvalidStateTransition, "Khóa đã ngừng dùng.");
            key.IsActive = false;
            await audit.AppendAsync(ctx.CompanyId, ctx, "exchange_key.deactivate", "exchange_key", key.Id.ToString(), new { key.SourceSystem, key.KeyId, reason });
            await db.SaveChangesAsync();
        });
    }
}

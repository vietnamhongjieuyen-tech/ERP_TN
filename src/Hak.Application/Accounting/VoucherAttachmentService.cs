using System.Security.Cryptography;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Accounting;

public sealed record AttachmentInfo(Guid Id, Guid VoucherId, string FileName, long Size, string Sha256, DateTime UploadedAt, Guid UploadedBy,
    bool IsRemoved, string? RemoveReason);

public sealed record AttachmentFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Source documents attached to vouchers. Files can be added at any stage (the paper often arrives after posting); a file is
/// never deleted — removing it needs a reason, keeps the bytes and is audited, so the evidence trail stays complete.
/// </summary>
public sealed class VoucherAttachmentService(HakDbContext db, AuditService audit, IClock clock)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxPerVoucher = 20;

    /// <summary>Accepted file types and the content type each is served with. Active content (html, svg, scripts) is not accepted.</summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["pdf"] = "application/pdf", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["png"] = "image/png",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ["xls"] = "application/octet-stream",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ["doc"] = "application/octet-stream",
        ["xml"] = "application/octet-stream", ["csv"] = "application/octet-stream", ["txt"] = "application/octet-stream", ["zip"] = "application/zip",
    };

    public async Task<IReadOnlyList<AttachmentInfo>> ListAsync(SessionContext ctx, Guid voucherId)
    {
        ctx.Require(Permissions.VoucherView);
        return await db.VoucherAttachments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.VoucherId == voucherId).OrderBy(a => a.UploadedAt)
            .Select(a => new AttachmentInfo(a.Id, a.VoucherId, a.FileName, a.Size, a.Sha256, a.UploadedAt, a.UploadedBy, a.RemovedAt != null, a.RemoveReason)).ToListAsync();
    }

    public async Task<AttachmentInfo> AddAsync(SessionContext ctx, Guid voucherId, string fileName, byte[] content)
    {
        ctx.Require(Permissions.VoucherCreate);
        // Only the name part, whatever path the browser or a script sent.
        fileName = Path.GetFileName((fileName ?? "").Replace('\\', '/').Trim());
        if (fileName.Length is 0 or > 200) throw new DomainException(ErrorCodes.ValidationFailed, "Tên tệp bắt buộc, tối đa 200 ký tự.");
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!Types.ContainsKey(ext))
            throw new DomainException(ErrorCodes.ValidationFailed, $"Loại tệp .{ext} không được đính kèm. Chấp nhận: {string.Join(", ", Types.Keys.Order())}.");
        if (content.Length == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Tệp rỗng.");
        if (content.Length > MaxBytes) throw new DomainException(ErrorCodes.ValidationFailed, $"Tệp lớn hơn {MaxBytes / 1024 / 1024} MB; nén hoặc quét ở độ phân giải thấp hơn.");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        return await db.InTransactionAsync(async () =>
        {
            var voucher = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == voucherId && v.CompanyId == ctx.CompanyId)
                          ?? throw new DomainException(ErrorCodes.NotFound, "Chứng từ không tồn tại.");
            var existing = await db.VoucherAttachments.Where(a => a.VoucherId == voucherId && a.RemovedAt == null).Select(a => new { a.Sha256, a.FileName }).ToListAsync();
            if (existing.Count >= MaxPerVoucher) throw new DomainException(ErrorCodes.ValidationFailed, $"Mỗi chứng từ tối đa {MaxPerVoucher} tệp đính kèm.");
            if (existing.FirstOrDefault(e => e.Sha256 == sha) is { } same)
                throw new DomainException(ErrorCodes.DuplicateCode, $"Tệp này đã được đính kèm (trùng nội dung với {same.FileName}).");
            var a = new VoucherAttachment
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, VoucherId = voucherId, FileName = fileName, Extension = ext, Size = content.Length, Sha256 = sha,
                Content = content, UploadedAt = clock.UtcNow, UploadedBy = ctx.UserId,
            };
            db.VoucherAttachments.Add(a);
            await audit.AppendAsync(ctx.CompanyId, ctx, "voucher.attach", "voucher", voucherId.ToString(), new { voucher.Number, attachment = a.Id, fileName, a.Size, sha256 = sha });
            await db.SaveChangesAsync();
            return new AttachmentInfo(a.Id, voucherId, fileName, a.Size, sha, a.UploadedAt, a.UploadedBy, false, null);
        });
    }

    /// <summary>The file itself; a removed attachment stays readable for whoever may view the audit trail.</summary>
    public async Task<AttachmentFile> DownloadAsync(SessionContext ctx, Guid attachmentId)
    {
        ctx.Require(Permissions.VoucherView);
        var a = await db.VoucherAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.CompanyId == ctx.CompanyId)
                ?? throw new DomainException(ErrorCodes.NotFound, "Tệp đính kèm không tồn tại.");
        if (a.RemovedAt is not null) ctx.Require(Permissions.AuditView);
        return new AttachmentFile(a.FileName, Types.GetValueOrDefault(a.Extension, "application/octet-stream"), a.Content);
    }

    public async Task RemoveAsync(SessionContext ctx, Guid attachmentId, string reason)
    {
        ctx.Require(Permissions.VoucherCreate);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do gỡ tệp đính kèm.");
        await db.InTransactionAsync(async () =>
        {
            var a = await db.VoucherAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Tệp đính kèm không tồn tại.");
            if (a.RemovedAt is not null) throw new DomainException(ErrorCodes.InvalidStateTransition, "Tệp đã được gỡ trước đó.");
            a.RemovedAt = clock.UtcNow;
            a.RemovedBy = ctx.UserId;
            a.RemoveReason = reason.Trim();
            await audit.AppendAsync(ctx.CompanyId, ctx, "voucher.detach", "voucher", a.VoucherId.ToString(), new { attachment = a.Id, a.FileName, reason = a.RemoveReason });
            await db.SaveChangesAsync();
        });
    }
}

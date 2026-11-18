using System.Security.Cryptography;
using System.Text;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Api;

/// <summary>
/// Idempotency-Key for POST / PUT under /api. The screens send one key per "Lưu" of a form and send the same key again when the
/// reply was lost (timeout, network). The first request runs; a repeat with the same body gets the stored reply (header
/// Idempotent-Replay: true) instead of creating a second voucher; the same key with a different body is refused; a repeat while
/// the first is still running is told to wait. The caller must still be signed in (and, with X-Company-Id, still have access
/// to that company) when repeating. Only successful replies are kept: a failed request changed nothing and may simply run again.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> log)
{
    public const string Header = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext http)
    {
        var key = http.Request.Headers[Header].ToString();
        if (key.Length == 0 || (!HttpMethods.IsPost(http.Request.Method) && !HttpMethods.IsPut(http.Request.Method))
            || !http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }
        if (key.Length > 100) { await RefuseAsync(http, 400, ErrorCodes.ValidationFailed, "Idempotency-Key dài quá 100 ký tự."); return; }

        Guid userId;
        Guid? companyId = null;
        try
        {
            var (user, _) = await http.AuthAsync();
            userId = user.Id;
            if (Guid.TryParse(http.Request.Headers["X-Company-Id"], out var co))
            {
                await http.SessionAsync();   // still has access to that company now
                companyId = co;
            }
        }
        catch (DomainException)
        {
            await next(http);   // not signed in / no access: the endpoint answers with the usual error
            return;
        }

        http.Request.EnableBuffering();
        string hash;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"{http.Request.Method} {http.Request.Path}{http.Request.QueryString} {companyId}\n"));
            var buffer = new byte[16384];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer)) > 0) sha.AppendData(buffer, 0, read);
            hash = Convert.ToHexString(sha.GetHashAndReset());
        }
        http.Request.Body.Position = 0;

        var db = http.RequestServices.GetRequiredService<HakDbContext>();
        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key);
        if (existing is not null)
        {
            await ReplayOrRefuseAsync(http, existing, hash);
            return;
        }

        var record = new IdempotencyRecord
        {
            Id = Guid.CreateVersion7(), UserId = userId, CompanyId = companyId, Key = key, Method = http.Request.Method,
            Path = http.Request.Path.ToString(), RequestHash = hash, CreatedAt = DateTime.UtcNow,
        };
        db.IdempotencyRecords.Add(record);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // The same key arrived twice at once: the other request owns it.
            db.ChangeTracker.Clear();
            var other = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key);
            if (other is null) throw;
            await ReplayOrRefuseAsync(http, other, hash);
            return;
        }
        db.ChangeTracker.Clear();

        var original = http.Response.Body;
        using var captured = new MemoryStream();
        http.Response.Body = captured;
        try
        {
            await next(http);
        }
        finally
        {
            http.Response.Body = original;
            var ok = http.Response.StatusCode is >= 200 and < 300;
            try
            {
                if (ok)
                    await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteUpdateAsync(u => u
                        .SetProperty(r => r.StatusCode, http.Response.StatusCode)
                        .SetProperty(r => r.ResponseBody, Encoding.UTF8.GetString(captured.ToArray()))
                        .SetProperty(r => r.CompletedAt, DateTime.UtcNow));
                else
                    await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteDeleteAsync();
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                log.LogWarning(ex, "Could not store the idempotent reply for key {Key}", key);
            }
            captured.Position = 0;
            await captured.CopyToAsync(original);
        }
    }

    private static Task ReplayOrRefuseAsync(HttpContext http, IdempotencyRecord r, string hash)
    {
        if (r.RequestHash != hash)
            return RefuseAsync(http, 422, ErrorCodes.ValidationFailed, "Khóa gửi lại (Idempotency-Key) đã dùng cho một yêu cầu khác; tải lại màn hình rồi lưu lại.");
        if (r.CompletedAt is null)
            return RefuseAsync(http, 409, ErrorCodes.ConcurrencyConflict, "Yêu cầu trước đang được xử lý; chờ vài giây rồi tải lại danh sách.");
        http.Response.StatusCode = r.StatusCode ?? 200;
        http.Response.Headers["Idempotent-Replay"] = "true";
        if (r.ResponseBody is null) return Task.CompletedTask;
        http.Response.ContentType = "application/json; charset=utf-8";
        return http.Response.WriteAsync(r.ResponseBody);
    }

    private static Task RefuseAsync(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { code, message });
    }

    /// <summary>Start-up clean-up: replies older than <paramref name="days"/> days are not needed for retries any more.</summary>
    public static Task<int> PurgeAsync(HakDbContext db, int days = 7)
    {
        var before = DateTime.UtcNow.AddDays(-days);
        return db.IdempotencyRecords.Where(r => r.CreatedAt < before).ExecuteDeleteAsync();
    }
}

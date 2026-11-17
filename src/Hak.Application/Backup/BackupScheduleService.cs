using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Data;
using Hak.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Backup;

/// <summary>What the administrator sees of the schedule (never the passphrase).</summary>
public sealed record BackupScheduleInfo(bool Enabled, int Hour, int Keep, string? CopyTo, bool HasPassphrase, string? ConfiguredBy,
    DateTime? LastRunAt, string? LastFile, string? LastError, string Directory);

/// <summary>
/// Sao lưu tự động theo lịch (SQLite, one-machine mode): once a day at or after the chosen hour, an encrypted backup named
/// hak-auto-*.hakbk is made, optionally copied to a second folder (USB, NAS), and automatic backups beyond the kept count are
/// deleted — manual backups are never touched. The passphrase is kept beside the backups wrapped with Windows DPAPI (machine
/// scope), so the schedule file alone does not decrypt anything elsewhere. The settings live in a file, not the database, so a
/// restored database does not bring back an old schedule.
/// </summary>
public sealed class BackupScheduleService(HakDbContext db, BackupService backups, AuditService audit, IClock clock, BackupOptions options)
{
    public const string AutoPrefix = "hak-auto";
    private const string FileName = "schedule.json";
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "HAK.BackupSchedule.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Settings
    {
        public bool Enabled { get; set; }
        public int Hour { get; set; } = 18;
        public int Keep { get; set; } = 14;
        public string? CopyTo { get; set; }
        public string? Passphrase { get; set; }
        public Guid ConfiguredBy { get; set; }
        public DateTime? LastRunAt { get; set; }
        public DateOnly? LastRunLocalDate { get; set; }
        public string? LastFile { get; set; }
        public string? LastError { get; set; }
    }

    private string SettingsPath => Path.Combine(options.Directory, FileName);

    public async Task<BackupScheduleInfo> GetAsync(Guid userId)
    {
        await RequireSystemAdminAsync(userId);
        return await InfoAsync(Load());
    }

    public async Task<BackupScheduleInfo> SetAsync(Guid userId, bool enabled, int hour, int keep, string? copyTo, string? passphrase)
    {
        await RequireSystemAdminAsync(userId);
        if (!db.Database.IsSqlite())
            throw new DomainException(ErrorCodes.NotSupported, "Chế độ PostgreSQL (LAN): đặt lịch pg_dump trên máy chủ cơ sở dữ liệu theo hướng dẫn vận hành.");
        if (hour is < 0 or > 23) throw new DomainException(ErrorCodes.ValidationFailed, "Giờ sao lưu phải từ 0 đến 23.");
        if (keep is < 1 or > 365) throw new DomainException(ErrorCodes.ValidationFailed, "Số bản tự động giữ lại phải từ 1 đến 365.");
        copyTo = string.IsNullOrWhiteSpace(copyTo) ? null : copyTo.Trim();
        if (copyTo is not null)
        {
            if (!Path.IsPathFullyQualified(copyTo)) throw new DomainException(ErrorCodes.ValidationFailed, "Thư mục chép thêm phải là đường dẫn đầy đủ (ví dụ E:\\SaoLuuHAK hoặc \\\\may-chu\\thu-muc).");
            if (!Directory.Exists(copyTo)) throw new DomainException(ErrorCodes.ValidationFailed, $"Không thấy thư mục {copyTo} (ổ USB / thư mục mạng phải đang kết nối).");
            if (Path.GetFullPath(copyTo).TrimEnd('\\', '/').Equals(Path.GetFullPath(options.Directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new DomainException(ErrorCodes.ValidationFailed, "Thư mục chép thêm phải khác thư mục sao lưu chính (nên ở ổ đĩa / máy khác).");
        }
        var s = Load();
        if (!string.IsNullOrEmpty(passphrase))
        {
            if (passphrase.Length < options.MinPassphraseLength)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Mật khẩu sao lưu phải có ít nhất {options.MinPassphraseLength} ký tự (lưu giữ riêng, mất mật khẩu là mất bản sao lưu).");
            s.Passphrase = Protect(passphrase);
        }
        if (enabled && s.Passphrase is null) throw new DomainException(ErrorCodes.ValidationFailed, "Nhập mật khẩu sao lưu cho lịch tự động.");
        s.Enabled = enabled; s.Hour = hour; s.Keep = keep; s.CopyTo = copyTo; s.ConfiguredBy = userId;
        Save(s);
        await db.InTransactionAsync(async () =>
        {
            await audit.AppendAsync(Guid.Empty, null, "backup.schedule", "backup", FileName,
                new { enabled, hour, keep, copyTo, passphraseChanged = !string.IsNullOrEmpty(passphrase) }, userIdOverride: userId);
            await db.SaveChangesAsync();
        });
        return await InfoAsync(s);
    }

    /// <summary>Runs the scheduled backup when it is due (called every few minutes by the server). Returns the file made, or null.</summary>
    public async Task<string?> RunDueAsync(DateTime localNow, bool force = false)
    {
        var s = Load();
        if (!s.Enabled || s.Passphrase is null) return null;
        var today = DateOnly.FromDateTime(localNow);
        if (!force && (localNow.Hour < s.Hour || s.LastRunLocalDate >= today)) return null;
        s.LastRunAt = clock.UtcNow; s.LastRunLocalDate = today;
        try
        {
            var info = await backups.CreateAsync(s.ConfiguredBy, Unprotect(s.Passphrase), "Sao lưu tự động", AutoPrefix);
            s.LastFile = info.FileName; s.LastError = null;
            if (s.CopyTo is not null)
            {
                try
                {
                    File.Copy(Path.Combine(options.Directory, info.FileName), Path.Combine(s.CopyTo, info.FileName), overwrite: false);
                    Prune(s.CopyTo, s.Keep);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    s.LastError = $"Đã sao lưu nhưng không chép được sang {s.CopyTo}: {ex.Message}";
                }
            }
            Prune(options.Directory, s.Keep);
            return info.FileName;
        }
        catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException or CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        {
            s.LastError = ex.Message;
            return null;
        }
        finally { Save(s); }
    }

    /// <summary>Administrator's "Chạy ngay": same as the scheduled run, at once.</summary>
    public async Task<BackupScheduleInfo> RunNowAsync(Guid userId)
    {
        await RequireSystemAdminAsync(userId);
        var s = Load();
        if (!s.Enabled) throw new DomainException(ErrorCodes.ValidationFailed, "Lịch sao lưu tự động đang tắt.");
        await RunDueAsync(DateTime.Now, force: true);
        return await InfoAsync(Load());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Keeps the newest <paramref name="keep"/> automatic backups in a folder (names sort by time).</summary>
    private static void Prune(string directory, int keep)
    {
        foreach (var old in Directory.GetFiles(directory, AutoPrefix + "-*.hakbk").OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Skip(keep))
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next run */ }
    }

    private Settings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private void Save(Settings s)
    {
        Directory.CreateDirectory(options.Directory);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    private async Task<BackupScheduleInfo> InfoAsync(Settings s)
    {
        var by = s.ConfiguredBy == Guid.Empty ? null : await db.Users.Where(u => u.Id == s.ConfiguredBy).Select(u => u.UserName).FirstOrDefaultAsync();
        return new BackupScheduleInfo(s.Enabled, s.Hour, s.Keep, s.CopyTo, s.Passphrase is not null, by, s.LastRunAt, s.LastFile, s.LastError, options.Directory);
    }

    private async Task RequireSystemAdminAsync(Guid userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.IsSystemAdmin))
            throw new DomainException(ErrorCodes.Forbidden, "Chỉ quản trị hệ thống được đặt lịch sao lưu (bản sao lưu chứa dữ liệu mọi công ty).");
    }

    private static string Protect(string passphrase)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase);
        return OperatingSystem.IsWindows()
            ? Prefix + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine))
            : Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
        if (!OperatingSystem.IsWindows()) throw new DomainException(ErrorCodes.NotSupported, "Mật khẩu lịch sao lưu được bảo vệ bằng Windows DPAPI; nhập lại mật khẩu.");
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine)); }
        catch (CryptographicException)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Không đọc được mật khẩu lịch sao lưu (thư mục đã chuyển sang máy khác): nhập lại mật khẩu ở Quản trị → Sao lưu.");
        }
    }
}

using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Data;
using Hak.Domain.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Backup;

public sealed class BackupOptions
{
    /// <summary>Folder holding encrypted backups and restored copies.</summary>
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAK", "backups");
    public int Iterations { get; set; } = BackupFile.DefaultIterations;
    public int MinPassphraseLength { get; set; } = 12;
}

public sealed record BackupInfo(string FileName, long Size, DateTime CreatedAt, long DatabaseBytes, string DatabaseSha256, string Note);

/// <summary>
/// One company in a checked backup: counts and posted totals in the backup, the same figures in the live database (null when
/// the company is not there), and the attachments whose stored SHA-256 no longer matches their content.
/// </summary>
public sealed record CompanyChainCheck(Guid CompanyId, string CompanyCode, int Vouchers, int JournalEntries, long? BrokenAuditSeq,
    decimal PostedDebit = 0, decimal PostedCredit = 0, int Attachments = 0, int AttachmentsDamaged = 0,
    int? LiveVouchers = null, int? LiveJournalEntries = null, decimal? LivePostedDebit = null)
{
    public bool Balanced => PostedDebit == PostedCredit;
}

/// <summary>Result of decrypting and checking a backup. RestoredPath is set only by a restore.</summary>
public sealed record BackupVerification(string FileName, DateTime CreatedAt, string Integrity, bool SystemAuditChainOk, IReadOnlyList<CompanyChainCheck> Companies,
    string? RestoredPath)
{
    public bool IsValid => Integrity == "ok" && SystemAuditChainOk && Companies.All(c => c.BrokenAuditSeq is null && c.Balanced && c.AttachmentsDamaged == 0);
}

/// <summary>
/// Sao lưu / khôi phục (SQLite, one-machine mode). A backup is an online snapshot of the whole database (all companies), so only
/// a system administrator may create, verify or restore one. Restore never touches the live database: the backup is decrypted
/// to a new file, checked (SHA-256, PRAGMA integrity_check, every company's audit hash chain) and left beside the backups for
/// the operator to switch Hak:DatabasePath to it. PostgreSQL (LAN) mode is backed up with pg_dump on the server [docs].
/// </summary>
public sealed class BackupService(HakDbContext db, AuditService audit, IClock clock, BackupOptions options)
{
    public async Task<BackupInfo> CreateAsync(Guid userId, string passphrase, string? note = null, string filePrefix = "hak-backup")
    {
        await RequireSystemAdminAsync(userId);
        RequireSqlite();
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < options.MinPassphraseLength)
            throw new DomainException(ErrorCodes.ValidationFailed, $"Mật khẩu sao lưu phải có ít nhất {options.MinPassphraseLength} ký tự (lưu giữ riêng, mất mật khẩu là mất bản sao lưu).");
        Directory.CreateDirectory(options.Directory);
        var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss");
        var snapshot = Path.Combine(options.Directory, $".snapshot-{Guid.NewGuid():N}.db");
        var fileName = $"{filePrefix}-{stamp}.hakbk";
        var target = Path.Combine(options.Directory, fileName);
        if (File.Exists(target)) throw new DomainException(ErrorCodes.DuplicateCode, "Đã có bản sao lưu cùng thời điểm; thử lại sau một giây.");
        try
        {
            // Online backup API: a consistent snapshot while other users keep working.
            await using (var source = new SqliteConnection(db.Database.GetConnectionString()))
            await using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
            {
                await source.OpenAsync();
                await dest.OpenAsync();
                source.BackupDatabase(dest);
            }
            BackupHeader header;
            await using (var input = File.OpenRead(snapshot))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                header = BackupFile.Encrypt(input, output, passphrase, "SQLite", note?.Trim() ?? "", clock.UtcNow, options.Iterations);
            var info = new BackupInfo(fileName, new FileInfo(target).Length, header.CreatedAt, header.PlaintextLength, header.PlaintextSha256, header.Note);
            await LogAsync(userId, "backup.create", fileName, new { info.Size, info.DatabaseBytes, info.DatabaseSha256, note });
            return info;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        finally { TryDelete(snapshot); }
    }

    public async Task<IReadOnlyList<BackupInfo>> ListAsync(Guid userId)
    {
        await RequireSystemAdminAsync(userId);
        if (!Directory.Exists(options.Directory)) return [];
        var list = new List<BackupInfo>();
        foreach (var path in Directory.GetFiles(options.Directory, "*.hakbk").OrderByDescending(p => p, StringComparer.Ordinal))
        {
            try
            {
                await using var s = File.OpenRead(path);
                var h = BackupFile.ReadHeader(s);
                list.Add(new BackupInfo(Path.GetFileName(path), s.Length, h.CreatedAt, h.PlaintextLength, h.PlaintextSha256, h.Note));
            }
            catch (DomainException) { /* not a HAK backup: skipped */ }
        }
        return list;
    }

    /// <summary>Decrypts and checks a backup. With <paramref name="restore"/> the checked copy is kept as restored-&lt;stamp&gt;.db.</summary>
    public async Task<BackupVerification> VerifyAsync(Guid userId, string fileName, string passphrase, bool restore = false)
    {
        await RequireSystemAdminAsync(userId);
        var path = BackupPath(fileName);
        var work = Path.Combine(options.Directory, $".verify-{Guid.NewGuid():N}.db");
        BackupVerification result;
        try
        {
            BackupHeader header;
            await using (var input = File.OpenRead(path))
            await using (var output = new FileStream(work, FileMode.CreateNew, FileAccess.Write))
                header = BackupFile.Decrypt(input, output, passphrase);

            string integrity;
            await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = work, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                integrity = (string)(await cmd.ExecuteScalarAsync())!;
            }
            var companies = new List<CompanyChainCheck>();
            bool systemOk;
            await using (var copy = new HakDbContext(HakDb.SqliteOptions(work)))
            {
                var chain = new AuditService(copy, clock);
                systemOk = await chain.VerifyChainAsync(Guid.Empty) is null;
                foreach (var co in await copy.Companies.AsNoTracking().OrderBy(x => x.Code).ToListAsync())
                {
                    var (debit, credit) = await PostedTotalsAsync(copy, co.Id);
                    // Attachments: the content must still hash to the SHA-256 recorded when it was uploaded.
                    int attachments = 0, damaged = 0;
                    foreach (var a in await copy.VoucherAttachments.AsNoTracking().Where(a => a.CompanyId == co.Id).Select(a => new { a.Sha256, a.Content }).ToListAsync())
                    {
                        attachments++;
                        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Content)), a.Sha256, StringComparison.OrdinalIgnoreCase)) damaged++;
                    }
                    var live = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == co.Id);
                    companies.Add(new CompanyChainCheck(co.Id, co.Code, await copy.Vouchers.CountAsync(v => v.CompanyId == co.Id),
                        await copy.JournalEntries.CountAsync(e => e.CompanyId == co.Id), await chain.VerifyChainAsync(co.Id),
                        debit, credit, attachments, damaged,
                        live ? await db.Vouchers.CountAsync(v => v.CompanyId == co.Id) : null,
                        live ? await db.JournalEntries.CountAsync(e => e.CompanyId == co.Id) : null,
                        live ? (await PostedTotalsAsync(db, co.Id)).Debit : null));
                }
            }
            SqliteConnection.ClearAllPools();
            string? restored = null;
            var ok = integrity == "ok" && systemOk && companies.All(c => c.BrokenAuditSeq is null);
            if (restore)
            {
                if (!ok) throw new DomainException(ErrorCodes.BackupInvalid, "Bản sao lưu không vượt qua kiểm tra toàn vẹn; không khôi phục.");
                restored = Path.Combine(options.Directory, $"restored-{clock.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(fileName)}.db");
                File.Move(work, restored);
            }
            result = new BackupVerification(fileName, header.CreatedAt, integrity, systemOk, companies, restored);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(work);
        }
        await LogAsync(userId, restore ? "backup.restore" : "backup.verify", fileName, new { result.IsValid, result.Integrity, result.RestoredPath });
        return result;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Σ debit and Σ credit of every journal line of a company (amounts summed here: SQLite stores money as integers).</summary>
    private static async Task<(decimal Debit, decimal Credit)> PostedTotalsAsync(HakDbContext ctx, Guid companyId)
    {
        var lines = await ctx.JournalLines.AsNoTracking().Where(l => l.CompanyId == companyId).Select(l => new { l.Side, l.Amount }).ToListAsync();
        return (lines.Where(l => l.Side == Hak.Domain.Vouchers.Side.Debit).Sum(l => l.Amount), lines.Where(l => l.Side == Hak.Domain.Vouchers.Side.Credit).Sum(l => l.Amount));
    }

    private string BackupPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".hakbk", StringComparison.Ordinal))
            throw new DomainException(ErrorCodes.ValidationFailed, "Tên file sao lưu không hợp lệ.");
        var path = Path.Combine(options.Directory, fileName);
        return File.Exists(path) ? path : throw new DomainException(ErrorCodes.NotFound, $"Không có file sao lưu {fileName}.");
    }

    private void RequireSqlite()
    {
        if (!db.Database.IsSqlite())
            throw new DomainException(ErrorCodes.NotSupported, "Chế độ PostgreSQL (LAN): sao lưu bằng pg_dump trên máy chủ cơ sở dữ liệu theo hướng dẫn vận hành.");
    }

    private async Task RequireSystemAdminAsync(Guid userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.IsSystemAdmin))
            throw new DomainException(ErrorCodes.Forbidden, "Chỉ quản trị hệ thống được sao lưu / khôi phục (bản sao lưu chứa dữ liệu mọi công ty).");
    }

    private Task LogAsync(Guid userId, string action, string fileName, object data)
        => db.InTransactionAsync(async () =>
        {
            await audit.AppendAsync(Guid.Empty, null, action, "backup", fileName, data, userIdOverride: userId);
            await db.SaveChangesAsync();
        });

    /// <summary>
    /// Start-up clean-up: a snapshot or verification copy is an unencrypted database; one left behind by a crash or power cut
    /// must not stay beside the backups. Nothing else is running at start-up, so every such file is stale. Returns the count removed.
    /// </summary>
    public static int DeleteStaleWorkFiles(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var pattern in new[] { ".snapshot-*.db*", ".verify-*.db*" })
            foreach (var path in Directory.GetFiles(directory, pattern))
            {
                TryDelete(path);
                if (!File.Exists(path)) removed++;
            }
        return removed;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; removed at the next start-up */ }
    }
}

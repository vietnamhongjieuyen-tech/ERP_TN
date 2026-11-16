using Hak.Application.Backup;
using Hak.Data;
using Hak.Domain.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hak.Integration.Tests;

/// <summary>Encrypted backup / verify / restore (SQLite mode). Low PBKDF2 iteration count is test-only.</summary>
public class BackupTests
{
    private const string Pass = "Mat-khau-sao-luu-2026";
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;

    [Fact]
    public void Container_RoundTrips_AndRejectsWrongPassphrase_Tampering_Truncation()
    {
        var data = new byte[3 * 1000 + 17];
        Random.Shared.NextBytes(data);
        using var enc = new MemoryStream();
        var header = BackupFile.Encrypt(new MemoryStream(data), enc, Pass, "SQLite", "test", DateTime.UtcNow, iterations: 1_000, chunkSize: 1_000);
        Assert.Equal(data.Length, header.PlaintextLength);
        var bytes = enc.ToArray();

        using var dec = new MemoryStream();
        BackupFile.Decrypt(new MemoryStream(bytes), dec, Pass);
        Assert.Equal(data, dec.ToArray());

        Assert.Equal(ErrorCodes.BackupInvalid, Assert.Throws<DomainException>(() => BackupFile.Decrypt(new MemoryStream(bytes), Stream.Null, "sai-mat-khau-123")).Code);
        var tampered = (byte[])bytes.Clone();
        tampered[^40] ^= 0x01;
        Assert.Equal(ErrorCodes.BackupInvalid, Assert.Throws<DomainException>(() => BackupFile.Decrypt(new MemoryStream(tampered), Stream.Null, Pass)).Code);
        var truncated = bytes[..(bytes.Length - 1_100)];   // drops the last chunk
        Assert.Equal(ErrorCodes.BackupInvalid, Assert.Throws<DomainException>(() => BackupFile.Decrypt(new MemoryStream(truncated), Stream.Null, Pass)).Code);
        var otherHeader = (byte[])bytes.Clone();
        var noteAt = System.Text.Encoding.UTF8.GetString(otherHeader).IndexOf("\"test\"", StringComparison.Ordinal);
        otherHeader[noteAt + 1] = (byte)'T';                // header edits break every chunk's associated data
        Assert.Equal(ErrorCodes.BackupInvalid, Assert.Throws<DomainException>(() => BackupFile.Decrypt(new MemoryStream(otherHeader), Stream.Null, Pass)).Code);
    }

    [Fact]
    public async Task Backup_Verify_Restore_OnlySystemAdmin_NeverTouchesLiveDatabase()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var dir = Path.Combine(TestHost.Root, $"backups-{Guid.NewGuid():N}");
        var svc = new BackupService(s.Db, s.Audit, h.Clock, new BackupOptions { Directory = dir, Iterations = 1_000 });
        if (h.IsPostgres)
        {
            Assert.Equal(ErrorCodes.NotSupported, await CodeOf(() => svc.CreateAsync(h.AdminId, Pass)));
            return;
        }
        await h.PostedAsync(s, h.Sale(1_250_000));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => svc.CreateAsync(h.ApproverId, Pass)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => svc.CreateAsync(h.AdminId, "ngan")));

        var backup = await svc.CreateAsync(h.AdminId, Pass, "Trước khi khóa sổ quý 1");
        Assert.Equal(backup.FileName, Assert.Single(await svc.ListAsync(h.AdminId)).FileName);

        var check = await svc.VerifyAsync(h.AdminId, backup.FileName, Pass);
        Assert.True(check.IsValid);
        Assert.Equal((1, 1), (check.Companies.Single().Vouchers, check.Companies.Single().JournalEntries));
        Assert.Null(check.RestoredPath);
        Assert.Equal(ErrorCodes.BackupInvalid, await CodeOf(() => svc.VerifyAsync(h.AdminId, backup.FileName, "mat-khau-sai-123")));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => svc.VerifyAsync(h.AdminId, "..\\hak.db", Pass)));

        // Work continues after the backup; restoring gives the state at backup time in a separate file.
        await h.PostedAsync(s, h.Sale(2_000_000));
        var restored = await svc.VerifyAsync(h.AdminId, backup.FileName, Pass, restore: true);
        Assert.NotNull(restored.RestoredPath);
        Assert.NotEqual(Path.GetFullPath(h.DbPath), Path.GetFullPath(restored.RestoredPath!));
        await using (var copy = new HakDbContext(HakDb.SqliteOptions(restored.RestoredPath!)))
            Assert.Equal(1, await copy.JournalEntries.CountAsync());
        SqliteConnection.ClearAllPools();
        Assert.Equal(2, await s.Db.JournalEntries.CountAsync());
        Assert.Contains(await s.Db.AuditLogs.Where(a => a.CompanyId == Guid.Empty).Select(a => a.Action).ToListAsync(), a => a == "backup.restore");
        Assert.Null(await s.Audit.VerifyChainAsync(Guid.Empty));
        Assert.DoesNotContain(Pass, string.Join("|", await s.Db.AuditLogs.Select(a => a.DataJson ?? "").ToListAsync()));

        // An interrupted backup leaves an unencrypted work copy; start-up removes those and nothing else.
        File.WriteAllText(Path.Combine(dir, ".snapshot-abc.db"), "x");
        File.WriteAllText(Path.Combine(dir, ".verify-abc.db"), "x");
        Assert.Equal(2, BackupService.DeleteStaleWorkFiles(dir));
        Assert.Equal(0, BackupService.DeleteStaleWorkFiles(dir));
        Assert.True(File.Exists(Path.Combine(dir, backup.FileName)) && File.Exists(restored.RestoredPath!));
    }
}

using Hak.Application.Backup;
using Hak.Domain.Common;

namespace Hak.Integration.Tests;

/// <summary>Daily automatic backup: due once a day from the chosen hour, kept count, second folder, manual backups untouched.</summary>
public class BackupScheduleTests
{
    private const string Pass = "Mat-khau-lich-2026";

    [Fact]
    public async Task Schedule_RunsOncePerDay_PrunesOnlyAutomaticCopies_AndCopiesToSecondFolder()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var dir = Path.Combine(TestHost.Root, $"backups-{Guid.NewGuid():N}");
        var copy = Path.Combine(TestHost.Root, $"usb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        var options = new BackupOptions { Directory = dir, Iterations = 1_000 };
        var backups = new BackupService(s.Db, s.Audit, h.Clock, options);
        var svc = new BackupScheduleService(s.Db, backups, s.Audit, h.Clock, options);
        if (h.IsPostgres)
        {
            Assert.Equal(ErrorCodes.NotSupported, (await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(h.AdminId, true, 18, 2, null, Pass))).Code);
            return;
        }
        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<DomainException>(() => svc.GetAsync(h.ApproverId))).Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(h.AdminId, true, 18, 2, null, null))).Code);   // no passphrase yet
        Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(h.AdminId, true, 25, 2, null, Pass))).Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(h.AdminId, true, 18, 2, "khong-day-du", Pass))).Code);
        Assert.Equal(ErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<DomainException>(() => svc.SetAsync(h.AdminId, true, 18, 2, dir, Pass))).Code);

        var manual = await backups.CreateAsync(h.AdminId, "Mat-khau-tay-2026", "tay");
        var info = await svc.SetAsync(h.AdminId, true, 18, 2, copy, Pass);
        Assert.True(info.Enabled && info.HasPassphrase);
        Assert.DoesNotContain(Pass, File.ReadAllText(Path.Combine(dir, "schedule.json")), StringComparison.Ordinal);

        var day = new DateTime(2026, 3, 15);
        Assert.Null(await svc.RunDueAsync(day.AddHours(17)));                 // before the hour
        var files = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            h.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);
            var f = await svc.RunDueAsync(day.AddDays(i).AddHours(18).AddMinutes(5));
            Assert.NotNull(f);
            Assert.StartsWith("hak-auto-", f);
            files.Add(f!);
            Assert.Null(await svc.RunDueAsync(day.AddDays(i).AddHours(23)));   // once a day
        }
        // Two automatic copies kept in each folder; the manual backup stays.
        var kept = Directory.GetFiles(dir, "*.hakbk").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { files[1], files[2], manual.FileName }.Order(), kept);
        Assert.Equal(new[] { files[1], files[2] }.Order(), Directory.GetFiles(copy, "*.hakbk").Select(Path.GetFileName).Order());
        // The automatic backup opens with the scheduled passphrase.
        Assert.True((await backups.VerifyAsync(h.AdminId, files[2], Pass)).IsValid);

        var after = await svc.GetAsync(h.AdminId);
        Assert.Equal(files[2], after.LastFile);
        Assert.Null(after.LastError);

        // Turning it off keeps the passphrase; runs stop.
        await svc.SetAsync(h.AdminId, false, 18, 2, null, null);
        Assert.Null(await svc.RunDueAsync(day.AddDays(10).AddHours(19)));
    }
}

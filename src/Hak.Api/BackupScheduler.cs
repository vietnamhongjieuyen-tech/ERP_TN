using Hak.Application.Backup;

namespace Hak.Api;

/// <summary>Checks every five minutes whether the daily automatic backup is due (Quản trị → Sao lưu → Lịch tự động).</summary>
public sealed class BackupScheduler(IServiceScopeFactory scopes, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var file = await scope.ServiceProvider.GetRequiredService<BackupScheduleService>().RunDueAsync(DateTime.Now);
                if (file is not null) log.LogInformation("Automatic backup {File} created.", file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Automatic backup check failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

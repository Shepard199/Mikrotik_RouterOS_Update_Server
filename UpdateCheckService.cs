using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public class UpdateCheckService(
    IUpdateOrchestrator updateOrchestrator,
    ScheduleService scheduleService,
    ILogger<UpdateCheckService> logger,
    IConfiguration config)
    : BackgroundService
{
    private DateTime _lastScheduledCheck = DateTime.MinValue;
    private PeriodicTimer? _timer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Update check service starting...");

        // Получаем интервал из конфигурации (по умолчанию 10 минут)
        var intervalMinutes = config.GetValue("UpdateCheckIntervalMinutes", 10);
        logger.LogInformation("Update check interval: {Interval} minutes", intervalMinutes);

        // Запускаем первоначальную проверку
        var result = await updateOrchestrator.CheckAndDownloadUpdatesAsync();
        logger.LogInformation("Initial update check completed. Downloaded {Count} files", result.Downloaded);

        _timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
                // Проверяем расписание
                if (scheduleService.ShouldRunNow() && _lastScheduledCheck.Date < DateTime.Today)
                {
                    logger.LogInformation("Scheduled update check triggered");
                    var checkResult = await updateOrchestrator.CheckAndDownloadUpdatesAsync();
                    _lastScheduledCheck = DateTime.Now;

                    if (checkResult.Downloaded > 0)
                        logger.LogInformation("Scheduled update check downloaded {Count} files",
                            checkResult.Downloaded);
                    else
                        logger.LogInformation("Scheduled update check completed - no new files");
                }
                else
                {
                    // Обычная периодическая проверка (можно сделать менее частой)
                    var checkResult = await updateOrchestrator.CheckAndDownloadUpdatesAsync();
                    if (checkResult.Downloaded > 0)
                        logger.LogInformation("Background update check downloaded {Count} files",
                            checkResult.Downloaded);
                }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Update check service cancelled");
        }
        finally
        {
            _timer?.Dispose();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Update check service stopping...");
        _timer?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
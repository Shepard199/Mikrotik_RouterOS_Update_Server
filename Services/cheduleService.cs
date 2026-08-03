using System.Text.Json;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

public class ScheduleService
{
    private readonly string _configPath;
    private readonly ILogger<ScheduleService> _logger;
    private ScheduleConfig _config;

    public ScheduleService(ILogger<ScheduleService> logger)
    {
        _logger = logger;
        _configPath = Path.Combine(AppContext.BaseDirectory, "schedule.json");
        _config = LoadConfig();
    }

    public ScheduleConfig GetConfig()
    {
        return _config;
    }

    public ScheduleStatus GetStatus()
    {
        var nextCheck = CalculateNextCheckTime();
        return new ScheduleStatus
        {
            Config = _config,
            NextScheduledCheck = nextCheck
        };
    }

    public async Task UpdateConfigAsync(ScheduleConfig newConfig)
    {
        try
        {
            _config = newConfig;
            await SaveConfigAsync();
            _logger.LogInformation("Schedule configuration updated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating schedule configuration");
            throw;
        }
    }

    public async Task PauseAsync(TimeSpan duration)
    {
        _config.PausedUntil = DateTime.Now.Add(duration);
        await SaveConfigAsync();
        _logger.LogInformation("Updates paused until {PausedUntil}", _config.PausedUntil);
    }

    public async Task ResumeAsync()
    {
        _config.PausedUntil = null;
        await SaveConfigAsync();
        _logger.LogInformation("Updates resumed");
    }

    public bool ShouldRunNow()
    {
        if (!_config.Enabled) return false;
        if (_config.PausedUntil.HasValue && _config.PausedUntil > DateTime.Now) return false;

        var now = DateTime.Now;
        var today = now.Date;
        var scheduledTime = today.Add(_config.CheckTime);

        // Проверяем, наступило ли время для сегодняшнего дня
        if (now >= scheduledTime && now < scheduledTime.AddMinutes(5)) // 5-минутное окно
        {
            // Проверяем день недели
            var todayName = now.DayOfWeek.ToString();
            return _config.DaysOfWeek.Contains(todayName);
        }

        return false;
    }

    private DateTime CalculateNextCheckTime()
    {
        if (!_config.Enabled)
            return DateTime.MaxValue;

        if (_config.PausedUntil.HasValue && _config.PausedUntil > DateTime.Now)
            return _config.PausedUntil.Value;

        var now = DateTime.Now;
        var today = now.Date;
        var scheduledTime = today.Add(_config.CheckTime);

        // Если сегодня еще не наступило время проверки
        if (now < scheduledTime)
        {
            var todayName = now.DayOfWeek.ToString();
            if (_config.DaysOfWeek.Contains(todayName))
                return scheduledTime;
        }

        // Ищем следующий подходящий день
        for (var i = 1; i <= 7; i++)
        {
            var nextDay = today.AddDays(i);
            var nextDayName = nextDay.DayOfWeek.ToString();

            if (_config.DaysOfWeek.Contains(nextDayName)) return nextDay.Add(_config.CheckTime);
        }

        return DateTime.MaxValue;
    }

    private ScheduleConfig LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                var defaultConfig = new ScheduleConfig();
                SaveConfigAsync().Wait();
                return defaultConfig;
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<ScheduleConfig>(json) ?? new ScheduleConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading schedule configuration, using defaults");
            return new ScheduleConfig();
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving schedule configuration");
            throw;
        }
    }
}
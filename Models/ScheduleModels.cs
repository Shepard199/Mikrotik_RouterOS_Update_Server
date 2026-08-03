namespace MikroTik.UpdateServer.Models;

public class ScheduleConfig
{
    public bool Enabled { get; init; } = true;

    public string[] DaysOfWeek { get; init; } =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    ];

    public TimeSpan CheckTime { get; init; } = new(2, 0, 0); // 2:00 AM
    public DateTime? PausedUntil { get; set; }
    public int IntervalMinutes { get; init; } = 60;
    public bool NotifyOnCompletion { get; init; } = true;
    public bool NotifyOnError { get; init; } = true;
}

public class ScheduleStatus
{
    public ScheduleConfig Config { get; init; } = new();
    public DateTime NextScheduledCheck { get; init; }
    public bool IsPaused => Config.PausedUntil.HasValue && Config.PausedUntil > DateTime.Now;
    public TimeSpan TimeUntilNextCheck => NextScheduledCheck - DateTime.Now;
    public string Status => IsPaused ? "Paused" : Config.Enabled ? "Active" : "Disabled";
}
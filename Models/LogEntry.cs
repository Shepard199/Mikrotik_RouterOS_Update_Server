namespace MikroTik.UpdateServer.Models;

public class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Level { get; init; } = "Information"; // Information / Warning / Error / Debug
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
}
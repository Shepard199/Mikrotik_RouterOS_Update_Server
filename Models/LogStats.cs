namespace MikroTik.UpdateServer.Models;

public class LogStats
{
    public int TotalEntries { get; set; }
    public int InfoCount { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }

    public DateTime? OldestEntry { get; set; }
    public DateTime? NewestEntry { get; set; }
}
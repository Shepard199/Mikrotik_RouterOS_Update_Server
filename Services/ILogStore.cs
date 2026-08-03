using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

public interface ILogStore
{
    void Add(LogEntry entry);
    IReadOnlyList<LogEntry> Query(string? level, string? search, int take);
    LogStats GetStats();
    byte[] ExportAsZip();
}
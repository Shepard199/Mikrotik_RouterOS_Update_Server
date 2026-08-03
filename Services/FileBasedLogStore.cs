using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using MikroTik.UpdateServer.Models;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Хранилище логов с поддержкой ротации файлов и ограничением памяти
/// </summary>
public class FileBasedLogStore : ILogStore, IAsyncDisposable
{
    private const string LogFilePrefix = "app-";
    private const string LogFileExtension = ".jsonl"; // JSON Lines format
    private const int DefaultMaxMemoryEntries = 5000;
    private const int DefaultRetentionDays = 7;
    private readonly string _logDirectory;
    private readonly ILogger<FileBasedLogStore> _logger;
    private readonly int _logRetentionDays;
    private readonly int _maxMemoryEntries;
    private readonly ConcurrentQueue<LogEntry> _memoryBuffer = new();
    private readonly Lock _rotateLock = new();

    public FileBasedLogStore(ILogger<FileBasedLogStore> logger, IConfiguration config)
    {
        _logger = logger;
        var baseDir = AppContext.BaseDirectory;
        _logDirectory = Path.Combine(baseDir, "logs");
        _maxMemoryEntries = config.GetValue("Logging:MaxMemoryEntries", DefaultMaxMemoryEntries);
        _logRetentionDays = config.GetValue("Logging:RetentionDays", DefaultRetentionDays);

        Directory.CreateDirectory(_logDirectory);
        _logger.LogInformation("FileBasedLogStore initialized. Directory: {LogDirectory}", _logDirectory);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushToDiskAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during FileBasedLogStore disposal");
        }
    }

    public void Add(LogEntry entry)
    {
        _memoryBuffer.Enqueue(entry);

        // Если буфер переполнен - сохраняем на диск
        if (_memoryBuffer.Count > _maxMemoryEntries) FlushToDiskAsync().GetAwaiter().GetResult();
    }

    public IReadOnlyList<LogEntry> Query(string? level, string? search, int take)
    {
        if (take <= 0) take = 100;
        if (take > 1000) take = 1000;

        // Сначала ищем в памяти
        var query = _memoryBuffer.Reverse();

        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(e =>
                string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLowerInvariant();
            query = query.Where(e =>
                e.Source.ToLowerInvariant().Contains(s) ||
                e.Message.ToLowerInvariant().Contains(s) ||
                (e.Exception != null && e.Exception.ToLowerInvariant().Contains(s)));
        }

        var result = query.Take(take).ToList();

        // Если мало результатов - добавляем из файлов
        if (result.Count < take)
        {
            var diskEntries = LoadEntriesFromDisk(level, search, take - result.Count);
            result.AddRange(diskEntries);
        }

        return result.OrderByDescending(x => x.Timestamp).Take(take).ToArray();
    }

    public LogStats GetStats()
    {
        var memorySnapshot = _memoryBuffer.ToArray();

        // Читаем статистику из файлов
        var diskStats = GetDiskStats();

        var totalEntries = memorySnapshot.Length + diskStats.TotalEntries;
        var oldestEntry = memorySnapshot.Length > 0
            ? memorySnapshot.Min(e => e.Timestamp)
            : diskStats.OldestEntry;
        var newestEntry = memorySnapshot.Length > 0
            ? memorySnapshot.Max(e => e.Timestamp)
            : diskStats.NewestEntry;

        return new LogStats
        {
            TotalEntries = totalEntries,
            InfoCount = memorySnapshot.Count(e =>
                string.Equals(e.Level, "Information", StringComparison.OrdinalIgnoreCase)) + diskStats.InfoCount,
            WarningCount = memorySnapshot.Count(e =>
                string.Equals(e.Level, "Warning", StringComparison.OrdinalIgnoreCase)) + diskStats.WarningCount,
            ErrorCount = memorySnapshot.Count(e =>
                string.Equals(e.Level, "Error", StringComparison.OrdinalIgnoreCase)) + diskStats.ErrorCount,
            OldestEntry = oldestEntry,
            NewestEntry = newestEntry
        };
    }

    public byte[] ExportAsZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // Экспортируем логи из памяти
            var memoryEntry = zip.CreateEntry(
                $"logs-memory-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl",
                CompressionLevel.Optimal);

            using (var writer = new StreamWriter(memoryEntry.Open()))
            {
                foreach (var log in _memoryBuffer.OrderBy(e => e.Timestamp))
                {
                    var json = JsonSerializer.Serialize(log);
                    writer.WriteLine(json);
                }
            }

            // Экспортируем логи с диска
            var logFiles = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}*{LogFileExtension}");
            foreach (var file in logFiles.OrderByDescending(x => x))
            {
                var fileName = Path.GetFileName(file);
                var zipEntry = zip.CreateEntry($"logs/{fileName}", CompressionLevel.Optimal);

                using (var fileStream = File.OpenRead(file))
                using (var zipStream = zipEntry.Open())
                {
                    fileStream.CopyTo(zipStream);
                }
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     Сохраняет логи из памяти на диск с ротацией
    /// </summary>
    private async Task FlushToDiskAsync()
    {
        if (_memoryBuffer.IsEmpty)
            return;

        try
        {
            lock (_rotateLock)
            {
                var todayFile = GetTodayLogFilePath();
                var entries = new List<LogEntry>();

                // Вытягиваем все логи из памяти
                while (_memoryBuffer.TryDequeue(out var entry) && entries.Count < _maxMemoryEntries) entries.Add(entry);

                if (entries.Count == 0)
                    return;

                // Записываем в JSONL формат (одна запись на строку)
                using (var writer = new StreamWriter(todayFile, true))
                {
                    foreach (var entry in entries)
                    {
                        var json = JsonSerializer.Serialize(entry);
                        writer.WriteLine(json);
                    }
                }

                _logger.LogDebug("Flushed {Count} log entries to disk", entries.Count);
            }

            // Асинхронно очищаем старые файлы
            await CleanupOldLogsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing logs to disk");
        }
    }

    /// <summary>
    ///     Удаляет логи старше установленного периода
    /// </summary>
    private async Task CleanupOldLogsAsync()
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-_logRetentionDays);
            var logFiles = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}*{LogFileExtension}");

            foreach (var file in logFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    File.Delete(file);
                    _logger.LogInformation("Deleted old log file: {File}", file);
                }
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during log cleanup");
        }
    }

    /// <summary>
    ///     Получает путь к файлу логов на сегодня
    /// </summary>
    private string GetTodayLogFilePath()
    {
        var fileName = $"{LogFilePrefix}{DateTime.Now:yyyy-MM-dd}{LogFileExtension}";
        return Path.Combine(_logDirectory, fileName);
    }

    /// <summary>
    ///     Загружает логи из файлов на диске
    /// </summary>
    private List<LogEntry> LoadEntriesFromDisk(string? level, string? search, int take)
    {
        var result = new List<LogEntry>();

        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}*{LogFileExtension}")
                .OrderByDescending(x => x);

            foreach (var file in logFiles)
            {
                if (result.Count >= take)
                    break;

                try
                {
                    using (var reader = new StreamReader(file))
                    {
                        while (reader.ReadLine() is { } line && result.Count < take)
                            try
                            {
                                var entry = JsonSerializer.Deserialize<LogEntry>(line);
                                if (entry == null) continue;

                                // Применяем фильтры
                                if (!string.IsNullOrWhiteSpace(level) &&
                                    !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (!string.IsNullOrWhiteSpace(search))
                                {
                                    var s = search.ToLowerInvariant();
                                    if (!entry.Source.ToLowerInvariant().Contains(s) &&
                                        !entry.Message.ToLowerInvariant().Contains(s) &&
                                        (entry.Exception == null || !entry.Exception.ToLowerInvariant().Contains(s)))
                                        continue;
                                }

                                result.Add(entry);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Skipping malformed log entry in {File}", file);
                            }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading log file: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading entries from disk");
        }

        return result;
    }

    /// <summary>
    ///     Получает статистику из файлов логов
    /// </summary>
    private LogStats GetDiskStats()
    {
        var stats = new LogStats();

        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, $"{LogFilePrefix}*{LogFileExtension}");

            foreach (var file in logFiles)
                try
                {
                    using (var reader = new StreamReader(file))
                    {
                        while (reader.ReadLine() is { } line)
                            try
                            {
                                var entry = JsonSerializer.Deserialize<LogEntry>(line);
                                if (entry == null) continue;

                                stats.TotalEntries++;

                                if (string.Equals(entry.Level, "Information", StringComparison.OrdinalIgnoreCase))
                                    stats.InfoCount++;
                                else if (string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase))
                                    stats.WarningCount++;
                                else if (string.Equals(entry.Level, "Error", StringComparison.OrdinalIgnoreCase))
                                    stats.ErrorCount++;

                                if (stats.OldestEntry == null || entry.Timestamp < stats.OldestEntry)
                                    stats.OldestEntry = entry.Timestamp;
                                if (stats.NewestEntry == null || entry.Timestamp > stats.NewestEntry)
                                    stats.NewestEntry = entry.Timestamp;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Skipping malformed log entry in {File}", file);
                            }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading stats from file: {File}", file);
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting disk stats");
        }

        return stats;
    }
}

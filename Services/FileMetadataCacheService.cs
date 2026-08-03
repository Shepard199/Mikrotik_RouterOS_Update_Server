using System.Collections.Concurrent;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Кэш для метаданных файлов с поддержкой TTL
/// </summary>
public class FileMetadataCache
{
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;

    public override string ToString()
    {
        return
            $"CreatedAt={CreatedAt:O}; ExpiresAt={ExpiresAt:O}; Size={Size}; LastModified={LastModified:O}; IsExpired={IsExpired}";
    }
}

/// <summary>
///     Сервис кэширования метаданных файлов
/// </summary>
public interface IFileMetadataCacheService
{
    void Set(string filePath, long size, DateTime lastModified, int ttlSeconds = 60);
    void Clear();
}

public class FileMetadataCacheService : IFileMetadataCacheService, IDisposable
{
    private readonly ConcurrentDictionary<string, FileMetadataCache> _cache = new();
    private readonly Timer? _cleanupTimer;
    private readonly int _defaultTtlSeconds;
    private readonly ILogger<FileMetadataCacheService> _logger;

    public FileMetadataCacheService(ILogger<FileMetadataCacheService> logger, IConfiguration config)
    {
        _logger = logger;
        _defaultTtlSeconds = config.GetValue("Cache:DefaultTtlSeconds", 60);

        // Запускаем периодическую очистку кэша каждые 5 минут
        _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _logger.LogInformation("FileMetadataCacheService initialized with TTL: {TtlSeconds}s", _defaultTtlSeconds);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    public void Set(string filePath, long size, DateTime lastModified, int ttlSeconds = -1)
    {
        if (ttlSeconds < 0)
            ttlSeconds = _defaultTtlSeconds;

        var cache = new FileMetadataCache
        {
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(ttlSeconds),
            Size = size,
            LastModified = lastModified
        };

        _cache.AddOrUpdate(filePath, cache, (_, _) => cache);
    }

    public void Clear()
    {
        _cache.Clear();
        _logger.LogInformation("File metadata cache cleared");
    }

    private void CleanupExpiredEntries(object? state)
    {
        try
        {
            var expiredKeys = _cache
                .Where(x => x.Value.IsExpired)
                .Select(x => x.Key)
                .ToList();

            foreach (var key in expiredKeys) _cache.TryRemove(key, out _);

            if (expiredKeys.Count > 0) _logger.LogDebug("Cleaned {Count} expired cache entries", expiredKeys.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }
    }
}

/// <summary>
///     Сервис для оптимизированного расчета размера папки
/// </summary>
public interface IDiskUsageService
{
    Task<DiskUsageInfo> GetDiskUsageAsync(string path);
}

public class DiskUsageInfo
{
    public long TotalBytes { get; set; }
    public string TotalMB => (TotalBytes / 1024.0 / 1024.0).ToString("F2");
    public string TotalGB => (TotalBytes / 1024.0 / 1024.0 / 1024.0).ToString("F2");
    public int FileCount { get; set; }
    public DateTime CalculatedAt { get; init; }

    public override string ToString()
    {
        return $"TotalBytes={TotalBytes}; FileCount={FileCount}; CalculatedAt={CalculatedAt:O}";
    }
}

public class OptimizedDiskUsageService(
    IFileMetadataCacheService cacheService,
    ILogger<OptimizedDiskUsageService> logger)
    : IDiskUsageService
{
    public async Task<DiskUsageInfo> GetDiskUsageAsync(string path)
    {
        return await Task.Run(() => GetDiskUsageSync(path));
    }

    private DiskUsageInfo GetDiskUsageSync(string path)
    {
        var info = new DiskUsageInfo {CalculatedAt = DateTime.UtcNow};

        if (!Directory.Exists(path))
        {
            logger.LogWarning("Directory does not exist: {Path}", path);
            return info;
        }

        try
        {
            var dir = new DirectoryInfo(path);
            var files = dir.EnumerateFiles("*", SearchOption.AllDirectories);

            foreach (var file in files)
                try
                {
                    info.TotalBytes += file.Length;
                    info.FileCount++;

                    // Кэшируем метаданные файла (TTL = 60 сек)
                    cacheService.Set(file.FullName, file.Length, file.LastWriteTimeUtc);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error accessing file: {File}", file.FullName);
                    // Пропускаем недоступные файлы
                }

            logger.LogDebug(
                "Disk usage calculated: {TotalMB}MB ({TotalGB}GB) in {FileCount} files",
                info.TotalMB,
                info.TotalGB,
                info.FileCount);
            return info;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calculating disk usage for {Path}", path);
            return info;
        }
    }
}
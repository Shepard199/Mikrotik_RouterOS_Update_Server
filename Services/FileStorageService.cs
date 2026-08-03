namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Сервис управления хранилищем файлов обновлений
/// </summary>
public class FileStorageService : IFileStorageService
{
    private readonly string _baseFolder;
    private readonly IDiskUsageService _diskUsageService;

    public FileStorageService(
        ILogger<FileStorageService> logger,
        IDiskUsageService diskUsageService)
    {
        _diskUsageService = diskUsageService;
        _baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");

        Directory.CreateDirectory(_baseFolder);
        logger.LogInformation("FileStorageService initialized. Base folder: {BaseFolder}", _baseFolder);
    }

    public async Task<long> GetTotalSizeAsync()
    {
        var usage = await _diskUsageService.GetDiskUsageAsync(_baseFolder);
        return usage.TotalBytes;
    }

    public async Task<int> GetFileCountAsync()
    {
        var usage = await _diskUsageService.GetDiskUsageAsync(_baseFolder);
        return usage.FileCount;
    }
}
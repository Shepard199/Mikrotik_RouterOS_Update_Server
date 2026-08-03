using System.Text.RegularExpressions;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Сервис управления версиями RouterOS
///     Загружает и управляет доступными версиями
/// </summary>
public class VersionManagementService : IVersionManagementService
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly string _baseFolder;
    private readonly IFileMetadataCacheService _cacheService;
    private readonly ILogger<VersionManagementService> _logger;

    public VersionManagementService(
        ILogger<VersionManagementService> logger,
        IFileMetadataCacheService cacheService)
    {
        _logger = logger;
        // storageService parameter kept for backwards compatibility
        _cacheService = cacheService;
        _baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");
        Directory.CreateDirectory(_baseFolder);
    }

    public async Task<RouterOSVersion[]> GetAvailableVersionsAsync()
    {
        try
        {
            var versions = new Dictionary<string, RouterOSVersion>(StringComparer.OrdinalIgnoreCase);

            var patternFiles = Directory.GetFiles(_baseFolder, "LATEST.*")
                .Concat(Directory.GetFiles(_baseFolder, "NEWEST*.*"))
                .ToArray();

            _logger.LogDebug("Found {Count} version marker files", patternFiles.Length);

            foreach (var patternFile in patternFiles)
                try
                {
                    var fileName = Path.GetFileName(patternFile);
                    var content = await File.ReadAllTextAsync(patternFile);
                    var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        var parts = line.Split(';');
                        if (parts.Length < 2)
                            continue;

                        var version = parts[0].Trim();
                        var archsStr = parts[1].Trim();
                        var architectures = string.IsNullOrEmpty(archsStr)
                            ? []
                            : archsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

                        var branch = ExtractBranchFromFileName(fileName);

                        if (!versions.TryGetValue(version, out var existing))
                        {
                            versions[version] = new RouterOSVersion
                            {
                                Version = version,
                                Branch = branch,
                                Architecture = string.Join(",", architectures),
                                Files = GetFilesForVersion(version, architectures),
                                Released = GetVersionReleaseDate(version)
                            };
                        }
                        else
                        {
                            var existingArches =
                                existing.Architecture.Split(',', StringSplitOptions.RemoveEmptyEntries);

                            var allArches = existingArches
                                .Union(architectures)
                                .Distinct()
                                .ToArray();

                            existing.Architecture = string.Join(",", allArches);
                            existing.Files = GetFilesForVersion(version, allArches);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing version file: {File}", patternFile);
                }

            var result = versions.Values.ToArray();
            _logger.LogInformation("Loaded {Count} available versions", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available versions");
            return [];
        }
    }

    public async Task DeleteVersionAsync(string version)
    {
        await _semaphore.WaitAsync();
        try
        {
            _logger.LogInformation("Deleting version: {Version}", version);

            var searchPattern = $"{version}*";
            var filesToDelete = Directory.GetFiles(_baseFolder, searchPattern);

            foreach (var file in filesToDelete)
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted file: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete file: {File}", file);
                }

            _cacheService.Clear();
            _logger.LogInformation("Completed deletion of version: {Version}", version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting version: {Version}", version);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ExtractBranchFromFileName(string fileName)
    {
        // LATEST.6 -> stable v6
        // LATEST.7 -> stable v7
        // NEWEST6.stable -> stable v6
        // NEWEST6.testing -> testing v6
        // NEWEST7.release-candidate -> release-candidate v7
        // NEWESTa6.development -> development v6

        if (fileName.StartsWith("LATEST", StringComparison.OrdinalIgnoreCase))
            return "stable";

        var match = Regex.Match(fileName, @"NEWEST[a6]*\.(.+)$",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "unknown";
    }

    private string[] GetFilesForVersion(string version, string[] architectures)
    {
        var files = new List<string>();

        try
        {
            foreach (var arch in architectures)
            {
                var archPattern = $"{version}-{arch.Trim()}*";
                var archFiles = Directory.GetFiles(_baseFolder, archPattern);
                files.AddRange(archFiles.Select(Path.GetFileName)!);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting files for version: {Version}", version);
        }

        return files.Distinct().ToArray();
    }

    private DateTime GetVersionReleaseDate(string version)
    {
        // Пытаемся определить дату выпуска из названия версии
        // Обычно версии в RouterOS выглядят как: 6.48.1, 7.1rc1 и т.д.
        // Мы используем время последнего изменения одного из файлов версии

        try
        {
            var versionFiles = Directory.GetFiles(_baseFolder, $"{version}*");
            if (versionFiles.Length > 0)
            {
                var latestFile = versionFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
                return File.GetLastWriteTimeUtc(latestFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not determine release date for version: {Version}", version);
        }

        return DateTime.UtcNow;
    }
}
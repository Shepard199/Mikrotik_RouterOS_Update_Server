using System.Text.Json.Serialization;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Модель информации о версии RouterOS
/// </summary>
public class RouterOSVersion
{
    [JsonPropertyName("version")] public string Version { get; init; } = "";

    [JsonPropertyName("branch")] public string Branch { get; init; } = "";

    [JsonPropertyName("arch")] public string Architecture { get; set; } = "";

    [JsonPropertyName("files")] public string[] Files { get; set; } = [];

    [JsonPropertyName("released")] public DateTime Released { get; init; }
}

/// <summary>
///     Результат проверки и загрузки обновлений
/// </summary>
public class UpdateCheckResult
{
    public int Downloaded { get; set; }
    public int Total { get; set; }
    public string[] CheckedVersions { get; set; } = [];
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public override string ToString()
    {
        return
            $"Downloaded={Downloaded}; Total={Total}; CheckedVersions={CheckedVersions.Length}; Success={Success}; Timestamp={Timestamp:O}; Error={Error}";
    }
}

/// <summary>
///     Интерфейс управления версиями RouterOS
/// </summary>
public interface IVersionManagementService
{
    Task<RouterOSVersion[]> GetAvailableVersionsAsync();
    Task DeleteVersionAsync(string version);
}

/// <summary>
///     Интерфейс загрузки файлов обновлений
/// </summary>
public interface IFileDownloadService
{
    Task<DownloadResult> DownloadFileAsync(string url, string outputPath);
}

/// <summary>
///     Интерфейс управления хранилищем файлов
/// </summary>
public interface IFileStorageService
{
    Task<long> GetTotalSizeAsync();
    Task<int> GetFileCountAsync();
}

/// <summary>
///     Интерфейс управления метаданными
/// </summary>
public interface IMetadataService
{
    Task<DateTime> GetLastCheckTimeAsync();
    Task SetLastCheckTimeAsync();
    Task<string[]> GetAllowedArchitecturesAsync();
    Task SetAllowedArchitecturesAsync(string[] arches);
}

/// <summary>
///     Интерфейс проверки доступности
/// </summary>
public interface IConnectivityService
{
    Task<bool> CheckMikroTikConnectivityAsync();
    Task<bool> CheckInternetConnectivityAsync();
}

/// <summary>
///     Главный оркестратор обновлений
/// </summary>
public interface IUpdateOrchestrator
{
    Task<UpdateCheckResult> CheckAndDownloadUpdatesAsync(string checkType = "stable");
    Task<object> GetVersionsInfoAsync();
    Task<object> GetStatusInfoAsync();
    Task<bool> SetActiveVersionAsync(string version);
    Task<bool> RemoveVersionAsync(string version, string? branch = null);
    Task<List<VersionLog>> GetVersionHistoryAsync(int take = 50);
    Task<string?> GetGlobalChangelogContentAsync();
    Task<string?> GetChangelogContentAsync(string version);
    Task<string?> GetGlobalChangelogPathAsync();
    Task<string?> GetChangelogPathAsync(string version);
    Task<string?> GetPackagesCsvPathAsync(string branchVersion);
    Task<string?> GetFilePathAsync(string path);
    Task<string?> GetFilePathAsync(string version, string filename);
    Task<string?> EnsureFileDownloadedAsync(string version, string filename);
    string? GetPointerFileContent(string filename);
    Task<object> GetPointerRoutingAsync();
    Task<(bool success, string? error)> SetPointerBranchRouteAsync(string pointer, string branch);
    Task<string[]> GetAllowedArchesAsync();
    Task UpdateAllowedArchesAsync(string[] arches);
}

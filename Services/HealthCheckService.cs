namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Результат проверки здоровья компонента
/// </summary>
public class HealthCheckResult
{
    public string Component { get; init; } = string.Empty;
    public HealthStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CheckTime { get; init; }
    public long? ResponseTime { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();

    public override string ToString()
    {
        return
            $"Component={Component}; Status={Status}; CheckTime={CheckTime:O}; ResponseTime={ResponseTime}; Message={Message}";
    }
}

/// <summary>
///     Статусы здоровья компонента
/// </summary>
public enum HealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2
}

/// <summary>
///     Интерфейс для проверки здоровья системы
/// </summary>
public interface IHealthCheckService
{
    Task<HealthCheckResult> CheckMikroTikConnectivityAsync();
    Task<HealthCheckResult> CheckDiskSpaceAsync();
    Task<HealthCheckResult> CheckFileSystemAsync();
    Task<HealthCheckResult> CheckDownloadServiceAsync();
    Task<HealthCheckResult[]> RunAllChecksAsync();
    HealthStatus GetOverallStatus(HealthCheckResult[] results);
}

/// <summary>
///     Сервис проверки здоровья приложения
///     Мониторит критические компоненты: connectivity, disk, files
/// </summary>
public class HealthCheckService(
    IConnectivityService connectivityService,
    IOptimizedDownloadService downloadService,
    ILogger<HealthCheckService> logger)
    : IHealthCheckService
{
    public async Task<HealthCheckResult> CheckMikroTikConnectivityAsync()
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var isConnected = await connectivityService.CheckMikroTikConnectivityAsync();
            var responseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new HealthCheckResult
            {
                Component = "MikroTik Connectivity",
                Status = isConnected ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                Message = isConnected ? "Connected to MikroTik source" : "Cannot reach MikroTik source",
                CheckTime = DateTime.UtcNow,
                ResponseTime = responseTime,
                Details = new Dictionary<string, object>
                {
                    {"timestamp", DateTime.UtcNow.ToString("O")},
                    {"response_time_ms", responseTime}
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MikroTik connectivity check failed");
            return new HealthCheckResult
            {
                Component = "MikroTik Connectivity",
                Status = HealthStatus.Unhealthy,
                Message = $"Check failed: {ex.Message}",
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = new Dictionary<string, object>
                {
                    {"error", ex.Message}
                }
            };
        }
    }

    public Task<HealthCheckResult> CheckDiskSpaceAsync()
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");

            if (!Directory.Exists(baseFolder))
                return Task.FromResult(new HealthCheckResult
                {
                    Component = "Disk Space",
                    Status = HealthStatus.Unhealthy,
                    Message = "Base folder does not exist",
                    CheckTime = DateTime.UtcNow,
                    ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                    Details = new Dictionary<string, object> {{"path", baseFolder}}
                });

            var drive = DriveInfo.GetDrives()
                .FirstOrDefault(d => baseFolder.StartsWith(d.Name));

            if (drive == null)
                return Task.FromResult(new HealthCheckResult
                {
                    Component = "Disk Space",
                    Status = HealthStatus.Unhealthy,
                    Message = "Could not determine disk information",
                    CheckTime = DateTime.UtcNow,
                    ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds
                });

            var totalBytes = drive.TotalSize;
            var availableBytes = drive.AvailableFreeSpace;
            var usedBytes = totalBytes - availableBytes;
            var usagePercent = (double) usedBytes / totalBytes * 100;

            // Thresholds
            var status = usagePercent > 90 ? HealthStatus.Unhealthy :
                usagePercent > 80 ? HealthStatus.Degraded :
                HealthStatus.Healthy;

            var message = usagePercent > 90 ? "Critical disk space" :
                usagePercent > 80 ? "High disk usage" :
                "Disk space OK";

            return Task.FromResult(new HealthCheckResult
            {
                Component = "Disk Space",
                Status = status,
                Message = $"{message} ({usagePercent:F1}% used)",
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = new Dictionary<string, object>
                {
                    {"total_gb", Math.Round(totalBytes / 1e9, 2)},
                    {"used_gb", Math.Round(usedBytes / 1e9, 2)},
                    {"available_gb", Math.Round(availableBytes / 1e9, 2)},
                    {"usage_percent", Math.Round(usagePercent, 1)},
                    {"drive", drive.Name}
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Disk space check failed");
            return Task.FromResult(new HealthCheckResult
            {
                Component = "Disk Space",
                Status = HealthStatus.Unhealthy,
                Message = $"Check failed: {ex.Message}",
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = new Dictionary<string, object> {{"error", ex.Message}}
            });
        }
    }

    public async Task<HealthCheckResult> CheckFileSystemAsync()
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");
            var logsFolder = Path.Combine(AppContext.BaseDirectory, "logs");

            var issues = new List<string>();
            var details = new Dictionary<string, object>();

            // Check base folder
            if (!Directory.Exists(baseFolder))
            {
                issues.Add("Base folder does not exist");
            }
            else
            {
                var fileCount = Directory.GetFiles(baseFolder).Length;
                var totalSize = new DirectoryInfo(baseFolder)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);

                details["base_folder_file_count"] = fileCount;
                details["base_folder_total_size_mb"] = Math.Round(totalSize / 1e6, 2);
            }

            // Check logs folder
            if (!Directory.Exists(logsFolder))
            {
                issues.Add("Logs folder does not exist");
            }
            else
            {
                var logFiles = Directory.GetFiles(logsFolder).Length;
                var logSize = Directory.GetFiles(logsFolder).Sum(f => new FileInfo(f).Length);

                details["log_file_count"] = logFiles;
                details["log_total_size_mb"] = Math.Round(logSize / 1e6, 2);

                if (logSize > 1e9) // > 1GB
                    issues.Add("Log folder exceeds 1GB");
            }

            // Check write permissions
            try
            {
                var testFile = Path.Combine(baseFolder, ".health-check-test");
                await File.WriteAllTextAsync(testFile, "test");
                File.Delete(testFile);
                details["write_permission"] = "OK";
            }
            catch
            {
                issues.Add("No write permission in base folder");
            }

            var status = issues.Count > 0 ? HealthStatus.Unhealthy : HealthStatus.Healthy;
            var message = issues.Count > 0
                ? string.Join("; ", issues)
                : "File system OK";

            return new HealthCheckResult
            {
                Component = "File System",
                Status = status,
                Message = message,
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = details
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "File system check failed");
            return new HealthCheckResult
            {
                Component = "File System",
                Status = HealthStatus.Unhealthy,
                Message = $"Check failed: {ex.Message}",
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = new Dictionary<string, object> {{"error", ex.Message}}
            };
        }
    }

    public async Task<HealthCheckResult> CheckDownloadServiceAsync()
    {
        var startTime = DateTime.UtcNow;
        try
        {
            // Try a simple HEAD request to verify service is responsive
            var testUrl = "https://www.google.com";

            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // This will test the download service's HTTP client
                await downloadService.DownloadFileAsync(
                    testUrl,
                    Path.Combine(Path.GetTempPath(), ".health-check-test"),
                    cts.Token);

                // Clean up test file
                try
                {
                    File.Delete(Path.Combine(Path.GetTempPath(), ".health-check-test"));
                }
                catch
                {
                    // ignored
                }

                return new HealthCheckResult
                {
                    Component = "Download Service",
                    Status = HealthStatus.Healthy,
                    Message = "Download service operational",
                    CheckTime = DateTime.UtcNow,
                    ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                    Details = new Dictionary<string, object>
                    {
                        {"response_time_ms", (DateTime.UtcNow - startTime).TotalMilliseconds}
                    }
                };
            }
            catch (OperationCanceledException)
            {
                return new HealthCheckResult
                {
                    Component = "Download Service",
                    Status = HealthStatus.Unhealthy,
                    Message = "Download service timeout",
                    CheckTime = DateTime.UtcNow,
                    ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download service check failed");
            return new HealthCheckResult
            {
                Component = "Download Service",
                Status = HealthStatus.Unhealthy,
                Message = $"Check failed: {ex.Message}",
                CheckTime = DateTime.UtcNow,
                ResponseTime = (long) (DateTime.UtcNow - startTime).TotalMilliseconds,
                Details = new Dictionary<string, object> {{"error", ex.Message}}
            };
        }
    }

    public async Task<HealthCheckResult[]> RunAllChecksAsync()
    {
        logger.LogInformation("Starting comprehensive health check");

        var checks = new[]
        {
            CheckMikroTikConnectivityAsync(),
            CheckDiskSpaceAsync(),
            CheckFileSystemAsync(),
            CheckDownloadServiceAsync()
        };

        var results = await Task.WhenAll(checks);

        var overallStatus = GetOverallStatus(results);
        logger.LogInformation(
            "Health check completed: {Status} ({HealthyCount}/{TotalCount})",
            overallStatus,
            results.Count(r => r.Status == HealthStatus.Healthy),
            results.Length);

        return results;
    }

    public HealthStatus GetOverallStatus(HealthCheckResult[] results)
    {
        if (results.Any(r => r.Status == HealthStatus.Unhealthy))
            return HealthStatus.Unhealthy;

        if (results.Any(r => r.Status == HealthStatus.Degraded))
            return HealthStatus.Degraded;

        return HealthStatus.Healthy;
    }
}
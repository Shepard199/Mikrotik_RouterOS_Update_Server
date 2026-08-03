using System.Net;
using Polly;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Результат загрузки файла
/// </summary>
public class DownloadResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long BytesDownloaded { get; set; }
    public TimeSpan Duration { get; set; }
    public int AttemptCount { get; set; }

    public override string ToString()
    {
        return
            $"Success={Success}; BytesDownloaded={BytesDownloaded}; Duration={Duration}; AttemptCount={AttemptCount}; Error={Error}";
    }
}

/// <summary>
///     Конфигурация для оптимизированной загрузки файлов
/// </summary>
public class HttpClientDownloadOptions
{
    public int Timeout { get; set; } = 300; // 5 минут
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
    public int ChunkSize { get; set; } = 1048576; // 1MB
    public long MaxFileSizeMB { get; set; } = 500;
}

/// <summary>
///     Оптимизированный сервис загрузки файлов с поддержкой Polly
/// </summary>
public interface IOptimizedDownloadService
{
    Task<DownloadResult> DownloadFileAsync(string url, string outputPath, CancellationToken ct = default);
}

public class OptimizedDownloadService : IOptimizedDownloadService, IFileDownloadService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OptimizedDownloadService> _logger;
    private readonly HttpClientDownloadOptions _options;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;

    public OptimizedDownloadService(
        ILogger<OptimizedDownloadService> logger,
        IConfiguration config)
    {
        _logger = logger;
        _options = new HttpClientDownloadOptions();

        // Читаем конфигурацию
        config.GetSection("HttpClientDownloadOptions").Bind(_options);

        // Создаем HttpClient с оптимальными настройками
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 4
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_options.Timeout)
        };

        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "MikroTik-ROS-UpdateServer/1.0 (+https://github.com)");

        // Настраиваем Polly политики - retry с exponential backoff
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                _options.MaxRetries,
                attempt =>
                {
                    var delayMs = _options.RetryDelayMs * (int) Math.Pow(2, attempt - 1);
                    return TimeSpan.FromMilliseconds(delayMs);
                },
                (outcome, timespan, retryCount, _) =>
                {
                    _logger.LogWarning(
                        "Retry #{RetryCount} after {DelayMs}ms. Error: {Error}",
                        retryCount,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
                });

        _logger.LogInformation(
            "OptimizedDownloadService initialized with MaxRetries={MaxRetries}, ChunkSize={ChunkSize}",
            _options.MaxRetries,
            _options.ChunkSize);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // IFileDownloadService implementation
    async Task<DownloadResult> IFileDownloadService.DownloadFileAsync(string url, string outputPath)
    {
        return await DownloadFileAsync(url, outputPath);
    }

    public async Task<DownloadResult> DownloadFileAsync(string url, string outputPath, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var result = new DownloadResult();
        var attemptCount = 0;

        try
        {
            // Удаляем существующий файл
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Используем retry политику
            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                attemptCount++;
                result.AttemptCount = attemptCount;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                return resp;
            });

            using (response)
            {
                // Проверяем размер файла
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    var fileSizeBytes = response.Content.Headers.ContentLength.Value;
                    var fileSizeMB = fileSizeBytes / 1024.0 / 1024.0;

                    if (fileSizeMB > _options.MaxFileSizeMB)
                        throw new InvalidOperationException(
                            $"File size {fileSizeMB:F2}MB exceeds maximum allowed {_options.MaxFileSizeMB}MB");
                }

                // Скачиваем чанками
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fileStream =
                             new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[_options.ChunkSize];
                    int bytesRead;
                    long totalBytes = 0;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) != 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                        totalBytes += bytesRead;
                        result.BytesDownloaded = totalBytes;
                    }
                }
            }

            result.Success = true;
        }
        catch (InvalidOperationException ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Invalid operation downloading {Url}", url);
        }
        catch (HttpRequestException ex)
        {
            result.Success = false;
            result.Error = $"HTTP error: {ex.Message}";
            _logger.LogError(ex, "HTTP error downloading {Url}", url);
        }
        catch (TaskCanceledException ex)
        {
            result.Success = false;
            result.Error = $"Download timeout: {ex.Message}";
            _logger.LogError(ex, "Timeout downloading {Url}", url);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = $"Unexpected error: {ex.Message}";
            _logger.LogError(ex, "Unexpected error downloading {Url}", url);

            // Удаляем частично загруженный файл
            if (File.Exists(outputPath))
                try
                {
                    File.Delete(outputPath);
                }
                catch
                {
                    /* ignore */
                }
        }

        result.Duration = DateTime.UtcNow - startTime;

        _logger.LogInformation(
            "Download completed: {Success}, {BytesMB:F2}MB in {Duration:F2}s, Attempts: {Attempts}",
            result.Success,
            result.BytesDownloaded / 1024.0 / 1024.0,
            result.Duration.TotalSeconds,
            result.AttemptCount);

        return result;
    }
}
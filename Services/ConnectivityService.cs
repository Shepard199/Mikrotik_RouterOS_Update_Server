namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Сервис проверки доступности сервисов
/// </summary>
public class ConnectivityService : IConnectivityService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ConnectivityService> _logger;

    public ConnectivityService(
        ILogger<ConnectivityService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "MikroTik-ROS-UpdateServer/1.0 (+https://github.com)");
    }

    public async Task<bool> CheckMikroTikConnectivityAsync()
    {
        try
        {
            _logger.LogInformation("Checking connectivity to upgrade.mikrotik.com...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request =
                new HttpRequestMessage(HttpMethod.Head, "https://upgrade.mikrotik.com/routeros/LATEST.6");
            using var response = await _httpClient.SendAsync(request, cts.Token);

            var isConnected = response.IsSuccessStatusCode;
            _logger.LogInformation("MikroTik server connectivity: {Status}", isConnected ? "✓ OK" : "✗ FAILED");
            return isConnected;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("MikroTik server timeout");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "MikroTik server unreachable (network error)");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error checking MikroTik connectivity");
            return false;
        }
    }

    public async Task<bool> CheckInternetConnectivityAsync()
    {
        try
        {
            _logger.LogInformation("Checking connectivity to download.mikrotik.com...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://download.mikrotik.com/");
            using var response = await _httpClient.SendAsync(request, cts.Token);

            var isConnected = (int)response.StatusCode < 500;
            _logger.LogInformation("MikroTik download server connectivity: {Status}",
                isConnected ? "✓ OK" : "✗ FAILED");
            return isConnected;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("MikroTik download server timeout");
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("MikroTik download server connectivity check failed: {Message}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MikroTik download server connectivity check failed");
            return false;
        }
    }
}

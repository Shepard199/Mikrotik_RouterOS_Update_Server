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
            var response = await _httpClient.SendAsync(request, cts.Token);

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
            _logger.LogInformation("Checking internet connectivity...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.google.com");
            var response = await _httpClient.SendAsync(request, cts.Token);

            var isConnected = response.IsSuccessStatusCode;
            _logger.LogInformation("Internet connectivity: {Status}", isConnected ? "✓ OK" : "✗ FAILED");
            return isConnected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Internet connectivity check failed");
            return false;
        }
    }
}
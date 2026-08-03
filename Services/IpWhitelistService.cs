using System.Net;
using System.Net.Sockets;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Конфигурация IP Whitelist
/// </summary>
public class IpWhitelistOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> AllowedIps { get; set; } = [];
    public List<string> AllowedRanges { get; set; } = []; // CIDR notation
    public bool AllowLocalhost { get; set; } = true;
    public bool AllowPrivateNetworks { get; set; } = true;
    public string[]? ExcludedEndpoints { get; set; } // Endpoints to bypass whitelist (e.g., /health, /swagger)
}

/// <summary>
///     Интерфейс для проверки IP адреса
/// </summary>
public interface IIpWhitelistService
{
    bool IsIpAllowed(string? ipAddress);
    bool IsEndpointBypassEnabled(string endpoint);
}

/// <summary>
///     Сервис для проверки IP Whitelist
///     Позволяет ограничить доступ к API только определенным IP адресам
/// </summary>
public class IpWhitelistService : IIpWhitelistService
{
    private readonly ILogger<IpWhitelistService> _logger;
    private readonly IpWhitelistOptions _options;

    public IpWhitelistService(IConfiguration config, ILogger<IpWhitelistService> logger)
    {
        _logger = logger;
        _options = config.GetSection("IpWhitelist").Get<IpWhitelistOptions>() ?? new IpWhitelistOptions();

        if (_options.Enabled)
        {
            _logger.LogInformation("IP Whitelist enabled with {Count} IPs and {RangeCount} ranges",
                _options.AllowedIps.Count,
                _options.AllowedRanges.Count);

            if (_options.AllowLocalhost)
                _logger.LogInformation("Localhost (127.0.0.1, ::1) is allowed");

            if (_options.AllowPrivateNetworks)
                _logger.LogInformation("Private networks (10.*, 172.16-31.*, 192.168.*) are allowed");
        }
        else
        {
            _logger.LogWarning("IP Whitelist is disabled");
        }
    }

    public bool IsIpAllowed(string? ipAddress)
    {
        if (!_options.Enabled)
            return true;

        if (string.IsNullOrEmpty(ipAddress))
            return false;

        // Удаляем порт если он есть
        // Для IPv6 адресов берем часть до ':'
        // Для IPv4 адресов со странами '[10.0.0.1]:5000' берем IP часть
        ipAddress = ipAddress.Split(':')[0];

        // Проверяем localhost
        if (_options.AllowLocalhost && ipAddress is "127.0.0.1" or "::1" or "localhost")
        {
            _logger.LogDebug("Localhost access allowed: {Ip}", ipAddress);
            return true;
        }

        // Проверяем приватные сети
        if (_options.AllowPrivateNetworks && IsPrivateNetwork(ipAddress))
        {
            _logger.LogDebug("Private network access allowed: {Ip}", ipAddress);
            return true;
        }

        // Проверяем точные совпадения
        if (_options.AllowedIps.Contains(ipAddress))
        {
            _logger.LogDebug("IP allowed (exact match): {Ip}", ipAddress);
            return true;
        }

        // Проверяем диапазоны (CIDR)
        foreach (var range in _options.AllowedRanges.Where(range => IsInCidrRange(ipAddress, range)))
        {
            _logger.LogDebug("IP allowed (CIDR range): {Ip} in {Range}", ipAddress, range);
            return true;
        }

        _logger.LogWarning("IP access denied: {Ip}", ipAddress);
        return false;
    }

    public bool IsEndpointBypassEnabled(string endpoint)
    {
        if (_options.ExcludedEndpoints == null || _options.ExcludedEndpoints.Length == 0)
            return false;

        return _options.ExcludedEndpoints.Any(excluded =>
            endpoint.StartsWith(excluded, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsPrivateNetwork(string ipAddress)
    {
        try
        {
            var ip = IPAddress.Parse(ipAddress);

            // IPv4 private ranges
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();

                // 10.0.0.0/8
                if (bytes[0] == 10)
                    return true;

                // 172.16.0.0/12
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;

                // 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;

                // 127.0.0.0/8 (loopback)
                if (bytes[0] == 127)
                    return true;
            }

            // IPv6 private ranges
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // ::1 (loopback)
                if (IPAddress.IsLoopback(ip))
                    return true;

                // fc00::/7 (ULA - Unique Local Addresses)
                var bytes = ip.GetAddressBytes();
                if (bytes[0] >= 0xFC && bytes[0] <= 0xFD)
                    return true;

                // fe80::/10 (Link-local)
                if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool IsInCidrRange(string ipAddress, string cidrRange)
    {
        try
        {
            if (!cidrRange.Contains('/'))
                return ipAddress == cidrRange;

            var parts = cidrRange.Split('/');
            if (parts.Length != 2)
                return false;

            var networkAddress = parts[0];
            if (!int.TryParse(parts[1], out var prefixLength))
                return false;

            var ip = IPAddress.Parse(ipAddress);
            var network = IPAddress.Parse(networkAddress);

            // Проверяем что версия IP совпадает
            if (ip.AddressFamily != network.AddressFamily)
                return false;

            var ipBytes = ip.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();

            // Вычисляем маску

            for (var i = 0; i < prefixLength; i++)
            {
                var byteIndex = i / 8;
                var bitIndex = i % 8;

                var mask = (byte) (0xFF << (8 - bitIndex - 1));

                if ((ipBytes[byteIndex] & mask) != (networkBytes[byteIndex] & mask))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
///     Middleware для проверки IP Whitelist
/// </summary>
public class IpWhitelistMiddleware(
    RequestDelegate next,
    IIpWhitelistService whitelistService,
    ILogger<IpWhitelistMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.Request.Path.Value ?? "/";

        // Проверяем может ли этот endpoint быть пропущен
        if (whitelistService.IsEndpointBypassEnabled(endpoint))
        {
            await next(context);
            return;
        }

        // Получаем IP адрес клиента
        var ipAddress = GetClientIpAddress(context);

        // Проверяем whitelist
        if (!whitelistService.IsIpAllowed(ipAddress))
        {
            logger.LogWarning("Access denied for IP {Ip} to endpoint {Endpoint}", ipAddress, endpoint);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                code = "access_denied",
                message = "Access to this resource is not allowed",
                error = "IP address is not in whitelist"
            });
            return;
        }

        await next(context);
    }

    private string? GetClientIpAddress(HttpContext context)
    {
        // Проверяем X-Forwarded-For header (для reverse proxy)
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var ips = forwardedFor.ToString().Split(',');
            var ip = ips[0].Trim();
            if (!string.IsNullOrEmpty(ip))
                return ip;
        }

        // Проверяем X-Real-IP header
        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
        {
            var ip = realIp.ToString().Trim();
            if (!string.IsNullOrEmpty(ip))
                return ip;
        }

        // Возвращаем RemoteIpAddress
        return context.Connection.RemoteIpAddress?.ToString();
    }
}

/// <summary>
///     Extension методы для добавления IP Whitelist
/// </summary>
public static class IpWhitelistExtensions
{
    public static void AddIpWhitelist(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<IpWhitelistOptions>(config.GetSection("IpWhitelist"));
        services.AddSingleton<IIpWhitelistService, IpWhitelistService>();
    }

    public static void UseIpWhitelist(this IApplicationBuilder app)
    {
        app.UseMiddleware<IpWhitelistMiddleware>();
    }
}
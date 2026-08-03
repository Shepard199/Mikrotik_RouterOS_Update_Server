using System.Collections.Concurrent;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Конфигурация для Rate Limiting
/// </summary>
public class RateLimitOptions
{
    public bool Enabled { get; set; } = true;
    public int DefaultLimitPerMinute { get; set; } = 100;
    public int DownloadLimitPerMinute { get; set; } = 10;
    public int UpdateCheckLimitPerMinute { get; set; } = 5;
}

/// <summary>
///     Результат проверки Rate Limit
/// </summary>
public class RateLimitResult
{
    public bool IsAllowed { get; init; }
    public int RemainingRequests { get; init; }
    public int LimitPerMinute { get; init; }
    public DateTime ResetTime { get; init; }
}

/// <summary>
///     Сервис Rate Limiting для защиты от DDoS
///     Отслеживает количество запросов от клиента за минуту
/// </summary>
public class RateLimitService : IRateLimitService
{
    // Ключ: clientId:endpoint, Значение: список timestamps запросов
    private readonly ConcurrentDictionary<string, RateLimitBucket> _buckets;
    // Дедупликация логов: ключ → количество подавленных предупреждений
    private readonly ConcurrentDictionary<string, int> _suppressedWarnings = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<RateLimitService> _logger;
    private readonly RateLimitOptions _options;

    public RateLimitService(IConfiguration config, ILogger<RateLimitService> logger)
    {
        _logger = logger;
        _options = config.GetSection("RateLimiting").Get<RateLimitOptions>() ?? new RateLimitOptions();
        _buckets = new ConcurrentDictionary<string, RateLimitBucket>();

        // Очищаем старые buckets каждые 5 минут
        _cleanupTimer = new Timer(CleanupOldBuckets, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _logger.LogInformation("RateLimitService initialized with limit {Limit} req/min",
            _options.DefaultLimitPerMinute);
    }

    public RateLimitResult CheckRateLimit(string clientId, string endpoint)
    {
        if (!_options.Enabled)
            return new RateLimitResult {IsAllowed = true, RemainingRequests = int.MaxValue};

        var key = $"{clientId}:{endpoint}";
        var limit = GetLimitForEndpoint(endpoint);

        var bucket = _buckets.AddOrUpdate(key,
            _ => new RateLimitBucket(),
            (_, existing) =>
            {
                existing.RemoveExpiredRequests();
                return existing;
            });

        var isAllowed = bucket.RequestCount < limit;

        if (isAllowed)
        {
            bucket.AddRequest();
        }
        else
        {
            // Дедупликация логов: логируем только каждое 10-е нарушение для одного ключа
            var suppressCount = _suppressedWarnings.AddOrUpdate(key, 0, (_, c) => c + 1);
            if (suppressCount == 0 || suppressCount % 10 == 0)
            {
                _logger.LogWarning(
                    "Rate limit exceeded for {ClientId}:{Endpoint} ({Count}/{Limit}){Suppressed}",
                    clientId, endpoint, bucket.RequestCount, limit,
                    suppressCount > 0 ? $" [suppressed {suppressCount} duplicates]" : "");
            }
        }

        return new RateLimitResult
        {
            IsAllowed = isAllowed,
            RemainingRequests = Math.Max(0, limit - bucket.RequestCount),
            LimitPerMinute = limit,
            ResetTime = bucket.GetResetTime()
        };
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private int GetLimitForEndpoint(string endpoint)
    {
        return endpoint switch
        {
            "/api/download" => _options.DownloadLimitPerMinute,
            "/api/update-check" => _options.UpdateCheckLimitPerMinute,
            _ => _options.DefaultLimitPerMinute
        };
    }

    private void CleanupOldBuckets(object? state)
    {
        try
        {
            var oldBuckets = _buckets
                .Where(kvp => kvp.Value.IsExpired(TimeSpan.FromMinutes(2)))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldBuckets)
                if (_buckets.TryRemove(key, out _))
                {
                    _logger.LogDebug("Cleaned up rate limit bucket: {Key}", key);
                    _suppressedWarnings.TryRemove(key, out _);
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during rate limit cleanup");
        }
    }
}

/// <summary>
///     Bucket для хранения информации о запросах клиента
/// </summary>
public class RateLimitBucket
{
    private readonly Lock _lock = new();
    private readonly Queue<DateTime> _requests = new();

    public int RequestCount { get; private set; }

    public void AddRequest()
    {
        lock (_lock)
        {
            _requests.Enqueue(DateTime.UtcNow);
            RequestCount = _requests.Count;
        }
    }

    public void RemoveExpiredRequests()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            while (_requests.Count > 0 && _requests.Peek() < cutoff) _requests.Dequeue();
            RequestCount = _requests.Count;
        }
    }

    public DateTime GetResetTime()
    {
        lock (_lock)
        {
            if (_requests.Count == 0)
                return DateTime.UtcNow;

            return _requests.Peek().AddMinutes(1);
        }
    }

    public bool IsExpired(TimeSpan timeout)
    {
        lock (_lock)
        {
            return _requests.Count == 0 ||
                   DateTime.UtcNow - _requests.Peek() > timeout;
        }
    }
}

/// <summary>
///     Интерфейс для Rate Limiting
/// </summary>
public interface IRateLimitService : IDisposable
{
    RateLimitResult CheckRateLimit(string clientId, string endpoint);
}

/// <summary>
///     Middleware для Rate Limiting
/// </summary>
public class RateLimitMiddleware(
    RequestDelegate next,
    IRateLimitService rateLimitService,
    ILogger<RateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Получаем IP адрес клиента
        var clientId = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var endpoint = context.Request.Path.Value ?? "/";

        // Проверяем rate limit
        var result = rateLimitService.CheckRateLimit(clientId, endpoint);

        // Добавляем headers с информацией о rate limit
        context.Response.Headers["X-RateLimit-Limit"] = result.LimitPerMinute.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = result.RemainingRequests.ToString();
        context.Response.Headers["X-RateLimit-Reset"] = result.ResetTime.ToString("O");

        if (!result.IsAllowed)
        {
            logger.LogDebug("Rate limit 429 response for {ClientId} on {Endpoint}", clientId, endpoint);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                code = "rate_limit_exceeded",
                message = "Too many requests",
                retry_after = (int) (result.ResetTime - DateTime.UtcNow).TotalSeconds
            });
            return;
        }

        await next(context);
    }
}

/// <summary>
///     Extension методы для добавления Rate Limiting
/// </summary>
public static class RateLimitExtensions
{
    public static void AddRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RateLimitOptions>(config.GetSection("RateLimiting"));
        services.AddSingleton<IRateLimitService, RateLimitService>();
    }

    public static void UseRateLimiting(this IApplicationBuilder app)
    {
        app.UseMiddleware<RateLimitMiddleware>();
    }
}

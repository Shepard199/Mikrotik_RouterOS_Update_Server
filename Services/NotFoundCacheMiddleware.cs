using System.Collections.Concurrent;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Middleware для кеширования 404-ответов на /routeros/ запросы.
///     MikroTik роутеры агрессивно повторяют запросы на несуществующие файлы,
///     расходуя бюджет rate-limiter и заполняя логи предупреждениями.
///     Этот middleware запоминает пути, вернувшие 404, и отдаёт 404 мгновенно
///     без прохождения через rate-limiter и обработчик маршрута.
/// </summary>
public class NotFoundCacheMiddleware(
    RequestDelegate next,
    ILogger<NotFoundCacheMiddleware> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    // Ключ: нормализованный путь, Значение: время первого 404
    private static readonly ConcurrentDictionary<string, DateTime> NotFoundCache = new();
    private static DateTime _lastCleanup = DateTime.UtcNow;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;

        // Применяем только к /routeros/ запросам
        if (path is not null && path.StartsWith("/routeros/", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedPath = path.ToLowerInvariant();

            // Проверяем кеш — если путь недавно вернул 404, отдаём мгновенно
            if (NotFoundCache.TryGetValue(normalizedPath, out var cachedAt))
            {
                if (DateTime.UtcNow - cachedAt < CacheDuration)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "File not found (cached)",
                        requested = path.TrimStart('/')
                    });
                    return;
                }

                // TTL истёк — удаляем из кеша, чтобы повторно проверить
                NotFoundCache.TryRemove(normalizedPath, out _);
            }

            // Пропускаем запрос дальше по pipeline и наблюдаем результат
            await next(context);

            // Если обработчик вернул 404, запоминаем путь
            if (context.Response.StatusCode == StatusCodes.Status404NotFound)
            {
                NotFoundCache.TryAdd(normalizedPath, DateTime.UtcNow);
                logger.LogInformation("Cached 404 for path: {Path} (TTL={Minutes}m)", path, CacheDuration.TotalMinutes);
            }
        }
        else
        {
            await next(context);
        }

        // Периодическая очистка старых записей
        CleanupIfNeeded();
    }

    private static void CleanupIfNeeded()
    {
        if (DateTime.UtcNow - _lastCleanup < CleanupInterval) return;
        _lastCleanup = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - CacheDuration;
        foreach (var kvp in NotFoundCache)
        {
            if (kvp.Value < cutoff)
                NotFoundCache.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    ///     Принудительно очистить кеш 404 (например, после загрузки новых файлов)
    /// </summary>
    public static void ClearCache()
    {
        NotFoundCache.Clear();
    }

    /// <summary>
    ///     Удалить конкретный путь из кеша (например, после загрузки конкретного файла)
    /// </summary>
    public static void InvalidatePath(string path)
    {
        NotFoundCache.TryRemove(path.ToLowerInvariant(), out _);
    }

    /// <summary>
    ///     Количество закешированных 404-путей (для диагностики)
    /// </summary>
    public static int CachedCount => NotFoundCache.Count;
}

public static class NotFoundCacheExtensions
{
    public static void UseNotFoundCache(this IApplicationBuilder app)
    {
        app.UseMiddleware<NotFoundCacheMiddleware>();
    }
}

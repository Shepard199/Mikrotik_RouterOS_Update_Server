using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using MikroTik.UpdateServer.Models;
using MikroTik.UpdateServer.Services;
using Serilog;

namespace MikroTik.UpdateServer;

public static partial class Program
{
    public static void Main(string[] args)
    {
        // Настраиваем Serilog сразу в начале
        SerilogConfiguration.ConfigureLogging();

        try
        {
            Log.Information("Инициализация приложения...");
            MainAsync(args).Wait();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Приложение завершилось с ошибкой");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static async Task MainAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Заменяем встроенное логирование на Serilog
        builder.Host.UseSerilog();

        // DI
        builder.Services.AddSingleton<ScheduleService>();
        builder.Services.AddHostedService<UpdateCheckService>();
        builder.Services.AddSingleton<ILogStore, FileBasedLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, LogStoreLoggerProvider>();

        // Кэширование и оптимизация
        builder.Services.AddSingleton<IFileMetadataCacheService, FileMetadataCacheService>();
        builder.Services.AddSingleton<IDiskUsageService, OptimizedDiskUsageService>();
        builder.Services.AddSingleton<IOptimizedDownloadService, OptimizedDownloadService>();

        // Phase 2: Modular services
        builder.Services.AddSingleton<IVersionManagementService, VersionManagementService>();
        builder.Services.AddSingleton<IFileDownloadService, OptimizedDownloadService>();
        builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
        builder.Services.AddSingleton<IMetadataService, MetadataService>();
        builder.Services.AddSingleton<IConnectivityService, ConnectivityService>();
        builder.Services.AddSingleton<IUpdateOrchestrator, UpdateOrchestrator>();

        // Phase 3: Rate Limiting
        builder.Services.AddRateLimiting(builder.Configuration);

        // Phase 4: Health Checks
        builder.Services.AddSingleton<IHealthCheckService, HealthCheckService>();
        builder.Services.AddApplicationTelemetry();

        // IP Whitelist Security
        builder.Services.AddIpWhitelist(builder.Configuration);

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<GzipCompressionProvider>();
            options.Providers.Add<BrotliCompressionProvider>();
        });

        var app = builder.Build();
        var logStore = app.Services.GetRequiredService<ILogStore>();
        var config = app.Services.GetRequiredService<IConfiguration>();

        var baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");
        WriteConsoleLog($"[STARTUP] Base folder path: {baseFolder}");
        WriteConsoleLog($"[STARTUP] Base folder exists: {Directory.Exists(baseFolder)}");

        if (Directory.Exists(baseFolder))
        {
            var files = Directory.GetFiles(baseFolder);
            WriteConsoleLog($"[STARTUP] Files in base folder: {string.Join(", ", files.Select(Path.GetFileName))}");
        }

        app.UseResponseCompression();
        app.UseCors();
        app.UseIpWhitelist();
        app.UseNotFoundCache();
        app.UseRateLimiting();

        // Лог запросов
        app.Use(async (context, next) =>
        {
            var startTime = DateTime.UtcNow;
            var path = context.Request.Path;
            var method = context.Request.Method;

            // Получаем IP адрес клиента
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
                clientIp = forwardedFor.ToString().Split(',')[0].Trim();

            try
            {
                await next.Invoke();
                var duration = DateTime.UtcNow - startTime;
                var statusCode = context.Response.StatusCode;

                //WriteConsoleLog(
                //    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{clientIp}] - {method} {path} -> {statusCode} ({duration.TotalMilliseconds:F0}ms)");

                var level = statusCode >= 500
                    ? "Error"
                    : statusCode >= 400
                        ? "Warning"
                        : "Information";

                logStore.Add(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = level,
                    Source = "HTTP",
                    Message = $"[{clientIp}] - {method} {path} -> {statusCode} ({duration.TotalMilliseconds:F0}ms)"
                });
            }
            catch (Exception ex)
            {
                WriteConsoleLog($"[ERROR] [{clientIp}] - {method} {path}: {ex.Message}");

                logStore.Add(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Source = "HTTP",
                    Message = $"[{clientIp}] - {method} {path} -> exception",
                    Exception = ex.ToString()
                });

                throw;
            }
        });

        // Безопасные заголовки
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
            context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            await next.Invoke();
        });

        // Cache-Control
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.EndsWith(".npk") == true ||
                context.Request.Path.Value?.EndsWith(".zip") == true)
                context.Response.Headers.Append("Cache-Control", "public, max-age=31536000, immutable");
            else if (context.Request.Path.Value?.StartsWith("/api/") == true)
                context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");

            await next.Invoke();
        });

        #region Api

        // API
        var api = app.MapGroup("/api");

        api.MapGet("/versions", GetVersions);
        api.MapGet("/status", GetStatus);
        api.MapPost("/update-check", TriggerUpdateCheck);
        api.MapPost("/set-active-version/{version}", SetActiveVersion);
        api.MapDelete("/remove-version/{version}", RemoveVersion);
        api.MapPost("/remove-versions", RemoveVersions);
        api.MapGet("/download/{version}/{filename}", DownloadFile);
        api.MapGet("/versions/history", GetVersionHistory);
        api.MapGet("/changelog", GetGlobalChangelog);
        api.MapGet("/changelog/{version}", GetVersionChangelog);

        // ===== ДИАГНОСТИКА =====
        api.MapGet("/diagnostics", GetDiagnostics);
        api.MapGet("/health/tls", GetTlsHealth);

        // ===== LOGS =====
        api.MapGet("/logs", (string? level, string? search, int? take, ILogStore store) =>
        {
            var logs = store.Query(level, search, take ?? 100);
            return Results.Ok(new {logs});
        });

        api.MapGet("/logs/stats", (ILogStore store) =>
        {
            var stats = store.GetStats();
            return Results.Ok(stats);
        });

        api.MapGet("/logs/download", (ILogStore store) =>
        {
            var zipBytes = store.ExportAsZip();
            var fileName = $"logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(zipBytes, "application/zip", fileName);
        });

        // ===== Dashboard =====
        api.MapGet("/dashboard/clients-today", GetTodayClientUpdates);

        // ===== Schedule =====
        api.MapGet("/schedule", GetSchedule);
        api.MapGet("/schedule/status", GetScheduleStatus);
        api.MapPost("/schedule", UpdateSchedule);
        api.MapPost("/schedule/pause", PauseSchedule);
        api.MapPost("/schedule/resume", ResumeSchedule);


        // Healthcheck
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow
        }));

        // Detailed health check endpoint (Phase 4)
        app.MapGet("/health/detailed", async (IHealthCheckService healthCheckService) =>
        {
            var results = await healthCheckService.RunAllChecksAsync();
            var overallStatus = healthCheckService.GetOverallStatus(results);

            return Results.Ok(new
            {
                overall_status = overallStatus.ToString(),
                timestamp = DateTime.UtcNow,
                checks = results.Select(r => new
                {
                    component = r.Component,
                    status = r.Status.ToString(),
                    message = r.Message,
                    response_time_ms = r.ResponseTime,
                    details = r.Details
                })
            });
        });

        // Individual component health checks
        app.MapGet("/health/connectivity", async (IHealthCheckService healthCheckService) =>
        {
            var result = await healthCheckService.CheckMikroTikConnectivityAsync();
            return Results.Ok(new
            {
                component = result.Component,
                status = result.Status.ToString(),
                message = result.Message,
                details = result.Details
            });
        });

        app.MapGet("/health/disk", async (IHealthCheckService healthCheckService) =>
        {
            var result = await healthCheckService.CheckDiskSpaceAsync();
            return Results.Ok(new
            {
                component = result.Component,
                status = result.Status.ToString(),
                message = result.Message,
                details = result.Details
            });
        });

        app.MapGet("/health/filesystem", async (IHealthCheckService healthCheckService) =>
        {
            var result = await healthCheckService.CheckFileSystemAsync();
            return Results.Ok(new
            {
                component = result.Component,
                status = result.Status.ToString(),
                message = result.Message,
                details = result.Details
            });
        });

        app.MapGet("/health/downloads", async (IHealthCheckService healthCheckService) =>
        {
            var result = await healthCheckService.CheckDownloadServiceAsync();
            return Results.Ok(new
            {
                component = result.Component,
                status = result.Status.ToString(),
                message = result.Message,
                details = result.Details
            });
        });

        // ===== Settings / Architectures =====
        api.MapGet("/settings/arches", GetAllowedArches);
        api.MapPost("/settings/arches", UpdateAllowedArches);
        api.MapGet("/settings/pointers", GetPointerRouting);
        api.MapPost("/settings/pointers/route", UpdatePointerRouting);

        // ===== Settings / Delete Prefixes =====
        api.MapGet("/settings/delete-prefixes", GetDeletePrefixes);
        api.MapPost("/settings/delete-prefixes", UpdateDeletePrefixes);

        // ===== Settings / RouterOS v7 Extra Packages =====
        api.MapGet("/settings/v7-packages", GetV7Packages);
        api.MapPost("/settings/v7-packages", UpdateV7Packages);

        // ===== Settings / Localization =====
        api.MapGet("/locales", GetAvailableLocales);
        api.MapGet("/settings/language", GetCurrentLanguage);
        api.MapPost("/settings/language", (Delegate) SetCurrentLanguage);

        // ===== Settings / Console Logs =====
        api.MapGet("/settings/console-logs", GetConsoleLogSettings);
        api.MapPost("/settings/console-logs", (Delegate) SetConsoleLogSettings);

        // Специальные маршруты для MikroTik обновлений (эмулируют официальные пути)
        app.MapMethods("/routeros/{filename}", ["GET", "HEAD"], ServeMikroTikFile);
        app.MapMethods("/routeros/{version}/{filename}", ["GET", "HEAD"], ServeMikroTikFile);

        #endregion

        // Статика — wwwroot рядом с exe (ДОЛЖНА БЫТЬ ПОСЛЕ ВСЕХ API МАРШРУТОВ)
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        // Middleware для проверки доступа к index.html только администраторам
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            if (path is "/index.html" or "/" or "")
            {
                var adminConfig = config.GetSection("AdminIps");
                if (adminConfig.GetValue<bool>("Enabled"))
                {
                    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
                        clientIp = forwardedFor.ToString().Split(',')[0].Trim();

                    // Проверяем явно разрешенные IP
                    var allowedIps = adminConfig.GetSection("AllowedIps").Get<string[]>() ?? [];
                    var isAllowed = allowedIps.Contains(clientIp, StringComparer.OrdinalIgnoreCase);

                    if (!isAllowed)
                    {
                        WriteConsoleLog($"[ADMIN ACCESS DENIED] {clientIp} tried to access {path}");
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new {error = "Access denied"});
                        return;
                    }
                }
            }

            await next.Invoke();
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(webRoot),
            RequestPath = "",
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream"
        });

        // Корень на UI
        app.MapGet("/", () => Results.Redirect("/index.html"));

        // Обработка ошибок JSON-эндпоинтом
        app.UseExceptionHandler("/error");
        app.MapGet("/error", HandleError);

        Log.Information("\n");
        Log.Information("┌────────────────────────────────────────────────────────┐");
        Log.Information("│   MikroTik ROS Local Update Server v2.0                │");
        Log.Information("│   Powered by Shepard199                                │");
        Log.Information("└────────────────────────────────────────────────────────┘");

        await app.RunAsync();
    }
}

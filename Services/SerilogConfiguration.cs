using System.Runtime.InteropServices;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Конфигурация структурированного логирования Serilog
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>
    ///     Настраивает Serilog для приложения
    /// </summary>
    public static void ConfigureLogging()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            // Минимальный уровень логирования
            .MinimumLevel.Debug()

            // Override логирование Microsoft
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)

            // Обогащение логов
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithEnvironmentUserName()

            // Console sink (цветной вывод)
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")

            // File sink (структурированный JSON)
            .WriteTo.File(
                path: Path.Combine(logDirectory, "app-.json"),
                formatter: new CompactJsonFormatter(),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 104857600, // 100MB
                rollOnFileSizeLimit: true,
                shared: true)

            // File sink (текстовый формат для быстрого анализа)
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.txt"),
                outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 52428800, // 50MB
                rollOnFileSizeLimit: true,
                shared: true)

            // Error файл отдельно
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(le => le.Level >= LogEventLevel.Error)
                .WriteTo.File(
                    Path.Combine(logDirectory, "errors-.txt"),
                    outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 10485760, // 10MB
                    rollOnFileSizeLimit: true,
                    shared: true)).CreateLogger();

        Log.Information("═══════════════════════════════════════════════════════════");
        Log.Information("MikroTik UpdateServer запущен");
        Log.Information("Версия: .NET {Framework}", RuntimeInformation.FrameworkDescription);
        Log.Information("Путь логов: {LogPath}", logDirectory);
        Log.Information("═══════════════════════════════════════════════════════════");
    }
}
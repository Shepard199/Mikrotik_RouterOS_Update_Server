namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Базовая конфигурация телеметрии приложения.
///     Точка расширения для будущей интеграции OpenTelemetry.
/// </summary>
public static class TelemetryConfiguration
{
    public static void AddApplicationTelemetry(this IServiceCollection services)
    {
        _ = services;
    }
}
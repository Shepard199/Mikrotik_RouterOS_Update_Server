namespace MikroTik.UpdateServer;

public static partial class Program
{
    private enum ConsoleLogLevel
    {
        Debug = 0,
        Information = 1,
        Warning = 2,
        Error = 3
    }

    private static readonly Lock ConsoleLogSettingsLock = new();
    private static bool ConsoleLogsEnabled = true;
    private static ConsoleLogLevel ConsoleLogsMinLevel = ConsoleLogLevel.Information;

    private static readonly string[] ConsoleLogLevels =
    [
        nameof(ConsoleLogLevel.Debug),
        nameof(ConsoleLogLevel.Information),
        nameof(ConsoleLogLevel.Warning),
        nameof(ConsoleLogLevel.Error)
    ];

    private static void WriteConsoleLog(string message)
    {
        var level = InferConsoleLogLevel(message);

        bool enabled;
        ConsoleLogLevel minLevel;
        lock (ConsoleLogSettingsLock)
        {
            enabled = ConsoleLogsEnabled;
            minLevel = ConsoleLogsMinLevel;
        }

        if (!enabled || level < minLevel)
            return;

        Console.WriteLine(message);
    }

    private static ConsoleLogLevel InferConsoleLogLevel(string message)
    {
        if (message.StartsWith("[DEBUG]", StringComparison.OrdinalIgnoreCase))
            return ConsoleLogLevel.Debug;
        if (message.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
            return ConsoleLogLevel.Error;
        if (message.StartsWith("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("[ADMIN ACCESS DENIED]", StringComparison.OrdinalIgnoreCase))
            return ConsoleLogLevel.Warning;

        return ConsoleLogLevel.Information;
    }

    private static object GetConsoleLogSettingsSnapshot()
    {
        lock (ConsoleLogSettingsLock)
        {
            return new
            {
                enabled = ConsoleLogsEnabled,
                level = ConsoleLogsMinLevel.ToString(),
                levels = ConsoleLogLevels
            };
        }
    }

    private static (bool success, string? error) UpdateConsoleLogSettings(bool enabled, string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return (false, "Level is required");

        if (!Enum.TryParse<ConsoleLogLevel>(level, true, out var parsedLevel))
            return (false, $"Unsupported level: {level}");

        lock (ConsoleLogSettingsLock)
        {
            ConsoleLogsEnabled = enabled;
            ConsoleLogsMinLevel = parsedLevel;
        }

        WriteConsoleLog($"[INFO] Console logs updated: enabled={enabled}, level={parsedLevel}");
        return (true, null);
    }
}

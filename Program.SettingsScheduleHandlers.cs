using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MikroTik.UpdateServer.Models;
using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public static partial class Program
{
    private static async Task<IResult> GetAllowedArches(IUpdateOrchestrator orchestrator)
    {
        var arches = await orchestrator.GetAllowedArchesAsync();
        return Results.Ok(arches);
    }

    private static async Task<IResult> UpdateAllowedArches(
        IUpdateOrchestrator orchestrator,
        string[] arches)
    {
        try
        {
            await orchestrator.UpdateAllowedArchesAsync(arches);
            return Results.Ok(new {message = "Allowed architectures updated successfully"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating allowed architectures: {ex.Message}");
        }
    }

    private static IResult GetConsoleLogSettings()
    {
        return Results.Ok(GetConsoleLogSettingsSnapshot());
    }

    private static IResult SetConsoleLogSettings(ConsoleLogSettingsRequest request)
    {
        var (success, error) = UpdateConsoleLogSettings(request.Enabled, request.Level);
        if (!success)
            return Results.BadRequest(new {message = error ?? "Invalid console log settings"});

        return Results.Ok(new
        {
            message = "Console log settings updated successfully",
            settings = GetConsoleLogSettingsSnapshot()
        });
    }

    private static IResult GetDeletePrefixes()
    {
        try
        {
            var deletePrefixesPath = Path.Combine(AppContext.BaseDirectory, "delete_prefixes.json");
            if (!File.Exists(deletePrefixesPath))
            {
                WriteConsoleLog("[DEBUG] delete_prefixes.json not found, returning empty array");
                return Results.Ok(new string[] { });
            }

            var json = File.ReadAllText(deletePrefixesPath);
            WriteConsoleLog($"[DEBUG] Read delete_prefixes.json: {json}");

            string[] prefixes;

            try
            {
                // Пытаемся десериализовать как прямой массив
                prefixes = JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch
            {
                // Если это не работает, пытаемся как объект с полем deletePrefixes
                var obj = JsonSerializer.Deserialize<JsonElement>(json);
                if (obj.TryGetProperty("deletePrefixes", out var prefixesElement))
                    prefixes = JsonSerializer.Deserialize<string[]>(prefixesElement.GetRawText()) ?? [];
                else
                    prefixes = [];
            }

            WriteConsoleLog($"[DEBUG] Deserialized {prefixes.Length} prefixes");
            return Results.Ok(prefixes);
        }
        catch (Exception ex)
        {
            WriteConsoleLog($"[ERROR] Error in GetDeletePrefixes: {ex}");
            return Results.Problem($"Error reading delete prefixes: {ex.Message}");
        }
    }

    private static async Task<IResult> UpdateDeletePrefixes(string[] prefixes)
    {
        try
        {
            var deletePrefixesPath = Path.Combine(AppContext.BaseDirectory, "delete_prefixes.json");

            if (prefixes.Length == 0)
            {
                // Если пустой массив, удаляем файл или сохраняем пустой массив
                if (File.Exists(deletePrefixesPath))
                {
                    File.Delete(deletePrefixesPath);
                    WriteConsoleLog("[DEBUG] Deleted delete_prefixes.json");
                }

                return Results.Ok(new {message = "Delete prefixes cleared"});
            }

            // Нормализуем префиксы (удаляем пробелы, преобразуем в нижний регистр)
            var normalized = prefixes
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct()
                .OrderBy(p => p)
                .ToArray();

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions {WriteIndented = true});
            await File.WriteAllTextAsync(deletePrefixesPath, json);

            WriteConsoleLog($"[DEBUG] Saved {normalized.Length} delete prefixes to delete_prefixes.json");

            return Results.Ok(new {message = "Delete prefixes updated successfully", count = normalized.Length});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating delete prefixes: {ex.Message}");
        }
    }

    private static IResult GetV7Packages()
    {
        try
        {
            var v7PackagesPath = Path.Combine(AppContext.BaseDirectory, "v7_packages.json");
            if (!File.Exists(v7PackagesPath))
            {
                WriteConsoleLog("[DEBUG] v7_packages.json not found, returning empty array");
                return Results.Ok(new string[] { });
            }

            var json = File.ReadAllText(v7PackagesPath);
            WriteConsoleLog($"[DEBUG] Read v7_packages.json: {json}");

            string[] packages;

            try
            {
                packages = JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch
            {
                var obj = JsonSerializer.Deserialize<JsonElement>(json);
                if (obj.TryGetProperty("v7Packages", out var packagesElement))
                    packages = JsonSerializer.Deserialize<string[]>(packagesElement.GetRawText()) ?? [];
                else
                    packages = [];
            }

            WriteConsoleLog($"[DEBUG] Deserialized {packages.Length} v7 packages");
            return Results.Ok(packages);
        }
        catch (Exception ex)
        {
            WriteConsoleLog($"[ERROR] Error in GetV7Packages: {ex}");
            return Results.Problem($"Error reading v7 packages: {ex.Message}");
        }
    }

    private static async Task<IResult> UpdateV7Packages(string[] packages)
    {
        try
        {
            var v7PackagesPath = Path.Combine(AppContext.BaseDirectory, "v7_packages.json");

            if (packages.Length == 0)
            {
                if (File.Exists(v7PackagesPath))
                {
                    File.Delete(v7PackagesPath);
                    WriteConsoleLog("[DEBUG] Deleted v7_packages.json");
                }

                return Results.Ok(new {message = "v7 packages cleared"});
            }

            var normalized = packages
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct()
                .OrderBy(p => p)
                .ToArray();

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions {WriteIndented = true});
            await File.WriteAllTextAsync(v7PackagesPath, json);

            WriteConsoleLog($"[DEBUG] Saved {normalized.Length} v7 packages to v7_packages.json");

            return Results.Ok(new {message = "v7 packages updated successfully", count = normalized.Length});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating v7 packages: {ex.Message}");
        }
    }

    // ===== Localization Handlers =====
    private static IResult GetAvailableLocales()
    {
        try
        {
            var langDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "lang");
            if (!Directory.Exists(langDir)) return Results.Ok(new[] {"en"});

            var locales = Directory.GetFiles(langDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x)
                .ToArray();

            return Results.Ok(locales.Length > 0 ? locales : ["en"]);
        }
        catch (Exception ex)
        {
            WriteConsoleLog($"[ERROR] Error getting available locales: {ex.Message}");
            return Results.Ok(new[] {"en"});
        }
    }

    private static IResult GetCurrentLanguage()
    {
        try
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .Build();

            var language = config["CurrentLanguage"] ?? "en";
            return Results.Ok(new {language});
        }
        catch (Exception ex)
        {
            WriteConsoleLog($"[ERROR] Error getting current language: {ex.Message}");
            return Results.Ok(new {language = "en"});
        }
    }

    private static async Task<IResult> SetCurrentLanguage(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest(new {message = "Language cannot be empty"});

            // Remove quotes if JSON string
            var language = body.Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(language))
                return Results.BadRequest(new {message = "Language cannot be empty"});

            var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var jsonText = await File.ReadAllTextAsync(appsettingsPath);
            var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(root.GetRawText())
                       ?? new Dictionary<string, object>();

            dict["CurrentLanguage"] = language.Trim().ToLowerInvariant();

            var options = new JsonSerializerOptions {WriteIndented = true};
            var newJson = JsonSerializer.Serialize(dict, options);
            await File.WriteAllTextAsync(appsettingsPath, newJson);

            WriteConsoleLog($"[DEBUG] Changed language to: {language}");

            return Results.Ok(new {message = "Language updated successfully", language});
        }
        catch (Exception ex)
        {
            WriteConsoleLog($"[ERROR] Error setting language: {ex.Message}");
            return Results.Problem($"Error setting language: {ex.Message}");
        }
    }


    // Schedule
    private static IResult GetSchedule(ScheduleService scheduleService)
    {
        var config = scheduleService.GetConfig();
        return Results.Ok(config);
    }

    private static IResult GetScheduleStatus(ScheduleService scheduleService)
    {
        var status = scheduleService.GetStatus();
        return Results.Ok(new
        {
            status.Config,
            status.NextScheduledCheck,
            status.IsPaused,
            status.TimeUntilNextCheck,
            status.Status
        });
    }

    private static async Task<IResult> UpdateSchedule(ScheduleService scheduleService, ScheduleConfig config)
    {
        try
        {
            await scheduleService.UpdateConfigAsync(config);
            return Results.Ok(new {message = "Schedule updated successfully"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error updating schedule: {ex.Message}");
        }
    }

    private static async Task<IResult> PauseSchedule(ScheduleService scheduleService, [FromQuery] int hours)
    {
        try
        {
            await scheduleService.PauseAsync(TimeSpan.FromHours(hours));
            return Results.Ok(new {message = $"Updates paused for {hours} hours"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error pausing schedule: {ex.Message}");
        }
    }

    private static async Task<IResult> ResumeSchedule(ScheduleService scheduleService)
    {
        try
        {
            await scheduleService.ResumeAsync();
            return Results.Ok(new {message = "Updates resumed"});
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error resuming schedule: {ex.Message}");
        }
    }
}

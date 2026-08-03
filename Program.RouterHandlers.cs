using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public static partial class Program
{
    private static async Task<IResult> ServeMikroTikFile(
        string? version,
        string? filename,
        IUpdateOrchestrator orchestrator,
        HttpContext context)
    {
        // Получаем IP адрес клиента
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
            clientIp = forwardedFor.ToString().Split(',')[0].Trim();

        WriteConsoleLog($"[GET] {clientIp} - {filename}{(string.IsNullOrEmpty(version) ? "" : $" (v{version})")}");
        WriteConsoleLog($"[DEBUG] [{clientIp}] ServeMikroTikFile called: version='{version}', filename='{filename}'");

        // Защита от пустого имени
        if (string.IsNullOrEmpty(filename))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Filename required but not provided");
            return Results.BadRequest("Filename required");
        }

        // 1. Pointer-файлы (LATEST.6, NEWEST6.stable, NEWESTa6.long-term и т.п.)
        // ОБРАБАТЫВАЕМ ВНЕ ЗАВИСИМОСТИ ОТ ВЕРСИИ В URL!
        if (IsPointerFile(filename))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Processing pointer file request: {filename}");

            var content = orchestrator.GetPointerFileContent(filename);
            if (content is null)
            {
                var req = version is null
                    ? $"routeros/{filename}"
                    : $"routeros/{version}/{filename}";

                WriteConsoleLog($"[DEBUG] [{clientIp}] Pointer content not available for: {req}");
                return Results.NotFound(new
                {
                    error = "Pointer not available",
                    requested = req
                });
            }

            return Results.Text(content, "text/plain; charset=utf-8");
        }

        // 2. Реальные файлы с версией: /routeros/{version}/{filename}
        if (!string.IsNullOrEmpty(version))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Processing versioned file: {version}/{filename}");

            if (version.Contains("..") || version.Contains("\\") || version.Contains("/") ||
                filename.Contains("..") || filename.Contains("\\") || filename.Contains("/"))
            {
                WriteConsoleLog($"[DEBUG] [{clientIp}] Security violation detected for: {version}/{filename}");
                return Results.StatusCode(403);
            }

            string? filePath;

            if (filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase))
            {
                WriteConsoleLog($"[DEBUG] [{clientIp}] Looking for CHANGELOG in version: {version}");
                filePath = await orchestrator.GetChangelogPathAsync(version);
            }
            else if (filename.Equals("packages.csv", StringComparison.OrdinalIgnoreCase))
            {
                // Тут version – это ветка, например 7.20
                WriteConsoleLog($"[DEBUG] [{clientIp}] Looking for packages.csv for branch: {version}");
                filePath = await orchestrator.GetPackagesCsvPathAsync(version);
            }
            else
            {
                // Для файлов прошивок ищем в v6/v7
                WriteConsoleLog($"[DEBUG] [{clientIp}] Looking for firmware file: {version}/{filename}");
                filePath = await orchestrator.GetFilePathAsync(version, filename);
            }

            if (filePath is null || !File.Exists(filePath))
            {
                WriteConsoleLog($"[DEBUG] [{clientIp}] Local file missing, trying on-demand upstream download: {version}/{filename}");
                filePath = await orchestrator.EnsureFileDownloadedAsync(version, filename);
            }

            WriteConsoleLog($"[DEBUG] [{clientIp}] Final filePath: {filePath}");
            WriteConsoleLog(filePath != null
                ? $"[DEBUG] [{clientIp}] File exists: {File.Exists(filePath)}"
                : $"[DEBUG] [{clientIp}] File path is null");

            if (filePath is null || !File.Exists(filePath))
            {
                WriteConsoleLog($"[DEBUG] [{clientIp}] File not found: routeros/{version}/{filename}");
                return Results.NotFound(new
                {
                    error = "File not found",
                    requested = $"routeros/{version}/{filename}"
                });
            }

            return await ServePhysicalFile(filePath, filename, context);
        }

        // 3. Одиночные файлы: /routeros/{filename}
        WriteConsoleLog($"[DEBUG] [{clientIp}] Processing regular file: {filename}");

        if (filename.Contains("..") || filename.Contains("\\") || filename.Contains("/"))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Security violation detected for: {filename}");
            return Results.StatusCode(403);
        }

        string? filePathRegular;

        // Для глобального CHANGELOG
        if (filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Looking for global CHANGELOG");
            filePathRegular = await orchestrator.GetGlobalChangelogPathAsync();
        }
        else
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] Looking for regular file: {filename}");
            filePathRegular = await orchestrator.GetFilePathAsync(filename);
        }

        WriteConsoleLog($"[DEBUG] [{clientIp}] Final filePath: {filePathRegular}");
        WriteConsoleLog(filePathRegular != null
            ? $"[DEBUG] [{clientIp}] File exists: {File.Exists(filePathRegular)}"
            : $"[DEBUG] [{clientIp}] File path is null");

        if (filePathRegular is null || !File.Exists(filePathRegular))
        {
            WriteConsoleLog($"[DEBUG] [{clientIp}] File not found: {filename}");
            return Results.NotFound(new
            {
                error = "File not found",
                requested = filename
            });
        }

        return await ServePhysicalFile(filePathRegular, filename, context);
    }

    // Вспомогательный метод для определения pointer-файлов
    private static bool IsPointerFile(string filename)
    {
        var lowerFilename = filename.ToLowerInvariant();

        // Проверка основных паттернов
        return lowerFilename.StartsWith("latest.") ||
               lowerFilename.StartsWith("newest6") ||
               lowerFilename.StartsWith("newest7") ||
               lowerFilename.StartsWith("newesta6") ||
               lowerFilename.StartsWith("newesta7") ||
               (lowerFilename.Contains("stable") && !lowerFilename.Contains(".")) ||
               (lowerFilename.Contains("long-term") && !lowerFilename.Contains(".")) ||
               (lowerFilename.Contains("testing") && !lowerFilename.Contains(".")) ||
               (lowerFilename.Contains("development") && !lowerFilename.Contains("."));
    }

    // Вспомогательный метод для обслуживания физических файлов
    private static Task<IResult> ServePhysicalFile(string filePath, string filename, HttpContext context)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".npk" => "application/octet-stream",
            ".zip" => "application/zip",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            _ => "application/octet-stream"
        };

        // Для CHANGELOG не добавляем Content-Disposition
        if (!filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase))
        {
            var fileNameOnly = Path.GetFileName(filePath);
            context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileNameOnly}\"";
        }

        WriteConsoleLog($"[DEBUG] Serving file: {filePath} with contentType: {contentType}");
        var stream = File.OpenRead(filePath);
        var result = Results.File(stream, contentType, Path.GetFileName(filePath));
        return Task.FromResult(result);
    }


    // ===== Handlers =====
}

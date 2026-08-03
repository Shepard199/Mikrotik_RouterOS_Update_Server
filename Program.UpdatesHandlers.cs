using Microsoft.AspNetCore.Mvc;
using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public static partial class Program
{
    private static async Task<IResult> GetVersions(IUpdateOrchestrator orchestrator)
    {
        var data = await orchestrator.GetVersionsInfoAsync();
        return Results.Ok(data);
    }

    private static async Task<IResult> GetStatus(IUpdateOrchestrator orchestrator)
    {
        var data = await orchestrator.GetStatusInfoAsync();
        return Results.Ok(data);
    }

    private static async Task<IResult> TriggerUpdateCheck(IUpdateOrchestrator orchestrator)
    {
        var result = await orchestrator.CheckAndDownloadUpdatesAsync();
        var status = string.IsNullOrWhiteSpace(result.Error)
            ? result.Success
                ? "success"
                : "error"
            : result.Error;

        return status switch
        {
            "already_in_progress" => Results.Json(
                new
                {
                    code = "update_in_progress",
                    message = "Update check is already in progress"
                },
                statusCode: 409),

            "network_unavailable" => Results.Json(
                new
                {
                    code = "network_unavailable",
                    message = "Cannot reach MikroTik servers. Check internet connection or firewall settings.",
                    details = "Server cannot connect to upgrade.mikrotik.com"
                },
                statusCode: 503),

            "network_error" => Results.Json(
                new
                {
                    code = "network_error",
                    message = "Network error occurred during update check",
                    details = "Please check your internet connection"
                },
                statusCode: 503),

            "timeout" => Results.Json(
                new
                {
                    code = "timeout",
                    message = "Update check timed out",
                    details = "MikroTik servers took too long to respond"
                },
                statusCode: 504),

            "fetch_failed" => Results.Json(
                new
                {
                    code = "fetch_failed",
                    message = "Failed to fetch latest version information from MikroTik servers",
                    details = "Ensure upgrade.mikrotik.com is accessible"
                },
                statusCode: 503),

            "error" => Results.Json(
                new
                {
                    code = "internal_error",
                    message = "Unexpected error during update check"
                },
                statusCode: 500),

            "success" => Results.Ok(new
            {
                message = "Update check completed",
                downloaded = result.Downloaded,
                checkedVersions = result.CheckedVersions,
                timestamp = DateTime.UtcNow
            }),

            _ => Results.Json(
                new
                {
                    code = "unknown_error",
                    message = $"Unknown status: {status}"
                },
                statusCode: 500)
        };
    }

    private static async Task<IResult> SetActiveVersion(
        string version,
        IUpdateOrchestrator orchestrator)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        try
        {
            var result = await orchestrator.SetActiveVersionAsync(version);
            if (!result)
                return Results.Json(
                    new {code = "version_not_found", message = $"Version {version} not found"},
                    statusCode: 404);

            return Results.Ok(new {message = "Active version updated", version});
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to set active version"},
                statusCode: 500);
        }
    }

    private static async Task<IResult> RemoveVersion(
        string version,
        [FromQuery] string? branch,
        IUpdateOrchestrator orchestrator)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        if (!TryNormalizeVersionBranch(branch, out var normalizedBranch))
            return Results.Json(
                new {code = "bad_request", message = "Branch must be 'v6' or 'v7'"},
                statusCode: 400);

        try
        {
            var result = await orchestrator.RemoveVersionAsync(version, normalizedBranch);
            if (!result)
                return Results.Json(
                    new {code = "version_protected", message = $"Version {version} is active or protected"},
                    statusCode: 409);

            return Results.Ok(new {message = "Version removed", version});
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to remove version"},
                statusCode: 500);
        }
    }

    private static async Task<IResult> RemoveVersions(
        [FromBody] VersionsBulkDeleteRequest request,
        IUpdateOrchestrator orchestrator)
    {
        var rawVersions = request.Versions ?? [];
        var versions = rawVersions
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (versions.Length == 0)
            return Results.Json(
                new {code = "bad_request", message = "At least one version must be provided"},
                statusCode: 400);

        if (!TryNormalizeVersionBranch(request.Branch, out var normalizedBranch))
            return Results.Json(
                new {code = "bad_request", message = "Branch must be 'v6' or 'v7'"},
                statusCode: 400);

        var deleted = new List<string>();
        var failed = new List<object>();

        foreach (var version in versions)
        {
            try
            {
                var removed = await orchestrator.RemoveVersionAsync(version, normalizedBranch);
                if (removed)
                {
                    deleted.Add(version);
                }
                else
                {
                    failed.Add(new {version, reason = "active_or_missing"});
                }
            }
            catch
            {
                failed.Add(new {version, reason = "error"});
            }
        }

        return Results.Ok(new
        {
            deleted = deleted.Count,
            versions = deleted,
            failed
        });
    }

    private static bool TryNormalizeVersionBranch(string? branch, out string? normalizedBranch)
    {
        normalizedBranch = null;
        if (string.IsNullOrWhiteSpace(branch))
            return true;

        if (branch.Trim().Equals("v6", StringComparison.OrdinalIgnoreCase))
        {
            normalizedBranch = "v6";
            return true;
        }

        if (branch.Trim().Equals("v7", StringComparison.OrdinalIgnoreCase))
        {
            normalizedBranch = "v7";
            return true;
        }

        return false;
    }

    private static async Task<IResult> DownloadFile(
        string version,
        string filename,
        IUpdateOrchestrator orchestrator,
        HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(filename))
            return Results.Json(
                new {code = "bad_request", message = "Version and filename are required"},
                statusCode: 400);

        var filePath = await orchestrator.GetFilePathAsync(version, filename);
        if (filePath == null || !File.Exists(filePath))
            filePath = await orchestrator.EnsureFileDownloadedAsync(version, filename);

        if (filePath == null || !File.Exists(filePath))
            return Results.Json(
                new {code = "file_not_found", message = $"File not found: {version}/{filename}"},
                statusCode: 404);

        try
        {
            var fileInfo = new FileInfo(filePath);
            var etag = $"\"{fileInfo.LastWriteTimeUtc.Ticks}\"";
            context.Response.Headers["ETag"] = etag;

            if (context.Request.Headers.TryGetValue("If-None-Match", out var clientEtag) &&
                clientEtag == etag)
                return Results.StatusCode(304);

            var stream = File.OpenRead(filePath);
            return Results.File(stream, "application/octet-stream", filename);
        }
        catch
        {
            return Results.Json(
                new {code = "internal_error", message = "Failed to download file"},
                statusCode: 500);
        }
    }

    private static IResult GetTodayClientUpdates(
        ILogStore store,
        [FromQuery] int take = 20)
    {
        if (take <= 0)
            take = 20;
        if (take > 100)
            take = 100;

        var todayStartUtc = DateTime.UtcNow.Date;
        var logs = store.Query(null, "/routeros/", 1000);
        var byClient = new Dictionary<string, ClientUpdateActivity>(StringComparer.OrdinalIgnoreCase);
        ClientDownloadSnapshot? latestDownload = null;

        foreach (var entry in logs)
        {
            if (entry.Timestamp < todayStartUtc)
                continue;

            if (!string.Equals(entry.Source, "HTTP", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryParseHttpAccessLog(entry.Message, out var ipAddress, out var method, out var path,
                    out var statusCode))
                continue;

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                continue;

            if (statusCode < 200 || statusCode >= 400)
                continue;

            if (!path.StartsWith("/routeros/", StringComparison.OrdinalIgnoreCase))
                continue;

            var normalizedIp = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress.Trim();
            var version = ExtractVersionFromRouterPath(path);
            var fileName = ExtractFileFromRouterPath(path);

            if (!byClient.TryGetValue(normalizedIp, out var aggregate))
            {
                aggregate = new ClientUpdateActivity(normalizedIp);
                byClient[normalizedIp] = aggregate;
            }

            aggregate.RequestCount++;
            if (entry.Timestamp >= aggregate.LastSeenUtc)
            {
                aggregate.LastSeenUtc = entry.Timestamp;
                aggregate.LastVersion = version;
                aggregate.LastFile = fileName;
            }

            if (!string.IsNullOrWhiteSpace(version))
                aggregate.Versions.Add(version);

            if (latestDownload is null || entry.Timestamp >= latestDownload.LastSeenUtc)
                latestDownload = new ClientDownloadSnapshot
                {
                    ClientIp = normalizedIp,
                    Version = version,
                    FileName = fileName,
                    LastSeenUtc = entry.Timestamp
                };
        }

        var rows = byClient.Values
            .OrderByDescending(x => x.LastSeenUtc)
            .Take(take)
            .Select(x => new
            {
                clientIp = x.ClientIp,
                version = !string.IsNullOrWhiteSpace(x.LastVersion)
                    ? x.LastVersion
                    : x.Versions.Count > 0
                        ? string.Join(", ", x.Versions
                            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
                            .Take(3))
                        : "-",
                file = string.IsNullOrWhiteSpace(x.LastFile) ? "-" : x.LastFile,
                requests = x.RequestCount,
                lastSeen = x.LastSeenUtc
            })
            .ToArray();

        var nowUtc = DateTime.UtcNow;
        var currentDownload = latestDownload is null
            ? null
            : new
            {
                clientIp = latestDownload.ClientIp,
                version = latestDownload.Version ?? "-",
                file = latestDownload.FileName ?? "-",
                lastSeen = latestDownload.LastSeenUtc,
                isActive = latestDownload.LastSeenUtc >= nowUtc.AddSeconds(-45)
            };

        return Results.Ok(new
        {
            dateUtc = todayStartUtc,
            count = rows.Length,
            currentDownload,
            data = rows
        });
    }

    private static bool TryParseHttpAccessLog(
        string message,
        out string ipAddress,
        out string method,
        out string path,
        out int statusCode)
    {
        ipAddress = "";
        method = "";
        path = "";
        statusCode = 0;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var openBracket = message.IndexOf('[');
        var closeBracket = message.IndexOf(']');
        if (openBracket < 0 || closeBracket <= openBracket)
            return false;

        ipAddress = message[(openBracket + 1)..closeBracket].Trim();

        var dashIndex = message.IndexOf(" - ", closeBracket, StringComparison.Ordinal);
        if (dashIndex < 0)
            return false;

        var arrowIndex = message.IndexOf(" -> ", dashIndex + 3, StringComparison.Ordinal);
        if (arrowIndex < 0)
            return false;

        var requestPart = message[(dashIndex + 3)..arrowIndex].Trim();
        var firstSpace = requestPart.IndexOf(' ');
        if (firstSpace <= 0 || firstSpace >= requestPart.Length - 1)
            return false;

        method = requestPart[..firstSpace].Trim();
        path = requestPart[(firstSpace + 1)..].Trim();

        var statusStart = arrowIndex + 4;
        var statusEnd = message.IndexOf(' ', statusStart);
        var statusText = statusEnd > statusStart
            ? message[statusStart..statusEnd]
            : message[statusStart..].Trim();

        return int.TryParse(statusText, out statusCode);
    }

    private static string? ExtractVersionFromRouterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var cleanPath = path.Split('?', 2)[0];
        var segments = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 3)
            return null;

        if (!segments[0].Equals("routeros", StringComparison.OrdinalIgnoreCase))
            return null;

        var version = segments[1].Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string? ExtractFileFromRouterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var cleanPath = path.Split('?', 2)[0];
        var segments = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
            return null;

        if (!segments[0].Equals("routeros", StringComparison.OrdinalIgnoreCase))
            return null;

        var file = segments.Length >= 3 ? segments[2] : segments[1];
        file = file.Trim();
        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    private static IResult HandleError(HttpContext context)
    {
        return Results.Json(
            new
            {
                error = "Internal Server Error",
                timestamp = DateTime.UtcNow,
                traceId = context.TraceIdentifier
            },
            statusCode: 500);
    }

    private static async Task<IResult> GetVersionHistory(
        IUpdateOrchestrator orchestrator,
        [FromQuery] int take = 50)
    {
        var history = await orchestrator.GetVersionHistoryAsync(take);
        return Results.Ok(new
        {
            count = history.Count,
            data = history
        });
    }

    private static async Task<IResult> GetGlobalChangelog(IUpdateOrchestrator orchestrator)
    {
        var content = await orchestrator.GetGlobalChangelogContentAsync();
        if (content is null)
            return Results.Text(string.Empty, "text/plain; charset=utf-8");

        return Results.Text(content, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> GetVersionChangelog(
        string version,
        IUpdateOrchestrator orchestrator)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Results.Json(
                new {code = "bad_request", message = "Version parameter is required"},
                statusCode: 400);

        var content = await orchestrator.GetChangelogContentAsync(version);
        if (content is null)
            return Results.Text(string.Empty, "text/plain; charset=utf-8");

        return Results.Text(content, "text/plain; charset=utf-8");
    }
}

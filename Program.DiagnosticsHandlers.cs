using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using MikroTik.UpdateServer.Services;

namespace MikroTik.UpdateServer;

public static partial class Program
{
    private static async Task<IResult> GetDiagnostics(IUpdateOrchestrator orchestrator)
    {
        try
        {
            var upgradeProbe =
                await ProbeTlsEndpointAsync("https://upgrade.mikrotik.com/routeros/NEWEST7.stable", 5);
            var downloadProbe =
                await ProbeTlsEndpointAsync("https://download.mikrotik.com/routeros/", 5);
            var internetProbe =
                await ProbeTlsEndpointAsync("https://www.google.com/", 5);

            var connectivity = new
            {
                mikrotikServer = upgradeProbe.IsSuccess ? "✓ Connected" : "✗ Network Error",
                details = upgradeProbe.IsSuccess
                    ? upgradeProbe.Message
                    : BuildConnectivityDetails(upgradeProbe),
                probes = new
                {
                    upgrade = ToTlsProbePayload(upgradeProbe),
                    download = ToTlsProbePayload(downloadProbe),
                    internet = ToTlsProbePayload(internetProbe)
                },
                tlsRuntime = ToTlsRuntimePayload(GetTlsRuntimeInfo())
            };

            var diagnostics = new
            {
                timestamp = DateTime.UtcNow,
                server = new
                {
                    framework = ".NET " + RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    processorCount = Environment.ProcessorCount,
                    workingDirectory = AppContext.BaseDirectory
                },
                network = connectivity,
                versions = await orchestrator.GetVersionsInfoAsync(),
                status = await orchestrator.GetStatusInfoAsync()
            };

            return Results.Ok(diagnostics);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new
                {
                    code = "diagnostics_error",
                    message = ex.Message,
                    timestamp = DateTime.UtcNow
                },
                statusCode: 500);
        }
    }

    private static async Task<IResult> GetPointerRouting(IUpdateOrchestrator orchestrator)
    {
        try
        {
            var data = await orchestrator.GetPointerRoutingAsync();
            return Results.Ok(data);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error loading pointer routing: {ex.Message}");
        }
    }

    private static async Task<IResult> UpdatePointerRouting(
        IUpdateOrchestrator orchestrator,
        PointerRouteUpdateRequest request)
    {
        var pointer = request.Pointer ?? string.Empty;
        var branch = request.Branch ?? string.Empty;

        var (success, error) = await orchestrator.SetPointerBranchRouteAsync(pointer, branch);
        if (!success)
            return Results.Json(
                new {code = "bad_request", message = error ?? "Failed to update pointer route"},
                statusCode: 400);

        return Results.Ok(new
        {
            message = "Pointer route updated",
            pointer,
            branch
        });
    }

    private static async Task<IResult> GetTlsHealth(
        [FromQuery] string? target,
        [FromQuery] int timeoutSeconds = 8)
    {
        var normalizedTarget = NormalizeTlsTarget(target);
        var probe = await ProbeTlsEndpointAsync(normalizedTarget, timeoutSeconds);
        var tlsRuntime = GetTlsRuntimeInfo();

        return Results.Ok(new
        {
            timestamp = DateTime.UtcNow,
            probe = ToTlsProbePayload(probe),
            tlsRuntime = ToTlsRuntimePayload(tlsRuntime)
        });
    }

    private static string BuildConnectivityDetails(TlsProbeResult probe)
    {
        if (probe.IsSuccess)
            return probe.Message;

        if (!string.IsNullOrWhiteSpace(probe.Recommendation))
            return $"{probe.Message} ({probe.Recommendation})";

        return probe.Message;
    }

    private static string NormalizeTlsTarget(string? target)
    {
        const string fallback = "https://upgrade.mikrotik.com/routeros/NEWEST7.stable";

        if (string.IsNullOrWhiteSpace(target))
            return fallback;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed))
            return fallback;

        return parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? parsed.ToString()
            : fallback;
    }

    private static async Task<TlsProbeResult> ProbeTlsEndpointAsync(string targetUrl, int timeoutSeconds)
    {
        var timeout = Math.Clamp(timeoutSeconds, 2, 30);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, targetUrl);
            using var response = await client.SendAsync(request);

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            return new TlsProbeResult
            {
                Target = targetUrl,
                IsSuccess = response.IsSuccessStatusCode,
                Status = response.IsSuccessStatusCode ? "ok" : "http_error",
                HttpStatus = (int) response.StatusCode,
                LatencyMs = (long) elapsed.TotalMilliseconds,
                Message = $"HTTP {(int) response.StatusCode}"
            };
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var chain = BuildExceptionChain(ex);
            var failureCategory = ClassifyTlsFailure(chain);

            return new TlsProbeResult
            {
                Target = targetUrl,
                IsSuccess = false,
                Status = "error",
                LatencyMs = (long) elapsed.TotalMilliseconds,
                Message = ex.Message,
                FailureCategory = failureCategory,
                Recommendation = GetTlsRecommendation(failureCategory),
                ExceptionChain = chain
            };
        }
    }

    private static List<TlsExceptionInfo> BuildExceptionChain(Exception ex)
    {
        var chain = new List<TlsExceptionInfo>();
        var current = ex;

        while (current is not null)
        {
            chain.Add(new TlsExceptionInfo
            {
                Type = current.GetType().FullName ?? current.GetType().Name,
                Message = current.Message,
                HResult = $"0x{current.HResult:X8}",
                SocketError = current is SocketException socket ? socket.SocketErrorCode.ToString() : null,
                NativeErrorCode = current is Win32Exception win32 ? win32.NativeErrorCode : null
            });

            current = current.InnerException;
        }

        return chain;
    }

    private static string ClassifyTlsFailure(IReadOnlyCollection<TlsExceptionInfo> chain)
    {
        static bool HasToken(TlsExceptionInfo x, string token)
        {
            return x.Type.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                   x.Message.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        if (chain.Any(x =>
                string.Equals(x.HResult, "0x8009030E", StringComparison.OrdinalIgnoreCase) ||
                HasToken(x, "SEC_E_NO_CREDENTIALS") ||
                x.Message.Contains("отсутствуют учетные данные", StringComparison.OrdinalIgnoreCase)))
            return "tls_client_credentials_missing";

        if (chain.Any(x => HasToken(x, "SocketException")) &&
            chain.Any(x => HasToken(x, "forbidden by access permissions") ||
                           x.Message.Contains("запрещенным правами доступа", StringComparison.OrdinalIgnoreCase)))
            return "socket_access_denied";

        if (chain.Any(x => HasToken(x, nameof(TaskCanceledException)) || HasToken(x, "timeout")))
            return "timeout";

        if (chain.Any(x => HasToken(x, "AuthenticationException")))
            return "tls_authentication_failed";

        if (chain.Any(x => HasToken(x, "HttpRequestException")))
            return "http_request_error";

        return "unknown";
    }

    private static string GetTlsRecommendation(string failureCategory)
    {
        return failureCategory switch
        {
            "tls_client_credentials_missing" =>
                "TLS stack for current account is broken. Run under another account and repair Schannel/SSPI.",
            "socket_access_denied" =>
                "Outbound TCP/443 is blocked by local policy, firewall, or endpoint protection.",
            "timeout" =>
                "Remote endpoint is reachable slowly or blocked in transit. Check route, proxy, and DNS.",
            "tls_authentication_failed" =>
                "TLS handshake failed. Check system certificates, crypto providers, and HTTPS inspection.",
            "http_request_error" =>
                "Request failed before successful HTTP response. Verify connectivity and TLS policy.",
            _ =>
                "Unknown network/TLS failure. Check detailed exception chain and Windows Schannel logs."
        };
    }

    private static TlsRuntimeInfo GetTlsRuntimeInfo()
    {
        if (!OperatingSystem.IsWindows())
            return new TlsRuntimeInfo
            {
                Warnings = ["TLS registry diagnostics are available on Windows only."]
            };

        var securityProviders = ReadRegistryString(
            @"SYSTEM\CurrentControlSet\Control\SecurityProviders",
            "SecurityProviders");

        var lsaSecurityPackages = ReadRegistryMultiString(
            @"SYSTEM\CurrentControlSet\Control\Lsa",
            "Security Packages");

        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(securityProviders))
            warnings.Add("SecurityProviders registry value is empty.");
        else if (!securityProviders.Contains("schannel.dll", StringComparison.OrdinalIgnoreCase))
            warnings.Add("schannel.dll is missing in SecurityProviders.");

        if (lsaSecurityPackages.Count == 0) warnings.Add("Lsa\\Security Packages is empty.");

        return new TlsRuntimeInfo
        {
            SecurityProviders = securityProviders ?? string.Empty,
            LsaSecurityPackages = lsaSecurityPackages,
            Warnings = warnings
        };
    }

    private static object ToTlsProbePayload(TlsProbeResult probe)
    {
        return new
        {
            target = probe.Target,
            isSuccess = probe.IsSuccess,
            status = probe.Status,
            httpStatus = probe.HttpStatus,
            latencyMs = probe.LatencyMs,
            message = probe.Message,
            failureCategory = probe.FailureCategory,
            recommendation = probe.Recommendation,
            exceptionChain = probe.ExceptionChain?.Select(ToTlsExceptionPayload).ToArray()
        };
    }

    private static object ToTlsExceptionPayload(TlsExceptionInfo info)
    {
        return new
        {
            type = info.Type,
            message = info.Message,
            hResult = info.HResult,
            socketError = info.SocketError,
            nativeErrorCode = info.NativeErrorCode
        };
    }

    private static object ToTlsRuntimePayload(TlsRuntimeInfo runtime)
    {
        return new
        {
            securityProviders = runtime.SecurityProviders,
            lsaSecurityPackages = runtime.LsaSecurityPackages,
            warnings = runtime.Warnings
        };
    }

    private static string? ReadRegistryString(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var value = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{subKey}", valueName, null);
            return value as string;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadRegistryMultiString(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            var value = key?.GetValue(valueName);

            if (value is string[] many)
                return many
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x != "\"\"")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (value is string one)
                return one
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x != "\"\"")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch
        {
            // intentionally ignored, diagnostics endpoint should stay resilient
        }

        return [];
    }
}
namespace MikroTik.UpdateServer;

public static partial class Program
{
    private sealed class ClientUpdateActivity(string clientIp)
    {
        public string ClientIp { get; } = clientIp;
        public int RequestCount { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;
        public string? LastVersion { get; set; }
        public string? LastFile { get; set; }
        public HashSet<string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ClientDownloadSnapshot
    {
        public string ClientIp { get; init; } = "unknown";
        public string? Version { get; init; }
        public string? FileName { get; init; }
        public DateTime LastSeenUtc { get; init; }
    }

    private sealed class ConsoleLogSettingsRequest
    {
        public bool Enabled { get; init; }
        public string? Level { get; init; }
    }

    private sealed class VersionsBulkDeleteRequest
    {
        public string[] Versions { get; init; } = [];
        public string? Branch { get; init; }
    }

    private readonly record struct PointerRouteUpdateRequest(string? Pointer, string? Branch);

    private sealed class TlsProbeResult
    {
        public string Target { get; init; } = "";
        public bool IsSuccess { get; init; }
        public string Status { get; init; } = "unknown";
        public int? HttpStatus { get; init; }
        public long LatencyMs { get; init; }
        public string Message { get; init; } = "";
        public string? FailureCategory { get; init; }
        public string? Recommendation { get; init; }
        public IReadOnlyCollection<TlsExceptionInfo>? ExceptionChain { get; init; }
    }

    private sealed class TlsExceptionInfo
    {
        public string Type { get; init; } = "";
        public string Message { get; init; } = "";
        public string HResult { get; init; } = "";
        public string? SocketError { get; init; }
        public int? NativeErrorCode { get; init; }
    }

    private sealed class TlsRuntimeInfo
    {
        public string SecurityProviders { get; init; } = "";
        public IReadOnlyCollection<string> LsaSecurityPackages { get; init; } = [];
        public IReadOnlyCollection<string> Warnings { get; init; } = [];
    }
}

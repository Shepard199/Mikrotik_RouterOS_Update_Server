using System.Text.Json.Serialization;

namespace MikroTik.UpdateServer;

public class VersionLog
{
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; }

    [JsonPropertyName("v6Stable")] public string V6Stable { get; init; } = string.Empty;

    [JsonPropertyName("v7Fixed")] public string V7Fixed { get; init; } = string.Empty;

    [JsonPropertyName("v7Stable")] public string V7Stable { get; init; } = string.Empty;
}
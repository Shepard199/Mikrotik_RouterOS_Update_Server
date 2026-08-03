using System.Text.Json;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Сервис управления метаданными версий
/// </summary>
public class MetadataService : IMetadataService
{
    private static readonly string[] DefaultAllowedArches =
    [
        "arm", "arm64", "mipsbe", "mmips", "smips", "tile", "ppc", "x86"
    ];

    private readonly string _allowedArchesFile;

    private readonly IFileMetadataCacheService _cacheService;
    private readonly string _lastCheckFile;
    private readonly ILogger<MetadataService> _logger;

    public MetadataService(
        ILogger<MetadataService> logger,
        IFileMetadataCacheService cacheService)
    {
        _logger = logger;
        _cacheService = cacheService;
        var baseFolder = Path.Combine(AppContext.BaseDirectory, "routeros");
        _lastCheckFile = Path.Combine(AppContext.BaseDirectory, "last_check.json");
        _allowedArchesFile = Path.Combine(AppContext.BaseDirectory, "allowed_arches.json");

        Directory.CreateDirectory(baseFolder);
        _logger.LogInformation("MetadataService initialized");
    }

    public async Task<DateTime> GetLastCheckTimeAsync()
    {
        try
        {
            if (!File.Exists(_lastCheckFile))
                return DateTime.MinValue;

            var content = await File.ReadAllTextAsync(_lastCheckFile);
            if (DateTime.TryParse(content, out var lastCheck))
                return lastCheck;

            return DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading last check time");
            return DateTime.MinValue;
        }
    }

    public async Task SetLastCheckTimeAsync()
    {
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            await File.WriteAllTextAsync(_lastCheckFile, now);
            _logger.LogDebug("Updated last check time");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving last check time");
        }
    }

    public async Task<string[]> GetAllowedArchitecturesAsync()
    {
        try
        {
            if (!File.Exists(_allowedArchesFile))
                return DefaultAllowedArches;

            var json = await File.ReadAllTextAsync(_allowedArchesFile);
            var arches = JsonSerializer.Deserialize<string[]>(json);

            if (arches is {Length: > 0})
            {
                var normalized = arches
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToArray();

                if (normalized.Length > 0)
                {
                    _logger.LogInformation("Loaded {Count} allowed architectures", normalized.Length);
                    return normalized;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading allowed architectures");
        }

        _logger.LogInformation("Using default architectures");
        return DefaultAllowedArches;
    }

    public async Task SetAllowedArchitecturesAsync(string[] arches)
    {
        try
        {
            var normalized = arches
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim().ToLowerInvariant())
                .Distinct()
                .ToArray();

            if (normalized.Length == 0)
                normalized = DefaultAllowedArches;

            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions {WriteIndented = true});
            await File.WriteAllTextAsync(_allowedArchesFile, json);

            _logger.LogInformation("Updated allowed architectures: {Arches}", string.Join(", ", normalized));
            _cacheService.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving allowed architectures");
            throw;
        }
    }
}
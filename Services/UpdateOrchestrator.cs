using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace MikroTik.UpdateServer.Services;

/// <summary>
///     Главный оркестратор обновлений - координирует все сервисы
///     Заменяет монолитный MikroTikUpdateService
/// </summary>
internal sealed class UpdateOrchestrator(
    IVersionManagementService versionService,
    IFileDownloadService downloadService,
    IFileStorageService storageService,
    IMetadataService metadataService,
    IConnectivityService connectivityService,
    ILogger<UpdateOrchestrator> logger,
    IConfiguration configuration)
    : IUpdateOrchestrator
{
    private static readonly string[] ManagedPointerFiles =
    [
        "LATEST.6",
        "NEWEST6.stable",
        "NEWESTa6.stable",
        "NEWESTa6.long-term",
        "NEWEST6.upgrade",
        "NEWESTa6.upgrade",
        "NEWEST7.long-term",
        "NEWESTa7.long-term",
        "NEWEST7.stable",
        "NEWESTa7.stable",
        "LATEST.7",
        "NEWESTa6.development",
        "NEWEST6.development",
        "NEWESTa7.development",
        "NEWEST7.development",
        "NEWESTa6.testing",
        "NEWEST6.testing",
        "NEWESTa7.testing",
        "NEWEST7.testing",
        "NEWESTa6.release-candidate",
        "NEWEST6.release-candidate",
        "NEWESTa7.release-candidate",
        "NEWEST7.release-candidate"
    ];

    private static readonly HashSet<string> AllowedPointerBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        "v6",
        "v7Fixed",
        "v7Latest"
    };

    private readonly Lock _cpuLock = new();
    private readonly Lock _pointerRoutesLock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _onDemandDownloadLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unavailableV7ExtraPackageUrls =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _upstreamClient = CreateUpstreamClient();
    private readonly Lock _versionsHistoryLock = new();
    private int _isChecking;
    private DateTime _lastCpuCheck = DateTime.MinValue;
    private double _lastCpuValue;
    private string? _lastV6ExtractionSignature;

    public async Task<UpdateCheckResult> CheckAndDownloadUpdatesAsync(string checkType = "stable")
    {
        if (Interlocked.Exchange(ref _isChecking, 1) != 0)
            return new UpdateCheckResult
            {
                Success = false,
                Error = "already_in_progress",
                Timestamp = DateTime.UtcNow
            };

        try
        {
            if (checkType.Equals("stable", StringComparison.OrdinalIgnoreCase))
                return await CheckAndDownloadStableAsync();

            var result = new UpdateCheckResult {Timestamp = DateTime.UtcNow};

            logger.LogInformation("Starting update check: {CheckType}", checkType);

            // 1. Проверяем подключение
            var mikrotikOk = await connectivityService.CheckMikroTikConnectivityAsync();
            var internetOk = await connectivityService.CheckInternetConnectivityAsync();

            if (!mikrotikOk || !internetOk)
            {
                logger.LogWarning("Connectivity check failed - MikroTik: {MikroTik}, Internet: {Internet}",
                    mikrotikOk ? "✓" : "✗", internetOk ? "✓" : "✗");
                result.Error = "network_unavailable";
                return result;
            }

            // 2. Получаем текущие версии
            var versions = await versionService.GetAvailableVersionsAsync();
            if (versions.Length == 0)
            {
                logger.LogWarning("No available versions found");
                result.Error = "fetch_failed";
                return result;
            }

            logger.LogInformation("Found {Count} available versions", versions.Length);

            // 3. Фильтруем по типу проверки (stable, testing, rc, development)
            var filteredVersions = FilterVersionsByCheckType(versions, checkType);
            result.Total = filteredVersions.Length;
            result.CheckedVersions = filteredVersions
                .Select(v => $"{v.Branch}:{v.Version}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (filteredVersions.Length == 0)
            {
                logger.LogInformation("No versions match check type: {CheckType}", checkType);
                result.Downloaded = 0;
                result.Success = true;
                await metadataService.SetLastCheckTimeAsync();
                return result;
            }

            // 4. Проверяем и скачиваем файлы
            var allowedArches = await metadataService.GetAllowedArchitecturesAsync();
            var filesToDownload = PrepareFilesForDownload(filteredVersions, allowedArches);

            logger.LogInformation("Preparing to download {Count} files", filesToDownload.Count);

            var downloadedCount = 0;
            foreach (var file in filesToDownload)
                try
                {
                    var downloadResult = await downloadService.DownloadFileAsync(
                        file.Url,
                        file.LocalPath);

                    if (downloadResult.Success)
                    {
                        downloadedCount++;
                        // Инвалидируем кеш 404 для скачанного файла
                        NotFoundCacheMiddleware.InvalidatePath($"/{file.FileName}");
                        logger.LogDebug("Downloaded: {File} ({Size})", file.FileName,
                            FormatSize(downloadResult.BytesDownloaded));
                    }
                    else
                    {
                        logger.LogWarning("Failed to download: {File}", file.FileName);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error downloading {File}", file.FileName);
                }

            result.Downloaded = downloadedCount;
            result.Success = true;

            // 5. Обновляем метаданные
            await metadataService.SetLastCheckTimeAsync();

            // 6. Очищаем старые версии если нужно
            var maxVersionsToKeep = configuration.GetValue("UpdateCheckOptions:MaxVersionsToKeep", 5);
            await CleanupOldVersionsAsync(versions, maxVersionsToKeep);

            // После загрузки новых файлов очищаем весь кеш 404
            if (downloadedCount > 0)
                NotFoundCacheMiddleware.ClearCache();

            logger.LogInformation("Update check completed: Downloaded {Downloaded}/{Total}", downloadedCount,
                result.Total);

            return result;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "Timeout during update check");
            return new UpdateCheckResult
            {
                Success = false,
                Error = "timeout",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error during update check");
            return new UpdateCheckResult
            {
                Success = false,
                Error = "network_error",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during update check");
            return new UpdateCheckResult
            {
                Success = false,
                Error = "error",
                Timestamp = DateTime.UtcNow
            };
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    public async Task<object> GetVersionsInfoAsync()
    {
        var baseFolder = GetRouterOsBaseFolder();
        var v6Dir = Path.Combine(baseFolder, "v6");
        var v7Dir = Path.Combine(baseFolder, "v7");

        var v6Versions = BuildVersionEntries(v6Dir);
        var v7Versions = BuildVersionEntries(v7Dir);

        var (activeV6, activeV7Fixed, activeV7Latest) = await GetActiveVersionsFromHistoryAsync();
        var lastCheck = await metadataService.GetLastCheckTimeAsync();

        object payload = new
        {
            v6 = new {active = activeV6, versions = v6Versions},
            v7 = new {activeFixed = activeV7Fixed, activeLatest = activeV7Latest, versions = v7Versions},
            lastCheck = lastCheck == DateTime.MinValue ? (DateTime?) null : lastCheck
        };

        return payload;
    }

    public async Task<object> GetStatusInfoAsync()
    {
        var baseFolder = GetRouterOsBaseFolder();
        var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;
        var totalBytes = await storageService.GetTotalSizeAsync();
        var fileCount = await storageService.GetFileCountAsync();
        var totalGb = Math.Round(totalBytes / 1024d / 1024d / 1024d, 2);
        var (activeV6, activeV7Fixed, activeV7Latest) = await GetActiveVersionsFromHistoryAsync();
        var lastCheck = await metadataService.GetLastCheckTimeAsync();

        ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

        object payload = new
        {
            status = "online",
            timestamp = DateTime.UtcNow,
            uptime = new {days = uptime.Days, hours = uptime.Hours, minutes = uptime.Minutes},
            process = new
            {
                memory = (process.WorkingSet64 / 1024.0 / 1024.0).ToString("F2") + " MB",
                threads = new
                {
                    threadPoolActive = ThreadPool.ThreadCount,
                    workerThreadsAvailable = workerThreads,
                    maxWorkerThreads,
                    completionPortThreadsAvailable = completionPortThreads,
                    maxCompletionPortThreads
                },
                cpuUsage = GetCpuUsage()
            },
            activeVersions = new {v6 = activeV6, v7Fixed = activeV7Fixed, v7Latest = activeV7Latest},
            diskUsage = new
            {
                totalMB = (totalBytes / 1024.0 / 1024.0).ToString("F2"),
                totalGB = (totalBytes / 1024.0 / 1024.0 / 1024.0).ToString("F2")
            },
            downloads = new
            {
                total = totalGb,
                totalGb,
                totalBytes,
                files = fileCount,
                activity = (object?) null
            },
            lastCheck = lastCheck == DateTime.MinValue ? (DateTime?) null : lastCheck,
            settings = new
            {
                updatesFolder = baseFolder
            }
        };

        return payload;
    }

    public async Task<bool> SetActiveVersionAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var normalizedVersion = version.Trim();
        var baseFolder = GetRouterOsBaseFolder();
        var v6Dir = Path.Combine(baseFolder, "v6", normalizedVersion);
        var v7Dir = Path.Combine(baseFolder, "v7", normalizedVersion);

        var (activeV6, activeV7Fixed, activeV7Latest) = await GetActiveVersionsFromHistoryAsync();

        if (Directory.Exists(v6Dir))
        {
            activeV6 = normalizedVersion;
            await AppendVersionHistoryAsync(activeV6, activeV7Fixed, activeV7Latest);
            return true;
        }

        if (Directory.Exists(v7Dir))
        {
            if (normalizedVersion.Equals(activeV7Fixed, StringComparison.OrdinalIgnoreCase))
                activeV7Fixed = normalizedVersion;
            else
                activeV7Latest = normalizedVersion;

            await AppendVersionHistoryAsync(activeV6, activeV7Fixed, activeV7Latest);
            return true;
        }

        return false;
    }

    public async Task<bool> RemoveVersionAsync(string version, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        try
        {
            var normalizedVersion = version.Trim();
            var normalizedBranch = NormalizeVersionBranch(branch);
            if (!string.IsNullOrWhiteSpace(branch) && normalizedBranch is null)
                return false;

            var (activeV6, activeV7Fixed, activeV7Latest) = await GetActiveVersionsFromHistoryAsync();

            var allowV6 = normalizedBranch is null || normalizedBranch.Equals("v6", StringComparison.Ordinal);
            var allowV7 = normalizedBranch is null || normalizedBranch.Equals("v7", StringComparison.Ordinal);

            if (allowV6 && normalizedVersion.Equals(activeV6, StringComparison.OrdinalIgnoreCase))
                return false;
            if (allowV7 &&
                (normalizedVersion.Equals(activeV7Fixed, StringComparison.OrdinalIgnoreCase) ||
                 normalizedVersion.Equals(activeV7Latest, StringComparison.OrdinalIgnoreCase)))
                return false;

            var baseFolder = GetRouterOsBaseFolder();
            var removed = false;

            if (allowV6)
            {
                var v6Dir = Path.Combine(baseFolder, "v6", normalizedVersion);
                if (Directory.Exists(v6Dir))
                {
                    Directory.Delete(v6Dir, true);
                    removed = true;
                }
            }

            if (allowV7)
            {
                var v7Dir = Path.Combine(baseFolder, "v7", normalizedVersion);
                if (Directory.Exists(v7Dir))
                {
                    Directory.Delete(v7Dir, true);
                    removed = true;
                }
            }

            if (allowV6 || allowV7)
            {
                var legacyDir = Path.Combine(baseFolder, "routeros", normalizedVersion);
                if (Directory.Exists(legacyDir))
                {
                    Directory.Delete(legacyDir, true);
                    removed = true;
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error removing version {Version}", version);
            return false;
        }
    }

    public async Task<List<VersionLog>> GetVersionHistoryAsync(int take = 50)
    {
        var versionsFile = GetVersionsHistoryFilePath();
        if (!File.Exists(versionsFile))
            return [];

        try
        {
            var content = await File.ReadAllTextAsync(versionsFile);
            var logs = JsonSerializer.Deserialize<List<VersionLog>>(content) ?? [];

            return logs
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(take, 1, 500))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading version history from {Path}", versionsFile);
            return [];
        }
    }

    public async Task<string?> GetGlobalChangelogContentAsync()
    {
        try
        {
            var path = await GetGlobalChangelogPathAsync();
            return path is not null && File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading global changelog");
            return null;
        }
    }

    public async Task<string?> GetChangelogContentAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        try
        {
            var path = await GetChangelogPathAsync(version);
            return path is not null && File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading changelog for {Version}", version);
            return null;
        }
    }

    public async Task<string?> GetGlobalChangelogPathAsync()
    {
        var path = Path.Combine(GetRouterOsBaseFolder(), "CHANGELOG");
        return await Task.FromResult(File.Exists(path) ? path : null);
    }

    public async Task<string?> GetChangelogPathAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var baseFolder = GetRouterOsBaseFolder();
        var v6Path = Path.Combine(baseFolder, "v6", version, "CHANGELOG");
        if (File.Exists(v6Path))
            return await Task.FromResult<string?>(v6Path);

        var v7Path = Path.Combine(baseFolder, "v7", version, "CHANGELOG");
        return await Task.FromResult(File.Exists(v7Path) ? v7Path : null);
    }

    public async Task<string?> GetPackagesCsvPathAsync(string branchVersion)
    {
        if (string.IsNullOrWhiteSpace(branchVersion))
            return null;

        var baseFolder = GetRouterOsBaseFolder();
        var localPath = Path.Combine(baseFolder, "routeros", branchVersion, "packages.csv");
        if (File.Exists(localPath))
            return await Task.FromResult<string?>(localPath);

        var legacyPath = Path.Combine(baseFolder, "packages", $"{branchVersion}.csv");
        return await Task.FromResult(File.Exists(legacyPath) ? legacyPath : null);
    }

    public async Task<string?> GetFilePathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var baseFolder = GetRouterOsBaseFolder();
        var fullPath = Path.GetFullPath(Path.Combine(baseFolder, path));
        if (!fullPath.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase))
            return null;

        return await Task.FromResult(File.Exists(fullPath) ? fullPath : null);
    }

    public async Task<string?> GetFilePathAsync(string version, string filename)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(filename))
            return null;

        var baseFolder = GetRouterOsBaseFolder();
        var candidates = BuildVersionedFileNameCandidates(version, filename)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var searchFolders = new[]
        {
            Path.Combine(baseFolder, "routeros", version),
            Path.Combine(baseFolder, "v6", version),
            Path.Combine(baseFolder, "v7", version)
        };

        foreach (var folder in searchFolders)
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(folder, candidate);
            if (File.Exists(path))
                return await Task.FromResult<string?>(path);
        }

        return null;
    }

    public async Task<string?> EnsureFileDownloadedAsync(string version, string filename)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(filename))
            return null;

        var normalizedVersion = version.Trim();
        var normalizedFilename = filename.Trim();
        if (!CanDownloadFileOnDemand(normalizedVersion, normalizedFilename))
            return null;

        var existingPath = await GetFilePathAsync(normalizedVersion, normalizedFilename);
        if (existingPath is not null && File.Exists(existingPath))
            return existingPath;

        var lockKey = $"{normalizedVersion}/{normalizedFilename}";
        var downloadLock = _onDemandDownloadLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await downloadLock.WaitAsync();
        try
        {
            existingPath = await GetFilePathAsync(normalizedVersion, normalizedFilename);
            if (existingPath is not null && File.Exists(existingPath))
                return existingPath;

            var candidates = BuildVersionedFileNameCandidates(normalizedVersion, normalizedFilename)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            {
                var url = BuildOnDemandDownloadUrl(normalizedVersion, candidate);
                if (!await IsRemoteFileAvailableAsync(url))
                    continue;

                var localPath = BuildOnDemandLocalPath(normalizedVersion, candidate);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                var tempPath = $"{localPath}.{Guid.NewGuid():N}.download";
                try
                {
                    logger.LogInformation("On-demand download started for {Version}/{File} from {Url}",
                        normalizedVersion, candidate, url);

                    var result = await downloadService.DownloadFileAsync(url, tempPath);
                    if (!result.Success || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                    {
                        logger.LogWarning(
                            "On-demand download failed for {Version}/{File}: {Error}",
                            normalizedVersion,
                            candidate,
                            result.Error ?? "empty_file");
                        continue;
                    }

                    if (File.Exists(localPath))
                        File.Delete(localPath);

                    File.Move(tempPath, localPath);
                    NotFoundCacheMiddleware.InvalidatePath($"/routeros/{normalizedVersion}/{normalizedFilename}");
                    NotFoundCacheMiddleware.InvalidatePath($"/routeros/{normalizedVersion}/{candidate}");

                    logger.LogInformation("On-demand download completed for {Version}/{File}", normalizedVersion,
                        candidate);

                    return await GetFilePathAsync(normalizedVersion, normalizedFilename) ?? localPath;
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // ignore cleanup failures
                        }
                    }
                }
            }

            return null;
        }
        finally
        {
            downloadLock.Release();
        }
    }

    public string? GetPointerFileContent(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        var baseFolder = GetRouterOsBaseFolder();

        if (Directory.Exists(baseFolder))
        {
            var normalizedFilename = filename.ToLowerInvariant();
            var existingFile = Directory.GetFiles(baseFolder)
                .FirstOrDefault(f => Path.GetFileName(f).ToLowerInvariant() == normalizedFilename);

            if (!string.IsNullOrWhiteSpace(existingFile))
                try
                {
                    return File.ReadAllText(existingFile);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error reading pointer file {File}", filename);
                }
        }

        var pointerRoutes = LoadPointerRoutes();
        var v6Build = ReadBuildFromPointerFile("LATEST.6");
        var v7FixedBuild = ReadBuildFromPointerFile("NEWEST6.upgrade");
        var v7LatestBuild = ReadBuildFromPointerFile("NEWEST7.stable");
        var active = GetActiveVersionsFromHistory();
        var pointerMap = BuildPointerMap(active.v6, v6Build, active.v7Fixed, v7FixedBuild, active.v7Latest,
            v7LatestBuild,
            pointerRoutes);

        if (!pointerMap.TryGetValue(filename, out var versionData))
            return null;

        return string.IsNullOrWhiteSpace(versionData.version)
            ? null
            : $"{versionData.version} {versionData.build}\n";
    }

    public async Task<object> GetPointerRoutingAsync()
    {
        Dictionary<string, string> pointerRoutes;
        lock (_pointerRoutesLock)
        {
            pointerRoutes = LoadPointerRoutes();
        }

        var active = await GetActiveVersionsFromHistoryAsync();
        var v6Build = ReadBuildFromPointerFile("LATEST.6");
        var v7FixedBuild = ReadBuildFromPointerFile("NEWEST6.upgrade");
        var v7LatestBuild = ReadBuildFromPointerFile("NEWEST7.stable");
        var pointerMap = BuildPointerMap(
            active.v6, v6Build,
            active.v7Fixed, v7FixedBuild,
            active.v7Latest, v7LatestBuild,
            pointerRoutes);

        var upstreamTasks = ManagedPointerFiles.Select(async pointer =>
        {
            var (version, build) =
                await GetVersionFromUrlAsync($"https://upgrade.mikrotik.com/routeros/{pointer}", 8);
            return new
            {
                pointer,
                upstreamVersion = version ?? "",
                upstreamBuild = build,
                upstreamAvailable = !string.IsNullOrWhiteSpace(version)
            };
        });

        var upstreamRows = await Task.WhenAll(upstreamTasks);
        var upstreamByPointer = upstreamRows.ToDictionary(x => x.pointer, StringComparer.OrdinalIgnoreCase);

        var upstreamV6Best = SelectBestVersionCandidate(
            ManagedPointerFiles
                .Where(pointer => GetDefaultPointerBranch(pointer).Equals("v6", StringComparison.OrdinalIgnoreCase))
                .Select(pointer =>
                {
                    upstreamByPointer.TryGetValue(pointer, out var upstream);
                    return (upstream?.upstreamVersion, upstream?.upstreamBuild ?? 0L, pointer);
                }));

        var upstreamV7FixedBest = SelectBestVersionCandidate(
            ManagedPointerFiles
                .Where(pointer =>
                    GetDefaultPointerBranch(pointer).Equals("v7Fixed", StringComparison.OrdinalIgnoreCase))
                .Select(pointer =>
                {
                    upstreamByPointer.TryGetValue(pointer, out var upstream);
                    return (upstream?.upstreamVersion, upstream?.upstreamBuild ?? 0L, pointer);
                }));

        var upstreamV7LatestBest = SelectBestVersionCandidate(
            ManagedPointerFiles
                .Where(pointer =>
                    GetDefaultPointerBranch(pointer).Equals("v7Latest", StringComparison.OrdinalIgnoreCase))
                .Select(pointer =>
                {
                    upstreamByPointer.TryGetValue(pointer, out var upstream);
                    return (upstream?.upstreamVersion, upstream?.upstreamBuild ?? 0L, pointer);
                }));

        var rows = ManagedPointerFiles
            .Select(pointer =>
            {
                var defaultBranch = GetDefaultPointerBranch(pointer);
                var activeBranch = pointerRoutes.TryGetValue(pointer, out var route) ? route : defaultBranch;
                pointerMap.TryGetValue(pointer, out var served);
                upstreamByPointer.TryGetValue(pointer, out var upstream);

                return new
                {
                    pointer,
                    activeBranch,
                    defaultBranch,
                    servedVersion = served.version,
                    servedBuild = served.build,
                    upstreamVersion = upstream?.upstreamVersion ?? "",
                    upstreamBuild = upstream?.upstreamBuild ?? 0L,
                    upstreamAvailable = upstream?.upstreamAvailable ?? false
                };
            })
            .ToArray();

        return new
        {
            branchOptions = new[]
            {
                new {value = "v6", label = "v6"},
                new {value = "v7Fixed", label = "v7Fixed"},
                new {value = "v7Latest", label = "v7Latest"}
            },
            branchVersions = new
            {
                active.v6,
                active.v7Fixed,
                active.v7Latest
            },
            upstreamBranchVersions = new
            {
                v6 = new
                {
                    version = upstreamV6Best?.version ?? "",
                    build = upstreamV6Best?.build ?? 0L,
                    source = upstreamV6Best?.source ?? ""
                },
                v7Fixed = new
                {
                    version = upstreamV7FixedBest?.version ?? "",
                    build = upstreamV7FixedBest?.build ?? 0L,
                    source = upstreamV7FixedBest?.source ?? ""
                },
                v7Latest = new
                {
                    version = upstreamV7LatestBest?.version ?? "",
                    build = upstreamV7LatestBest?.build ?? 0L,
                    source = upstreamV7LatestBest?.source ?? ""
                }
            },
            rows
        };
    }

    public Task<(bool success, string? error)> SetPointerBranchRouteAsync(string pointer, string branch)
    {
        if (string.IsNullOrWhiteSpace(pointer))
            return Task.FromResult<(bool success, string? error)>((false, "Pointer is required"));
        if (string.IsNullOrWhiteSpace(branch))
            return Task.FromResult<(bool success, string? error)>((false, "Branch is required"));

        var normalizedPointer = pointer.Trim();
        var normalizedBranch = CanonicalizeBranch(branch.Trim());

        if (!ManagedPointerFiles.Contains(normalizedPointer, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult<(bool success, string? error)>((false, $"Unknown pointer: {normalizedPointer}"));
        if (!AllowedPointerBranches.Contains(normalizedBranch))
            return Task.FromResult<(bool success, string? error)>((false, $"Unknown branch: {branch}"));

        lock (_pointerRoutesLock)
        {
            var routes = LoadPointerRoutes();
            routes[normalizedPointer] = normalizedBranch;
            SavePointerRoutes(routes);
        }

        logger.LogInformation("Pointer route updated: {Pointer} -> {Branch}", normalizedPointer, normalizedBranch);
        return Task.FromResult<(bool success, string? error)>((true, null));
    }

    public async Task<string[]> GetAllowedArchesAsync()
    {
        return await metadataService.GetAllowedArchitecturesAsync();
    }

    public async Task UpdateAllowedArchesAsync(string[] arches)
    {
        var normalized = arches
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        await metadataService.SetAllowedArchitecturesAsync(normalized);
    }

    private static IEnumerable<string> BuildVersionedFileNameCandidates(string version, string filename)
    {
        yield return filename;

        var routerOsPrefix = $"routeros-{version}-";
        if (filename.StartsWith(routerOsPrefix, StringComparison.OrdinalIgnoreCase) &&
            filename.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
        {
            var arch = filename[routerOsPrefix.Length..^4];
            if (!string.IsNullOrWhiteSpace(arch))
                yield return $"routeros-{arch}-{version}.npk";
        }

        var v6PackagesPrefix = $"all_packages-{version}-";
        if (filename.StartsWith(v6PackagesPrefix, StringComparison.OrdinalIgnoreCase) &&
            filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var arch = filename[v6PackagesPrefix.Length..^4];
            if (!string.IsNullOrWhiteSpace(arch))
                yield return $"all_packages-{arch}-{version}.zip";
        }
    }

    private static bool CanDownloadFileOnDemand(string version, string filename)
    {
        if (version.Contains("..") || version.Contains('\\') || version.Contains('/') ||
            filename.Contains("..") || filename.Contains('\\') || filename.Contains('/'))
            return false;

        if (filename.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase) ||
            filename.Equals("packages.csv", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(filename);
        return extension.Equals(".npk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildOnDemandDownloadUrl(string version, string filename)
    {
        return filename.Equals("packages.csv", StringComparison.OrdinalIgnoreCase)
            ? $"https://upgrade.mikrotik.com/routeros/{version}/packages.csv"
            : $"https://download.mikrotik.com/routeros/{version}/{filename}";
    }

    private static string BuildOnDemandLocalPath(string version, string filename)
    {
        if (filename.Equals("packages.csv", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(GetRouterOsBaseFolder(), "routeros", version, "packages.csv");

        var major = version.Split('.', 2)[0];
        var branchFolder = major.Equals("6", StringComparison.OrdinalIgnoreCase) ? "v6" : "v7";
        return Path.Combine(GetRouterOsBaseFolder(), branchFolder, version, filename);
    }

    private static HttpClient CreateUpstreamClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "MikroTik-ROS-UpdateServer/1.0 (+https://github.com)");
        return client;
    }

    private async Task<UpdateCheckResult> CheckAndDownloadStableAsync()
    {
        var result = new UpdateCheckResult {Timestamp = DateTime.UtcNow};

        var mikrotikOk = await connectivityService.CheckMikroTikConnectivityAsync();
        var internetOk = await connectivityService.CheckInternetConnectivityAsync();
        if (!mikrotikOk || !internetOk)
        {
            result.Error = "network_unavailable";
            return result;
        }

        var (v6Version, v6Build) = await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/LATEST.6", 20);
        var newest7Stable = await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/NEWEST7.stable", 20);
        var newestA7Stable = await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/NEWESTa7.stable", 20);
        var latest7 = await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/LATEST.7", 20);
        var newest6Upgrade = await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/NEWEST6.upgrade", 20);
        var newestA6Upgrade =
            await GetVersionFromUrlAsync("https://upgrade.mikrotik.com/routeros/NEWESTa6.upgrade", 20);

        var v7LatestCandidates = new (string? version, long build, string source)[]
        {
            (newest7Stable.version, newest7Stable.build, "NEWEST7.stable"),
            (newestA7Stable.version, newestA7Stable.build, "NEWESTa7.stable"),
            (latest7.version, latest7.build, "LATEST.7")
        };

        var selectedV7Latest = SelectBestVersionCandidate(v7LatestCandidates);
        if (string.IsNullOrWhiteSpace(v6Version) || selectedV7Latest is null)
        {
            result.Error = "fetch_failed";
            return result;
        }

        var v7Latest = selectedV7Latest.Value.version;
        var v7LatestBuild = selectedV7Latest.Value.build;

        var v7FixedCandidates = new (string? version, long build, string source)[]
        {
            (newest6Upgrade.version, newest6Upgrade.build, "NEWEST6.upgrade"),
            (newestA6Upgrade.version, newestA6Upgrade.build, "NEWESTa6.upgrade")
        };

        var selectedV7Fixed = SelectBestVersionCandidate(v7FixedCandidates);
        var v7Fixed = selectedV7Fixed?.version;
        var v7FixedBuild = selectedV7Fixed?.build ?? 0L;

        if (string.IsNullOrWhiteSpace(v7Fixed))
        {
            v7Fixed = v7Latest;
            v7FixedBuild = v7LatestBuild;
        }

        var allowedArches = await metadataService.GetAllowedArchitecturesAsync();

        var downloaded = 0;
        downloaded += await EnsureVersionDownloadedAsync(v6Version, true, allowedArches);

        if (!v7Fixed.Equals(v7Latest, StringComparison.OrdinalIgnoreCase))
            downloaded += await EnsureVersionDownloadedAsync(v7Fixed, false, allowedArches);

        downloaded += await EnsureVersionDownloadedAsync(v7Latest, false, allowedArches);

        await UpdatePointerFilesAsync(v6Version, v7Fixed, v7Latest, v6Build, v7FixedBuild, v7LatestBuild);
        await AppendVersionHistoryAsync(v6Version, v7Fixed, v7Latest);
        await metadataService.SetLastCheckTimeAsync();

        // Распаковываем все v6-архивы и применяем delete_prefixes
        EnsureAllV6Extracted();

        CleanupOldVersionFolders(Path.Combine(GetRouterOsBaseFolder(), "v6"), 3, v6Version);
        CleanupOldVersionFolders(Path.Combine(GetRouterOsBaseFolder(), "v7"), 3, v7Latest, v7Fixed);

        // После загрузки новых файлов очищаем весь кеш 404
        NotFoundCacheMiddleware.ClearCache();

        result.Downloaded = downloaded;
        result.Total = 3;
        result.CheckedVersions =
        [
            $"v6:{v6Version}",
            $"v7-fixed:{v7Fixed}",
            $"v7-latest:{v7Latest}"
        ];
        result.Success = true;
        return result;
    }

    private async Task<int> EnsureVersionDownloadedAsync(string version, bool isV6, string[] allowedArches)
    {
        if (string.IsNullOrWhiteSpace(version))
            return 0;

        if (await IsVersionCompleteAsync(version, isV6, allowedArches))
        {
            await EnsureChangelogAsync(version, isV6);
            return 0;
        }

        var downloaded = 0;
        var versionDir = Path.Combine(GetRouterOsBaseFolder(), isV6 ? "v6" : "v7", version);
        Directory.CreateDirectory(versionDir);
        string[] v7Packages = isV6 ? [] : LoadV7Packages();

        foreach (var arch in allowedArches.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var normalizedArch = arch.Trim();
            var fileNames = isV6
                ? new[] { BuildPackageFileName(version, normalizedArch, true) }
                : new[] { BuildPackageFileName(version, normalizedArch, false) }
                    .Concat(v7Packages.Select(package => $"{package}-{version}-{normalizedArch}.npk"));

            foreach (var fileName in fileNames)
            {
                var localPath = Path.Combine(versionDir, fileName);
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                    continue;

                var url = $"https://download.mikrotik.com/routeros/{version}/{fileName}";
                var isV7ExtraPackage = !isV6 && !fileName.StartsWith("routeros-", StringComparison.OrdinalIgnoreCase);
                if (isV7ExtraPackage && _unavailableV7ExtraPackageUrls.ContainsKey(url))
                    continue;

                if (!await IsRemoteFileAvailableAsync(url))
                {
                    if (isV7ExtraPackage)
                        _unavailableV7ExtraPackageUrls.TryAdd(url, 0);
                    continue;
                }

                var downloadResult = await downloadService.DownloadFileAsync(url, localPath);
                if (downloadResult.Success)
                {
                    downloaded++;
                    // Инвалидируем кеш 404 для скачанного файла
                    NotFoundCacheMiddleware.InvalidatePath($"/routeros/{version}/{fileName}");

                    // Для v6: распаковываем zip и удаляем ненужные пакеты
                    if (isV6)
                        ExtractAndCleanV6Packages(localPath);
                }
            }
        }

        await EnsureChangelogAsync(version, isV6);
        return downloaded;
    }

    private Task<bool> IsVersionCompleteAsync(string version, bool isV6, string[] allowedArches)
    {
        var versionDir = Path.Combine(GetRouterOsBaseFolder(), isV6 ? "v6" : "v7", version);
        if (!Directory.Exists(versionDir))
            return Task.FromResult(false);

        foreach (var arch in allowedArches.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var normalizedArch = arch.Trim();
            var fileNames = isV6
                ? new[] { BuildPackageFileName(version, normalizedArch, true) }
                : new[] { BuildPackageFileName(version, normalizedArch, false) }
                    .Concat(LoadV7Packages().Select(package => $"{package}-{version}-{normalizedArch}.npk"));

            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(versionDir, fileName);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    return Task.FromResult(false);
            }
        }

        return Task.FromResult(true);
    }

    private static string BuildPackageFileName(string version, string arch, bool isV6)
    {
        return isV6
            ? $"all_packages-{arch}-{version}.zip"
            : $"routeros-{arch}-{version}.npk";
    }

    private async Task<bool> IsRemoteFileAvailableAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _upstreamClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureChangelogAsync(string version, bool isV6)
    {
        var versionDir = Path.Combine(GetRouterOsBaseFolder(), isV6 ? "v6" : "v7", version);
        var changelogPath = Path.Combine(versionDir, "CHANGELOG");
        if (File.Exists(changelogPath) && new FileInfo(changelogPath).Length > 0)
            return;

        var url = $"https://download.mikrotik.com/routeros/{version}/CHANGELOG";
        try
        {
            var content = await _upstreamClient.GetStringAsync(url);
            await File.WriteAllTextAsync(changelogPath, content);
        }
        catch
        {
            // CHANGELOG optional; ignore failures
        }
    }

    private void CleanupOldVersionFolders(string rootPath, int maxToKeep, params string[] protectedVersions)
    {
        try
        {
            if (!Directory.Exists(rootPath))
                return;

            var keepSet = new HashSet<string>(protectedVersions.Where(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.OrdinalIgnoreCase);

            var dirs = Directory.GetDirectories(rootPath)
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .OrderByDescending(ParseVersionForSort)
                .ThenByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var toDelete = dirs
                .Where(v => !keepSet.Contains(v))
                .Skip(Math.Max(0, maxToKeep - keepSet.Count))
                .ToList();

            foreach (var version in toDelete)
            {
                var fullPath = Path.Combine(rootPath, version);
                try
                {
                    Directory.Delete(fullPath, true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete old version folder {VersionPath}", fullPath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error cleaning old versions in {RootPath}", rootPath);
        }
    }

    /// <summary>
    ///     Загружает список префиксов для удаления из delete_prefixes.json
    /// </summary>
    private static string[] LoadDeletePrefixes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "delete_prefixes.json");
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            // Поддерживаем оба формата: массив ["a","b"] и объект {"deletePrefixes":["a","b"]}
            JsonElement arrayElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arrayElement = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("deletePrefixes", out arrayElement) &&
                     arrayElement.ValueKind == JsonValueKind.Array)
            { /* ok */ }
            else
                return [];

            return arrayElement.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray()!;
        }
        catch
        {
            return [];
        }
    }

    private static string[] LoadV7Packages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "v7_packages.json");
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))?
                .Where(package => !string.IsNullOrWhiteSpace(package))
                .Select(package => package.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Распаковывает v6 all_packages zip, пропуская файлы по delete_prefixes
    /// </summary>
    private void ExtractAndCleanV6Packages(string zipPath)
    {
        if (!File.Exists(zipPath))
            return;

        var versionDir = Path.GetDirectoryName(zipPath)!;
        var deletePrefixes = LoadDeletePrefixes();

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var extracted = 0;
            var skipped = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                // Защита от path traversal
                var destPath = Path.Combine(versionDir, entry.Name);
                if (!destPath.StartsWith(versionDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Пропускаем файлы, попадающие под delete_prefixes (не распаковываем совсем)
                if (deletePrefixes.Length > 0 &&
                    deletePrefixes.Any(p => entry.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                if (!File.Exists(destPath))
                {
                    entry.ExtractToFile(destPath);
                    extracted++;
                }
            }

            if (extracted > 0)
                logger.LogInformation(
                    "Zip {Zip}: extracted {Extracted}, skipped {Skipped} (delete_prefixes)",
                    Path.GetFileName(zipPath), extracted, skipped);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract {ZipPath}", zipPath);
        }
    }

    /// <summary>
    ///     Удаляет .npk файлы, чьи имена начинаются с любого из delete_prefixes
    ///     (для ретроактивной очистки при изменении списка префиксов)
    /// </summary>
    private void CleanUnwantedPackages(string versionDir, string version)
    {
        var deletePrefixes = LoadDeletePrefixes();
        if (deletePrefixes.Length == 0)
            return;

        var deletedCount = 0;
        foreach (var npkFile in Directory.GetFiles(versionDir, "*.npk"))
        {
            var fileName = Path.GetFileName(npkFile);
            if (deletePrefixes.Any(prefix =>
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    File.Delete(npkFile);
                    deletedCount++;
                    logger.LogDebug("Deleted unwanted package: {File}", fileName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete unwanted package: {File}", fileName);
                }
            }
        }

        if (deletedCount > 0)
            logger.LogInformation(
                "Cleaned {Count} unwanted packages from {Version} (prefixes: {Prefixes})",
                deletedCount, version, string.Join(", ", deletePrefixes));
    }

    /// <summary>
    ///     Проходит по всем v6-версиям и применяет delete_prefixes + распаковку
    /// </summary>
    private void EnsureAllV6Extracted()
    {
        var signature = string.Join("\n", LoadDeletePrefixes()
            .OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase));
        if (string.Equals(_lastV6ExtractionSignature, signature, StringComparison.Ordinal))
            return;

        _lastV6ExtractionSignature = signature;
        var v6Root = Path.Combine(GetRouterOsBaseFolder(), "v6");
        if (!Directory.Exists(v6Root))
            return;

        foreach (var versionDir in Directory.GetDirectories(v6Root))
        {
            var version = Path.GetFileName(versionDir);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            // Распаковываем все zip-архивы в папке версии
            foreach (var zipFile in Directory.GetFiles(versionDir, "all_packages-*.zip"))
                ExtractAndCleanV6Packages(zipFile);

            // Также применяем delete_prefixes к уже распакованным файлам
            CleanUnwantedPackages(versionDir, version);
        }
    }

    private static string GetRouterOsBaseFolder()
    {
        return Path.Combine(AppContext.BaseDirectory, "routeros");
    }

    private static string GetVersionsHistoryFilePath()
    {
        return Path.Combine(GetRouterOsBaseFolder(), "versions.json");
    }

    private static string GetPointerRoutesFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "pointer_routes.json");
    }

    private async Task<(string v6, string v7Fixed, string v7Latest)> GetActiveVersionsFromHistoryAsync()
    {
        var history = await GetVersionHistoryAsync(1);
        if (history.Count == 0)
            return ("", "", "");

        var latest = history[0];
        return (latest.V6Stable, latest.V7Fixed, latest.V7Stable);
    }

    private (string v6, string v7Fixed, string v7Latest) GetActiveVersionsFromHistory()
    {
        lock (_versionsHistoryLock)
        {
            var path = GetVersionsHistoryFilePath();
            if (!File.Exists(path))
                return ("", "", "");

            try
            {
                var content = File.ReadAllText(path);
                var logs = JsonSerializer.Deserialize<List<VersionLog>>(content) ?? [];
                var latest = logs.OrderByDescending(x => x.Timestamp).FirstOrDefault();
                if (latest is null)
                    return ("", "", "");

                return (latest.V6Stable, latest.V7Fixed, latest.V7Stable);
            }
            catch
            {
                return ("", "", "");
            }
        }
    }

    private async Task AppendVersionHistoryAsync(string v6, string v7Fixed, string v7Latest)
    {
        lock (_versionsHistoryLock)
        {
            var path = GetVersionsHistoryFilePath();
            var logs = new List<VersionLog>();

            if (File.Exists(path))
                try
                {
                    var content = File.ReadAllText(path);
                    logs = JsonSerializer.Deserialize<List<VersionLog>>(content) ?? [];
                }
                catch
                {
                    logs = [];
                }

            logs.Add(new VersionLog
            {
                Timestamp = DateTime.Now,
                V6Stable = v6,
                V7Fixed = v7Fixed,
                V7Stable = v7Latest
            });

            if (logs.Count > 100)
                logs = logs.TakeLast(100).ToList();

            var json = JsonSerializer.Serialize(logs, new JsonSerializerOptions {WriteIndented = true});
            File.WriteAllText(path, json);
        }

        await Task.CompletedTask;
    }

    private long ReadBuildFromPointerFile(string fileName)
    {
        try
        {
            var path = Path.Combine(GetRouterOsBaseFolder(), fileName);
            if (!File.Exists(path))
                return 0;

            var content = File.ReadAllText(path);
            var parts = content.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            return parts.Length > 1 && long.TryParse(parts[1], out var build) ? build : 0;
        }
        catch
        {
            return 0;
        }
    }

    private Dictionary<string, string> LoadPointerRoutes()
    {
        var path = GetPointerRoutesFilePath();
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null || parsed.Count == 0)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pointer, branch) in parsed)
            {
                if (!ManagedPointerFiles.Contains(pointer, StringComparer.OrdinalIgnoreCase))
                    continue;

                var canonicalBranch = CanonicalizeBranch(branch);
                if (!AllowedPointerBranches.Contains(canonicalBranch))
                    continue;

                result[pointer] = canonicalBranch;
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SavePointerRoutes(Dictionary<string, string> routes)
    {
        var path = GetPointerRoutesFilePath();
        var snapshot = routes
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions {WriteIndented = true});
        File.WriteAllText(path, json);
    }

    private static string CanonicalizeBranch(string branch)
    {
        if (branch.Equals("v6", StringComparison.OrdinalIgnoreCase))
            return "v6";
        if (branch.Equals("v7Fixed", StringComparison.OrdinalIgnoreCase))
            return "v7Fixed";
        if (branch.Equals("v7Latest", StringComparison.OrdinalIgnoreCase))
            return "v7Latest";
        return branch;
    }

    private Dictionary<string, (string version, long build)> BuildPointerMap(
        string v6Version, long v6Build,
        string v7Fixed, long v7FixedBuild,
        string v7Latest, long v7LatestBuild,
        Dictionary<string, string> routes)
    {
        var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pointer in ManagedPointerFiles)
        {
            var branch = ResolvePointerBranch(pointer, routes);
            var versionData = ResolveBranchVersionData(
                branch,
                v6Version, v6Build,
                v7Fixed, v7FixedBuild,
                v7Latest, v7LatestBuild);

            if (versionData is null)
                continue;

            map[pointer] = versionData.Value;
        }

        return map;
    }

    private static string ResolvePointerBranch(string pointer, Dictionary<string, string> routes)
    {
        if (routes.TryGetValue(pointer, out var configuredBranch) && AllowedPointerBranches.Contains(configuredBranch))
            return configuredBranch;

        return GetDefaultPointerBranch(pointer);
    }

    private static string GetDefaultPointerBranch(string pointer)
    {
        if (pointer is "LATEST.6" or "NEWEST6.stable" or "NEWESTa6.stable" or "NEWESTa6.long-term")
            return "v6";

        if (pointer is "NEWEST6.upgrade" or "NEWESTa6.upgrade")
            return "v7Fixed";

        return "v7Latest";
    }

    private static (string version, long build)? ResolveBranchVersionData(
        string branch,
        string v6Version, long v6Build,
        string v7Fixed, long v7FixedBuild,
        string v7Latest, long v7LatestBuild)
    {
        var normalizedBranch = branch.Trim().ToLowerInvariant();
        return normalizedBranch switch
        {
            "v6" when !string.IsNullOrWhiteSpace(v6Version) => (v6Version, v6Build),
            "v7fixed" when !string.IsNullOrWhiteSpace(v7Fixed) => (v7Fixed, v7FixedBuild),
            "v7latest" when !string.IsNullOrWhiteSpace(v7Latest) => (v7Latest, v7LatestBuild),
            _ => null
        };
    }

    private async Task<(string? version, long build)> GetVersionFromUrlAsync(string url, int timeoutSeconds = 0)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            HttpResponseMessage response;
            if (timeoutSeconds > 0)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                response = await _upstreamClient.SendAsync(request, cts.Token);
            }
            else
            {
                response = await _upstreamClient.SendAsync(request);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return (null, 0L);

                var content = await response.Content.ReadAsStringAsync();
                var parts = content.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return (null, 0L);

                var version = parts[0];
                long build = 0;
                if (parts.Length > 1)
                    long.TryParse(parts[1], out build);

                return string.IsNullOrWhiteSpace(version) ? (null, 0L) : (version, build);
            }
        }
        catch
        {
            return (null, 0L);
        }
    }

    private static (string version, long build, string source)? SelectBestVersionCandidate(
        IEnumerable<(string? version, long build, string source)> candidates)
    {
        (string version, long build, string source)? best = null;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.version))
                continue;

            var normalizedVersion = candidate.version.Trim();
            if (best is null)
            {
                best = (normalizedVersion, candidate.build, candidate.source);
                continue;
            }

            var compare = CompareRouterOsVersions(normalizedVersion, best.Value.version);
            if (compare > 0 || (compare == 0 && candidate.build > best.Value.build))
                best = (normalizedVersion, candidate.build, candidate.source);
        }

        return best;
    }

    private static int CompareRouterOsVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdatePointerFilesAsync(
        string v6Version,
        string v7Fixed,
        string v7Latest,
        long v6Build,
        long v7FixedBuild,
        long v7LatestBuild)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(GetRouterOsBaseFolder(), "v6"));
            Directory.CreateDirectory(Path.Combine(GetRouterOsBaseFolder(), "v7"));

            Dictionary<string, string> routes;
            lock (_pointerRoutesLock)
            {
                routes = LoadPointerRoutes();
            }

            var pointerMap =
                BuildPointerMap(v6Version, v6Build, v7Fixed, v7FixedBuild, v7Latest, v7LatestBuild, routes);
            foreach (var (fileName, (version, build)) in pointerMap)
            {
                var path = Path.Combine(GetRouterOsBaseFolder(), fileName);
                await File.WriteAllTextAsync(path, $"{version} {build}\n");
            }

            var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(v7Fixed))
                branches.Add(GetBranchFromVersion(v7Fixed));
            if (!string.IsNullOrWhiteSpace(v7Latest))
                branches.Add(GetBranchFromVersion(v7Latest));

            foreach (var branch in branches)
                await DownloadPackagesCsvForBranchAsync(branch);

            await UpdateGlobalChangelogAsync(v6Version, v7Fixed, v7Latest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating pointer files");
        }
    }

    private static string GetBranchFromVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;

        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    private async Task DownloadPackagesCsvForBranchAsync(string branchVersion)
    {
        if (string.IsNullOrWhiteSpace(branchVersion))
            return;

        var branchDir = Path.Combine(GetRouterOsBaseFolder(), "routeros", branchVersion);
        Directory.CreateDirectory(branchDir);
        var localPath = Path.Combine(branchDir, "packages.csv");
        if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            return;

        var url = $"https://upgrade.mikrotik.com/routeros/{branchVersion}/packages.csv";
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await _upstreamClient.SendAsync(head);
            if (!headResp.IsSuccessStatusCode)
                return;

            var csv = await _upstreamClient.GetStringAsync(url);
            await File.WriteAllTextAsync(localPath, csv);
        }
        catch
        {
            // optional artifact
        }
    }

    private async Task UpdateGlobalChangelogAsync(string v6Version, string v7Fixed, string v7Latest)
    {
        try
        {
            var changelogPath = Path.Combine(GetRouterOsBaseFolder(), "CHANGELOG");
            var entries = new List<string>
            {
                $"Current versions at {DateTime.Now:yyyy-MM-dd HH:mm:ss}:",
                $"  RouterOS v6: {v6Version}",
                $"  RouterOS v7 (fixed): {v7Fixed}",
                $"  RouterOS v7 (latest): {v7Latest}",
                ""
            };

            var activeVersions = new List<(string version, bool isV6)>
            {
                (v6Version, true),
                (v7Fixed, false),
                (v7Latest, false)
            };

            foreach (var (version, isV6) in activeVersions)
            {
                if (string.IsNullOrWhiteSpace(version))
                    continue;

                var versionDir = Path.Combine(GetRouterOsBaseFolder(), isV6 ? "v6" : "v7", version);
                var versionChangelogPath = Path.Combine(versionDir, "CHANGELOG");
                if (!File.Exists(versionChangelogPath))
                    continue;

                try
                {
                    var versionChangelog = await File.ReadAllTextAsync(versionChangelogPath);
                    entries.Add($"=== RouterOS {version} CHANGELOG ===");
                    entries.Add(versionChangelog);
                    entries.Add("");
                }
                catch
                {
                    // skip broken changelog entries
                }
            }

            await File.WriteAllLinesAsync(changelogPath, entries);
        }
        catch
        {
            // keep update flow resilient even if changelog aggregation fails
        }
    }

    private RouterOSVersion[] FilterVersionsByCheckType(RouterOSVersion[] versions, string checkType)
    {
        return checkType.ToLowerInvariant() switch
        {
            "stable" => versions.Where(v => v.Branch.Contains("stable", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            "testing" => versions.Where(v => v.Branch.Contains("testing", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            "rc" or "release-candidate" => versions.Where(v =>
                v.Branch.Contains("release-candidate", StringComparison.OrdinalIgnoreCase)).ToArray(),
            "development" => versions
                .Where(v => v.Branch.Contains("development", StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => versions
        };
    }

    private static List<(string FileName, string Url, string LocalPath)> PrepareFilesForDownload(
        RouterOSVersion[] versions,
        string[] allowedArches)
    {
        var files = new List<(string, string, string)>();

        foreach (var version in versions)
        {
            var versionFiles = version.Files;

            if (versionFiles.Length == 0)
                continue;

            files.AddRange(
                from file in versionFiles
                where allowedArches.Any(arch => file.Contains(arch, StringComparison.OrdinalIgnoreCase))
                let url = $"https://download.mikrotik.com/{file}"
                let localPath = Path.Combine(AppContext.BaseDirectory, "routeros", file)
                where !File.Exists(localPath)
                select (file, url, localPath));
        }

        return files;
    }

    private async Task CleanupOldVersionsAsync(RouterOSVersion[] allVersions, int maxVersionsToKeep)
    {
        try
        {
            if (allVersions.Length <= maxVersionsToKeep)
                return;

            // Сортируем по дате (новые первыми)
            var oldVersions = allVersions
                .OrderByDescending(v => v.Released)
                .Skip(maxVersionsToKeep)
                .ToList();

            logger.LogInformation("Cleaning up {Count} old versions", oldVersions.Count);

            foreach (var version in oldVersions)
                try
                {
                    await versionService.DeleteVersionAsync(version.Version);
                    logger.LogDebug("Deleted old version: {Version}", version.Version);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete version: {Version}", version.Version);
                }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error during cleanup of old versions");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    private string GetCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();

            lock (_cpuLock)
            {
                if ((DateTime.Now - _lastCpuCheck).TotalMilliseconds < 1000)
                    return _lastCpuValue.ToString("F2") + "%";

                var totalRunTime = (DateTime.Now - process.StartTime).TotalMilliseconds;
                var cpuTime = process.TotalProcessorTime.TotalMilliseconds;

                if (totalRunTime > 0)
                    _lastCpuValue = cpuTime / totalRunTime / Environment.ProcessorCount * 100;

                _lastCpuCheck = DateTime.Now;
                return _lastCpuValue.ToString("F2") + "%";
            }
        }
        catch
        {
            return "N/A";
        }
    }

    private static Version ParseVersionForSort(string versionText)
    {
        return Version.TryParse(versionText, out var parsed)
            ? parsed
            : new Version(0, 0, 0, 0);
    }

    private static string? NormalizeVersionBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return null;

        if (branch.Trim().Equals("v6", StringComparison.OrdinalIgnoreCase))
            return "v6";
        if (branch.Trim().Equals("v7", StringComparison.OrdinalIgnoreCase))
            return "v7";

        return null;
    }

    private static List<object> BuildVersionEntries(string branchRootDir)
    {
        if (!Directory.Exists(branchRootDir))
            return [];

        return Directory.GetDirectories(branchRootDir)
            .Select(dir =>
            {
                var version = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(version))
                    return null;

                var architectures = ExtractArchitecturesFromVersionDirectory(dir, version);
                var files = Directory.GetFiles(dir).Length;

                return new
                {
                    version,
                    architectures,
                    files
                };
            })
            .Where(x => x is not null)
            .OrderByDescending(x => ParseVersionForSort(x!.version))
            .ThenByDescending(x => x!.version, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToList();
    }

    private static string[] ExtractArchitecturesFromVersionDirectory(string versionDirectory, string version)
    {
        if (!Directory.Exists(versionDirectory))
            return [];

        var knownArchitectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "arm",
            "arm64",
            "mipsbe",
            "mmips",
            "smips",
            "tile",
            "ppc",
            "x86",
            "x86_64"
        };

        var allCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionSuffix = "-" + version;

        foreach (var filePath in Directory.GetFiles(versionDirectory))
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(stem))
                continue;

            if (!stem.EndsWith(versionSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            var beforeVersion = stem[..^versionSuffix.Length];
            var separatorIndex = beforeVersion.LastIndexOf('-');
            if (separatorIndex < 0 || separatorIndex >= beforeVersion.Length - 1)
                continue;

            var arch = beforeVersion[(separatorIndex + 1)..].Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(arch))
                continue;

            allCandidates.Add(arch);
            if (knownArchitectures.Contains(arch))
                knownCandidates.Add(arch);
        }

        var selected = knownCandidates.Count > 0 ? knownCandidates : allCandidates;

        return selected
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

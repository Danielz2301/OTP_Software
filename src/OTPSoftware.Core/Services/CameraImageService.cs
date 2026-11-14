using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using OTPSoftware.Core.Caching;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Locates panel optical inspection image folders across dual-camera SMB network shares.
/// Includes SMB hang prevention via non-blocking TCP port 445 reachability probes, per-host circuit breaker,
/// 3-tier hierarchical search, and in-memory LRU caching to eliminate redundant SMB lookups.
/// </summary>
public sealed partial class CameraImageService : ICameraImageService
{
    private static readonly TimeSpan DefaultNegativeCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultHostCooldown = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"\.(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HostLastOctetRegex();

    private readonly LruMemoryCache<string, string> _imageFolderCache = new(capacity: 100, StringComparer.OrdinalIgnoreCase);
    private readonly LruMemoryCache<string, DateTime> _missingFolderCache = new(capacity: 256, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _unreachableHostUntilTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _negativeCacheTtl;
    private readonly TimeSpan _hostCooldown;

    public CameraImageService(TimeSpan? negativeCacheTtl = null, TimeSpan? hostCooldown = null)
    {
        _negativeCacheTtl = negativeCacheTtl is { } ttl && ttl > TimeSpan.Zero
            ? ttl
            : DefaultNegativeCacheTtl;
        _hostCooldown = hostCooldown is { } cooldown && cooldown > TimeSpan.Zero
            ? cooldown
            : DefaultHostCooldown;
    }

    public LruMemoryCache<string, string> ImageFolderCache => _imageFolderCache;

    public async Task<bool> TestHostReachableAsync(
        string? uncPath,
        int timeoutMs = 1200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        string hostName = SettingsService.GetUncServerName(uncPath);
        if (!string.IsNullOrEmpty(hostName))
        {
            if (_unreachableHostUntilTick.TryGetValue(hostName, out long blockedUntilTick) &&
                Environment.TickCount64 < blockedUntilTick)
            {
                return false;
            }

            CancellationTokenSource? cts = null;
            try
            {
                using var client = new TcpClient();
                try
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }

                cts.CancelAfter(timeoutMs);

                await client.ConnectAsync(hostName, 445, cts.Token).ConfigureAwait(false);
                if (client.Connected)
                {
                    _unreachableHostUntilTick.TryRemove(hostName, out _);
                    return true;
                }

                MarkHostUnreachable(hostName);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkHostUnreachable(hostName);
                return false;
            }
            finally
            {
                cts?.Dispose();
            }
        }

        return Directory.Exists(uncPath) || File.Exists(uncPath);
    }

    private void MarkHostUnreachable(string hostName)
    {
        long unblockAt = Environment.TickCount64 + Math.Max(1L, (long)_hostCooldown.TotalMilliseconds);
        _unreachableHostUntilTick[hostName] = unblockAt;
    }

    public async Task<string?> FindPanelImageFolderAsync(
        string rootPath,
        string panelName,
        string sideCode,
        DateTime? timestamp,
        bool skipHostCheck = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(panelName))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        if (!skipHostCheck)
        {
            bool reachable = await TestHostReachableAsync(rootPath, 1200, cancellationToken).ConfigureAwait(false);
            if (!reachable)
                return null;
        }

        string dateKey = timestamp.HasValue && timestamp.Value != DateTime.MinValue
            ? timestamp.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : string.Empty;
        string cacheKey = $"{rootPath}|{panelName}|{sideCode}|{dateKey}";
        if (_imageFolderCache.TryGetValue(cacheKey, out var cachedFolder))
        {
            bool exists = cachedFolder.StartsWith(@"\\", StringComparison.Ordinal)
                ? await Task.Run(() => Directory.Exists(cachedFolder), cancellationToken).ConfigureAwait(false)
                : Directory.Exists(cachedFolder);
            if (exists)
            {
                return cachedFolder;
            }
            _imageFolderCache.Remove(cacheKey);
        }

        if (_missingFolderCache.TryGetValue(cacheKey, out var missingCachedUtc))
        {
            if (DateTime.UtcNow - missingCachedUtc < _negativeCacheTtl)
            {
                return null;
            }
            _missingFolderCache.Remove(cacheKey);
        }

        string? found = await Task.Run(
            () => SearchImageFolderInternal(rootPath, panelName, sideCode, timestamp, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(found))
        {
            _missingFolderCache.Remove(cacheKey);
            _imageFolderCache.Set(cacheKey, found);
        }
        else
        {
            _missingFolderCache.Set(cacheKey, DateTime.UtcNow);
        }
        return found;
    }

    private static string? SearchImageFolderInternal(
        string rootPath,
        string panelName,
        string sideCode,
        DateTime? timestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(rootPath))
            return null;

        var strictSidePatterns = !string.IsNullOrWhiteSpace(sideCode)
            ? new List<string> { $"*{panelName}*{sideCode}*" }
            : new List<string> { $"*{panelName}*" };

        // Tier 1 Fast path: Exact probe if timestamp is known and sideCode is present
        // Format: <OK|NG>_<Panel>_<MMddHHmmss>_<Side>
        if (!string.IsNullOrWhiteSpace(sideCode) && timestamp.HasValue && timestamp.Value != DateTime.MinValue)
        {
            string stamp = timestamp.Value.ToString("MMddHHmmss", CultureInfo.InvariantCulture);
            string dayDir = Path.Combine(
                rootPath,
                timestamp.Value.ToString("yyyyMM", CultureInfo.InvariantCulture),
                timestamp.Value.ToString("dd", CultureInfo.InvariantCulture));
            string[] probeFolders = [dayDir, rootPath];
            string[] prefixes = ["NG", "OK"];

            foreach (var folder in probeFolders)
            {
                foreach (var prefix in prefixes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string exact = Path.Combine(folder, $"{prefix}_{panelName}_{stamp}_{sideCode}");
                    if (Directory.Exists(exact))
                        return exact;
                }
            }
        }

        // Tier 2: Prioritized candidate search folders
        var searchFolders = new List<string>();

        // a0. Standard structure: LOG\yyyyMM\dd
        if (timestamp.HasValue && timestamp.Value != DateTime.MinValue)
        {
            string stdPath = Path.Combine(
                rootPath,
                timestamp.Value.ToString("yyyyMM", CultureInfo.InvariantCulture),
                timestamp.Value.ToString("dd", CultureInfo.InvariantCulture));
            searchFolders.Add(stdPath);
        }

        // a. Date directories derived from record Timestamp
        if (timestamp.HasValue && timestamp.Value != DateTime.MinValue)
        {
            string[] formats = ["yyyy-MM", "yyyy-MM-dd", "yyyyMMdd", "yyyyMM", "yyyy_MM_dd", "yyyy_MM", "MMdd", "MM-dd"];
            foreach (var fmt in formats)
            {
                string candPath = Path.Combine(rootPath, timestamp.Value.ToString(fmt, CultureInfo.InvariantCulture));
                if (!searchFolders.Contains(candPath, StringComparer.OrdinalIgnoreCase))
                {
                    searchFolders.Add(candPath);
                }
            }
        }

        // b. Date directories derived from current system time
        var now = DateTime.Now;
        string stdNow = Path.Combine(
            rootPath,
            now.ToString("yyyyMM", CultureInfo.InvariantCulture),
            now.ToString("dd", CultureInfo.InvariantCulture));
        if (!searchFolders.Contains(stdNow, StringComparer.OrdinalIgnoreCase))
        {
            searchFolders.Add(stdNow);
        }
        string[] nowFormats = ["yyyy-MM", "yyyy-MM-dd", "yyyyMMdd", "yyyyMM"];
        foreach (var fmt in nowFormats)
        {
            string candPath = Path.Combine(rootPath, now.ToString(fmt, CultureInfo.InvariantCulture));
            if (!searchFolders.Contains(candPath, StringComparer.OrdinalIgnoreCase))
            {
                searchFolders.Add(candPath);
            }
        }

        // c. Root path itself
        searchFolders.Add(rootPath);

        // Scan prioritized folder candidates
        foreach (var folder in searchFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
                continue;

            string? matched = TryFindFirstMatchingDirectory(folder, strictSidePatterns, cancellationToken);
            if (matched is not null)
                return matched;
        }

        // Tier 3: Scan level-1 container subdirectories (SKIP folders starting with OK_ or NG_)
        try
        {
            var containerDirs = GetRecentContainerSubdirectories(rootPath, maxCount: 15, cancellationToken);
            foreach (var cDir in containerDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!searchFolders.Contains(cDir, StringComparer.OrdinalIgnoreCase))
                {
                    string? matched = TryFindFirstMatchingDirectory(cDir, strictSidePatterns, cancellationToken);
                    if (matched is not null)
                        return matched;
                }

                // Inspect level-2 inside container (e.g. 2026-11\2026-11-06)
                try
                {
                    var l2Dirs = GetRecentContainerSubdirectories(cDir, maxCount: 10, cancellationToken);
                    foreach (var l2Dir in l2Dirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (searchFolders.Contains(l2Dir, StringComparer.OrdinalIgnoreCase))
                            continue;

                        string? matched = TryFindFirstMatchingDirectory(l2Dir, strictSidePatterns, cancellationToken);
                        if (matched is not null)
                            return matched;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    private static string? TryFindFirstMatchingDirectory(
        string folder,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken)
    {
        foreach (var pat in patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folder, pat))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return dir;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        return null;
    }

    private static List<string> GetRecentContainerSubdirectories(
        string parentDir,
        int maxCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Directory.EnumerateDirectories(parentDir)
            .Where(d =>
            {
                ReadOnlySpan<char> leaf = Path.GetFileName(d.AsSpan());
                return !leaf.StartsWith("OK_", StringComparison.OrdinalIgnoreCase) &&
                       !leaf.StartsWith("NG_", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }

    public IReadOnlyList<CameraEndpointDescriptor> GetCameraEndpoints(OtpSettings settings)
    {
        var list = new List<CameraEndpointDescriptor>();
        if (settings?.Machines == null)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in settings.Machines)
        {
            if (m?.Images == null) continue;
            string mName = m.Name;

            foreach (var img in m.Images)
            {
                if (img == null || string.IsNullOrWhiteSpace(img.Root) || string.IsNullOrWhiteSpace(img.Code))
                    continue;

                string key = $"{img.Code}|{img.Root}";
                if (!seen.Add(key))
                    continue;

                string hostIp = SettingsService.GetUncServerName(img.Root);

                string shortIp;
                var dotMatch = HostLastOctetRegex().Match(hostIp);
                if (dotMatch.Success)
                    shortIp = $".{dotMatch.Groups[1].Value}";
                else if (!string.IsNullOrEmpty(hostIp))
                    shortIp = hostIp;
                else
                    shortIp = "Local";

                string label = $"{img.Code} ({shortIp})";

                list.Add(new CameraEndpointDescriptor(
                    img.Code,
                    mName,
                    img.Root,
                    hostIp,
                    shortIp,
                    label
                ));
            }
        }

        return list.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<CameraHealthStatus>> CheckCameraEndpointsHealthAsync(
        IReadOnlyList<CameraEndpointDescriptor> endpoints,
        int timeoutMs = 600,
        CancellationToken cancellationToken = default)
    {
        if (endpoints == null || endpoints.Count == 0)
            return [];

        var tasks = endpoints.Select(async ep =>
        {
            bool online = await TestHostReachableAsync(ep.Root, timeoutMs, cancellationToken).ConfigureAwait(false);
            return new CameraHealthStatus
            {
                Code = ep.Code,
                Machine = ep.Machine,
                Root = ep.Root,
                Host = ep.Host,
                Label = ep.Label,
                Online = online
            };
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

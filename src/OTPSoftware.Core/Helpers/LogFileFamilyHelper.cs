using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OTPSoftware.Core.Helpers;

/// <summary>
/// Parsed base identity of a rotating log file family (e.g. OTP_1.log and OTP_1.1.log..OTP_1.N.log).
/// </summary>
public sealed record LogFileIdentity(
    string Directory,
    string BaseName,
    string Extension,
    string ActiveFilePath,
    string WatcherFilter,
    int RequestedRotationIndex
);

/// <summary>
/// Discovered log file in a rotating file family along with ordering metadata.
/// </summary>
public sealed record LogFileFamilyEntry(
    string Path,
    int RotationIndex,
    DateTime LastWriteTimeUtc,
    DateTime? FirstLogTimestamp,
    long Length,
    bool Exists
);

/// <summary>
/// Resolved log file family ordered both chronologically (oldest to newest) and reverse-chronologically.
/// </summary>
public sealed record LogFileFamily(
    LogFileIdentity Identity,
    IReadOnlyList<LogFileFamilyEntry> OldestToNewest,
    IReadOnlyList<LogFileFamilyEntry> NewestToOldest
);

/// <summary>
/// Shared discovery and chronological sorting helper for rotating OTP_*.log and MES_*.log file chains.
/// </summary>
public static partial class LogFileFamilyHelper
{
    private const int HeaderProbeBytes = 4096;
    private const int MaxRotationIndex = 999;

    [GeneratedRegex(@"\.(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex RotationSuffixRegex();

    [GeneratedRegex(@"\[(\d{4}[-/]\d{2}[-/]\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex LogTimestampPrefixRegex();

    public static LogFileIdentity? ParseFileFamilyIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string trimmed = path.Trim();
        string dir = Path.GetDirectoryName(trimmed) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(trimmed);
        string ext = Path.GetExtension(trimmed);

        if (string.IsNullOrWhiteSpace(stem))
            return null;

        string baseName = stem;
        int requestedIndex = 0;

        var match = RotationSuffixRegex().Match(stem);
        if (match.Success &&
            match.Index > 0 &&
            int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) &&
            idx is >= 1 and <= MaxRotationIndex)
        {
            baseName = stem[..match.Index];
            requestedIndex = idx;
        }

        string activeFileName = $"{baseName}{ext}";
        string activeFilePath = string.IsNullOrEmpty(dir) ? activeFileName : Path.Combine(dir, activeFileName);
        string watcherFilter = $"{baseName}*{ext}";

        return new LogFileIdentity(dir, baseName, ext, activeFilePath, watcherFilter, requestedIndex);
    }

    public static string ResolveActiveLogPath(string? path)
    {
        var identity = ParseFileFamilyIdentity(path);
        return identity?.ActiveFilePath ?? (path?.Trim() ?? string.Empty);
    }

    public static LogFileFamily? ResolveLogFileFamily(
        string? path,
        int? maxRotatedFiles = null,
        bool includeFirstTimestamps = false)
    {
        var identity = ParseFileFamilyIdentity(path);
        if (identity is null)
            return null;

        var discovered = new Dictionary<string, (int RotationIndex, FileInfo Info)>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(identity.Directory) && Directory.Exists(identity.Directory))
        {
            try
            {
                var dirInfo = new DirectoryInfo(identity.Directory);
                foreach (var info in dirInfo.EnumerateFiles(identity.WatcherFilter))
                {
                    if (TryParseFamilyMemberRotationIndex(info.Name, identity.BaseName, identity.Extension, out int rotIdx))
                    {
                        discovered[info.FullName] = (rotIdx, info);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        else
        {
            try
            {
                var activeInfo = new FileInfo(identity.ActiveFilePath);
                if (activeInfo.Exists)
                {
                    discovered[activeInfo.FullName] = (0, activeInfo);
                }

                string trimmed = path!.Trim();
                if (!string.Equals(trimmed, identity.ActiveFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var requestedInfo = new FileInfo(trimmed);
                    if (requestedInfo.Exists)
                    {
                        discovered[requestedInfo.FullName] = (identity.RequestedRotationIndex, requestedInfo);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (discovered.Count == 0)
        {
            return new LogFileFamily(identity, [], []);
        }

        // Apply maxRotatedFiles cap BEFORE probing 4KB file headers over SMB to avoid redundant RPCs.
        var allDiscovered = discovered.Values.ToList();
        var activeRaw = allDiscovered.FirstOrDefault(x => x.RotationIndex == 0);
        IEnumerable<(int RotationIndex, FileInfo Info)> archivedCandidates = allDiscovered
            .Where(x => x.RotationIndex > 0)
            .OrderByDescending(x => x.Info.LastWriteTimeUtc.Ticks / (TimeSpan.TicksPerSecond * 2))
            .ThenBy(x => x.RotationIndex);

        if (maxRotatedFiles.HasValue)
        {
            int cap = Math.Max(0, maxRotatedFiles.Value);
            archivedCandidates = archivedCandidates.Take(cap);
        }

        var selectedRaw = new List<(int RotationIndex, FileInfo Info)>();
        if (activeRaw.Info is not null)
        {
            selectedRaw.Add(activeRaw);
        }
        selectedRaw.AddRange(archivedCandidates);

        bool needTimestampProbe = includeFirstTimestamps && selectedRaw.Count > 1;
        var entries = new List<LogFileFamilyEntry>(selectedRaw.Count);

        foreach (var (rotIdx, info) in selectedRaw)
        {
            long length = info.Length;
            DateTime? firstTs = needTimestampProbe && length > 0
                ? TryReadFirstLogTimestamp(info.FullName)
                : null;

            entries.Add(new LogFileFamilyEntry(
                info.FullName,
                rotIdx,
                info.LastWriteTimeUtc,
                firstTs,
                length,
                Exists: true));
        }

        // Do not let 0-byte empty archives disable timestamp ordering for valid non-empty archives.
        bool allNonEmptyArchivesHaveTimestamps = includeFirstTimestamps && entries
            .Where(e => e.RotationIndex > 0 && e.Length > 0)
            .All(e => e.FirstLogTimestamp.HasValue);

        var oldestToNewest = entries
            .OrderBy(e => e.RotationIndex == 0 ? 1 : 0)
            .ThenBy(e => allNonEmptyArchivesHaveTimestamps && e.RotationIndex > 0
                ? (e.FirstLogTimestamp ?? e.LastWriteTimeUtc.ToLocalTime()).Ticks
                : 0L)
            .ThenBy(e => e.LastWriteTimeUtc.Ticks / (TimeSpan.TicksPerSecond * 2))
            .ThenByDescending(e => e.RotationIndex)
            .ToList();

        var newestToOldest = new List<LogFileFamilyEntry>(oldestToNewest.Count);
        var activeEntry = oldestToNewest.FirstOrDefault(e => e.RotationIndex == 0);
        if (activeEntry is not null)
        {
            newestToOldest.Add(activeEntry);
        }

        newestToOldest.AddRange(oldestToNewest
            .Where(e => e.RotationIndex > 0)
            .Reverse());

        return new LogFileFamily(identity, oldestToNewest, newestToOldest);
    }

    public static bool TryParseFamilyMemberRotationIndex(
        string? fileName,
        string baseName,
        string extension,
        out int rotationIndex)
    {
        rotationIndex = -1;
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(baseName))
            return false;

        int minLen = baseName.Length + extension.Length;
        if (fileName.Length < minLen ||
            !fileName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> middle = fileName.AsSpan(baseName.Length, fileName.Length - baseName.Length - extension.Length);
        if (middle.IsEmpty)
        {
            rotationIndex = 0;
            return true;
        }

        if (middle.Length >= 2 &&
            middle.Length <= 4 &&
            middle[0] == '.' &&
            int.TryParse(middle[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) &&
            n is >= 1 and <= MaxRotationIndex)
        {
            rotationIndex = n;
            return true;
        }

        return false;
    }

    public static DateTime? TryReadFirstLogTimestamp(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int toRead = (int)Math.Min((long)HeaderProbeBytes, fs.Length);
            if (toRead <= 0)
                return null;

            byte[] buf = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                int got = 0;
                while (got < toRead)
                {
                    int n = fs.Read(buf, got, toRead - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got <= 0)
                    return null;

                string headText = Encoding.UTF8.GetString(buf, 0, got);
                var match = LogTimestampPrefixRegex().Match(headText);
                if (match.Success &&
                    DateTime.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                {
                    return parsed;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}


using System.Buffers;
using System.Text;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

public sealed class LogTailReaderService : ILogTailReaderService
{
    private const int AlignWindowBytes = 64 * 1024;
    private const int MaxChunkBytes = 4 * 1024 * 1024;
    private const int HeadFingerprintBytes = 4096;
    private const int TailFingerprintBytes = 256;
    private const int MaxFirstLineFingerprintChars = 512;
    private const int MaxPendingLineChars = 2 * 1024 * 1024;

    private readonly ILogParserService _parser;

    public LogTailReaderService(ILogParserService? parser = null)
    {
        _parser = parser ?? new LogParserService();
    }

    public LogTailState CreateTailState(string path, string source = "")
    {
        return new LogTailState
        {
            Path = LogFileFamilyHelper.ResolveActiveLogPath(path),
            Source = source
        };
    }

    public void ReadLogTailLines(
        LogTailState state,
        List<string> lines,
        bool skipExisting = false,
        long maxBytes = 0,
        bool enableRolloverDrain = true) =>
        ReadLogTailLinesCore(state, lines, skipExisting, maxBytes, enableRolloverDrain, CancellationToken.None);

    private static void ReadLogTailLinesCore(
        LogTailState state,
        List<string> lines,
        bool skipExisting,
        long maxBytes,
        bool enableRolloverDrain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lines);

        cancellationToken.ThrowIfCancellationRequested();

        state.Rotated = false;
        state.DrainedFilesCount = 0;
        state.ErrorText = null;
        int startCount = lines.Count;
        string savedPending = state.PendingLineBuffer.ToString();
        FileStream? fs = null;

        try
        {
            fs = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            DateTime created = File.GetCreationTimeUtc(fs.SafeFileHandle);
            long previousOffset = state.Offset;
            long offset = previousOffset;
            bool wasInitialized = state.Initialized;
            bool rotated = false;

            if (!wasInitialized)
            {
                offset = 0;
                if (skipExisting)
                {
                    offset = AlignSkipOffsetToNewline(fs, length);
                }
            }
            else if (DetectFileRotated(fs, length, created, state))
            {
                rotated = true;
                offset = 0;
            }

            bool hasMaxBytes = maxBytes > 0;
            long remainingBytes = maxBytes;
            string? drainError = null;
            bool drainCompleted = true;

            if (rotated)
            {
                state.Rotated = true;
                int prevHeadLen = state.HeadSampleLength;
                ulong prevHeadFp = state.HeadFingerprint;
                string prevFirstLine = state.FirstLineFingerprint;
                int prevTailLen = state.TailSampleLength;
                ulong prevTailFp = state.TailFingerprint;

                int drainedCount = enableRolloverDrain && !skipExisting
                    ? DrainRotatedArchives(
                        state,
                        lines,
                        previousOffset,
                        prevHeadLen,
                        prevHeadFp,
                        prevFirstLine,
                        prevTailLen,
                        prevTailFp,
                        ref remainingBytes,
                        hasMaxBytes,
                        out drainCompleted,
                        out drainError)
                    : 0;

                state.DrainedFilesCount = drainedCount;

                if (!drainCompleted)
                {
                    if (!string.IsNullOrEmpty(drainError))
                    {
                        state.ErrorText = drainError;
                    }
                    return;
                }

                if (drainedCount > 0)
                {
                    // Avoid gluing an unterminated trailing archive fragment onto line 1 of the new active log
                    state.PendingLineBuffer.Clear();
                }
                else
                {
                    state.InBlock = false;
                    state.BlockLines.Clear();
                    state.PendingLineBuffer.Clear();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (offset < length && (!hasMaxBytes || remainingBytes > 0))
            {
                offset = ReadStreamLines(fs, offset, length, hasMaxBytes ? remainingBytes : 0, state.PendingLineBuffer, lines, cancellationToken: cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (rotated || offset != previousOffset || !wasInitialized)
            {
                CaptureFingerprints(fs, length, offset, state);
            }

            cancellationToken.ThrowIfCancellationRequested();

            state.Offset = offset;
            state.FileLength = length;
            state.CreationUtc = created;
            state.Initialized = true;
            if (!string.IsNullOrEmpty(drainError))
            {
                state.ErrorText = drainError;
            }
        }
        catch (OperationCanceledException)
        {
            if (lines.Count > startCount)
            {
                lines.RemoveRange(startCount, lines.Count - startCount);
            }
            state.PendingLineBuffer.Clear();
            state.PendingLineBuffer.Append(savedPending);
            throw;
        }
        catch (Exception ex)
        {
            state.ErrorText = ex.Message;
            if (lines.Count > startCount)
            {
                lines.RemoveRange(startCount, lines.Count - startCount);
            }
            state.PendingLineBuffer.Clear();
            state.PendingLineBuffer.Append(savedPending);
        }
        finally
        {
            fs?.Dispose();
        }
    }

    public int ReadLogTailUnits(
        LogTailState state,
        List<UnitRecord> units,
        bool skipExisting = false,
        long maxBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(units);

        cancellationToken.ThrowIfCancellationRequested();

        int before = units.Count;
        var lines = new List<string>();
        ReadLogTailLinesCore(state, lines, skipExisting: skipExisting, maxBytes: maxBytes, enableRolloverDrain: true, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (lines.Count > 0)
        {
            ConvertLogLinesToUnitsCore(state, lines, units, cancellationToken);
        }
        return units.Count - before;
    }

    public void ConvertLogLinesToUnits(LogTailState state, IReadOnlyList<string> lines, List<UnitRecord> units) =>
        ConvertLogLinesToUnitsCore(state, lines, units, CancellationToken.None);

    private void ConvertLogLinesToUnitsCore(
        LogTailState state,
        IReadOnlyList<string> lines,
        List<UnitRecord> units,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(units);

        for (int i = 0; i < lines.Count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Contains("START*", StringComparison.Ordinal))
            {
                if (state.InBlock)
                {
                    state.DroppedBlocks++;
                }
                state.InBlock = true;
                state.BlockLines.Clear();
                continue;
            }

            if (state.InBlock && line.Contains("END*", StringComparison.Ordinal))
            {
                var unit = _parser.ConvertLogBlockToUnit(state.BlockLines, state.NextIndex++, state.Source);
                if (!string.IsNullOrEmpty(state.Source) && string.IsNullOrEmpty(unit.Source))
                {
                    unit = unit.WithSource(state.Source);
                }
                units.Add(unit);
                state.InBlock = false;
                continue;
            }

            if (state.InBlock)
            {
                state.BlockLines.Add(line);
            }
        }
    }

    private static readonly TimeSpan LoadPruneMargin = TimeSpan.FromHours(12);

    public void ReadLogUnitsFromFile(
        LogTailState state,
        List<UnitRecord> units,
        long chunkBytes = 16 * 1024 * 1024,
        DateTime? startTime = null,
        DateTime? endTime = null,
        Action? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(units);

        var workingState = new LogTailState
        {
            Path = state.Path,
            Source = state.Source,
            NextIndex = state.NextIndex
        };
        var parsedUnits = new List<UnitRecord>();

        var family = LogFileFamilyHelper.ResolveLogFileFamily(workingState.Path, includeFirstTimestamps: true);
        if (family is not null && !string.IsNullOrEmpty(family.Identity.ActiveFilePath))
        {
            workingState.Path = family.Identity.ActiveFilePath;
        }

        var orderedEntries = family?.OldestToNewest ?? [];
        if (orderedEntries.Count == 0)
        {
            ReadSingleFileUnitsFromStart(
                workingState,
                parsedUnits,
                chunkBytes,
                flushIncompleteEofLine: false,
                startTime,
                endTime,
                onProgress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            units.AddRange(parsedUnits);
            CommitTailState(state, workingState);
            return;
        }

        bool hasDateFilter = startTime.HasValue || endTime.HasValue;
        bool hasMonotonicTimestamps = hasDateFilter && HasMonotonicFirstTimestamps(orderedEntries);
        bool activeHandled = false;

        for (int i = 0; i < orderedEntries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = orderedEntries[i];
            bool shouldRead = !hasDateFilter ||
                ShouldReadEntryForTimeWindow(orderedEntries, i, hasMonotonicTimestamps, startTime, endTime);

            if (entry.RotationIndex > 0)
            {
                if (!shouldRead)
                {
                    workingState.InBlock = false;
                    workingState.BlockLines.Clear();
                    continue;
                }

                var archiveState = new LogTailState
                {
                    Path = entry.Path,
                    Source = workingState.Source,
                    NextIndex = workingState.NextIndex,
                    InBlock = workingState.InBlock
                };
                if (workingState.BlockLines.Count > 0)
                {
                    archiveState.BlockLines.AddRange(workingState.BlockLines);
                }

                ReadSingleFileUnitsFromStart(
                    archiveState,
                    parsedUnits,
                    chunkBytes,
                    flushIncompleteEofLine: true,
                    startTime,
                    endTime,
                    onProgress,
                    cancellationToken);

                workingState.NextIndex = archiveState.NextIndex;
                workingState.DroppedBlocks += archiveState.DroppedBlocks;
                workingState.InBlock = archiveState.InBlock;
                workingState.BlockLines.Clear();
                if (archiveState.BlockLines.Count > 0)
                {
                    workingState.BlockLines.AddRange(archiveState.BlockLines);
                }

                if (string.IsNullOrEmpty(archiveState.ErrorText) && archiveState.Initialized)
                {
                    workingState.LoadedTotalBytes += archiveState.FileLength;
                    workingState.LoadedFilesCount++;
                    onProgress?.Invoke();
                }
                else if (!string.IsNullOrEmpty(archiveState.ErrorText))
                {
                    workingState.ErrorText ??= archiveState.ErrorText;
                }
            }
            else
            {
                activeHandled = true;
                if (shouldRead)
                {
                    workingState.Offset = 0;
                    workingState.FileLength = 0;
                    workingState.Initialized = false;
                    workingState.PendingLineBuffer.Clear();
                    string? archiveError = workingState.ErrorText;

                    ReadSingleFileUnitsFromStart(
                        workingState,
                        parsedUnits,
                        chunkBytes,
                        flushIncompleteEofLine: false,
                        startTime,
                        endTime,
                        onProgress,
                        cancellationToken);
                    bool activeReadOk = string.IsNullOrEmpty(workingState.ErrorText) && workingState.Initialized;
                    workingState.ErrorText ??= archiveError;
                    if (activeReadOk)
                    {
                        workingState.LoadedTotalBytes += workingState.FileLength;
                        workingState.LoadedFilesCount++;
                        onProgress?.Invoke();
                    }
                }
                else
                {
                    workingState.InBlock = false;
                    workingState.BlockLines.Clear();
                    AnchorStateAtActiveTail(workingState);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!activeHandled)
        {
            if (File.Exists(workingState.Path))
            {
                if (hasDateFilter && hasMonotonicTimestamps &&
                    LogFileFamilyHelper.TryReadFirstLogTimestamp(workingState.Path) is { } activeFirstTs &&
                    endTime.HasValue && activeFirstTs > endTime.Value + LoadPruneMargin)
                {
                    workingState.InBlock = false;
                    workingState.BlockLines.Clear();
                    AnchorStateAtActiveTail(workingState);
                }
                else
                {
                    workingState.Offset = 0;
                    workingState.FileLength = 0;
                    workingState.Initialized = false;
                    workingState.PendingLineBuffer.Clear();
                    string? archiveError = workingState.ErrorText;

                    ReadSingleFileUnitsFromStart(
                        workingState,
                        parsedUnits,
                        chunkBytes,
                        flushIncompleteEofLine: false,
                        startTime,
                        endTime,
                        onProgress,
                        cancellationToken);
                    workingState.ErrorText ??= archiveError;
                    if (workingState.Initialized)
                    {
                        workingState.LoadedTotalBytes += workingState.FileLength;
                        workingState.LoadedFilesCount++;
                        onProgress?.Invoke();
                    }
                }
            }
            else if (workingState.LoadedFilesCount > 0 || (hasDateFilter && orderedEntries.Count > 0))
            {
                // Rotated backups exist while active file has not been recreated yet: anchor state to active file start
                workingState.Offset = 0;
                workingState.FileLength = 0;
                workingState.HeadSampleLength = 0;
                workingState.HeadFingerprint = 0;
                workingState.FirstLineFingerprint = string.Empty;
                workingState.TailSampleLength = 0;
                workingState.TailFingerprint = 0;
                workingState.CreationUtc = null;
                workingState.PendingLineBuffer.Clear();
                workingState.InBlock = false;
                workingState.BlockLines.Clear();
                workingState.Initialized = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        units.AddRange(parsedUnits);
        CommitTailState(state, workingState);
    }

    private static void CommitTailState(LogTailState target, LogTailState source) =>
        target.CopyFrom(source);

    private void ReadSingleFileUnitsFromStart(
        LogTailState state,
        List<UnitRecord> units,
        long chunkBytes,
        bool flushIncompleteEofLine,
        DateTime? startTime = null,
        DateTime? endTime = null,
        Action? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        state.ErrorText = null;
        state.Rotated = false;
        state.DrainedFilesCount = 0;

        bool filterByTime = startTime.HasValue || endTime.HasValue;
        var lines = new List<string>();
        var chunkUnits = filterByTime ? new List<UnitRecord>() : null;

        try
        {
            using var fs = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            long initialLength = fs.Length;
            DateTime created = File.GetCreationTimeUtc(fs.SafeFileHandle);
            long offset = 0;
            int guardHeadLen = (int)Math.Min(initialLength, (long)HeadFingerprintBytes);
            ulong guardHeadFp = guardHeadLen > 0 ? ComputeRegionFingerprint(fs, 0, guardHeadLen) : 0UL;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long currentLength = fs.Length;
                if (offset > 0)
                {
                    if (currentLength < offset ||
                        (guardHeadLen > 0 && ComputeRegionFingerprint(fs, 0, guardHeadLen) != guardHeadFp))
                    {
                        state.PendingLineBuffer.Clear();
                        state.InBlock = false;
                        state.BlockLines.Clear();
                        state.Offset = 0;
                        state.FileLength = currentLength;
                        state.Initialized = false;
                        state.ErrorText = "Log file was truncated or overwritten while reading.";
                        return;
                    }
                }

                if (offset >= currentLength)
                {
                    break;
                }

                lines.Clear();
                long before = offset;
                offset = ReadStreamLines(
                    fs,
                    offset,
                    currentLength,
                    chunkBytes,
                    state.PendingLineBuffer,
                    lines,
                    flushIncompleteEofLine: flushIncompleteEofLine,
                    cancellationToken: cancellationToken);

                if (lines.Count > 0)
                {
                    if (chunkUnits is not null)
                    {
                        chunkUnits.Clear();
                        ConvertLogLinesToUnitsCore(state, lines, chunkUnits, cancellationToken);
                        foreach (var u in chunkUnits)
                        {
                            if (MatchesLoadTimeWindow(u, startTime, endTime))
                            {
                                units.Add(u);
                            }
                        }
                    }
                    else
                    {
                        ConvertLogLinesToUnitsCore(state, lines, units, cancellationToken);
                    }
                }

                if (offset > before)
                {
                    onProgress?.Invoke();
                }
                else
                {
                    break;
                }
            }

            long finalLength = fs.Length;
            CaptureFingerprints(fs, finalLength, offset, state);
            state.Offset = offset;
            state.FileLength = finalLength;
            state.CreationUtc = created;
            state.Initialized = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Initialized = false;
            state.ErrorText = ex.Message;
        }
    }

    private static bool MatchesLoadTimeWindow(UnitRecord unit, DateTime? startTime, DateTime? endTime)
    {
        if (!startTime.HasValue && !endTime.HasValue)
            return true;

        if (unit.Timestamp is not { } ts)
            return false;

        if (startTime.HasValue && ts < startTime.Value)
            return false;

        if (endTime.HasValue && ts > endTime.Value)
            return false;

        return true;
    }

    private static bool HasMonotonicFirstTimestamps(IReadOnlyList<LogFileFamilyEntry> orderedEntries)
    {
        DateTime? prev = null;
        int timestampedCount = 0;
        foreach (var entry in orderedEntries)
        {
            if (entry.FirstLogTimestamp is not { } ts)
            {
                // Require timestamps on all non-empty archives before relying on file-level skipping
                if (entry.RotationIndex > 0 && entry.Length > 0)
                    return false;
                continue;
            }

            timestampedCount++;
            if (prev.HasValue && ts < prev.Value)
                return false;

            prev = ts;
        }

        return timestampedCount > 0;
    }

    private static bool ShouldReadEntryForTimeWindow(
        IReadOnlyList<LogFileFamilyEntry> orderedEntries,
        int index,
        bool hasMonotonicTimestamps,
        DateTime? startTime,
        DateTime? endTime)
    {
        if (!hasMonotonicTimestamps)
            return true;

        var entry = orderedEntries[index];
        if (endTime.HasValue &&
            entry.FirstLogTimestamp is { } firstTs &&
            firstTs > endTime.Value + LoadPruneMargin)
        {
            return false;
        }

        if (startTime.HasValue &&
            index + 1 < orderedEntries.Count &&
            orderedEntries[index + 1].FirstLogTimestamp is { } nextFirstTs &&
            nextFirstTs < startTime.Value - LoadPruneMargin)
        {
            return false;
        }

        return true;
    }

    private static void AnchorStateAtActiveTail(LogTailState state)
    {
        try
        {
            using var fs = new FileStream(state.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            DateTime created = File.GetCreationTimeUtc(fs.SafeFileHandle);
            long offset = AlignSkipOffsetToNewline(fs, length);
            CaptureFingerprints(fs, length, offset, state);

            state.Offset = offset;
            state.FileLength = length;
            state.CreationUtc = created;
            state.PendingLineBuffer.Clear();
            state.Initialized = true;
        }
        catch (Exception ex)
        {
            state.ErrorText = ex.Message;
        }
    }

    private static long AlignSkipOffsetToNewline(FileStream fs, long length)
    {
        long offset = length;
        int back = (int)Math.Min((long)AlignWindowBytes, length);
        if (back <= 0)
            return offset;

        fs.Seek(length - back, SeekOrigin.Begin);
        byte[] tailBuf = ArrayPool<byte>.Shared.Rent(back);
        try
        {
            int got = ReadExactUpTo(fs, tailBuf, back);
            if (got > 0)
            {
                int lf = Array.LastIndexOf(tailBuf, (byte)10, got - 1);
                if (lf >= 0)
                {
                    offset = (length - back) + lf + 1;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tailBuf);
        }

        return offset;
    }

    private static bool DetectFileRotated(FileStream fs, long length, DateTime created, LogTailState state)
    {
        long offset = state.Offset;
        if (length < offset)
            return true;

        if (state.CreationUtc.HasValue && created != state.CreationUtc.Value)
            return true;

        bool checkHeadFp = state.HeadSampleLength > 0 && length >= state.HeadSampleLength;
        bool checkFirstLine = !string.IsNullOrEmpty(state.FirstLineFingerprint);
        if (checkHeadFp || checkFirstLine)
        {
            int fpTargetLen = checkHeadFp ? state.HeadSampleLength : 0;
            ProbeHeadSampleAndFirstLine(
                fs,
                length,
                fpTargetLen,
                includeFirstLine: checkFirstLine,
                out int sampledLen,
                out ulong headFingerprint,
                out string currentFirstLine);

            if (checkHeadFp && sampledLen >= state.HeadSampleLength && headFingerprint != state.HeadFingerprint)
                return true;

            if (checkFirstLine &&
                !string.IsNullOrEmpty(currentFirstLine) &&
                !string.Equals(currentFirstLine, state.FirstLineFingerprint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (state.TailSampleLength > 0 &&
            offset >= state.TailSampleLength &&
            length >= offset)
        {
            ulong tailFingerprint = ComputeRegionFingerprint(fs, offset - state.TailSampleLength, state.TailSampleLength);
            if (tailFingerprint != state.TailFingerprint)
                return true;
        }

        return false;
    }

    private static int DrainRotatedArchives(
        LogTailState state,
        List<string> lines,
        long previousOffset,
        int prevHeadLen,
        ulong prevHeadFp,
        string prevFirstLine,
        int prevTailLen,
        ulong prevTailFp,
        ref long remainingBytes,
        bool hasMaxBytes,
        out bool drainCompleted,
        out string? errorText)
    {
        drainCompleted = true;
        errorText = null;
        if (prevHeadLen <= 0)
            return 0;

        var family = LogFileFamilyHelper.ResolveLogFileFamily(state.Path, includeFirstTimestamps: false);
        if (family is null || family.NewestToOldest.Count == 0)
            return 0;

        var archivedNewestFirst = family.NewestToOldest
            .Where(e => e.RotationIndex > 0)
            .ToList();

        if (archivedNewestFirst.Count == 0)
            return 0;

        var openedArchives = new List<(FileStream Stream, long Length, ulong HeadFp, int HeadLen, string FirstLine)>();
        int matchedIdx = -1;
        string? probeError = null;

        try
        {
            for (int i = 0; i < archivedNewestFirst.Count; i++)
            {
                FileStream? afs = null;
                try
                {
                    afs = new FileStream(
                        archivedNewestFirst[i].Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        4096,
                        FileOptions.SequentialScan);

                    long archiveLength = afs.Length;
                    ProbeHeadSampleAndFirstLine(
                        afs,
                        archiveLength,
                        HeadFingerprintBytes,
                        includeFirstLine: true,
                        out int headLen,
                        out ulong headFp,
                        out string firstLine);

                    openedArchives.Add((afs, archiveLength, headFp, headLen, firstLine));
                    afs = null; // Ownership transferred to openedArchives

                    var cur = openedArchives[^1];
                    if (MatchesOpenedRolloverSource(
                        cur.Stream,
                        cur.Length,
                        cur.HeadLen,
                        cur.HeadFp,
                        cur.FirstLine,
                        previousOffset,
                        prevHeadLen,
                        prevHeadFp,
                        prevFirstLine,
                        prevTailLen,
                        prevTailFp))
                    {
                        matchedIdx = openedArchives.Count - 1;
                        break;
                    }
                }
                catch (IOException ex)
                {
                    probeError ??= ex.Message;
                    afs?.Dispose();
                }
                catch (UnauthorizedAccessException ex)
                {
                    probeError ??= ex.Message;
                    afs?.Dispose();
                }
            }

            if (matchedIdx < 0)
            {
                errorText = probeError;
                return 0;
            }

            int drainedCount = 0;
            var seenArchives = new HashSet<(int HeadLen, ulong HeadFp, string FirstLine, long Length)>();

            for (int idx = matchedIdx; idx >= 0; idx--)
            {
                var (archiveFs, initialLength, headLen, headFp, firstLine) = (
                    openedArchives[idx].Stream,
                    openedArchives[idx].Length,
                    openedArchives[idx].HeadLen,
                    openedArchives[idx].HeadFp,
                    openedArchives[idx].FirstLine);

                var identityKey = (headLen, headFp, firstLine, initialLength);
                if (!seenArchives.Add(identityKey))
                    continue;

                long startPos = idx == matchedIdx ? previousOffset : 0;
                if (idx != matchedIdx)
                {
                    state.PendingLineBuffer.Clear();
                }

                try
                {
                    long archiveLen = archiveFs.Length;
                    long endPos = startPos;
                    if (archiveLen > startPos)
                    {
                        long readLimit = hasMaxBytes ? remainingBytes : 0;
                        endPos = ReadStreamLines(
                            archiveFs,
                            startPos,
                            archiveLen,
                            readLimit,
                            state.PendingLineBuffer,
                            lines,
                            flushIncompleteEofLine: true);
                        if (hasMaxBytes)
                        {
                            remainingBytes = Math.Max(0, remainingBytes - Math.Max(0, endPos - startPos));
                        }
                    }

                    drainedCount++;

                    if (endPos < archiveLen || (idx > 0 && hasMaxBytes && remainingBytes <= 0))
                    {
                        // Mid-archive or multi-archive budget stop: anchor state here so next poll resumes cleanly.
                        CaptureFingerprints(archiveFs, archiveLen, endPos, state);
                        state.Offset = endPos;
                        state.FileLength = archiveLen;
                        state.CreationUtc = File.GetCreationTimeUtc(archiveFs.SafeFileHandle);
                        state.Initialized = true;
                        drainCompleted = false;
                        break;
                    }
                }
                catch (IOException ex)
                {
                    errorText ??= ex.Message;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    errorText ??= ex.Message;
                    break;
                }
            }

            return drainedCount;
        }
        finally
        {
            foreach (var item in openedArchives)
            {
                item.Stream.Dispose();
            }
        }
    }

    private static bool MatchesOpenedRolloverSource(
        FileStream afs,
        long archiveLength,
        int candidateHeadLen,
        ulong candidateHeadFp,
        string candidateFirstLine,
        long previousOffset,
        int prevHeadLen,
        ulong prevHeadFp,
        string prevFirstLine,
        int prevTailLen,
        ulong prevTailFp)
    {
        if (archiveLength < previousOffset || archiveLength < prevHeadLen)
            return false;

        if (candidateHeadLen == prevHeadLen)
        {
            if (candidateHeadFp != prevHeadFp)
                return false;
        }
        else if (ComputeRegionFingerprint(afs, 0, prevHeadLen) != prevHeadFp)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(prevFirstLine) &&
            !string.Equals(candidateFirstLine, prevFirstLine, StringComparison.Ordinal))
        {
            return false;
        }

        if (prevTailLen > 0 && previousOffset >= prevTailLen)
        {
            if (ComputeRegionFingerprint(afs, previousOffset - prevTailLen, prevTailLen) != prevTailFp)
                return false;
        }

        return true;
    }

    private static long ReadStreamLines(
        FileStream fs,
        long offset,
        long length,
        long maxBytes,
        StringBuilder pendingLineBuffer,
        List<string> lines,
        bool flushIncompleteEofLine = false,
        CancellationToken cancellationToken = default)
    {
        long startOffset = offset;
        int maxChunk = MaxChunkBytes;
        if (maxBytes > 0 && maxBytes < maxChunk)
        {
            maxChunk = (int)Math.Max(8L, maxBytes);
        }
        int bufSize = (int)Math.Min((long)maxChunk, length - offset);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining = maxBytes > 0 ? maxBytes - (offset - startOffset) : long.MaxValue;
                if (remaining <= 0)
                    break;

                fs.Seek(offset, SeekOrigin.Begin);
                int want = (int)Math.Min(Math.Min((long)bufSize, length - offset), remaining);
                int got = ReadExactUpTo(fs, buffer, want);
                if (got <= 0)
                    break;

                int lf = buffer.AsSpan(0, got).LastIndexOf((byte)'\n');
                if (lf < 0)
                {
                    if (offset + got >= length)
                    {
                        if (flushIncompleteEofLine)
                        {
                            ReadOnlySpan<byte> eofSpan = buffer.AsSpan(0, got).TrimEnd((byte)'\r');
                            if (pendingLineBuffer.Length > 0)
                            {
                                AppendUtf8BytesWithCap(pendingLineBuffer, eofSpan, lines);
                                if (pendingLineBuffer.Length > 0)
                                {
                                    lines.Add(pendingLineBuffer.ToString().TrimEnd('\r'));
                                    pendingLineBuffer.Clear();
                                }
                            }
                            else if (eofSpan.Length <= 32 * 1024)
                            {
                                lines.Add(eofSpan.IsEmpty ? string.Empty : Encoding.UTF8.GetString(eofSpan));
                            }
                            else
                            {
                                AppendUtf8BytesWithCap(pendingLineBuffer, eofSpan, lines);
                                if (pendingLineBuffer.Length > 0)
                                {
                                    lines.Add(pendingLineBuffer.ToString().TrimEnd('\r'));
                                    pendingLineBuffer.Clear();
                                }
                            }
                            offset += got;
                        }
                        break;
                    }

                    int cut = FindSafeUtf8Boundary(buffer.AsSpan(0, got));
                    if (cut <= 0)
                    {
                        break;
                    }

                    AppendUtf8BytesWithCap(pendingLineBuffer, buffer.AsSpan(0, cut), lines);
                    offset += cut;
                    if (maxBytes > 0 && (offset - startOffset) >= maxBytes)
                        break;
                    continue;
                }

                // Slice lines directly on UTF-8 bytes to avoid multi-megabyte LOH UTF-16 string allocations.
                ReadOnlySpan<byte> chunkBytes = buffer.AsSpan(0, lf + 1);
                if (pendingLineBuffer.Length > 0)
                {
                    int firstNl = chunkBytes.IndexOf((byte)'\n');
                    if (firstNl >= 0)
                    {
                        ReadOnlySpan<byte> firstSegment = chunkBytes[..firstNl].TrimEnd((byte)'\r');
                        if (!firstSegment.IsEmpty)
                        {
                            AppendUtf8BytesWithCap(pendingLineBuffer, firstSegment, lines);
                        }
                        if (pendingLineBuffer.Length > 0)
                        {
                            lines.Add(pendingLineBuffer.ToString().TrimEnd('\r'));
                            pendingLineBuffer.Clear();
                        }
                        chunkBytes = chunkBytes[(firstNl + 1)..];
                    }
                }

                while (true)
                {
                    int nl = chunkBytes.IndexOf((byte)'\n');
                    if (nl < 0)
                        break;

                    ReadOnlySpan<byte> lineBytes = chunkBytes[..nl].TrimEnd((byte)'\r');
                    lines.Add(lineBytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(lineBytes));
                    chunkBytes = chunkBytes[(nl + 1)..];
                }

                offset += lf + 1;
                if (maxBytes > 0 && (offset - startOffset) >= maxBytes)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return offset;
    }

    private static void AppendUtf8BytesWithCap(
        StringBuilder pendingLineBuffer,
        ReadOnlySpan<byte> utf8Bytes,
        List<string> lines)
    {
        if (utf8Bytes.IsEmpty)
        {
            return;
        }

        // Keep slice <= 32 KB so rented char[] stays well under the 85 KB LOH threshold.
        const int maxSliceBytes = 32 * 1024;
        Span<char> stackCharBuf = stackalloc char[1024];

        while (!utf8Bytes.IsEmpty)
        {
            int sliceLen = utf8Bytes.Length;
            if (sliceLen > maxSliceBytes)
            {
                sliceLen = FindSafeUtf8Boundary(utf8Bytes[..maxSliceBytes]);
                if (sliceLen <= 0)
                {
                    sliceLen = Math.Min(maxSliceBytes, utf8Bytes.Length);
                }
            }

            ReadOnlySpan<byte> slice = utf8Bytes[..sliceLen];
            int maxChars = Encoding.UTF8.GetMaxCharCount(slice.Length);
            if (maxChars <= stackCharBuf.Length)
            {
                int written = Encoding.UTF8.GetChars(slice, stackCharBuf);
                AppendPendingFragmentWithCap(pendingLineBuffer, stackCharBuf[..written], lines);
            }
            else
            {
                char[] rented = ArrayPool<char>.Shared.Rent(maxChars);
                try
                {
                    int written = Encoding.UTF8.GetChars(slice, rented);
                    AppendPendingFragmentWithCap(pendingLineBuffer, rented.AsSpan(0, written), lines);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }

            utf8Bytes = utf8Bytes[sliceLen..];
        }
    }

    private static void AppendPendingFragmentWithCap(
        StringBuilder pendingLineBuffer,
        ReadOnlySpan<char> fragment,
        List<string> lines)
    {
        int available = MaxPendingLineChars - pendingLineBuffer.Length;
        if (fragment.Length <= available)
        {
            pendingLineBuffer.Append(fragment);
            return;
        }

        if (pendingLineBuffer.Length > 0)
        {
            lines.Add(pendingLineBuffer.ToString().TrimEnd('\r'));
            pendingLineBuffer.Clear();
        }

        if (fragment.Length > MaxPendingLineChars)
        {
            lines.Add(new string(fragment[..MaxPendingLineChars]).TrimEnd('\r'));
        }
        else
        {
            pendingLineBuffer.Append(fragment);
        }
    }

    private static int ReadExactUpTo(FileStream fs, byte[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = fs.Read(buffer, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    private static int FindSafeUtf8Boundary(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length <= 0)
            return 0;

        int idx = buffer.Length - 1;
        if (buffer[idx] < 0x80)
            return buffer.Length;

        int contBytes = 0;
        while (idx > 0 && (buffer[idx] & 0xC0) == 0x80 && contBytes < 3)
        {
            contBytes++;
            idx--;
        }

        byte lead = buffer[idx];
        if (lead < 0xC0)
        {
            return idx > 0 ? idx : buffer.Length;
        }

        int expectedLen = lead switch
        {
            >= 0xF0 => 4,
            >= 0xE0 => 3,
            _ => 2
        };

        return (1 + contBytes) < expectedLen ? idx : buffer.Length;
    }

    private static void CaptureFingerprints(FileStream fs, long length, long currentOffset, LogTailState state)
    {
        ProbeHeadSampleAndFirstLine(
            fs,
            length,
            HeadFingerprintBytes,
            includeFirstLine: true,
            out int newHeadLen,
            out ulong headFingerprint,
            out string firstLine);

        state.HeadSampleLength = newHeadLen;
        state.HeadFingerprint = headFingerprint;
        state.FirstLineFingerprint = firstLine;

        int newTailLen = currentOffset > 0 ? (int)Math.Min(currentOffset, (long)TailFingerprintBytes) : 0;
        state.TailSampleLength = newTailLen;
        state.TailFingerprint = newTailLen > 0
            ? ComputeRegionFingerprint(fs, currentOffset - newTailLen, newTailLen)
            : 0UL;
    }

    private static ulong ComputeRegionFingerprint(FileStream fs, long startOffset, int byteCount)
    {
        if (byteCount <= 0 || startOffset < 0)
            return 0UL;

        byte[] buf = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            fs.Seek(startOffset, SeekOrigin.Begin);
            int got = ReadExactUpTo(fs, buf, byteCount);
            return ComputeFnv1a64(buf.AsSpan(0, got));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static ulong ComputeFnv1a64(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return 0UL;

        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static void ProbeHeadSampleAndFirstLine(
        FileStream fs,
        long length,
        int fingerprintBytes,
        bool includeFirstLine,
        out int sampledFingerprintLen,
        out ulong headFingerprint,
        out string firstLine)
    {
        sampledFingerprintLen = 0;
        headFingerprint = 0UL;
        firstLine = string.Empty;

        int targetReadBytes = includeFirstLine
            ? Math.Max(HeadFingerprintBytes, fingerprintBytes)
            : fingerprintBytes;
        int probeLen = (int)Math.Min(length, (long)targetReadBytes);
        if (probeLen <= 0)
            return;

        byte[] buf = ArrayPool<byte>.Shared.Rent(probeLen);
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            int got = ReadExactUpTo(fs, buf, probeLen);
            if (got <= 0)
                return;

            if (fingerprintBytes > 0)
            {
                sampledFingerprintLen = Math.Min(got, fingerprintBytes);
                headFingerprint = ComputeFnv1a64(buf.AsSpan(0, sampledFingerprintLen));
            }

            if (includeFirstLine)
            {
                int scanStart = 0;
                while (scanStart < got)
                {
                    int lf = Array.IndexOf(buf, (byte)10, scanStart, got - scanStart);
                    if (lf < 0)
                        break;

                    string line = Encoding.UTF8.GetString(buf, scanStart, lf - scanStart).TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        firstLine = line.Length > MaxFirstLineFingerprintChars
                            ? line[..MaxFirstLineFingerprintChars]
                            : line;
                        break;
                    }

                    scanStart = lf + 1;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

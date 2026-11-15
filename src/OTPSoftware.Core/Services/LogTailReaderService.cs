using System.Buffers;
using System.Text;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

public sealed class LogTailReaderService(ILogParserService? parser = null) : ILogTailReaderService
{
    private const int AlignWindowBytes = 64 * 1024;
    private const int MaxChunkBytes = 4 * 1024 * 1024;
    private const int HeadFingerprintBytes = 4096;
    private const int TailFingerprintBytes = 256;
    private const int MaxFirstLineFingerprintChars = 512;
    private const int MaxPendingLineChars = 2 * 1024 * 1024;

    private readonly ILogParserService _parser = parser ?? new LogParserService();

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
        bool enableRolloverDrain = true)
    {
        Utf8UnitLineFilter noFilter = default;
        ReadLogTailLinesCore(state, lines, skipExisting, maxBytes, enableRolloverDrain, ref noFilter, CancellationToken.None);
    }

    private static void ReadLogTailLinesCore(
        LogTailState state,
        List<string> lines,
        bool skipExisting,
        long maxBytes,
        bool enableRolloverDrain,
        ref Utf8UnitLineFilter unitFilter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(lines);

        cancellationToken.ThrowIfCancellationRequested();

        state.Rotated = false;
        state.DrainedFilesCount = 0;
        state.ErrorText = null;
        int startCount = lines.Count;
        int savedPendingLen = state.PendingLineBuffer.Length;
        char[]? savedPendingBuf = savedPendingLen > 0 ? ArrayPool<char>.Shared.Rent(savedPendingLen) : null;
        if (savedPendingBuf is not null)
        {
            state.PendingLineBuffer.CopyTo(0, savedPendingBuf, 0, savedPendingLen);
        }
        FileStream? fs = null;

        try
        {
            fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                state.Path,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan,
                cancellationToken: cancellationToken);
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
                        ref unitFilter,
                        cancellationToken,
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
                    unitFilter.ResetBlock();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (offset < length && (!hasMaxBytes || remainingBytes > 0))
            {
                offset = ReadStreamLines(fs, offset, length, hasMaxBytes ? remainingBytes : 0, state.PendingLineBuffer, lines, ref unitFilter, cancellationToken: cancellationToken);
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
            RestorePendingBuffer(state.PendingLineBuffer, savedPendingBuf, savedPendingLen);
            throw;
        }
        catch (Exception ex)
        {
            state.ErrorText = ex.Message;
            if (lines.Count > startCount)
            {
                lines.RemoveRange(startCount, lines.Count - startCount);
            }
            RestorePendingBuffer(state.PendingLineBuffer, savedPendingBuf, savedPendingLen);
        }
        finally
        {
            if (savedPendingBuf is not null)
            {
                ArrayPool<char>.Shared.Return(savedPendingBuf);
            }
            fs?.Dispose();
        }
    }

    private static void RestorePendingBuffer(StringBuilder buffer, char[]? savedBuf, int savedLen)
    {
        buffer.Clear();
        if (savedLen > 0 && savedBuf is not null)
        {
            buffer.Append(savedBuf, 0, savedLen);
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
        var unitFilter = Utf8UnitLineFilter.Create(_parser is LogParserService, state);
        ReadLogTailLinesCore(state, lines, skipExisting: skipExisting, maxBytes: maxBytes, enableRolloverDrain: true, ref unitFilter, cancellationToken: cancellationToken);
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

        if (lines.Count >= 64)
        {
            units.EnsureCapacity(units.Count + Math.Clamp(lines.Count / 16, 4, 4096));
        }

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
        var unitFilter = Utf8UnitLineFilter.Create(_parser is LogParserService, state);
        var chunkUnits = filterByTime ? new List<UnitRecord>() : null;
        int unitsStartCount = units.Count;
        long lineBatchBytes = chunkBytes > 0 ? Math.Min(chunkBytes, 512L * 1024L) : 512L * 1024L;

        try
        {
            using var fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                state.Path,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan,
                cancellationToken: cancellationToken);
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
                        if (units.Count > unitsStartCount)
                        {
                            units.RemoveRange(unitsStartCount, units.Count - unitsStartCount);
                        }
                        state.PendingLineBuffer.Clear();
                        state.InBlock = false;
                        state.BlockLines.Clear();
                        unitFilter.ResetBlock();
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
                    lineBatchBytes,
                    state.PendingLineBuffer,
                    lines,
                    ref unitFilter,
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
            using var fs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                state.Path,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
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
        ref Utf8UnitLineFilter unitFilter,
        CancellationToken cancellationToken,
        out bool drainCompleted,
        out string? errorText)
    {
        drainCompleted = true;
        errorText = null;
        if (prevHeadLen <= 0)
            return 0;

        cancellationToken.ThrowIfCancellationRequested();

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
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? afs = null;
                try
                {
                    afs = FileIoRetryHelper.OpenSharedReadStreamWithRetry(
                        archivedNewestFirst[i].Path,
                        FileShare.ReadWrite | FileShare.Delete,
                        4096,
                        FileOptions.SequentialScan,
                        cancellationToken: cancellationToken);

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
                cancellationToken.ThrowIfCancellationRequested();
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
                            ref unitFilter,
                            flushIncompleteEofLine: true,
                            cancellationToken: cancellationToken);
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

    private const string StartMarkerSentinel = "START*";
    private const string EndMarkerSentinel = "END*";

    private struct Utf8UnitLineFilter
    {
        public readonly bool Enabled;
        private readonly bool _filterInsideBlock;
        public bool InBlock;
        private bool _blockHasTimestamp;

        private Utf8UnitLineFilter(bool enabled, bool filterInsideBlock, bool inBlock, bool blockHasTimestamp)
        {
            Enabled = enabled;
            _filterInsideBlock = filterInsideBlock;
            InBlock = inBlock;
            _blockHasTimestamp = blockHasTimestamp;
        }

        public static Utf8UnitLineFilter Create(bool filterInsideBlock, LogTailState state)
        {
            bool hasTs = false;
            if (state.InBlock && state.BlockLines.Count > 0)
            {
                for (int i = 0; i < state.BlockLines.Count; i++)
                {
                    if (ContainsValidBracketTimestampFromChars(state.BlockLines[i].AsSpan()))
                    {
                        hasTs = true;
                        break;
                    }
                }
            }

            return new Utf8UnitLineFilter(
                enabled: true,
                filterInsideBlock: filterInsideBlock,
                inBlock: state.InBlock,
                blockHasTimestamp: hasTs);
        }

        public void OnBlockStart()
        {
            InBlock = true;
            _blockHasTimestamp = false;
        }

        public void OnBlockEnd()
        {
            InBlock = false;
            _blockHasTimestamp = false;
        }

        public void ResetBlock()
        {
            InBlock = false;
            _blockHasTimestamp = false;
        }

        public bool ShouldKeepUtf8InsideBlock(ReadOnlySpan<byte> lineBytes)
        {
            int loggerIdx = lineBytes.IndexOf("[GovionLogger.cpp:"u8);
            if (loggerIdx >= 0)
            {
                ReadOnlySpan<byte> afterLogger = lineBytes[(loggerIdx + 18)..];
                if (afterLogger.IndexOf("PMNT"u8) >= 0 ||
                    afterLogger.IndexOf("PANELNAME:"u8) >= 0 ||
                    afterLogger.IndexOf("BLUNAME:"u8) >= 0 ||
                    afterLogger.IndexOf("OTP-"u8) >= 0 ||
                    afterLogger.IndexOf("Query"u8) >= 0 ||
                    afterLogger.IndexOf("MES debt collection"u8) >= 0)
                {
                    if (!_blockHasTimestamp && ContainsValidUtf8BracketTimestamp(lineBytes))
                    {
                        _blockHasTimestamp = true;
                    }
                    return true;
                }
            }

            if (!_filterInsideBlock)
            {
                return true;
            }

            if (!_blockHasTimestamp && ContainsValidUtf8BracketTimestamp(lineBytes))
            {
                _blockHasTimestamp = true;
                return true;
            }

            return false;
        }
    }

    private static bool TryParse2AsciiDigits(byte b0, byte b1, out int value)
    {
        uint d0 = (uint)(b0 - '0');
        uint d1 = (uint)(b1 - '0');
        if (d0 <= 9 && d1 <= 9)
        {
            value = (int)(d0 * 10 + d1);
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryParse4AsciiDigits(ReadOnlySpan<byte> span, int offset, out int value)
    {
        uint d0 = (uint)(span[offset] - '0');
        uint d1 = (uint)(span[offset + 1] - '0');
        uint d2 = (uint)(span[offset + 2] - '0');
        uint d3 = (uint)(span[offset + 3] - '0');
        if (d0 <= 9 && d1 <= 9 && d2 <= 9 && d3 <= 9)
        {
            value = (int)(d0 * 1000 + d1 * 100 + d2 * 10 + d3);
            return true;
        }
        value = 0;
        return false;
    }

    private static bool IsValidTimestampCalendarValues(int year, int month, int day, int hour, int minute, int second) =>
        year >= 1 &&
        month is >= 1 and <= 12 &&
        day >= 1 && day <= DateTime.DaysInMonth(year, month) &&
        hour <= 23 &&
        minute <= 59 &&
        second <= 59;

    private static bool ContainsValidBracketTimestampFromChars(ReadOnlySpan<char> line)
    {
        if (line.Length < 21 || line.IndexOf('[') < 0)
        {
            return false;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(line.Length);
        Span<byte> stackBuf = stackalloc byte[512];
        if (maxBytes <= stackBuf.Length)
        {
            int written = Encoding.UTF8.GetBytes(line, stackBuf);
            return ContainsValidUtf8BracketTimestamp(stackBuf[..written]);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int written = Encoding.UTF8.GetBytes(line, rented);
            return ContainsValidUtf8BracketTimestamp(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool ContainsValidUtf8BracketTimestamp(ReadOnlySpan<byte> lineBytes)
    {
        ReadOnlySpan<byte> remaining = lineBytes;
        while (true)
        {
            int openIdx = remaining.IndexOf((byte)'[');
            if (openIdx < 0)
            {
                return false;
            }

            remaining = remaining[(openIdx + 1)..];
            if (remaining.Length < 20)
            {
                return false;
            }

            if (!TryParse4AsciiDigits(remaining, 0, out int year) ||
                remaining[4] != (byte)'-' ||
                !TryParse2AsciiDigits(remaining[5], remaining[6], out int month) ||
                remaining[7] != (byte)'-' ||
                !TryParse2AsciiDigits(remaining[8], remaining[9], out int day) ||
                (remaining[10] != (byte)' ' && remaining[10] != (byte)'\t'))
            {
                continue;
            }

            int pos = 11;
            while (pos < remaining.Length && (remaining[pos] == (byte)' ' || remaining[pos] == (byte)'\t'))
            {
                pos++;
            }

            if (pos + 8 >= remaining.Length ||
                !TryParse2AsciiDigits(remaining[pos], remaining[pos + 1], out int hour) ||
                remaining[pos + 2] != (byte)':' ||
                !TryParse2AsciiDigits(remaining[pos + 3], remaining[pos + 4], out int minute) ||
                remaining[pos + 5] != (byte)':' ||
                !TryParse2AsciiDigits(remaining[pos + 6], remaining[pos + 7], out int second))
            {
                continue;
            }

            int endPos = pos + 8;
            if (endPos < remaining.Length && remaining[endPos] == (byte)'.')
            {
                int fracStart = endPos + 1;
                int fracEnd = fracStart;
                while (fracEnd < remaining.Length && (uint)(remaining[fracEnd] - '0') <= 9)
                {
                    fracEnd++;
                }

                int fracDigits = fracEnd - fracStart;
                if (fracDigits is 0 or > 7)
                {
                    continue;
                }
                endPos = fracEnd;
            }

            if (endPos < remaining.Length &&
                remaining[endPos] == (byte)']' &&
                IsValidTimestampCalendarValues(year, month, day, hour, minute, second))
            {
                return true;
            }
        }
    }

    private static bool IsAsciiWhitespaceOnlyUtf8(ReadOnlySpan<byte> lineBytes)
    {
        for (int i = 0; i < lineBytes.Length; i++)
        {
            byte b = lineBytes[i];
            if (b != (byte)' ' && (uint)(b - (byte)'\t') > (byte)'\r' - (byte)'\t')
            {
                return false;
            }
        }
        return true;
    }

    private static void EmitUtf8Line(
        List<string> lines,
        ReadOnlySpan<byte> lineBytes,
        ref Utf8UnitLineFilter unitFilter,
        string? preDecodedLine = null)
    {
        if (!unitFilter.Enabled)
        {
            lines.Add(preDecodedLine ?? (lineBytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(lineBytes)));
            return;
        }

        if (lineBytes.IsEmpty || IsAsciiWhitespaceOnlyUtf8(lineBytes))
        {
            return;
        }

        if (lineBytes.IndexOf("START*"u8) >= 0)
        {
            unitFilter.OnBlockStart();
            lines.Add(StartMarkerSentinel);
            return;
        }

        if (!unitFilter.InBlock)
        {
            return;
        }

        if (lineBytes.IndexOf("END*"u8) >= 0)
        {
            unitFilter.OnBlockEnd();
            lines.Add(EndMarkerSentinel);
            return;
        }

        if (unitFilter.ShouldKeepUtf8InsideBlock(lineBytes))
        {
            lines.Add(preDecodedLine ?? Encoding.UTF8.GetString(lineBytes));
        }
    }

    private static void EmitDecodedLine(
        List<string> lines,
        string line,
        ref Utf8UnitLineFilter unitFilter)
    {
        if (!unitFilter.Enabled)
        {
            lines.Add(line);
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(line.Length);
        Span<byte> stackBuf = stackalloc byte[512];
        if (maxBytes <= stackBuf.Length)
        {
            int written = Encoding.UTF8.GetBytes(line, stackBuf);
            EmitUtf8Line(lines, stackBuf[..written], ref unitFilter, line);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int written = Encoding.UTF8.GetBytes(line, rented);
            EmitUtf8Line(lines, rented.AsSpan(0, written), ref unitFilter, line);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static long ReadStreamLines(
        FileStream fs,
        long offset,
        long length,
        long maxBytes,
        StringBuilder pendingLineBuffer,
        List<string> lines,
        ref Utf8UnitLineFilter unitFilter,
        bool flushIncompleteEofLine = false,
        CancellationToken cancellationToken = default)
    {
        if (offset >= length)
        {
            return offset;
        }

        long startOffset = offset;
        long remainingAvailable = length - offset;
        long boundedBytes = maxBytes > 0 ? Math.Min(remainingAvailable, maxBytes) : remainingAvailable;
        // Estimate ~96 bytes per log line for raw tails, or a compact capacity when UTF-8 pre-filtering drops noise lines.
        int estimatedLines = unitFilter.Enabled
            ? (int)Math.Clamp(boundedBytes / 1024L, 16L, 2_048L)
            : (int)Math.Clamp(boundedBytes / 96L, 16L, 16_384L);
        lines.EnsureCapacity(lines.Count + estimatedLines);

        int maxChunk = MaxChunkBytes;
        if (maxBytes > 0 && maxBytes < maxChunk)
        {
            maxChunk = (int)Math.Max(8L, maxBytes);
        }
        int bufSize = (int)Math.Min((long)maxChunk, remainingAvailable);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining = maxBytes > 0 ? maxBytes - (offset - startOffset) : long.MaxValue;
                if (remaining <= 0)
                    break;

                if (fs.Position != offset)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                }
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
                                AppendUtf8BytesWithCap(pendingLineBuffer, eofSpan, lines, ref unitFilter);
                                if (pendingLineBuffer.Length > 0)
                                {
                                    EmitDecodedLine(lines, pendingLineBuffer.ToString().TrimEnd('\r'), ref unitFilter);
                                    pendingLineBuffer.Clear();
                                }
                            }
                            else if (eofSpan.Length <= 32 * 1024)
                            {
                                EmitUtf8Line(lines, eofSpan, ref unitFilter);
                            }
                            else
                            {
                                AppendUtf8BytesWithCap(pendingLineBuffer, eofSpan, lines, ref unitFilter);
                                if (pendingLineBuffer.Length > 0)
                                {
                                    EmitDecodedLine(lines, pendingLineBuffer.ToString().TrimEnd('\r'), ref unitFilter);
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

                    AppendUtf8BytesWithCap(pendingLineBuffer, buffer.AsSpan(0, cut), lines, ref unitFilter);
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
                            AppendUtf8BytesWithCap(pendingLineBuffer, firstSegment, lines, ref unitFilter);
                        }
                        if (pendingLineBuffer.Length > 0)
                        {
                            EmitDecodedLine(lines, pendingLineBuffer.ToString().TrimEnd('\r'), ref unitFilter);
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
                    EmitUtf8Line(lines, lineBytes, ref unitFilter);
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
        List<string> lines,
        ref Utf8UnitLineFilter unitFilter)
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
                AppendPendingFragmentWithCap(pendingLineBuffer, stackCharBuf[..written], lines, ref unitFilter);
            }
            else
            {
                char[] rented = ArrayPool<char>.Shared.Rent(maxChars);
                try
                {
                    int written = Encoding.UTF8.GetChars(slice, rented);
                    AppendPendingFragmentWithCap(pendingLineBuffer, rented.AsSpan(0, written), lines, ref unitFilter);
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
        List<string> lines,
        ref Utf8UnitLineFilter unitFilter)
    {
        int available = MaxPendingLineChars - pendingLineBuffer.Length;
        if (fragment.Length <= available)
        {
            pendingLineBuffer.Append(fragment);
            return;
        }

        if (pendingLineBuffer.Length > 0)
        {
            EmitDecodedLine(lines, pendingLineBuffer.ToString().TrimEnd('\r'), ref unitFilter);
            pendingLineBuffer.Clear();
        }

        if (fragment.Length > MaxPendingLineChars)
        {
            EmitDecodedLine(lines, new string(fragment[..MaxPendingLineChars]).TrimEnd('\r'), ref unitFilter);
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

using System.Text;

namespace OTPSoftware.Core.Models;

/// <summary>
/// Represents a single panel inspection cycle parsed from a START*** to END*** log block.
/// Payload fields are immutable (<c>init</c>) for safe handoff from background readers to UI/statistics threads;
/// <see cref="Index"/> is the sole mutable sequence coordinate, managed on the UI thread by <c>GuiDisplayState</c>.
/// </summary>
public sealed class UnitRecord
{
    private int _index;

    /// <summary>
    /// Sequential position assigned and updated on the UI thread upon ingestion or eviction.
    /// Uses Volatile acquire/release semantics so concurrent background analytics/exports observe consistent values.
    /// </summary>
    public int Index
    {
        get => Volatile.Read(ref _index);
        set => Volatile.Write(ref _index, value);
    }
    public string Source { get; init; } = string.Empty;
    public string Pmnt { get; init; } = string.Empty;
    public int PassCount { get; init; }
    public int TotalRuns { get; init; }
    public string PanelName { get; init; } = string.Empty;
    public string BluName { get; init; } = string.Empty;
    public IReadOnlyList<string> Errors { get; init; } = [];
    public DateTime? Timestamp { get; init; }
    public string MesResult { get; init; } = string.Empty;

    public bool IsNg => Errors.Count > 0;

    /// <summary>Returns a copy with <see cref="Source"/> populated when an <c>ILogParserService</c> implementation omits it.</summary>
    public UnitRecord WithSource(string source) => new()
    {
        Index = Index,
        Source = source,
        Pmnt = Pmnt,
        PassCount = PassCount,
        TotalRuns = TotalRuns,
        PanelName = PanelName,
        BluName = BluName,
        Errors = Errors,
        Timestamp = Timestamp,
        MesResult = MesResult
    };
}

/// <summary>
/// Immutable two-stage filter specification for evaluating base scope, PMNT scope, and status presentation.
/// </summary>
public sealed record UnitFilterSpec
{
    public string SourceName { get; init; } = string.Empty;
    public string PmntPattern { get; init; } = string.Empty;
    public string PanelPattern { get; init; } = string.Empty;
    public DateTime? StartTime { get; init; }
    public DateTime? EndTime { get; init; }
    public UnitStatusFilter Status { get; init; } = UnitStatusFilter.All;

    public string BaseScopeSignature => $"{SourceName}|{PanelPattern}|{StartTime:O}|{EndTime:O}";
    public string ScopeSignature => $"{BaseScopeSignature}|{PmntPattern}";
    public string Signature => $"{ScopeSignature}|{(int)Status}";
}

/// <summary>
/// Stateful tracker for non-locking SMB tail reading of an active OTP_1.log file.
/// </summary>
public sealed class LogTailState
{
    private TailIoState _io = new() { Path = string.Empty };
    private TailFingerprintState _fingerprints = new() { FirstLineFingerprint = string.Empty };
    private TailMetricsState _metrics;

    private struct TailIoState
    {
        public string Path;
        public long Offset;
        public long FileLength;
        public DateTime? CreationUtc;
        public bool Initialized;
        public bool SkipFirst;
        public bool InBlock;
        public bool Rotated;
        public string? ErrorText;
    }

    private struct TailFingerprintState
    {
        public int HeadSampleLength;
        public ulong HeadFingerprint;
        public string FirstLineFingerprint;
        public int TailSampleLength;
        public ulong TailFingerprint;
    }

    private struct TailMetricsState
    {
        public int DroppedBlocks;
        public int NextIndex;
        public int LoadedFilesCount;
        public long LoadedTotalBytes;
        public int DrainedFilesCount;
    }

    public required string Path { get => _io.Path; set => _io.Path = value; }
    public string Source { get; init; } = string.Empty;
    public long Offset { get => _io.Offset; set => _io.Offset = value; }
    public long FileLength { get => _io.FileLength; set => _io.FileLength = value; }
    public DateTime? CreationUtc { get => _io.CreationUtc; set => _io.CreationUtc = value; }
    public bool Initialized { get => _io.Initialized; set => _io.Initialized = value; }
    public bool SkipFirst { get => _io.SkipFirst; set => _io.SkipFirst = value; }
    public bool InBlock { get => _io.InBlock; set => _io.InBlock = value; }
    public List<string> BlockLines { get; } = [];
    public bool Rotated { get => _io.Rotated; set => _io.Rotated = value; }
    public string? ErrorText { get => _io.ErrorText; set => _io.ErrorText = value; }
    public int DroppedBlocks { get => _metrics.DroppedBlocks; set => _metrics.DroppedBlocks = value; }
    public int NextIndex { get => _metrics.NextIndex; set => _metrics.NextIndex = value; }
    public int HeadSampleLength { get => _fingerprints.HeadSampleLength; set => _fingerprints.HeadSampleLength = value; }
    public ulong HeadFingerprint { get => _fingerprints.HeadFingerprint; set => _fingerprints.HeadFingerprint = value; }
    public string FirstLineFingerprint { get => _fingerprints.FirstLineFingerprint; set => _fingerprints.FirstLineFingerprint = value; }
    public int TailSampleLength { get => _fingerprints.TailSampleLength; set => _fingerprints.TailSampleLength = value; }
    public ulong TailFingerprint { get => _fingerprints.TailFingerprint; set => _fingerprints.TailFingerprint = value; }
    public int LoadedFilesCount { get => _metrics.LoadedFilesCount; set => _metrics.LoadedFilesCount = value; }
    public long LoadedTotalBytes { get => _metrics.LoadedTotalBytes; set => _metrics.LoadedTotalBytes = value; }
    public int DrainedFilesCount { get => _metrics.DrainedFilesCount; set => _metrics.DrainedFilesCount = value; }
    public StringBuilder PendingLineBuffer { get; } = new();

    /// <summary>Atomically copies all I/O cursor, fingerprint, metric, and partial-line state from <paramref name="source"/>.</summary>
    public void CopyFrom(LogTailState source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _io = source._io;
        _fingerprints = source._fingerprints;
        _metrics = source._metrics;

        BlockLines.Clear();
        if (source.BlockLines.Count > 0)
        {
            BlockLines.AddRange(source.BlockLines);
        }

        PendingLineBuffer.Clear();
        if (source.PendingLineBuffer.Length > 0)
        {
            PendingLineBuffer.Append(source.PendingLineBuffer);
        }
    }
}


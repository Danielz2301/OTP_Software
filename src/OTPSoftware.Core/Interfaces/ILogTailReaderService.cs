using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Provides non-locking SMB tail reading (FileShare.ReadWrite | FileShare.Delete) with line-boundary preservation,
/// rotated file family resolution, and rollover draining.
/// </summary>
public interface ILogTailReaderService
{
    LogTailState CreateTailState(string path, string source = "");
    void ReadLogTailLines(LogTailState state, List<string> lines, bool skipExisting = false, long maxBytes = 0, bool enableRolloverDrain = true);
    void ConvertLogLinesToUnits(LogTailState state, IReadOnlyList<string> lines, List<UnitRecord> units);
    int ReadLogTailUnits(
        LogTailState state,
        List<UnitRecord> units,
        bool skipExisting = false,
        long maxBytes = 0,
        CancellationToken cancellationToken = default);
    void ReadLogUnitsFromFile(
        LogTailState state,
        List<UnitRecord> units,
        long chunkBytes = 16 * 1024 * 1024,
        DateTime? startTime = null,
        DateTime? endTime = null,
        Action? onProgress = null,
        CancellationToken cancellationToken = default);
}


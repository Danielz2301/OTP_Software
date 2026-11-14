using System.Globalization;
using System.Text;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class ReadLogTailTests : IDisposable
{
    private readonly string _testDir;
    private readonly LogTailReaderService _reader = new();

    public ReadLogTailTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "OTPTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    private static string NewTestLogText(int blocks)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= blocks; i++)
        {
            const string p = "[2026-11-06 10:00:00.000] [info] [GovionLogger.cpp:110]";
            sb.Append($"{p} *****************START*****************\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{p} Register address:2010.0, read value:PMNT10{(i % 90):D2}A\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"{p} PANELNAME:TL69900000{i}\r\n");
            sb.Append($"{p} *****************END*****************\r\n");
        }
        return sb.ToString();
    }

    private List<string> ReadTestLines(LogTailState state, bool skipExisting = false, long maxBytes = 0)
    {
        var lines = new List<string>();
        _reader.ReadLogTailLines(state, lines, skipExisting, maxBytes);
        return lines;
    }

    [Fact]
    public void AcceptsOnlyCompleteLines_LeavingPartialLineForNextRead()
    {
        string path = Path.Combine(_testDir, "partial.log");
        File.WriteAllText(path, "abc\nxyz", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);

        var lines = ReadTestLines(st);
        Assert.Single(lines);
        Assert.Equal("abc", lines[0]);
        Assert.Equal(4, st.Offset);

        File.AppendAllText(path, "123\n", new UTF8Encoding(false));
        lines = ReadTestLines(st);
        Assert.Single(lines);
        Assert.Equal("xyz123", lines[0]);
    }

    [Fact]
    public void TrimsCrAtLineEndAndParsesUtf8Correctly()
    {
        string path = Path.Combine(_testDir, "utf8.log");
        File.WriteAllText(path, "line one\r\nTest Text\r\n", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);

        var lines = ReadTestLines(st);
        Assert.Equal(2, lines.Count);
        Assert.Equal("line one", lines[0]);
        Assert.Equal("Test Text", lines[1]);
    }

    [Fact]
    public void NoDuplicateReads_SecondReadReturnsZeroLinesWhenFileUnchanged()
    {
        string path = Path.Combine(_testDir, "norepeat.log");
        File.WriteAllText(path, "a\nb\n", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);

        Assert.Equal(2, ReadTestLines(st).Count);
        Assert.Empty(ReadTestLines(st));
    }

    [Fact]
    public void DetectsLogRotation_FileTruncatedAndRewindsToStart()
    {
        string path = Path.Combine(_testDir, "rotate.log");
        File.WriteAllText(path, new string('x', 50) + "\n" + new string('y', 50) + "\n", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);
        _ = ReadTestLines(st);

        File.WriteAllText(path, "new1\nnew2\n", new UTF8Encoding(false));
        var lines = ReadTestLines(st);
        Assert.True(st.Rotated);
        Assert.Equal(2, lines.Count);
        Assert.Equal("new1", lines[0]);
    }

    [Fact]
    public void SkipExisting_IgnoresExistingDataWithoutSplittingLines()
    {
        string path = Path.Combine(_testDir, "skip.log");
        File.WriteAllText(path, "old1\nold2\npart", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);

        var lines = new List<string>();
        _reader.ReadLogTailLines(st, lines, skipExisting: true);
        Assert.Empty(lines);
        Assert.Equal(10, st.Offset);

        File.AppendAllText(path, "ial\nnew\n", new UTF8Encoding(false));
        lines = ReadTestLines(st);
        Assert.Equal(2, lines.Count);
        Assert.Equal("partial", lines[0]);
        Assert.Equal("new", lines[1]);
    }

    [Fact]
    public void NonExistentFile_ReportsErrorPreservesOffsetMarksUninitialized()
    {
        string path = Path.Combine(_testDir, "missing.log");
        var st = _reader.CreateTailState(path);
        var lines = ReadTestLines(st);

        Assert.Empty(lines);
        Assert.False(string.IsNullOrEmpty(st.ErrorText));
        Assert.False(st.Initialized);
        Assert.Equal(0, st.Offset);
    }

    [Fact]
    public void ConvertLogLinesToUnits_StitchesSplitBlockAcrossConsecutiveReads()
    {
        string path = Path.Combine(_testDir, "block.log");
        const string prefix = "[2026-11-06 10:00:00.000] [info] [GovionLogger.cpp:110]";
        File.WriteAllText(path, $"{prefix} *****************START*****************\n{prefix} Register address:2010.0, read value:PMNT1001A\n", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();

        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), units);
        Assert.Empty(units);

        File.AppendAllText(path, $"{prefix} PANELNAME:TL699041U0BF\n{prefix} *****************END*****************\n", new UTF8Encoding(false));
        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), units);
        Assert.Single(units);
        Assert.Equal("PMNT1001A", units[0].Pmnt);
        Assert.Equal("TL699041U0BF", units[0].PanelName);
    }

    [Fact]
    public void ConvertLogLinesToUnits_BlockMissingEndEncounteringNewStartIsTrackedAsDropped()
    {
        string path = Path.Combine(_testDir, "dropped.log");
        const string prefix = "[2026-11-06 10:00:00.000] [info] [GovionLogger.cpp:110]";
        string text = $"{prefix} *****************START*****************\n" +
                      $"{prefix} Register address:2010.0, read value:PMNT1001A\n" +
                      $"{prefix} *****************START*****************\n" +
                      $"{prefix} Register address:2010.0, read value:PMNT1002B\n" +
                      $"{prefix} *****************END*****************\n";
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();

        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), units);
        Assert.Single(units);
        Assert.Equal("PMNT1002B", units[0].Pmnt);
        Assert.Equal(1, st.DroppedBlocks);
    }

    [Fact]
    public void ConvertLogLinesToUnits_AttachesMachineSourceLabelToUnit()
    {
        string path = Path.Combine(_testDir, "source.log");
        File.WriteAllText(path, NewTestLogText(1), new UTF8Encoding(false));
        var st = _reader.CreateTailState(path, source: "MMAPI110");
        var units = new List<UnitRecord>();

        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), units);
        Assert.Single(units);
        Assert.Equal("MMAPI110", units[0].Source);
    }

    [Fact]
    public void ReadLogUnitsFromFile_ReadsEntireLargeFileAcrossMultipleChunks()
    {
        string path = Path.Combine(_testDir, "big.log");
        File.WriteAllText(path, NewTestLogText(1500), new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > 200000);

        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(st, units, chunkBytes: 65536);
        Assert.Equal(1500, units.Count);
    }

    [Fact]
    public void ReadLogUnitsFromFile_TerminatesWithoutInfiniteLoopWhenFileEndsWithIncompleteLine()
    {
        string path = Path.Combine(_testDir, "trailing.log");
        File.WriteAllText(path, NewTestLogText(3) + "incomplete line without newline", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();

        _reader.ReadLogUnitsFromFile(st, units);
        Assert.Equal(3, units.Count);
    }

    [Fact]
    public void ReadLogUnitsFromFile_NonExistentFileReturnsErrorInState()
    {
        string path = Path.Combine(_testDir, "nope.log");
        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();

        _reader.ReadLogUnitsFromFile(st, units);
        Assert.False(string.IsNullOrEmpty(st.ErrorText));
        Assert.Empty(units);
    }

    [Fact]
    public void ReadLogUnitsFromFile_PreservesMultiByteUtf8CharactersSplitAcrossSmallChunkBoundaries()
    {
        string path = Path.Combine(_testDir, "utf8_chunk.log");
        const string prefix = "[2026-11-06 14:30:00.000] [info] [GovionLogger.cpp:110]";
        string text = string.Join("\r\n", [
            $"{prefix} *****************START*****************",
            $"{prefix} Register address:2010.0, read value:PMNT1001A",
            $"{prefix} PANELNAME:TL699_VIỆT_NAM_한글_テスト",
            $"{prefix} BLUNAME:BLU_ĐẶC_BIỆT_01",
            $"{prefix} *****************END*****************",
            ""
        ]);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);
        var units = new List<UnitRecord>();

        _reader.ReadLogUnitsFromFile(st, units, chunkBytes: 64);
        Assert.Single(units);
        Assert.Equal("TL699_VIỆT_NAM_한글_テスト", units[0].PanelName);
        Assert.Equal("BLU_ĐẶC_BIỆT_01", units[0].BluName);
    }

    [Fact]
    public void ArrayPool_HandlesMultipleContinuousReads_WithoutStateCorruption()
    {
        string path = Path.Combine(_testDir, "arraypool_multi.log");
        File.WriteAllText(path, "line1\r\nline2\r\n", new UTF8Encoding(false));
        var st = _reader.CreateTailState(path);

        var lines1 = ReadTestLines(st);
        Assert.Equal(2, lines1.Count);
        Assert.Equal("line1", lines1[0]);
        Assert.Equal("line2", lines1[1]);

        // Append more lines across multiple poll cycles
        for (int i = 3; i <= 20; i++)
        {
            File.AppendAllText(path, $"line{i}\r\n", new UTF8Encoding(false));
            var read = ReadTestLines(st);
            Assert.Single(read);
            Assert.Equal($"line{i}", read[0]);
        }
    }

    [Fact]
    public void Rotation_WhenFileTruncatedSmallerThanOffset_ResetsAndReadsNewLines()
    {
        string path = Path.Combine(_testDir, "shift_rotation.log");
        // Start of shift 1: big file (2000 lines)
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++) sb.AppendLine(CultureInfo.InvariantCulture, $"Shift1_Line_{i}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var st = _reader.CreateTailState(path);
        var lines1 = ReadTestLines(st);
        Assert.Equal(2000, lines1.Count);
        long oldOffset = st.Offset;
        Assert.True(oldOffset > 20000);

        // Production machine starts Shift 2: truncates/clears log file to only 2 lines
        File.WriteAllText(path, "Shift2_Start\r\nShift2_Unit1\r\n", new UTF8Encoding(false));
        long newLength = new FileInfo(path).Length;
        Assert.True(newLength < oldOffset);

        // Reader should detect length < offset, mark Rotated = true, reset offset, and read the 2 lines
        var lines2 = ReadTestLines(st);
        Assert.True(st.Rotated);
        Assert.Equal(2, lines2.Count);
        Assert.Equal("Shift2_Start", lines2[0]);
        Assert.Equal("Shift2_Unit1", lines2[1]);
        Assert.Equal(newLength, st.Offset);
    }

    private static string BuildUnitBlock(string timestamp, string pmnt, string panelName)
    {
        string p = $"[{timestamp}] [info] [GovionLogger.cpp:110]";
        return $"{p} *****************START*****************\r\n" +
               $"{p} Register address:2010.0, read value:{pmnt}\r\n" +
               $"{p} PANELNAME:{panelName}\r\n" +
               $"{p} *****************END*****************\r\n";
    }

    [Fact]
    public void ReadLogUnitsFromFile_ReadsRotatedChainChronologically_OldestToActive()
    {
        string rot2 = Path.Combine(_testDir, "OTP_1.2.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");

        File.WriteAllText(
            rot2,
            BuildUnitBlock("2026-11-13 08:00:00.000", "PMNT1001A", "TL699_ROT2_A") +
            BuildUnitBlock("2026-11-13 08:05:00.000", "PMNT1002A", "TL699_ROT2_B"),
            new UTF8Encoding(false));

        File.WriteAllText(
            rot1,
            BuildUnitBlock("2026-11-13 09:00:00.000", "PMNT1003B", "TL699_ROT1_A"),
            new UTF8Encoding(false));

        File.WriteAllText(
            active,
            BuildUnitBlock("2026-11-13 10:00:00.000", "PMNT1004A", "TL699_ACTIVE"),
            new UTF8Encoding(false));

        // Even when user accidentally initializes pointing to OTP_1.1.log, reader normalizes and anchors to OTP_1.log
        var st = _reader.CreateTailState(rot1, "MMAPI120");
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(st, units);

        Assert.True(st.Initialized);
        Assert.Null(st.ErrorText);
        Assert.Equal(Path.GetFullPath(active), Path.GetFullPath(st.Path));
        Assert.Equal(new FileInfo(active).Length, st.Offset);
        Assert.Equal(3, st.LoadedFilesCount);
        Assert.Equal(4, units.Count);
        Assert.Equal(["TL699_ROT2_A", "TL699_ROT2_B", "TL699_ROT1_A", "TL699_ACTIVE"], units.Select(u => u.PanelName));
    }

    [Fact]
    public void ReadLogUnitsFromFile_RespectsFirstTimestampOverOutOfOrderLastWriteTime()
    {
        string rot2 = Path.Combine(_testDir, "OTP_1.2.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");

        File.WriteAllText(rot2, BuildUnitBlock("2026-11-13 07:30:00.000", "PMNT1001A", "TL699_OLDEST"), new UTF8Encoding(false));
        File.WriteAllText(rot1, BuildUnitBlock("2026-11-13 08:45:00.000", "PMNT1002B", "TL699_MIDDLE"), new UTF8Encoding(false));
        File.WriteAllText(active, BuildUnitBlock("2026-11-13 11:15:00.000", "PMNT1003A", "TL699_NEWEST"), new UTF8Encoding(false));

        // Simulate file copy bumping LastWriteTimeUtc of older OTP_1.2.log ahead of OTP_1.1.log
        DateTime baseUtc = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(rot1, baseUtc.AddMinutes(-30));
        File.SetLastWriteTimeUtc(rot2, baseUtc.AddMinutes(-5));

        var st = _reader.CreateTailState(active, "MMAPI110");
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(st, units);

        Assert.Equal(["TL699_OLDEST", "TL699_MIDDLE", "TL699_NEWEST"], units.Select(u => u.PanelName));
    }

    [Fact]
    public void ReadLogTail_RealtimeRollover_DrainsRemainingUnitsFromRotated1BeforeNewActive()
    {
        string active = Path.Combine(_testDir, "OTP_1.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        var utf8NoBom = new UTF8Encoding(false);

        File.WriteAllText(active, BuildUnitBlock("2026-11-13 12:00:00.000", "PMNT1001A", "TL699_BASE"), utf8NoBom);
        DateTime originalCreationUtc = File.GetCreationTimeUtc(active);

        var st = _reader.CreateTailState(active, "MMAPI120");
        var initialUnits = new List<UnitRecord>();
        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), initialUnits);
        Assert.Single(initialUnits);
        long oldOffset = st.Offset;

        // 2 more units written to active log right before rolling over to OTP_1.1.log
        File.AppendAllText(
            active,
            BuildUnitBlock("2026-11-13 12:01:00.000", "PMNT1002A", "TL699_DRAIN_1") +
            BuildUnitBlock("2026-11-13 12:02:00.000", "PMNT1003B", "TL699_DRAIN_2"),
            utf8NoBom);

        File.Move(active, rot1);

        // New OTP_1.log exceeds oldOffset and preserves CreationTimeUtc to simulate Windows NTFS Tunneling
        string fillerComment = "[2026-11-13 12:03:00.000] [info] " + new string('P', 300) + "\r\n";
        File.WriteAllText(
            active,
            BuildUnitBlock("2026-11-13 12:03:01.000", "PMNT1004A", "TL699_NEW_1") + fillerComment,
            utf8NoBom);
        File.SetCreationTimeUtc(active, originalCreationUtc);
        Assert.True(new FileInfo(active).Length > oldOffset);

        var nextLines = ReadTestLines(st);
        var nextUnits = new List<UnitRecord>();
        _reader.ConvertLogLinesToUnits(st, nextLines, nextUnits);

        Assert.True(st.Rotated);
        Assert.Equal(1, st.DrainedFilesCount);
        Assert.Equal(3, nextUnits.Count);
        Assert.Equal(["TL699_DRAIN_1", "TL699_DRAIN_2", "TL699_NEW_1"], nextUnits.Select(u => u.PanelName));

        // Subsequent poll yields zero duplicate lines/units
        var followLines = ReadTestLines(st);
        Assert.Empty(followLines);
    }

    [Fact]
    public void ReadLogTail_RealtimeRollover_DetectsTunnelingEvenWithIdenticalLeadingBanner()
    {
        string active = Path.Combine(_testDir, "OTP_1.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        var utf8NoBom = new UTF8Encoding(false);

        string sharedBanner = "=== GOVION OTP LOGGER HEADER V2 ===\r\nLINE_CONFIG=DEFAULT\r\n";
        File.WriteAllText(active, sharedBanner + BuildUnitBlock("2026-11-13 13:00:00.000", "PMNT1001A", "TL699_OLD_BODY"), utf8NoBom);
        DateTime originalCreationUtc = File.GetCreationTimeUtc(active);

        var st = _reader.CreateTailState(active, "MMAPI120");
        var initUnits = new List<UnitRecord>();
        _reader.ConvertLogLinesToUnits(st, ReadTestLines(st), initUnits);
        Assert.Single(initUnits);
        long oldOffset = st.Offset;

        File.AppendAllText(active, BuildUnitBlock("2026-11-13 13:01:00.000", "PMNT1002A", "TL699_DRAINED_AFTER_BANNER"), utf8NoBom);
        File.Move(active, rot1);

        // New file has the exact same banner, exceeds oldOffset, and mimics tunneled CreationTimeUtc
        File.WriteAllText(
            active,
            sharedBanner +
            BuildUnitBlock("2026-11-13 13:02:00.000", "PMNT1005B", "TL699_FRESH_1") +
            BuildUnitBlock("2026-11-13 13:02:10.000", "PMNT1006B", "TL699_FRESH_2"),
            utf8NoBom);
        File.SetCreationTimeUtc(active, originalCreationUtc);
        Assert.True(new FileInfo(active).Length > oldOffset);

        var postUnits = new List<UnitRecord>();
        int added = _reader.ReadLogTailUnits(st, postUnits);

        Assert.True(st.Rotated);
        Assert.Equal(3, added);
        Assert.Equal(["TL699_DRAINED_AFTER_BANNER", "TL699_FRESH_1", "TL699_FRESH_2"], postUnits.Select(u => u.PanelName));
    }

    [Fact]
    public void ReadLogUnitsFromFile_WithDateRange_FiltersUnitsAndPrunesOutOfRangeRotatedFiles()
    {
        string rot3 = Path.Combine(_testDir, "OTP_1.3.log");
        string rot2 = Path.Combine(_testDir, "OTP_1.2.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");

        File.WriteAllText(rot3, BuildUnitBlock("2026-08-01 10:00:00.000", "PMNT1001A", "TL_AUG_01"));
        File.WriteAllText(
            rot2,
            BuildUnitBlock("2026-09-01 08:00:00.000", "PMNT1001A", "TL_SEP_01") +
            BuildUnitBlock("2026-09-05 14:30:00.000", "PMNT1002A", "TL_SEP_05") +
            BuildUnitBlock("2026-09-20 09:00:00.000", "PMNT1003A", "TL_SEP_20"));
        File.WriteAllText(rot1, BuildUnitBlock("2026-10-10 11:00:00.000", "PMNT1004A", "TL_OCT_10"));
        File.WriteAllText(active, BuildUnitBlock("2026-11-10 12:00:00.000", "PMNT1005A", "TL_NOV_10"));

        var st = _reader.CreateTailState(active, "MMAPI110");
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(
            st,
            units,
            startTime: new DateTime(2026, 9, 2, 0, 0, 0),
            endTime: new DateTime(2026, 9, 15, 23, 59, 59, 999));

        Assert.Null(st.ErrorText);
        Assert.True(st.Initialized);
        Assert.Equal(1, st.LoadedFilesCount);
        Assert.Equal(["TL_SEP_05"], units.Select(u => u.PanelName));
        Assert.Equal(new FileInfo(active).Length, st.Offset);

        // Tail anchor on active file remains functional for subsequent live appends
        File.AppendAllText(active, BuildUnitBlock("2026-11-13 15:00:00.000", "PMNT1006A", "TL_LIVE_NEXT"));
        var liveUnits = new List<UnitRecord>();
        int added = _reader.ReadLogTailUnits(st, liveUnits);
        Assert.Equal(1, added);
        Assert.False(st.Rotated);
        Assert.Equal(["TL_LIVE_NEXT"], liveUnits.Select(u => u.PanelName));
    }

    [Fact]
    public void ReadLogUnitsFromFile_WithDateRange_FallsBackSafelyWhenArchiveTimestampsOutOfOrder()
    {
        string rot2 = Path.Combine(_testDir, "OTP_1.2.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");

        // Clock reset / manual active replacement where active starts earlier than archives -> non-monotonic timestamps
        File.WriteAllText(rot2, BuildUnitBlock("2026-10-01 10:00:00.000", "PMNT1001A", "TL_OUT_OF_ORDER_OCT1"));
        File.WriteAllText(rot1, BuildUnitBlock("2026-11-01 10:00:00.000", "PMNT1002A", "TL_OUT_OF_ORDER_NOV1"));
        File.WriteAllText(
            active,
            BuildUnitBlock("2026-09-01 10:00:00.000", "PMNT1003A", "TL_ACTIVE_SEP1") +
            BuildUnitBlock("2026-10-02 10:00:00.000", "PMNT1004A", "TL_ACTIVE_OCT2"));

        var st = _reader.CreateTailState(active, "MMAPI110");
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(
            st,
            units,
            startTime: new DateTime(2026, 9, 28, 0, 0, 0),
            endTime: new DateTime(2026, 10, 5, 23, 59, 59));

        Assert.Equal(3, st.LoadedFilesCount);
        Assert.Equal(2, units.Count);
        Assert.Contains(units, u => u.PanelName == "TL_OUT_OF_ORDER_OCT1");
        Assert.Contains(units, u => u.PanelName == "TL_ACTIVE_OCT2");
        Assert.DoesNotContain(units, u => u.PanelName == "TL_ACTIVE_SEP1");
        Assert.DoesNotContain(units, u => u.PanelName == "TL_OUT_OF_ORDER_NOV1");
    }

    [Fact]
    public void ReadLogUnitsFromFile_WithDateRangeBeforeAllFiles_AnchorsActiveStateWithoutError()
    {
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");

        File.WriteAllText(rot1, BuildUnitBlock("2026-09-01 10:00:00.000", "PMNT1001A", "TL_SEP"));
        File.WriteAllText(active, BuildUnitBlock("2026-11-10 10:00:00.000", "PMNT1002A", "TL_NOV"));

        var st = _reader.CreateTailState(active, "MMAPI110");
        var units = new List<UnitRecord>();
        _reader.ReadLogUnitsFromFile(
            st,
            units,
            startTime: new DateTime(2026, 1, 1, 0, 0, 0),
            endTime: new DateTime(2026, 1, 31, 23, 59, 59));

        Assert.Empty(units);
        Assert.Equal(0, st.LoadedFilesCount);
        Assert.True(st.Initialized);
        Assert.Null(st.ErrorText);
        Assert.Equal(new FileInfo(active).Length, st.Offset);
    }

    [Fact]
    public void ReadLogUnitsFromFile_CanceledDuringRead_DoesNotMarkStateInitializedOrCorruptOffset()
    {
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string active = Path.Combine(_testDir, "OTP_1.log");
        File.WriteAllText(rot1, BuildUnitBlock("2026-11-13 08:00:00.000", "PMNT1001A", "TL_ARCHIVE"));
        File.WriteAllText(active, BuildUnitBlock("2026-11-13 09:00:00.000", "PMNT1002A", "TL_ACTIVE"));

        var st = _reader.CreateTailState(active, "MMAPI110");
        st.Offset = 42;
        st.Initialized = false;
        var units = new List<UnitRecord>();
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            _reader.ReadLogUnitsFromFile(
                st,
                units,
                chunkBytes: 64,
                onProgress: () => cts.Cancel(),
                cancellationToken: cts.Token));

        Assert.Empty(units);
        Assert.False(st.Initialized);
        Assert.Equal(42, st.Offset);
        Assert.Equal(0, st.LoadedFilesCount);
    }

    [Fact]
    public void ReadLogTailLines_DoubleRolloverAndMaxBytes_DrainsFromOpenedArchiveStreamRespectingLimit()
    {
        string active = Path.Combine(_testDir, "OTP_1.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        string rot2 = Path.Combine(_testDir, "OTP_1.2.log");
        var utf8NoBom = new UTF8Encoding(false);

        string line0 = "2026-11-13 08:00:00 line0_initial_header\r\n";
        File.WriteAllText(active, line0, utf8NoBom);

        var st = _reader.CreateTailState(active, "MMAPI110");
        var initialLines = ReadTestLines(st);
        Assert.Single(initialLines);
        Assert.True(st.Initialized);

        // Append extra lines to active, then perform DOUBLE rollover -> active moved to OTP_1.2.log,
        // another rotated generation exists at OTP_1.1.log, and fresh active OTP_1.log starts.
        string archivedTail1 = "2026-11-13 08:01:00 archived_tail_1_alpha\r\n";
        string archivedTail2 = "2026-11-13 08:02:00 archived_tail_2_beta\r\n";
        File.AppendAllText(active, archivedTail1 + archivedTail2, utf8NoBom);
        File.Move(active, rot2);

        File.WriteAllText(rot1, "2026-11-13 08:03:00 intermediate_archive_line\r\n", utf8NoBom);
        File.WriteAllText(active, "2026-11-13 08:04:00 fresh_active_line\r\n", utf8NoBom);

        // Read with a small maxBytes budget across multiple poll ticks so maxBytes is respected across drain + active.
        var collected = new List<string>();
        int budget = Encoding.UTF8.GetByteCount(archivedTail1);
        var chunk1 = ReadTestLines(st, maxBytes: budget);
        collected.AddRange(chunk1);
        Assert.True(st.Rotated);

        var chunk2 = ReadTestLines(st, maxBytes: 1024);
        collected.AddRange(chunk2);

        Assert.Contains("2026-11-13 08:01:00 archived_tail_1_alpha", collected);
        Assert.Contains("2026-11-13 08:02:00 archived_tail_2_beta", collected);
        Assert.Contains("2026-11-13 08:04:00 fresh_active_line", collected);
        Assert.DoesNotContain("2026-11-13 08:00:00 line0_initial_header", collected);
    }

    [Fact]
    public void ReadLogTailLines_ArchiveAccessErrorDuringDrain_SurfacesErrorInState()
    {
        string active = Path.Combine(_testDir, "OTP_1.log");
        string rot1 = Path.Combine(_testDir, "OTP_1.1.log");
        var utf8NoBom = new UTF8Encoding(false);

        File.WriteAllText(active, "initial_long_line_for_rollover_detection_001\r\n", utf8NoBom);
        var st = _reader.CreateTailState(active, "MMAPI110");
        ReadTestLines(st);

        // Rotate active -> rot1 and create shorter new active file
        File.AppendAllText(active, "appended_unseen_tail\r\n", utf8NoBom);
        File.Move(active, rot1);
        File.WriteAllText(active, "short_new\r\n", utf8NoBom);

        // Exclusively lock rot1 so DrainRotatedArchives hits IOException
        using var lockStream = new FileStream(rot1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var lines = ReadTestLines(st);

        Assert.False(string.IsNullOrEmpty(st.ErrorText));
        Assert.Equal(["short_new"], lines);
    }

    [Fact]
    public void EmptyFile_ZeroBytes_ReturnsEmptyAndRecoversWhenAppended()
    {
        // Arrange
        string path = Path.Combine(_testDir, "OTP_1.log");
        File.WriteAllText(path, string.Empty);

        var st = _reader.CreateTailState(path);
        var fileUnits = new List<UnitRecord>();

        // Act 1: Initial full load from 0-byte file
        _reader.ReadLogUnitsFromFile(st, fileUnits);

        // Assert 1: Zero records, no error, offset stays at 0
        Assert.Empty(fileUnits);
        Assert.Null(st.ErrorText);
        Assert.Equal(0, st.Offset);

        // Act 2: Poll tail on still-empty file
        var tailUnits = new List<UnitRecord>();
        int initialTailCount = _reader.ReadLogTailUnits(st, tailUnits);

        // Assert 2: Still zero records, no error, offset stays at 0
        Assert.Equal(0, initialTailCount);
        Assert.Empty(tailUnits);
        Assert.Null(st.ErrorText);
        Assert.Equal(0, st.Offset);

        // Act 3: Append 1 valid unit block
        File.AppendAllText(path, BuildUnitBlock("2026-11-13 14:00:00.000", "PMNT1001A", "TL699001A0AB"));
        int added = _reader.ReadLogTailUnits(st, tailUnits);

        // Assert 3: Recovers cleanly and reads exactly 1 unit
        Assert.Equal(1, added);
        Assert.Single(tailUnits);
        Assert.Equal("TL699001A0AB", tailUnits[0].PanelName);
        Assert.Null(st.ErrorText);
        Assert.True(st.Offset > 0);
    }

    [Fact]
    public void MidReadCancellation_RollsBackPendingLineBufferCleanly()
    {
        // Arrange
        string path = Path.Combine(_testDir, "OTP_1.log");
        File.WriteAllText(path, "2026-04-06 08:00:00 [INF] Initial complete line\r\n", new UTF8Encoding(false));

        var st = _reader.CreateTailState(path);
        var initialLines = ReadTestLines(st);
        Assert.Single(initialLines);
        Assert.True(st.Initialized);
        Assert.Equal(string.Empty, st.PendingLineBuffer.ToString());

        // Act 1: Append line and read with maxBytes capped to partial length so PendingLineBuffer holds it
        const string partial = "2026-04-06 09:00:00 [INF] Partial";
        File.AppendAllText(path, partial + " completed\r\n", new UTF8Encoding(false));
        var partialLines = ReadTestLines(st, maxBytes: partial.Length);
        Assert.Empty(partialLines);
        Assert.Equal(partial, st.PendingLineBuffer.ToString());

        // Act 2: Attempt read with pre-canceled CancellationToken
        var units = new List<UnitRecord>();
        Assert.Throws<OperationCanceledException>(() =>
            _reader.ReadLogTailUnits(st, units, cancellationToken: new CancellationToken(true)));

        // Assert 2: PendingLineBuffer remains cleanly intact without corruption or double-buffering
        Assert.Equal(partial, st.PendingLineBuffer.ToString());

        // Act 3: Poll remaining bytes with valid token
        var nextLines = ReadTestLines(st);

        // Assert 3: Stitched line is exact without duplicate partial prefix
        Assert.Single(nextLines);
        Assert.Equal("2026-04-06 09:00:00 [INF] Partial completed", nextLines[0]);
        Assert.Equal(string.Empty, st.PendingLineBuffer.ToString());
    }
}

using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class ExportUnitDataTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExportService _service;

    public ExportUnitDataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExportUnitDataTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new ExportService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    private static List<string[]> ParseCsv(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var result = new List<string[]>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        cur.Append('\"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            parts.Add(cur.ToString());
            result.Add(parts.ToArray());
        }
        return result;
    }

    [Fact]
    public void ConvertToSafeCsvText_NeutralizesFormulas()
    {
        Assert.Equal("'=1+1", _service.ConvertToSafeCsvText("=1+1"));
        Assert.Equal("'+cmd", _service.ConvertToSafeCsvText("+cmd"));
        Assert.Equal("'-5", _service.ConvertToSafeCsvText("-5"));
        Assert.Equal("'@SUM(A1:A9)", _service.ConvertToSafeCsvText("@SUM(A1:A9)"));
        Assert.Equal("TL699041U0BF", _service.ConvertToSafeCsvText("TL699041U0BF"));
        Assert.Equal("a=b", _service.ConvertToSafeCsvText("a=b"));
        Assert.True(string.IsNullOrEmpty(_service.ConvertToSafeCsvText("")));
        Assert.True(string.IsNullOrEmpty(_service.ConvertToSafeCsvText(null)));
    }

    [Fact]
    public async Task ExportUnitDataAsync_ExportsExpectedContentAndNeutralizesFormulas()
    {
        var unit = new UnitRecord
        {
            Timestamp = new DateTime(2026, 11, 6, 14, 30, 0),
            Pmnt = "PMNT1001A",
            PassCount = 3,
            PanelName = "=HYPERLINK(\"http://x\")",
            BluName = "BLU001",
            Errors = []
        };

        string path = Path.Combine(_tempDir, "out.csv");
        await _service.ExportUnitDataAsync([unit], path);

        Assert.True(File.Exists(path));
        var rows = ParseCsv(path);

        Assert.Equal(2, rows.Count); // Header + 1 row
        var header = rows[0];
        var row = rows[1];

        int pmntIdx = Array.IndexOf(header, "PMNT");
        int panelIdx = Array.IndexOf(header, "PanelName");
        int statusIdx = Array.IndexOf(header, "Status");
        int tsIdx = Array.IndexOf(header, "Timestamp");

        Assert.Equal("PMNT1001A", row[pmntIdx]);
        Assert.Equal("'=HYPERLINK(\"http://x\")", row[panelIdx]);
        Assert.Equal("OK", row[statusIdx]);
        Assert.Equal("2026-11-06 14:30:00", row[tsIdx]);
    }

    [Fact]
    public async Task ExportUnitDataAsync_ExportsMachineColumnWhenIncludeMachine()
    {
        var unit = new UnitRecord
        {
            Source = "MMAPI110",
            Timestamp = new DateTime(2026, 11, 6, 15, 0, 0),
            Pmnt = "PMNT1002B",
            PassCount = 1,
            TotalRuns = 4,
            PanelName = "TL699",
            BluName = "BLU002",
            Errors = ["XYLV"]
        };

        string path = Path.Combine(_tempDir, "dual.csv");
        await _service.ExportUnitDataAsync([unit], path, includeMachine: true);

        Assert.True(File.Exists(path));
        var rows = ParseCsv(path);

        Assert.Equal(2, rows.Count);
        var header = rows[0];
        var row = rows[1];

        int machineIdx = Array.IndexOf(header, "Machine");
        int runIdx = Array.IndexOf(header, "RunIndex");
        int totalIdx = Array.IndexOf(header, "TotalRuns");
        int statusIdx = Array.IndexOf(header, "Status");

        Assert.Equal("MMAPI110", row[machineIdx]);
        Assert.Equal("1", row[runIdx]);
        Assert.Equal("4", row[totalIdx]);
        Assert.Equal("NG: XYLV", row[statusIdx]);
    }

    [Fact]
    public void GetShiftWindow_ComputesDayAndNightWindowsAndCompletedShifts()
    {
        var dayWin = _service.GetShiftWindow(new DateTime(2026, 11, 10, 14, 30, 0));
        Assert.Equal("DAY", dayWin.ShiftName);
        Assert.Equal("20261110_DAY", dayWin.ShiftKey);
        Assert.Equal(8, dayWin.StartTime.Hour);
        Assert.Equal(19, dayWin.EndTime.Hour);

        // Early morning (02:15 on Nov 10) belongs to Nov 09 NIGHT shift (20:00 Nov 09 -> 08:00 Nov 10)
        var nightWin = _service.GetShiftWindow(new DateTime(2026, 11, 10, 2, 15, 0));
        Assert.Equal("NIGHT", nightWin.ShiftName);
        Assert.Equal("20261109_NIGHT", nightWin.ShiftKey);
        Assert.Equal(new DateTime(2026, 11, 9, 20, 0, 0), nightWin.StartTime);

        // CompletedShift at 20:05 on Nov 10 returns 20261110_DAY
        var completed = _service.GetShiftWindow(new DateTime(2026, 11, 10, 20, 5, 0), completedShift: true);
        Assert.Equal("20261110_DAY", completed.ShiftKey);
    }

    [Fact]
    public async Task ExportShiftReportAsync_ExportsSummaryAndDetailFiles()
    {
        var units = new List<UnitRecord>
        {
            new() { Index = 0, Source = "MMAPI120", Timestamp = new DateTime(2026, 11, 10, 7, 50, 0), Pmnt = "PMNT1001A", PassCount = 1, TotalRuns = 1, PanelName = "P0", BluName = "B0", Errors = ["XYLV"] },
            new() { Index = 1, Source = "MMAPI120", Timestamp = new DateTime(2026, 11, 10, 9, 0, 0),  Pmnt = "PMNT1001A", PassCount = 1, TotalRuns = 4, PanelName = "P1", BluName = "B1", Errors = [] },
            new() { Index = 2, Source = "MMAPI120", Timestamp = new DateTime(2026, 11, 10, 10, 0, 0), Pmnt = "PMNT1001A", PassCount = 2, TotalRuns = 4, PanelName = "P2", BluName = "B2", Errors = ["XYLV"] },
            new() { Index = 3, Source = "MMAPI120", Timestamp = new DateTime(2026, 11, 10, 11, 0, 0), Pmnt = "PMNT1001A", PassCount = 3, TotalRuns = 4, PanelName = "P3", BluName = "B3", Errors = ["XYLV"] },
            new() { Index = 4, Source = "MMAPI120", Timestamp = new DateTime(2026, 11, 10, 12, 0, 0), Pmnt = "PMNT1001A", PassCount = 4, TotalRuns = 4, PanelName = "P4", BluName = "B4", Errors = ["XYLV"] }
        };

        var shift = _service.GetShiftWindow(new DateTime(2026, 11, 10, 15, 0, 0));
        string outDir = Path.Combine(_tempDir, "shift_exports");
        var rep = await _service.ExportShiftReportAsync(units, outDir, shift.StartTime, shift.EndTime, shift.ShiftKey, consecutiveNgThreshold: 3);

        Assert.Equal(4, rep.UnitCount);
        Assert.True(File.Exists(rep.SummaryPath));
        Assert.NotNull(rep.DetailPath);
        Assert.True(File.Exists(rep.DetailPath));

        var summaryRows = ParseCsv(rep.SummaryPath);
        Assert.Equal(3, summaryRows.Count); // Header + SHIFT_OVERALL + PMNT_A

        var header = summaryRows[0];
        var overallRow = summaryRows[1];
        var pmntRow = summaryRows[2];

        int catIdx = Array.IndexOf(header, "Category");
        int nameIdx = Array.IndexOf(header, "Name");
        int totUnitsIdx = Array.IndexOf(header, "TotalUnits");
        int passIdx = Array.IndexOf(header, "PassCount");
        int failIdx = Array.IndexOf(header, "FailCount");
        int alarmIdx = Array.IndexOf(header, "AlarmStatus");
        int curStreakIdx = Array.IndexOf(header, "CurrentStreak");
        int maxStreakIdx = Array.IndexOf(header, "MaxStreak");

        Assert.Equal("SHIFT_OVERALL", overallRow[catIdx]);
        Assert.Equal("4", overallRow[totUnitsIdx]);
        Assert.Equal("1", overallRow[passIdx]);
        Assert.Equal("3", overallRow[failIdx]);
        Assert.Equal("ALARM (1 PMNTs)", overallRow[alarmIdx]);

        Assert.Equal("PMNT_A", pmntRow[catIdx]);
        Assert.Equal("PMNT1001A", pmntRow[nameIdx]);
        Assert.Equal("3", pmntRow[curStreakIdx]);
        Assert.Equal("3", pmntRow[maxStreakIdx]);
        Assert.Equal("CONSECUTIVE_NG_ALARM", pmntRow[alarmIdx]);
    }

    [Fact]
    public void ConvertToSafeCsvText_PrefixesLeadingWhitespaceFormulaInjection()
    {
        Assert.Equal("'  =1+1", _service.ConvertToSafeCsvText("  =1+1"));
        Assert.Equal("'\t+cmd", _service.ConvertToSafeCsvText("\t+cmd"));
        Assert.Equal("'-100", _service.ConvertToSafeCsvText("-100"));
        Assert.Equal("normal", _service.ConvertToSafeCsvText("normal"));
    }

    [Fact]
    public void GetShiftWindow_InvertedOrOutOfRangeBoundaries_FallsBackToDefaultHours()
    {
        var refTime = new DateTime(2026, 11, 10, 14, 0, 0);

        // Inverted dayStart >= nightStart (e.g. 20:30 / 08:30) falls back to 08:00 - 20:00
        var inverted = _service.GetShiftWindow(refTime, 20, 30, 8, 30);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), inverted.StartTime);
        Assert.Equal(new DateTime(2026, 11, 10, 20, 0, 0).AddTicks(-1), inverted.EndTime);
        Assert.Equal("20261110_DAY", inverted.ShiftKey);

        // Equal start == night falls back to 08:00 - 20:00
        var equal = _service.GetShiftWindow(refTime, 12, 0, 12, 0);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), equal.StartTime);
        Assert.Equal(new DateTime(2026, 11, 10, 20, 0, 0).AddTicks(-1), equal.EndTime);

        // Out-of-range hour/minute falls back to 08:00 - 20:00
        var outOfRange = _service.GetShiftWindow(refTime, -1, 0, 25, 99);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0), outOfRange.StartTime);
        Assert.Equal(new DateTime(2026, 11, 10, 20, 0, 0).AddTicks(-1), outOfRange.EndTime);
    }

    [Fact]
    public async Task ExportUnitDataAsync_ComputesRunCountsWhenZeroAndWritesAtomically()
    {
        var u1 = new UnitRecord
        {
            Index = 1,
            Source = "MMAPI120",
            Timestamp = new DateTime(2026, 11, 10, 9, 0, 0),
            Pmnt = "PMNT1001A",
            PanelName = "TL699_SAME_PANEL",
            BluName = "BLU_1",
            PassCount = 0,
            TotalRuns = 0,
            Errors = []
        };
        var u2 = new UnitRecord
        {
            Index = 2,
            Source = "MMAPI120",
            Timestamp = new DateTime(2026, 11, 10, 9, 5, 0),
            Pmnt = "PMNT1001A",
            PanelName = "TL699_SAME_PANEL",
            BluName = "BLU_1",
            PassCount = 0,
            TotalRuns = 0,
            Errors = ["XYLV"]
        };

        string path = Path.Combine(_tempDir, "atomic_units.csv");
        await _service.ExportUnitDataAsync([u1, u2], path, includeMachine: true);

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

        var rows = ParseCsv(path);
        Assert.Equal(3, rows.Count);
        int runIdx = Array.IndexOf(rows[0], "RunIndex");
        int totalIdx = Array.IndexOf(rows[0], "TotalRuns");
        Assert.Equal("1", rows[1][runIdx]);
        Assert.Equal("2", rows[1][totalIdx]);
        Assert.Equal("2", rows[2][runIdx]);
        Assert.Equal("2", rows[2][totalIdx]);
    }

    [Fact]
    public void ConvertToSafeCsvText_BlocksPipeAndLeadingWhitespaceFormulaInjection()
    {
        Assert.Equal("'|cmd|' /C calc'!A0", _service.ConvertToSafeCsvText("|cmd|' /C calc'!A0"));
        Assert.Equal("'  |1+1", _service.ConvertToSafeCsvText("  |1+1"));
        Assert.Equal("'  =1+1", _service.ConvertToSafeCsvText("  =1+1"));
        Assert.Equal("'\tABC", _service.ConvertToSafeCsvText("\tABC"));
        Assert.Equal("   ", _service.ConvertToSafeCsvText("   "));
        Assert.Equal(string.Empty, _service.ConvertToSafeCsvText(null));
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1:A9)", "'@SUM(A1:A9)")]
    [InlineData("|cmd", "'|cmd")]
    [InlineData("  =SUM(A1)", "'  =SUM(A1)")]
    [InlineData("\t=SUM(A1)", "'\t=SUM(A1)")]
    [InlineData("\r+1", "'\r+1")]
    [InlineData("\n-1", "'\n-1")]
    [InlineData("OK\n=cmd|'/C calc'!A0", "'OK\n=cmd|'/C calc'!A0")]
    [InlineData("Line1\r\n+1+1", "'Line1\r\n+1+1")]
    public void ConvertToSafeCsvText_EdgeCasesAndMultilineFormulas_PrefixedWithApostrophe(string input, string expected)
    {
        // Act
        string actual = _service.ConvertToSafeCsvText(input);

        // Assert
        Assert.Equal(expected, actual);
    }
}

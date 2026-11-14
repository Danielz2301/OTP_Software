using System.Globalization;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class ShiftHandoverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExportService _exportService;

    public ShiftHandoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShiftHandoverTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _exportService = new ExportService();
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

    [Fact]
    public async Task ExportShiftSummaryReportCsv_CreatesFileWithUnitsAndKpiSummary()
    {
        // Arrange
        var shiftStart = new DateTime(2026, 11, 12, 8, 0, 0);
        var shiftEnd = new DateTime(2026, 11, 12, 19, 59, 59);
        var shiftWindow = new ShiftWindow("DAY", "20261112", "20261112_DAY", shiftStart, shiftEnd);

        var units = new List<UnitRecord>
        {
            new()
            {
                Source = "MMAPI120",
                Timestamp = shiftStart.AddHours(1),
                Pmnt = "PMNT-01",
                PanelName = "PANEL_A",
                BluName = "BLU_A",
                Errors = []
            },
            new()
            {
                Source = "MMAPI120",
                Timestamp = shiftStart.AddHours(2),
                Pmnt = "PMNT-01",
                PanelName = "PANEL_B",
                BluName = "BLU_B",
                Errors = ["XYLV"]
            },
            new()
            {
                Source = "MMAPI110",
                Timestamp = shiftStart.AddHours(3),
                Pmnt = "PMNT-02",
                PanelName = "PANEL_C",
                BluName = "BLU_C",
                Errors = ["FLICKER"]
            },
            new()
            {
                Source = "MMAPI110",
                Timestamp = shiftStart.AddHours(4),
                Pmnt = "PMNT-01",
                PanelName = "PANEL_D",
                BluName = "BLU_D",
                Errors = ["POWERON"]
            }
        };

        string filePath = Path.Combine(_tempDir, "ShiftReport_20261112_DAY.csv");

        // Act
        string resultPath = await _exportService.ExportShiftSummaryReportCsvAsync(units, filePath, shiftWindow);

        // Assert
        Assert.Equal(filePath, resultPath);
        Assert.True(File.Exists(filePath));

        var lines = await File.ReadAllLinesAsync(filePath);
        Assert.True(lines.Length >= 6);

        // Header
        Assert.Contains("\"Machine\",\"Timestamp\",\"PMNT\"", lines[0]);

        // Unit data rows
        Assert.Contains("PANEL_A", lines[1]);
        Assert.Contains("OK", lines[1]);

        Assert.Contains("PANEL_B", lines[2]);
        Assert.Contains("NG: XYLV", lines[2]);

        // Summary section
        Assert.Contains("--- SHIFT SUMMARY ---", string.Join("\n", lines));

        string summaryRow = lines[^1];
        Assert.Contains("\"SUMMARY\"", summaryRow);
        Assert.Contains("\"20261112_DAY\"", summaryRow);
        Assert.Contains("\"4\"", summaryRow); // Total
        Assert.Contains("\"1\"", summaryRow); // OK
        Assert.Contains("\"3\"", summaryRow); // NG
        Assert.Contains("\"25.00%\"", summaryRow); // FPY% = 1 / 4 * 100%
        Assert.Contains("PMNT-01 (2)", summaryRow); // Top defect fixture
    }

    [Fact]
    public async Task ExportShiftSummaryReportCsv_FormulaInjection_NeutralizesDangerousPrefixes()
    {
        // Arrange
        var shiftStart = new DateTime(2026, 11, 12, 8, 0, 0);
        var shiftEnd = new DateTime(2026, 11, 12, 19, 59, 59);
        var shiftWindow = new ShiftWindow("DAY", "20261112", "20261112_DAY", shiftStart, shiftEnd);

        var units = new List<UnitRecord>
        {
            new()
            {
                Source = "MMAPI120",
                Timestamp = shiftStart.AddMinutes(10),
                Pmnt = "=SUM(A1:A10)",
                PanelName = "@cmd",
                BluName = "+12345",
                Errors = ["-DANGEROUS"]
            }
        };

        string filePath = Path.Combine(_tempDir, "ShiftReport_Injection.csv");

        // Act
        await _exportService.ExportShiftSummaryReportCsvAsync(units, filePath, shiftWindow);

        // Assert
        string content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("'=SUM(A1:A10)", content);
        Assert.Contains("'@cmd", content);
        Assert.Contains("'+12345", content);
    }

    [Fact]
    public async Task ExportChartImageAsync_NullCanvas_ReturnsEmptyString()
    {
        // Arrange
        var chartVm = new ChartViewModel();
        string testPath = Path.Combine(_tempDir, "Chart_Null.png");

        // Act
        string result = await chartVm.ExportChartImageAsync(testPath, chartElement: null);

        // Assert
        Assert.Equal(string.Empty, result);
        Assert.False(File.Exists(testPath));
    }

    [Fact]
    public async Task AutoShiftExport_AntiDuplicateLock_PreventsDuplicateExportInSameShift()
    {
        // Arrange
        string rootDir = _tempDir;
        string configDir = Path.Combine(rootDir, "config");
        Directory.CreateDirectory(configDir);

        string settingsJson = """
        {
          "machines": [
            { "name": "MMAPI120", "enabled": true, "logPath": "" }
          ],
          "autoShiftExport": {
            "enabled": true,
            "dayShiftTime": "20:30",
            "nightShiftTime": "08:30"
          }
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(configDir, "settings.json"), settingsJson);

        using var mainVm = new MainViewModel(appRootDir: rootDir);

        // Populate units in DisplayState
        var shiftDate = new DateTime(2026, 11, 12, 10, 0, 0);
        mainVm.DisplayState.AllUnits.Add(new UnitRecord
        {
            Source = "MMAPI120",
            Timestamp = shiftDate,
            Pmnt = "PMNT-01",
            PanelName = "PANEL_TEST_1",
            Errors = []
        });

        // 1. Simulate time at 20:31:00 (within 20:30 Day Cutoff window)
        var cutoffTime1 = new DateTime(2026, 11, 12, 20, 31, 0);
        await mainVm.InvokeAutoShiftExportCheckAsync(cutoffTime1);

        // Verify export triggered
        Assert.Equal("20261112_DAY", mainVm.LastAutoExportedShiftKey);

        string expectedCsv = Path.Combine(rootDir, "exports", "ShiftReport_20261112_DAY.csv");
        Assert.True(File.Exists(expectedCsv));

        var csvLines = await File.ReadAllLinesAsync(expectedCsv);
        Assert.Contains("--- SHIFT SUMMARY ---", string.Join("\n", csvLines));
        Assert.Contains("PANEL_TEST_1", string.Join("\n", csvLines));

        // 2. Simulate time at 20:35:00 (still in cutoff window, should be blocked by anti-duplicate lock)
        var markerMtimeUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(expectedCsv, markerMtimeUtc);

        var cutoffTime2 = new DateTime(2026, 11, 12, 20, 35, 0);
        await mainVm.InvokeAutoShiftExportCheckAsync(cutoffTime2);

        // Anti-duplicate lock verified: shift key remains the same, file was not re-written
        Assert.Equal("20261112_DAY", mainVm.LastAutoExportedShiftKey);
        Assert.Equal(markerMtimeUtc, File.GetLastWriteTimeUtc(expectedCsv));
    }

    [Fact]
    public async Task AutoShiftExport_NightShiftCutoff_TriggersForPreviousNightShift()
    {
        // Arrange
        string rootDir = Path.Combine(_tempDir, "NightShiftTest");
        string configDir = Path.Combine(rootDir, "config");
        Directory.CreateDirectory(configDir);

        string settingsJson = """
        {
          "machines": [
            { "name": "MMAPI120", "enabled": true, "logPath": "" }
          ],
          "autoShiftExport": {
            "enabled": true,
            "dayShiftTime": "20:30",
            "nightShiftTime": "08:30"
          }
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(configDir, "settings.json"), settingsJson);

        using var mainVm = new MainViewModel(appRootDir: rootDir);

        // Populate unit during Night Shift (e.g. 23:00 on 2026-11-11)
        var nightTime = new DateTime(2026, 11, 11, 23, 0, 0);
        mainVm.DisplayState.AllUnits.Add(new UnitRecord
        {
            Source = "MMAPI120",
            Timestamp = nightTime,
            Pmnt = "PMNT-02",
            PanelName = "PANEL_NIGHT_1",
            Errors = ["POWERON"]
        });

        // Act: Simulate time at 08:32 on 2026-11-12 (Night shift handover window)
        var handoverTime = new DateTime(2026, 11, 12, 8, 32, 0);
        await mainVm.InvokeAutoShiftExportCheckAsync(handoverTime);

        // Assert
        Assert.Equal("20261111_NIGHT", mainVm.LastAutoExportedShiftKey);

        string expectedCsv = Path.Combine(rootDir, "exports", "ShiftReport_20261111_NIGHT.csv");
        Assert.True(File.Exists(expectedCsv));

        var csvLines = await File.ReadAllLinesAsync(expectedCsv);
        Assert.Contains("PANEL_NIGHT_1", string.Join("\n", csvLines));
        Assert.Contains("\"20261111_NIGHT\"", string.Join("\n", csvLines));
    }
}

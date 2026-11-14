using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Exports unit data and shift summary/detail reports to UTF-8 CSV with OWASP formula injection protection.
/// </summary>
public interface IExportService
{
    string ConvertToSafeCsvText(string? text);

    Task ExportUnitDataAsync(
        IReadOnlyList<UnitRecord> units,
        string path,
        bool append = false,
        bool includeMachine = false,
        CancellationToken cancellationToken = default);

    ShiftWindow GetShiftWindow(
        DateTime referenceTime,
        int dayStartHour = 8,
        int nightStartHour = 20,
        bool completedShift = false);

    ShiftWindow GetShiftWindow(
        DateTime referenceTime,
        int dayStartHour,
        int dayStartMinute,
        int nightStartHour,
        int nightStartMinute,
        bool completedShift = false);

    Task<ShiftReportResult> ExportShiftReportAsync(
        IReadOnlyList<UnitRecord> units,
        string exportDir,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string shiftKey = "",
        int consecutiveNgThreshold = 3,
        CancellationToken cancellationToken = default);

    Task<string> ExportShiftSummaryReportCsvAsync(
        IReadOnlyList<UnitRecord> units,
        string filePath,
        ShiftWindow shiftWindow,
        CancellationToken cancellationToken = default);
}


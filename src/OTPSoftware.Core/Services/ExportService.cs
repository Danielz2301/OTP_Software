using System.Globalization;
using System.Text;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Exports unit inspection data and shift KPI summary/detail reports to UTF-8 CSV.
/// Neutralizes spreadsheet formula injection risks according to OWASP guidelines.
/// </summary>
public sealed class ExportService : IExportService
{
    private readonly IStatisticsService _statisticsService;

    public ExportService(IStatisticsService? statisticsService = null)
    {
        _statisticsService = statisticsService ?? new StatisticsService();
    }

    public string ConvertToSafeCsvText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        if (text[0] is '=' or '+' or '-' or '@' or '|' or '\t' or '\r' or '\n')
        {
            return "'" + text;
        }

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return text;
        }

        if (trimmed[0] is '=' or '+' or '-' or '@' or '|' or '\t' or '\r' or '\n')
        {
            return "'" + text;
        }

        if (text[0] != '\'' && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '\r' or '\n')
                {
                    int j = i + 1;
                    while (j < text.Length && text[j] is not ('\r' or '\n') && char.IsWhiteSpace(text[j]))
                    {
                        j++;
                    }

                    if (j < text.Length && text[j] is '=' or '+' or '-' or '@' or '|')
                    {
                        return "'" + text;
                    }
                }
            }
        }

        return text;
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string CreateTempSiblingPath(string targetPath)
    {
        string fileName = Path.GetFileName(targetPath);
        string? dir = Path.GetDirectoryName(targetPath);
        string tempName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        return string.IsNullOrEmpty(dir) ? tempName : Path.Combine(dir, tempName);
    }

    private static void CleanupTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static (int[] RunIndices, int[] TotalRuns) BuildUnitRunCounts(IReadOnlyList<UnitRecord> units)
    {
        int count = units.Count;
        var runIndices = new int[count];
        var totalRuns = new int[count];
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keys = new string?[count];

        for (int i = 0; i < count; i++)
        {
            var u = units[i];
            if (u is null || u.PassCount > 0 || u.TotalRuns > 0 || string.IsNullOrEmpty(u.Pmnt))
                continue;

            string machine = !string.IsNullOrEmpty(u.Source) ? u.Source : "MMAPI120";
            string key = $"{machine}_{u.Pmnt}";
            keys[i] = key;
            counters.TryGetValue(key, out int run);
            counters[key] = ++run;
            runIndices[i] = run;
        }

        for (int i = 0; i < count; i++)
        {
            if (keys[i] is { } key)
            {
                totalRuns[i] = counters[key];
            }
        }

        return (runIndices, totalRuns);
    }

    public async Task ExportUnitDataAsync(
        IReadOnlyList<UnitRecord> units,
        string path,
        bool append = false,
        bool includeMachine = false,
        CancellationToken cancellationToken = default)
    {
        if (units == null || units.Count == 0)
            return;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string writePath = append ? path : CreateTempSiblingPath(path);
        var (computedRuns, computedTotals) = BuildUnitRunCounts(units);

        try
        {
            await using (var fs = new FileStream(
                writePath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                bool writeHeader = !append || fs.Length == 0;
                if (writeHeader)
                {
                    if (includeMachine)
                    {
                        await writer.WriteLineAsync("\"Machine\",\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\"");
                    }
                    else
                    {
                        await writer.WriteLineAsync("\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\"");
                    }
                }

                for (int i = 0; i < units.Count; i++)
                {
                    var unit = units[i];
                    if (unit == null) continue;

                    int runIndex = unit.PassCount > 0 ? unit.PassCount : computedRuns[i];
                    int total = unit.TotalRuns > 0
                        ? unit.TotalRuns
                        : (computedTotals[i] > 0 ? computedTotals[i] : runIndex);

                    string tsStr = unit.Timestamp.HasValue
                        ? unit.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : string.Empty;

                    string statusStr = unit.IsNg
                        ? "NG: " + string.Join(" | ", unit.Errors)
                        : "OK";

                    if (includeMachine)
                    {
                        string machine = !string.IsNullOrEmpty(unit.Source) ? unit.Source : "MMAPI120";
                        string line = string.Join(",",
                            EscapeCsv(ConvertToSafeCsvText(machine)),
                            EscapeCsv(tsStr),
                            EscapeCsv(ConvertToSafeCsvText(unit.Pmnt)),
                            EscapeCsv(runIndex.ToString(CultureInfo.InvariantCulture)),
                            EscapeCsv(total.ToString(CultureInfo.InvariantCulture)),
                            EscapeCsv(ConvertToSafeCsvText(unit.PanelName)),
                            EscapeCsv(ConvertToSafeCsvText(unit.BluName)),
                            EscapeCsv(ConvertToSafeCsvText(statusStr))
                        );
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                    }
                    else
                    {
                        string line = string.Join(",",
                            EscapeCsv(tsStr),
                            EscapeCsv(ConvertToSafeCsvText(unit.Pmnt)),
                            EscapeCsv(runIndex.ToString(CultureInfo.InvariantCulture)),
                            EscapeCsv(total.ToString(CultureInfo.InvariantCulture)),
                            EscapeCsv(ConvertToSafeCsvText(unit.PanelName)),
                            EscapeCsv(ConvertToSafeCsvText(unit.BluName)),
                            EscapeCsv(ConvertToSafeCsvText(statusStr))
                        );
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                    }
                }

                await writer.FlushAsync(cancellationToken);
            }

            if (!append)
            {
                File.Move(writePath, path, overwrite: true);
            }
        }
        catch
        {
            if (!append)
            {
                CleanupTempFile(writePath);
            }
            throw;
        }
    }

    public ShiftWindow GetShiftWindow(
        DateTime referenceTime,
        int dayStartHour = 8,
        int nightStartHour = 20,
        bool completedShift = false) =>
        GetShiftWindow(referenceTime, dayStartHour, 0, nightStartHour, 0, completedShift);

    public ShiftWindow GetShiftWindow(
        DateTime referenceTime,
        int dayStartHour,
        int dayStartMinute,
        int nightStartHour,
        int nightStartMinute,
        bool completedShift = false)
    {
        bool validTimes =
            dayStartHour is >= 0 and <= 23 &&
            dayStartMinute is >= 0 and <= 59 &&
            nightStartHour is >= 0 and <= 23 &&
            nightStartMinute is >= 0 and <= 59;

        var dayStartOffset = validTimes ? new TimeSpan(dayStartHour, dayStartMinute, 0) : new TimeSpan(8, 0, 0);
        var nightStartOffset = validTimes ? new TimeSpan(nightStartHour, nightStartMinute, 0) : new TimeSpan(20, 0, 0);

        if (dayStartOffset >= nightStartOffset)
        {
            dayStartHour = 8;
            dayStartMinute = 0;
            nightStartHour = 20;
            nightStartMinute = 0;
            dayStartOffset = new TimeSpan(8, 0, 0);
            nightStartOffset = new TimeSpan(20, 0, 0);
        }

        var timeOfDay = referenceTime.TimeOfDay;

        string shiftName;
        DateTime startTime;
        DateTime endTime;

        if (timeOfDay >= dayStartOffset && timeOfDay < nightStartOffset)
        {
            shiftName = "DAY";
            startTime = referenceTime.Date + dayStartOffset;
            endTime = (referenceTime.Date + nightStartOffset).AddTicks(-1);
        }
        else if (timeOfDay >= nightStartOffset)
        {
            shiftName = "NIGHT";
            startTime = referenceTime.Date + nightStartOffset;
            endTime = (referenceTime.Date.AddDays(1) + dayStartOffset).AddTicks(-1);
        }
        else
        {
            shiftName = "NIGHT";
            startTime = referenceTime.Date.AddDays(-1) + nightStartOffset;
            endTime = (referenceTime.Date + dayStartOffset).AddTicks(-1);
        }

        if (completedShift)
        {
            return GetShiftWindow(
                startTime.AddSeconds(-1),
                dayStartHour,
                dayStartMinute,
                nightStartHour,
                nightStartMinute,
                completedShift: false);
        }

        string shiftDate = startTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string shiftKey = $"{shiftDate}_{shiftName}";
        return new ShiftWindow(shiftName, shiftDate, shiftKey, startTime, endTime);
    }

    public async Task<ShiftReportResult> ExportShiftReportAsync(
        IReadOnlyList<UnitRecord> units,
        string exportDir,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string shiftKey = "",
        int consecutiveNgThreshold = 3,
        CancellationToken cancellationToken = default)
    {
        var shiftUnits = new List<UnitRecord>();
        var pmntTotalRuns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (units != null)
        {
            foreach (var u in units)
            {
                if (u == null) continue;
                var ts = u.Timestamp;
                if (startTime.HasValue && (!ts.HasValue || ts.Value < startTime.Value)) continue;
                if (endTime.HasValue && (!ts.HasValue || ts.Value > endTime.Value)) continue;

                shiftUnits.Add(u);

                if (!string.IsNullOrEmpty(u.Pmnt))
                {
                    pmntTotalRuns.TryGetValue(u.Pmnt, out int tot);
                    pmntTotalRuns[u.Pmnt] = tot + 1;
                }
            }
        }

        Directory.CreateDirectory(exportDir);

        string safeKey = string.IsNullOrWhiteSpace(shiftKey)
            ? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            : shiftKey;

        string summaryPath = Path.Combine(exportDir, $"SHIFT_SUMMARY_{safeKey}.csv");
        string detailPath = Path.Combine(exportDir, $"SHIFT_DETAIL_{safeKey}.csv");

        var stats = _statisticsService.GetUnitStatistics(shiftUnits, UnitStatusFilter.All, consecutiveNgThreshold);

        var summaryLines = new List<string>
        {
            "\"Category\",\"Name\",\"TotalUnits\",\"PassCount\",\"FailCount\",\"NgRatePercent\",\"CurrentStreak\",\"MaxStreak\",\"AlarmStatus\",\"XYLV\",\"POWERON\",\"POWEROFF\",\"FLICKER\""
        };

        string overallAlarm = stats.AlarmPmnts.Count > 0 ? $"ALARM ({stats.AlarmPmnts.Count} PMNTs)" : "OK";
        summaryLines.Add(string.Join(",",
            EscapeCsv("SHIFT_OVERALL"),
            EscapeCsv(ConvertToSafeCsvText(safeKey)),
            EscapeCsv(stats.TotalScope.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(stats.OkCount.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(stats.NgCount.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(Math.Round(stats.NgRate, 2).ToString(CultureInfo.InvariantCulture)),
            EscapeCsv("0"),
            EscapeCsv("0"),
            EscapeCsv(overallAlarm),
            EscapeCsv(stats.Xylv.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(stats.PowerOn.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(stats.PowerOff.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(stats.Flicker.ToString(CultureInfo.InvariantCulture))
        ));

        var pmntGroups = new (string Category, IReadOnlyList<PmntDefectRankItem> Items)[]
        {
            ("PMNT_A", stats.PmntA),
            ("PMNT_B", stats.PmntB),
            ("PMNT_OTHER", stats.PmntOther)
        };
        foreach (var (cat, pmntList) in pmntGroups)
        {
            foreach (var p in pmntList)
            {
                if (p == null) continue;
                pmntTotalRuns.TryGetValue(p.Name, out int totRuns);
                if (totRuns < p.Count) totRuns = p.Count;
                int okRuns = totRuns - p.Count;
                double pRate = totRuns > 0 ? Math.Round((double)p.Count / totRuns * 100.0, 2) : 0.0;

                summaryLines.Add(string.Join(",",
                    EscapeCsv(cat),
                    EscapeCsv(ConvertToSafeCsvText(p.Name)),
                    EscapeCsv(totRuns.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(okRuns.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(p.Count.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(pRate.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(p.Streak.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(p.MaxStreak.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(p.IsAlarm ? "CONSECUTIVE_NG_ALARM" : "NORMAL"),
                    EscapeCsv("0"),
                    EscapeCsv("0"),
                    EscapeCsv("0"),
                    EscapeCsv("0")
                ));
            }
        }

        string summaryTempPath = CreateTempSiblingPath(summaryPath);
        try
        {
            await File.WriteAllLinesAsync(summaryTempPath, summaryLines, new UTF8Encoding(false), cancellationToken);
            File.Move(summaryTempPath, summaryPath, overwrite: true);
        }
        catch
        {
            CleanupTempFile(summaryTempPath);
            throw;
        }

        string? exportedDetailPath = null;
        if (shiftUnits.Count > 0)
        {
            await ExportUnitDataAsync(shiftUnits, detailPath, append: false, includeMachine: true, cancellationToken: cancellationToken);
            exportedDetailPath = detailPath;
        }

        return new ShiftReportResult(safeKey, shiftUnits.Count, summaryPath, exportedDetailPath, stats);
    }

    public async Task<string> ExportShiftSummaryReportCsvAsync(
        IReadOnlyList<UnitRecord> units,
        string filePath,
        ShiftWindow shiftWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(shiftWindow);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var shiftUnits = new List<UnitRecord>();
        if (units != null)
        {
            foreach (var u in units)
            {
                if (u == null) continue;
                if (!u.Timestamp.HasValue || u.Timestamp.Value < shiftWindow.StartTime || u.Timestamp.Value > shiftWindow.EndTime)
                    continue;
                shiftUnits.Add(u);
            }
        }

        int totalCount = shiftUnits.Count;
        int okCount = 0;
        int ngCount = 0;
        var defectPmntCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in shiftUnits)
        {
            bool isNg = u.IsNg;
            if (isNg)
            {
                ngCount++;
                if (!string.IsNullOrWhiteSpace(u.Pmnt))
                {
                    defectPmntCounts.TryGetValue(u.Pmnt, out int count);
                    defectPmntCounts[u.Pmnt] = count + 1;
                }
            }
            else
            {
                okCount++;
            }
        }

        double fpyPercent = totalCount > 0 ? Math.Round((double)okCount / totalCount * 100.0, 2) : 0.0;
        var topDefects = defectPmntCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => $"{kv.Key} ({kv.Value})");
        string topDefectsStr = string.Join("; ", topDefects);

        var (computedRuns, computedTotals) = BuildUnitRunCounts(shiftUnits);
        string tempPath = CreateTempSiblingPath(filePath);

        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                // 1. Unit data header
                await writer.WriteLineAsync("\"Machine\",\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\"");

                // 2. Unit rows
                for (int i = 0; i < shiftUnits.Count; i++)
                {
                    var unit = shiftUnits[i];
                    int runIndex = unit.PassCount > 0 ? unit.PassCount : computedRuns[i];
                    int total = unit.TotalRuns > 0
                        ? unit.TotalRuns
                        : (computedTotals[i] > 0 ? computedTotals[i] : runIndex);

                    string tsStr = unit.Timestamp.HasValue
                        ? unit.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : string.Empty;

                    string statusStr = unit.IsNg
                        ? "NG: " + string.Join(" | ", unit.Errors)
                        : "OK";

                    string machine = !string.IsNullOrEmpty(unit.Source) ? unit.Source : "MMAPI120";

                    string line = string.Join(",",
                        EscapeCsv(ConvertToSafeCsvText(machine)),
                        EscapeCsv(tsStr),
                        EscapeCsv(ConvertToSafeCsvText(unit.Pmnt)),
                        EscapeCsv(runIndex.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(total.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(ConvertToSafeCsvText(unit.PanelName)),
                        EscapeCsv(ConvertToSafeCsvText(unit.BluName)),
                        EscapeCsv(ConvertToSafeCsvText(statusStr))
                    );
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                }

                // 3. Shift KPI summary section
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("\"--- SHIFT SUMMARY ---\",\"Shift Key\",\"Total Units\",\"OK Units\",\"NG Units\",\"FPY%\",\"Top Defect PMNTs\"");

                string summaryLine = string.Join(",",
                    EscapeCsv("SUMMARY"),
                    EscapeCsv(ConvertToSafeCsvText(shiftWindow.ShiftKey)),
                    EscapeCsv(totalCount.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(okCount.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(ngCount.ToString(CultureInfo.InvariantCulture)),
                    EscapeCsv(fpyPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%"),
                    EscapeCsv(ConvertToSafeCsvText(topDefectsStr))
                );
                await writer.WriteLineAsync(summaryLine.AsMemory(), cancellationToken);

                await writer.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            CleanupTempFile(tempPath);
            throw;
        }

        return filePath;
    }
}

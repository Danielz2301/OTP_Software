using System.Buffers;
using System.Globalization;
using System.Text;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Exports unit inspection data and shift KPI summary/detail reports to UTF-8 CSV.
/// Neutralizes spreadsheet formula injection risks according to OWASP guidelines.
/// </summary>
public sealed class ExportService(IStatisticsService? statisticsService = null) : IExportService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly IStatisticsService _statisticsService = statisticsService ?? new StatisticsService();

    public string ConvertToSafeCsvText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        if (text[0] is '=' or '+' or '-' or '@' or '|' or '\t' or '\r' or '\n')
        {
            return "'" + text;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
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

    private sealed class MachinePmntComparer : IEqualityComparer<(string Machine, string Pmnt)>
    {
        internal static readonly MachinePmntComparer Instance = new();

        public bool Equals((string Machine, string Pmnt) x, (string Machine, string Pmnt) y) =>
            string.Equals(x.Machine, y.Machine, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Pmnt, y.Pmnt, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Machine, string Pmnt) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Machine),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Pmnt));
    }

    private static void PopulateUnitRunCounts(IReadOnlyList<UnitRecord> units, int[] runIndices, int[] totalRuns)
    {
        int count = units.Count;
        Array.Clear(runIndices, 0, count);
        Array.Clear(totalRuns, 0, count);

        var counters = new Dictionary<(string Machine, string Pmnt), int>(MachinePmntComparer.Instance);

        for (int i = 0; i < count; i++)
        {
            var u = units[i];
            if (u is null || u.PassCount > 0 || u.TotalRuns > 0 || string.IsNullOrEmpty(u.Pmnt))
                continue;

            string machine = !string.IsNullOrEmpty(u.Source) ? u.Source : "MMAPI120";
            var key = (machine, u.Pmnt);
            counters.TryGetValue(key, out int run);
            counters[key] = ++run;
            runIndices[i] = run;
        }

        for (int i = 0; i < count; i++)
        {
            if (runIndices[i] > 0)
            {
                var u = units[i]!;
                string machine = !string.IsNullOrEmpty(u.Source) ? u.Source : "MMAPI120";
                totalRuns[i] = counters[(machine, u.Pmnt)];
            }
        }
    }

    private string FormatUnitCsvLine(UnitRecord unit, int computedRunIndex, int computedTotalRuns, bool includeMachine)
    {
        int runIndex = unit.PassCount > 0 ? unit.PassCount : computedRunIndex;
        int total = unit.TotalRuns > 0
            ? unit.TotalRuns
            : (computedTotalRuns > 0 ? computedTotalRuns : runIndex);

        string tsStr = unit.Timestamp.HasValue
            ? unit.Timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;

        string statusStr = unit.IsNg
            ? "NG: " + string.Join(" | ", unit.Errors)
            : "OK";

        if (includeMachine)
        {
            string machine = !string.IsNullOrEmpty(unit.Source) ? unit.Source : "MMAPI120";
            return string.Join(",",
                EscapeCsv(ConvertToSafeCsvText(machine)),
                EscapeCsv(tsStr),
                EscapeCsv(ConvertToSafeCsvText(unit.Pmnt)),
                EscapeCsv(runIndex.ToString(CultureInfo.InvariantCulture)),
                EscapeCsv(total.ToString(CultureInfo.InvariantCulture)),
                EscapeCsv(ConvertToSafeCsvText(unit.PanelName)),
                EscapeCsv(ConvertToSafeCsvText(unit.BluName)),
                EscapeCsv(ConvertToSafeCsvText(statusStr))
            );
        }

        return string.Join(",",
            EscapeCsv(tsStr),
            EscapeCsv(ConvertToSafeCsvText(unit.Pmnt)),
            EscapeCsv(runIndex.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(total.ToString(CultureInfo.InvariantCulture)),
            EscapeCsv(ConvertToSafeCsvText(unit.PanelName)),
            EscapeCsv(ConvertToSafeCsvText(unit.BluName)),
            EscapeCsv(ConvertToSafeCsvText(statusStr))
        );
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
        int count = units.Count;
        int[] computedRuns = ArrayPool<int>.Shared.Rent(count);
        int[] computedTotals = ArrayPool<int>.Shared.Rent(count);

        try
        {
            PopulateUnitRunCounts(units, computedRuns, computedTotals);

            var fs = FileIoRetryHelper.ExecuteWithSharingRetry(
                () => new FileStream(
                    writePath,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true),
                cancellationToken: cancellationToken);

            await using (fs.ConfigureAwait(false))
            {
                var writer = new StreamWriter(fs, Utf8NoBom);
                await using (writer.ConfigureAwait(false))
                {
                    bool writeHeader = !append || fs.Length == 0;
                    if (writeHeader)
                    {
                        string header = includeMachine
                            ? "\"Machine\",\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\""
                            : "\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\"";
                        await writer.WriteLineAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var unit = units[i];
                        if (unit == null) continue;

                        string line = FormatUnitCsvLine(unit, computedRuns[i], computedTotals[i], includeMachine);
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (!append)
            {
                _ = FileIoRetryHelper.ExecuteWithSharingRetry(
                    () =>
                    {
                        File.Move(writePath, path, overwrite: true);
                        return true;
                    },
                    cancellationToken: cancellationToken);
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
        finally
        {
            ArrayPool<int>.Shared.Return(computedRuns);
            ArrayPool<int>.Shared.Return(computedTotals);
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

        string safeKey;
        if (string.IsNullOrWhiteSpace(shiftKey))
        {
            safeKey = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }
        else
        {
            string trimmedKey = shiftKey.Trim();
            if (!SecurityPathValidator.IsSafeIdentifierToken(trimmedKey))
            {
                throw new ArgumentException("Shift key contains invalid or unsafe path characters.", nameof(shiftKey));
            }
            safeKey = trimmedKey;
        }

        string summaryPath = Path.Combine(exportDir, $"SHIFT_SUMMARY_{safeKey}.csv");
        string detailPath = Path.Combine(exportDir, $"SHIFT_DETAIL_{safeKey}.csv");
        if (!SecurityPathValidator.IsPathWithinRoot(exportDir, summaryPath) ||
            !SecurityPathValidator.IsPathWithinRoot(exportDir, detailPath))
        {
            throw new ArgumentException("Resolved shift report path escapes the target export directory.", nameof(shiftKey));
        }

        Directory.CreateDirectory(exportDir);

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
            await File.WriteAllLinesAsync(summaryTempPath, summaryLines, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            _ = FileIoRetryHelper.ExecuteWithSharingRetry(
                () =>
                {
                    File.Move(summaryTempPath, summaryPath, overwrite: true);
                    return true;
                },
                cancellationToken: cancellationToken);
        }
        catch
        {
            CleanupTempFile(summaryTempPath);
            throw;
        }

        string? exportedDetailPath = null;
        if (shiftUnits.Count > 0)
        {
            await ExportUnitDataAsync(shiftUnits, detailPath, append: false, includeMachine: true, cancellationToken: cancellationToken).ConfigureAwait(false);
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

        int[] computedRuns = ArrayPool<int>.Shared.Rent(totalCount);
        int[] computedTotals = ArrayPool<int>.Shared.Rent(totalCount);
        string tempPath = CreateTempSiblingPath(filePath);

        try
        {
            PopulateUnitRunCounts(shiftUnits, computedRuns, computedTotals);

            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            await using (fs.ConfigureAwait(false))
            {
                var writer = new StreamWriter(fs, Utf8NoBom);
                await using (writer.ConfigureAwait(false))
                {
                    // 1. Unit data header
                    await writer.WriteLineAsync("\"Machine\",\"Timestamp\",\"PMNT\",\"RunIndex\",\"TotalRuns\",\"PanelName\",\"BluName\",\"Status\"".AsMemory(), cancellationToken).ConfigureAwait(false);

                    // 2. Unit rows
                    for (int i = 0; i < shiftUnits.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string line = FormatUnitCsvLine(shiftUnits[i], computedRuns[i], computedTotals[i], includeMachine: true);
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    // 3. Shift KPI summary section
                    await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
                    await writer.WriteLineAsync("\"--- SHIFT SUMMARY ---\",\"Shift Key\",\"Total Units\",\"OK Units\",\"NG Units\",\"FPY%\",\"Top Defect PMNTs\"".AsMemory(), cancellationToken).ConfigureAwait(false);

                    string summaryLine = string.Join(",",
                        EscapeCsv("SUMMARY"),
                        EscapeCsv(ConvertToSafeCsvText(shiftWindow.ShiftKey)),
                        EscapeCsv(totalCount.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(okCount.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(ngCount.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(fpyPercent.ToString("0.00", CultureInfo.InvariantCulture) + "%"),
                        EscapeCsv(ConvertToSafeCsvText(topDefectsStr))
                    );
                    await writer.WriteLineAsync(summaryLine.AsMemory(), cancellationToken).ConfigureAwait(false);

                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _ = FileIoRetryHelper.ExecuteWithSharingRetry(
                () =>
                {
                    File.Move(tempPath, filePath, overwrite: true);
                    return true;
                },
                cancellationToken: cancellationToken);
        }
        catch
        {
            CleanupTempFile(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(computedRuns);
            ArrayPool<int>.Shared.Return(computedTotals);
        }

        return filePath;
    }
}

namespace OTPSoftware.Core.Services;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

/// <summary>
/// Calculation engine for KPI statistics, PMNT fixture defect rankings, and cumulative error chart data.
/// </summary>
public sealed class StatisticsService : IStatisticsService
{
    private static readonly SearchValues<char> DefectParamSeparators = SearchValues.Create(",|;");
    private static readonly SearchValues<char> DefectKeyValueDelimiters = SearchValues.Create("=:");
    private static readonly SearchValues<char> DefectTokenTrimChars = SearchValues.Create("[] \t\r\n");
    private sealed class PmntTrackingInfo
    {
        private string? _m1;
        private int _s1;
        private string? _m2;
        private int _s2;
        private Dictionary<string, int>? _overflowMachines;

        public int Count { get; set; }
        public int MaxIndex { get; set; }
        public int MaxStreak { get; set; }

        public void ResetStreak(string machine)
        {
            if (_m1 is null)
            {
                _m1 = machine;
                _s1 = 0;
                return;
            }

            if (string.Equals(_m1, machine, StringComparison.OrdinalIgnoreCase))
            {
                _s1 = 0;
                return;
            }

            if (_m2 is null)
            {
                _m2 = machine;
                _s2 = 0;
                return;
            }

            if (string.Equals(_m2, machine, StringComparison.OrdinalIgnoreCase))
            {
                _s2 = 0;
                return;
            }

            _overflowMachines ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _overflowMachines[machine] = 0;
        }

        public int IncrementStreak(string machine)
        {
            if (_m1 is null)
            {
                _m1 = machine;
                return _s1 = 1;
            }

            if (string.Equals(_m1, machine, StringComparison.OrdinalIgnoreCase))
            {
                return ++_s1;
            }

            if (_m2 is null)
            {
                _m2 = machine;
                return _s2 = 1;
            }

            if (string.Equals(_m2, machine, StringComparison.OrdinalIgnoreCase))
            {
                return ++_s2;
            }

            _overflowMachines ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _overflowMachines.TryGetValue(machine, out int cur);
            cur++;
            _overflowMachines[machine] = cur;
            return cur;
        }

        public int GetCurrentMaxStreak()
        {
            int max = _m1 is not null ? _s1 : 0;
            if (_m2 is not null && _s2 > max)
            {
                max = _s2;
            }

            if (_overflowMachines is not null)
            {
                foreach (int s in _overflowMachines.Values)
                {
                    if (s > max)
                    {
                        max = s;
                    }
                }
            }

            return max;
        }
    }

    public UnitStatisticsResult GetUnitStatistics(
        IReadOnlyList<UnitRecord> units,
        UnitStatusFilter filterStatus = UnitStatusFilter.All,
        int consecutiveNgThreshold = 3)
    {
        int totalScope = 0;
        int okCount = 0;
        int ngCount = 0;
        int xylv = 0;
        int pon = 0;
        int poff = 0;
        int flick = 0;

        var trackingDict = new Dictionary<string, PmntTrackingInfo>(StringComparer.OrdinalIgnoreCase);

        if (units != null)
        {
            foreach (var u in units)
            {
                if (u == null)
                {
                    continue;
                }

                totalScope++;
                bool hasErr = u.IsNg;
                string p = u.Pmnt;
                string machine = u.Source ?? string.Empty;

                if (!hasErr)
                {
                    okCount++;
                    if (!string.IsNullOrEmpty(p))
                    {
                        if (!trackingDict.TryGetValue(p, out var okInfo))
                        {
                            okInfo = new PmntTrackingInfo();
                            trackingDict[p] = okInfo;
                        }
                        okInfo.ResetStreak(machine);
                    }
                    continue;
                }

                PmntTrackingInfo? info = null;
                int curStreak = 0;
                if (!string.IsNullOrEmpty(p))
                {
                    if (!trackingDict.TryGetValue(p, out info))
                    {
                        info = new PmntTrackingInfo();
                        trackingDict[p] = info;
                    }
                    curStreak = info.IncrementStreak(machine);
                }

                if (filterStatus == UnitStatusFilter.OkOnly)
                {
                    // Filter OK only: exclude error records from display statistics
                    continue;
                }

                ngCount++;
                foreach (var e in u.Errors)
                {
                    if (string.Equals(e, "XYLV", StringComparison.OrdinalIgnoreCase))
                    {
                        xylv++;
                    }
                    else if (string.Equals(e, "POWERON", StringComparison.OrdinalIgnoreCase))
                    {
                        pon++;
                    }
                    else if (string.Equals(e, "POWEROFF", StringComparison.OrdinalIgnoreCase))
                    {
                        poff++;
                    }
                    else if (string.Equals(e, "FLICKER", StringComparison.OrdinalIgnoreCase))
                    {
                        flick++;
                    }
                }

                if (info is not null)
                {
                    int idx = u.Index;
                    if (info.Count == 0 || idx > info.MaxIndex)
                    {
                        info.MaxIndex = idx;
                    }
                    info.Count++;
                    if (curStreak > info.MaxStreak)
                    {
                        info.MaxStreak = curStreak;
                    }
                }
            }
        }

        int displayTotal = filterStatus switch
        {
            UnitStatusFilter.NgOnly => ngCount,
            UnitStatusFilter.OkOnly => okCount,
            _ => totalScope
        };

        int baseForRate = (filterStatus == UnitStatusFilter.NgOnly || filterStatus == UnitStatusFilter.OkOnly)
            ? totalScope
            : displayTotal;

        double rate = baseForRate > 0 ? ((double)ngCount / baseForRate) * 100.0 : 0.0;

        int maxA = 1;
        int maxB = 1;
        int maxOther = 1;
        int sumA = 0;
        int sumB = 0;

        foreach (var (pKey, info) in trackingDict)
        {
            if (info.Count == 0)
            {
                continue;
            }

            if (pKey.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Count > maxA)
                {
                    maxA = info.Count;
                }
                sumA += info.Count;
            }
            else if (pKey.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Count > maxB)
                {
                    maxB = info.Count;
                }
                sumB += info.Count;
            }
            else if (info.Count > maxOther)
            {
                maxOther = info.Count;
            }
        }

        int effectiveThreshold = Math.Max(1, consecutiveNgThreshold);
        var listA = new List<PmntDefectRankItem>();
        var listB = new List<PmntDefectRankItem>();
        var listOther = new List<PmntDefectRankItem>();
        var alarmPmnts = new List<PmntDefectRankItem>();

        foreach (var (pKey, info) in trackingDict)
        {
            if (info.Count == 0)
            {
                continue;
            }

            int curStreak = info.GetCurrentMaxStreak();
            bool isAlarm = curStreak >= effectiveThreshold;

            int maxCount = maxOther;
            if (pKey.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                maxCount = maxA;
            }
            else if (pKey.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                maxCount = maxB;
            }

            var item = new PmntDefectRankItem
            {
                Name = pKey,
                Count = info.Count,
                Streak = curStreak,
                MaxStreak = info.MaxStreak,
                IsAlarm = isAlarm,
                MaxCount = maxCount,
                MaxIndex = info.MaxIndex
            };

            if (isAlarm)
            {
                alarmPmnts.Add(item);
            }

            if (pKey.EndsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                listA.Add(item);
            }
            else if (pKey.EndsWith("B", StringComparison.OrdinalIgnoreCase))
            {
                listB.Add(item);
            }
            else
            {
                listOther.Add(item);
            }
        }

        listA.Sort((x, y) => x.MaxIndex.CompareTo(y.MaxIndex));
        listB.Sort((x, y) => x.MaxIndex.CompareTo(y.MaxIndex));
        listOther.Sort((x, y) => x.MaxIndex.CompareTo(y.MaxIndex));

        return new UnitStatisticsResult
        {
            TotalScope = totalScope,
            DisplayTotal = displayTotal,
            OkCount = okCount,
            NgCount = ngCount,
            NgRate = rate,
            Xylv = xylv,
            PowerOn = pon,
            PowerOff = poff,
            Flicker = flick,
            PmntA = listA,
            PmntB = listB,
            PmntOther = listOther,
            PmntAErrorSum = sumA,
            PmntBErrorSum = sumB,
            AlarmPmnts = alarmPmnts
        };
    }

    internal static bool MatchesPmntGroupFilter(string pmnt, PmntGroupFilter groupFilter) =>
        groupFilter switch
        {
            PmntGroupFilter.A => pmnt.EndsWith("A", StringComparison.OrdinalIgnoreCase),
            PmntGroupFilter.B => pmnt.EndsWith("B", StringComparison.OrdinalIgnoreCase),
            PmntGroupFilter.Other => !pmnt.EndsWith("A", StringComparison.OrdinalIgnoreCase) &&
                                     !pmnt.EndsWith("B", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    public ErrorChartData GetErrorChartData(
        IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All)
    {
        if (units == null || units.Count == 0)
        {
            return new ErrorChartData();
        }

        var rawNgUnits = new List<(UnitRecord Unit, string Pmnt, DateTime Timestamp, int OrderIndex)>();
        var totalsByPmnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool needsSort = false;
        long prevTicks = long.MinValue;

        foreach (var u in units)
        {
            if (u == null || !u.IsNg || u.Timestamp is not { } ts)
            {
                continue;
            }

            if (defectFilter != DefectFilterType.All)
            {
                string target = defectFilter switch
                {
                    DefectFilterType.Xylv => "XYLV",
                    DefectFilterType.PowerOn => "POWERON",
                    DefectFilterType.PowerOff => "POWEROFF",
                    DefectFilterType.Flicker => "FLICKER",
                    _ => string.Empty
                };

                bool hasTargetDefect = false;
                foreach (var err in u.Errors)
                {
                    if (string.Equals(err, target, StringComparison.OrdinalIgnoreCase))
                    {
                        hasTargetDefect = true;
                        break;
                    }
                }

                if (!hasTargetDefect)
                {
                    continue;
                }
            }

            string pmnt = string.IsNullOrWhiteSpace(u.Pmnt) ? "UNKNOWN" : u.Pmnt.Trim();
            if (!MatchesPmntGroupFilter(pmnt, groupFilter))
            {
                continue;
            }

            if (ts.Ticks < prevTicks)
            {
                needsSort = true;
            }
            prevTicks = ts.Ticks;

            totalsByPmnt[pmnt] = totalsByPmnt.GetValueOrDefault(pmnt) + 1;
            rawNgUnits.Add((u, pmnt, ts, rawNgUnits.Count));
        }

        if (rawNgUnits.Count == 0)
        {
            return new ErrorChartData();
        }

        // Stable chronological sort in-place only when input is non-monotonic
        if (needsSort)
        {
            rawNgUnits.Sort(static (a, b) =>
            {
                int cmp = a.Timestamp.Ticks.CompareTo(b.Timestamp.Ticks);
                return cmp != 0 ? cmp : a.OrderIndex.CompareTo(b.OrderIndex);
            });
        }

        // Build cumulative points and per-PMNT series using already-normalized PMNT & non-null Timestamp
        var runningCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seriesPointsMap = new Dictionary<string, List<ErrorChartPoint>>(StringComparer.OrdinalIgnoreCase);
        var seriesGroupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pointsList = new List<ErrorChartPoint>(rawNgUnits.Count);

        DateTime? minTime = null;
        DateTime? maxTime = null;
        int maxErrCount = 0;

        foreach (var (u, pmnt, ts, _) in rawNgUnits)
        {
            int cumCount = runningCounts.GetValueOrDefault(pmnt) + 1;
            runningCounts[pmnt] = cumCount;

            int totalForPmnt = totalsByPmnt[pmnt];
            if (cumCount > maxErrCount)
            {
                maxErrCount = cumCount;
            }

            if (minTime == null || ts < minTime.Value)
            {
                minTime = ts;
            }

            if (maxTime == null || ts > maxTime.Value)
            {
                maxTime = ts;
            }

            string grp = pmnt.EndsWith("A", StringComparison.OrdinalIgnoreCase) ? "A"
                       : pmnt.EndsWith("B", StringComparison.OrdinalIgnoreCase) ? "B"
                       : "Other";

            string src = !string.IsNullOrWhiteSpace(u.Source) ? u.Source : "MMAPI120";

            var pt = new ErrorChartPoint
            {
                PointIndex = pointsList.Count,
                UnitIndex = u.Index,
                Pmnt = pmnt,
                Group = grp,
                Timestamp = ts,
                TimeFormatted = ts.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ShortTime = ts.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ErrorCount = cumCount,
                TotalPmntError = totalForPmnt,
                IsPeakForPmnt = (cumCount == totalForPmnt),
                Errors = u.Errors,
                ErrorText = string.Join(" | ", u.Errors),
                PanelName = u.PanelName ?? string.Empty,
                BluName = u.BluName ?? string.Empty,
                Source = src
            };

            pointsList.Add(pt);

            if (!seriesPointsMap.TryGetValue(pmnt, out var seriesPoints))
            {
                seriesPoints = [];
                seriesPointsMap[pmnt] = seriesPoints;
                seriesGroupMap[pmnt] = grp;
            }
            seriesPoints.Add(pt);
        }

        var seriesList = seriesPointsMap
            .Select(kvp => new ErrorChartSeries(
                kvp.Key,
                seriesGroupMap[kvp.Key],
                totalsByPmnt[kvp.Key],
                kvp.Value
            ))
            .OrderByDescending(s => s.TotalErrors)
            .ThenBy(s => s.Pmnt, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string topPmnt = seriesList.Count > 0 ? seriesList[0].Pmnt : string.Empty;
        int topPmntCount = seriesList.Count > 0 ? seriesList[0].TotalErrors : 0;

        return new ErrorChartData
        {
            Points = pointsList,
            Series = seriesList,
            TotalErrors = pointsList.Count,
            UniquePmntCount = seriesPointsMap.Count,
            MaxErrorCount = maxErrCount,
            MinTime = minTime,
            MaxTime = maxTime,
            TopPmnt = topPmnt,
            TopPmntCount = topPmntCount
        };
    }

    public ParetoChartData GetParetoChartData(
        IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        int topN = 10)
    {
        if (units == null || units.Count == 0)
        {
            return new ParetoChartData();
        }

        var countsByPmnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalDefects = 0;

        foreach (var u in units)
        {
            if (u == null) continue;
            if (!u.IsNg) continue;

            if (defectFilter != DefectFilterType.All)
            {
                string target = defectFilter switch
                {
                    DefectFilterType.Xylv => "XYLV",
                    DefectFilterType.PowerOn => "POWERON",
                    DefectFilterType.PowerOff => "POWEROFF",
                    DefectFilterType.Flicker => "FLICKER",
                    _ => string.Empty
                };

                bool hasTargetDefect = false;
                foreach (var err in u.Errors)
                {
                    if (string.Equals(err, target, StringComparison.OrdinalIgnoreCase))
                    {
                        hasTargetDefect = true;
                        break;
                    }
                }

                if (!hasTargetDefect) continue;
            }

            string pmnt = string.IsNullOrWhiteSpace(u.Pmnt) ? "UNKNOWN" : u.Pmnt.Trim();
            if (!MatchesPmntGroupFilter(pmnt, groupFilter))
            {
                continue;
            }

            countsByPmnt[pmnt] = countsByPmnt.GetValueOrDefault(pmnt) + 1;
            totalDefects++;
        }

        if (totalDefects == 0)
        {
            return new ParetoChartData();
        }

        var sortedPmnts = countsByPmnt
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int vitalFewCount = 0;
        int runningVitalCount = 0;
        foreach (var kvp in sortedPmnts)
        {
            runningVitalCount += kvp.Value;
            vitalFewCount++;
            if ((double)runningVitalCount * 100.0 / totalDefects >= 80.0 - 1e-6)
            {
                break;
            }
        }

        var displayPmnts = topN > 0 ? sortedPmnts.Take(topN).ToList() : sortedPmnts;

        var items = new List<ParetoChartItem>(displayPmnts.Count);
        int runningCount = 0;

        foreach (var kvp in displayPmnts)
        {
            runningCount += kvp.Value;
            double pct = Math.Round((double)kvp.Value * 100.0 / totalDefects, 2);
            double cumPct = Math.Round((double)runningCount * 100.0 / totalDefects, 2);

            string grp = kvp.Key.EndsWith("A", StringComparison.OrdinalIgnoreCase) ? "A"
                       : kvp.Key.EndsWith("B", StringComparison.OrdinalIgnoreCase) ? "B"
                       : "Other";

            items.Add(new ParetoChartItem
            {
                Label = kvp.Key,
                Group = grp,
                Count = kvp.Value,
                Percentage = pct,
                CumulativePercentage = cumPct
            });
        }

        return new ParetoChartData
        {
            Items = items,
            TotalDefects = totalDefects,
            MaxCount = items.Count > 0 ? items[0].Count : 0,
            TopLabel = items.Count > 0 ? items[0].Label : string.Empty,
            VitalFewCount = vitalFewCount
        };
    }

    private struct HourlyAccumulator(long hourTicks)
    {
        public readonly long HourTicks = hourTicks;
        public int TotalCount;
        public int NgCount;
    }

    public HourlyTrendData GetHourlyTrendData(
        IReadOnlyList<UnitRecord> units,
        int dayShiftStartHour = 8,
        int nightShiftStartHour = 20)
    {
        if (units == null || units.Count == 0)
        {
            return new HourlyTrendData();
        }

        // Single-pass aggregation without LINQ GroupBy allocations or second enumeration pass.
        var accumulators = new List<HourlyAccumulator>(24);
        Dictionary<long, int>? hourToSlot = null;
        long lastHourTicks = -1;
        int lastSlotIdx = -1;
        DateTime? latestTs = null;

        foreach (var u in units)
        {
            if (u?.Timestamp == null)
            {
                continue;
            }

            DateTime ts = u.Timestamp.Value;
            if (latestTs == null || ts > latestTs.Value)
            {
                latestTs = ts;
            }

            long hourTicks = ts.Ticks - (ts.Ticks % TimeSpan.TicksPerHour);
            int slotIdx;
            if (hourTicks == lastHourTicks && lastSlotIdx >= 0)
            {
                slotIdx = lastSlotIdx;
            }
            else
            {
                hourToSlot ??= new Dictionary<long, int>(24);
                if (!hourToSlot.TryGetValue(hourTicks, out slotIdx))
                {
                    slotIdx = accumulators.Count;
                    hourToSlot[hourTicks] = slotIdx;
                    accumulators.Add(new HourlyAccumulator(hourTicks));
                }
                lastHourTicks = hourTicks;
                lastSlotIdx = slotIdx;
            }

            var acc = accumulators[slotIdx];
            acc.TotalCount++;
            if (u.IsNg)
            {
                acc.NgCount++;
            }
            accumulators[slotIdx] = acc;
        }

        if (accumulators.Count == 0)
        {
            return new HourlyTrendData();
        }

        accumulators.Sort(static (a, b) => a.HourTicks.CompareTo(b.HourTicks));

        var buckets = new List<HourlyThroughputBucket>(accumulators.Count);
        int totalUnits = 0;
        int totalOk = 0;
        int totalNg = 0;
        int maxHourlyTotal = 0;
        double maxNgRatePercent = 0.0;

        foreach (var acc in accumulators)
        {
            var hourStart = new DateTime(acc.HourTicks);
            int totalInHour = acc.TotalCount;
            int ngInHour = acc.NgCount;
            int okInHour = totalInHour - ngInHour;
            double ngRate = totalInHour > 0 ? Math.Round((double)ngInHour * 100.0 / totalInHour, 2) : 0.0;
            double fpy = Math.Round(100.0 - ngRate, 2);

            totalUnits += totalInHour;
            totalOk += okInHour;
            totalNg += ngInHour;

            if (totalInHour > maxHourlyTotal)
            {
                maxHourlyTotal = totalInHour;
            }

            if (ngRate > maxNgRatePercent)
            {
                maxNgRatePercent = ngRate;
            }

            buckets.Add(new HourlyThroughputBucket
            {
                HourStart = hourStart,
                HourLabel = hourStart.ToString("HH:00", CultureInfo.InvariantCulture),
                TotalCount = totalInHour,
                OkCount = okInHour,
                NgCount = ngInHour,
                NgRatePercent = ngRate,
                FpyPercent = fpy
            });
        }

        string shiftLabel = string.Empty;
        if (latestTs != null)
        {
            int hour = latestTs.Value.Hour;
            shiftLabel = (hour >= dayShiftStartHour && hour <= nightShiftStartHour - 1)
                ? "DAY SHIFT"
                : "NIGHT SHIFT";
        }

        return new HourlyTrendData
        {
            Buckets = buckets,
            TotalUnits = totalUnits,
            TotalOk = totalOk,
            TotalNg = totalNg,
            MaxHourlyTotal = maxHourlyTotal,
            MaxNgRatePercent = maxNgRatePercent,
            ShiftLabel = shiftLabel
        };
    }

    private const double SqrtTwoPi = 2.506628274631000502415765284811;

    /// <summary>
    /// Evaluates the probability density function of the Gaussian normal distribution
    /// f(x) = (1 / (sigma * sqrt(2 * pi))) * exp(-0.5 * ((x - mu) / sigma)^2).
    /// </summary>
    public static double EvaluateGaussianPdf(double x, double mean, double stdDev)
    {
        if (stdDev <= 1e-12 || !double.IsFinite(x) || !double.IsFinite(mean) || !double.IsFinite(stdDev))
        {
            return 0.0;
        }

        double z = (x - mean) / stdDev;
        return Math.Exp(-0.5 * z * z) / (stdDev * SqrtTwoPi);
    }

    /// <summary>
    /// Resolves canonical optical parameter name and any alternate MES log alias.
    /// </summary>
    public static string CanonicalizeSpcParameterName(string? parameterName, out string alias)
    {
        ReadOnlySpan<char> trimmed = parameterName is null ? default : parameterName.AsSpan().Trim();
        if (trimmed.IsEmpty ||
            EqualsIgnoringSeparators(trimmed, "whitelv") ||
            EqualsIgnoringSeparators(trimmed, "lv"))
        {
            alias = "whitelv";
            return "White_lv";
        }

        if (EqualsIgnoringSeparators(trimmed, "contrast") ||
            EqualsIgnoringSeparators(trimmed, "cr"))
        {
            alias = string.Empty;
            return "contrast";
        }

        if (EqualsIgnoringSeparators(trimmed, "ntsc"))
        {
            alias = string.Empty;
            return "ntsc";
        }

        if (EqualsIgnoringSeparators(trimmed, "cct") ||
            EqualsIgnoringSeparators(trimmed, "colortemp") ||
            EqualsIgnoringSeparators(trimmed, "tc"))
        {
            alias = "colortemp";
            return "cct";
        }

        if (EqualsIgnoringSeparators(trimmed, "flk") ||
            EqualsIgnoringSeparators(trimmed, "flicker"))
        {
            alias = "flicker";
            return "flk";
        }

        if (EqualsIgnoringSeparators(trimmed, "whitex"))
        {
            alias = "white_x";
            return "whitex";
        }

        if (EqualsIgnoringSeparators(trimmed, "whitey"))
        {
            alias = "white_y";
            return "whitey";
        }

        alias = string.Empty;
        return trimmed.ToString();
    }

    /// <summary>
    /// Resolves default optical specification limits (LSL, USL) when not explicitly overridden by the caller.
    /// </summary>
    public static void ResolveDefaultSpcSpecLimits(
        string canonicalParam,
        ReadOnlySpan<double> values,
        double? explicitLsl,
        double? explicitUsl,
        out double? resolvedLsl,
        out double? resolvedUsl,
        out string specDisplay)
    {
        double? cleanLsl = (explicitLsl.HasValue && double.IsFinite(explicitLsl.Value)) ? explicitLsl.Value : null;
        double? cleanUsl = (explicitUsl.HasValue && double.IsFinite(explicitUsl.Value)) ? explicitUsl.Value : null;

        if (cleanLsl.HasValue || cleanUsl.HasValue)
        {
            resolvedLsl = cleanLsl;
            resolvedUsl = cleanUsl;
            specDisplay = FormatSpcSpecDisplay(cleanLsl, cleanUsl);
            return;
        }

        if (string.Equals(canonicalParam, "White_lv", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 300.0;
            resolvedUsl = null;
            specDisplay = ">= 300.000";
            return;
        }

        if (string.Equals(canonicalParam, "contrast", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 1050.0;
            resolvedUsl = null;
            specDisplay = ">= 1050";
            return;
        }

        if (string.Equals(canonicalParam, "ntsc", StringComparison.OrdinalIgnoreCase))
        {
            bool isPercentScale = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (double.IsFinite(values[i]) && values[i] > 1.5)
                {
                    isPercentScale = true;
                    break;
                }
            }

            resolvedLsl = isPercentScale ? 42.0 : 0.42;
            resolvedUsl = null;
            specDisplay = isPercentScale ? ">= 42%" : ">= 0.42";
            return;
        }

        if (string.Equals(canonicalParam, "cct", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 5500.0;
            resolvedUsl = 8000.0;
            specDisplay = "5500 ~ 8000";
            return;
        }

        if (string.Equals(canonicalParam, "flk", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.0;
            resolvedUsl = 30.0;
            specDisplay = "0.0 ~ 30.0";
            return;
        }

        if (string.Equals(canonicalParam, "whitex", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.288;
            resolvedUsl = 0.338;
            specDisplay = "0.288 ~ 0.338";
            return;
        }

        if (string.Equals(canonicalParam, "whitey", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.304;
            resolvedUsl = 0.354;
            specDisplay = "0.304 ~ 0.354";
            return;
        }

        if (string.Equals(canonicalParam, "redx", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.645;
            resolvedUsl = 0.695;
            specDisplay = "0.645 ~ 0.695";
            return;
        }

        if (string.Equals(canonicalParam, "redy", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.292;
            resolvedUsl = 0.342;
            specDisplay = "0.292 ~ 0.342";
            return;
        }

        if (string.Equals(canonicalParam, "greenx", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.272;
            resolvedUsl = 0.322;
            specDisplay = "0.272 ~ 0.322";
            return;
        }

        if (string.Equals(canonicalParam, "greeny", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.585;
            resolvedUsl = 0.635;
            specDisplay = "0.585 ~ 0.635";
            return;
        }

        if (string.Equals(canonicalParam, "bluex", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.116;
            resolvedUsl = 0.166;
            specDisplay = "0.116 ~ 0.166";
            return;
        }

        if (string.Equals(canonicalParam, "bluey", StringComparison.OrdinalIgnoreCase))
        {
            resolvedLsl = 0.035;
            resolvedUsl = 0.085;
            specDisplay = "0.035 ~ 0.085";
            return;
        }

        resolvedLsl = null;
        resolvedUsl = null;
        specDisplay = "-";
    }

    private static string FormatSpcSpecDisplay(double? lsl, double? usl)
    {
        if (lsl.HasValue && usl.HasValue)
        {
            return $"{lsl.Value.ToString("G6", CultureInfo.InvariantCulture)} ~ {usl.Value.ToString("G6", CultureInfo.InvariantCulture)}";
        }

        if (lsl.HasValue)
        {
            return $">= {lsl.Value.ToString("G6", CultureInfo.InvariantCulture)}";
        }

        if (usl.HasValue)
        {
            return $"<= {usl.Value.ToString("G6", CultureInfo.InvariantCulture)}";
        }

        return "-";
    }

    /// <summary>
    /// Single-pass, zero-allocation Welford online statistical calculation of Mean (mu), StdDev (sigma),
    /// Min, Max, TotalCount, and process capability indices Cp / Cpk over a <see cref="ReadOnlySpan{Double}"/>.
    /// Does not use LINQ or heap allocations.
    /// </summary>
    public SpcMetrics ComputeSpcMetrics(
        ReadOnlySpan<double> values,
        double? lsl = null,
        double? usl = null,
        bool useSampleStdDev = true)
    {
        double? cleanLsl = (lsl.HasValue && double.IsFinite(lsl.Value)) ? lsl.Value : null;
        double? cleanUsl = (usl.HasValue && double.IsFinite(usl.Value)) ? usl.Value : null;

        int count = 0;
        double mean = 0.0;
        double m2 = 0.0;
        double min = double.MaxValue;
        double max = double.MinValue;
        int inSpec = 0;
        int outOfSpec = 0;

        for (int i = 0; i < values.Length; i++)
        {
            double x = values[i];
            if (!double.IsFinite(x))
            {
                continue;
            }

            count++;
            double delta = x - mean;
            mean += delta / count;
            double delta2 = x - mean;
            m2 += delta * delta2;

            if (x < min) min = x;
            if (x > max) max = x;

            bool withinSpec = (!cleanLsl.HasValue || x >= cleanLsl.Value) &&
                              (!cleanUsl.HasValue || x <= cleanUsl.Value);
            if (withinSpec)
            {
                inSpec++;
            }
            else
            {
                outOfSpec++;
            }
        }

        if (count == 0)
        {
            return new SpcMetrics
            {
                Lsl = cleanLsl,
                Usl = cleanUsl
            };
        }

        double stdDev = 0.0;
        if (count > 1)
        {
            int denom = useSampleStdDev ? (count - 1) : count;
            double variance = m2 / denom;
            if (variance > 0.0)
            {
                stdDev = Math.Sqrt(variance);
            }
        }

        double? cp = null;
        double? cpk = null;

        if (stdDev > 1e-12)
        {
            double threeSigma = 3.0 * stdDev;
            if (cleanLsl.HasValue && cleanUsl.HasValue)
            {
                if (cleanUsl.Value > cleanLsl.Value)
                {
                    cp = (cleanUsl.Value - cleanLsl.Value) / (6.0 * stdDev);
                    double cpu = (cleanUsl.Value - mean) / threeSigma;
                    double cpl = (mean - cleanLsl.Value) / threeSigma;
                    cpk = Math.Min(cpu, cpl);
                }
            }
            else if (cleanLsl.HasValue)
            {
                cpk = (mean - cleanLsl.Value) / threeSigma;
            }
            else if (cleanUsl.HasValue)
            {
                cpk = (cleanUsl.Value - mean) / threeSigma;
            }
        }

        return new SpcMetrics
        {
            Mean = mean,
            StdDev = stdDev,
            Min = min,
            Max = max,
            TotalCount = count,
            Lsl = cleanLsl,
            Usl = cleanUsl,
            Cp = cp,
            Cpk = cpk,
            InSpecCount = inSpec,
            OutOfSpecCount = outOfSpec
        };
    }

    /// <summary>
    /// Matches parameter names with case-insensitivity, aliases, and hyphen/underscore normalization.
    /// Zero-allocation parameter name matcher (case-insensitive, ignoring '_' and '-' separators and stackalloc-free).
    /// </summary>
    public static bool IsParamNameMatch(
        ReadOnlySpan<char> key,
        ReadOnlySpan<char> canonicalParam,
        ReadOnlySpan<char> aliasParam)
    {
        if (key.Equals(canonicalParam, StringComparison.OrdinalIgnoreCase) ||
            (!aliasParam.IsEmpty && key.Equals(aliasParam, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (EqualsIgnoringSeparators(key, canonicalParam))
        {
            return true;
        }

        if (!aliasParam.IsEmpty && EqualsIgnoringSeparators(key, aliasParam))
        {
            return true;
        }

        if (EqualsIgnoringSeparators(canonicalParam, "whitelv") &&
            EqualsIgnoringSeparators(key, "lv"))
        {
            return true;
        }

        if (EqualsIgnoringSeparators(canonicalParam, "contrast") &&
            EqualsIgnoringSeparators(key, "cr"))
        {
            return true;
        }

        if (EqualsIgnoringSeparators(canonicalParam, "cct") &&
            (EqualsIgnoringSeparators(key, "tc") ||
             EqualsIgnoringSeparators(key, "colortemp")))
        {
            return true;
        }

        return false;
    }

    private static bool EqualsIgnoringSeparators(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return false;
        }

        int ia = 0;
        int ib = 0;
        while (true)
        {
            while (ia < a.Length && (a[ia] == '_' || a[ia] == '-')) ia++;
            while (ib < b.Length && (b[ib] == '_' || b[ib] == '-')) ib++;

            bool aDone = ia >= a.Length;
            bool bDone = ib >= b.Length;
            if (aDone || bDone)
            {
                return aDone && bDone;
            }

            if (!a.Slice(ia, 1).Equals(b.Slice(ib, 1), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ia++;
            ib++;
        }
    }

    /// <summary>
    /// Parses numeric parameter values with optional trailing unit suffixes (e.g. 320.5nit, 6500K, 1280:1, 2.4dB, cd/m2).
    /// Zero heap allocations using Span scanner.
    /// </summary>
    public static bool TryParseOpticalNumericValue(
        ReadOnlySpan<char> rawSpan,
        bool requirePositive,
        out double value)
    {
        value = 0.0;
        ReadOnlySpan<char> valSpan = rawSpan.Trim("[] \t\r\n");
        if (valSpan.IsEmpty)
        {
            return false;
        }

        if (valSpan.EndsWith('%'))
        {
            valSpan = valSpan[..^1].Trim();
        }

        if (double.TryParse(valSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed) &&
            (!requirePositive || parsed > 0.0))
        {
            value = parsed;
            return true;
        }

        // Fallback: strip trailing optical engineering units
        int numLen = 0;
        while (numLen < valSpan.Length)
        {
            char c = valSpan[numLen];
            if (char.IsAsciiDigit(c) || c == '.' || c == '+' || c == '-' || c == 'e' || c == 'E')
            {
                numLen++;
            }
            else
            {
                break;
            }
        }

        if (numLen > 0 && numLen < valSpan.Length)
        {
            ReadOnlySpan<char> candidate = valSpan[..numLen].Trim();
            if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out double unitStripped) &&
                double.IsFinite(unitStripped) &&
                (!requirePositive || unitStripped > 0.0))
            {
                value = unitStripped;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Zero-allocation span scanner extracting a numeric parameter value from an MES OTPDEFECTVALUE string.
    /// Supports both '=' and ':' key-value separators and optical engineering units.
    /// </summary>
    public static bool TryExtractParameterFromDefectValue(
        ReadOnlySpan<char> rawText,
        ReadOnlySpan<char> canonicalParam,
        ReadOnlySpan<char> aliasParam,
        bool requirePositive,
        out double value)
    {
        value = 0.0;
        ReadOnlySpan<char> span = rawText.Trim();
        if (span.IsEmpty)
        {
            return false;
        }

        int openTag = span.IndexOf("<OTPDEFECTVALUE>", StringComparison.OrdinalIgnoreCase);
        if (openTag >= 0)
        {
            span = span[(openTag + "<OTPDEFECTVALUE>".Length)..];
            int closeTag = span.IndexOf("</OTPDEFECTVALUE>", StringComparison.OrdinalIgnoreCase);
            if (closeTag >= 0)
            {
                span = span[..closeTag];
            }
            span = span.Trim();
        }

        span = span.Trim("[]").Trim();
        if (span.IsEmpty)
        {
            return false;
        }

        while (!span.IsEmpty)
        {
            int sepIdx = span.IndexOfAny(DefectParamSeparators);
            ReadOnlySpan<char> token;
            if (sepIdx < 0)
            {
                token = span;
                span = default;
            }
            else
            {
                token = span[..sepIdx];
                span = span[(sepIdx + 1)..];
            }

            token = TrimDefectTokenSpan(token);
            if (token.IsEmpty)
            {
                continue;
            }

            int eqIdx = token.IndexOfAny(DefectKeyValueDelimiters);
            if (eqIdx <= 0)
            {
                continue;
            }

            ReadOnlySpan<char> key = TrimDefectTokenSpan(token[..eqIdx]);
            if (!IsParamNameMatch(key, canonicalParam, aliasParam))
            {
                continue;
            }

            ReadOnlySpan<char> valSpan = token[(eqIdx + 1)..].Trim();
            if (TryParseOpticalNumericValue(valSpan, requirePositive, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> TrimDefectTokenSpan(ReadOnlySpan<char> span)
    {
        int start = span.IndexOfAnyExcept(DefectTokenTrimChars);
        if (start < 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        int end = span.LastIndexOfAnyExcept(DefectTokenTrimChars);
        return span[start..(end + 1)];
    }

    public double[] ExtractMesParameterValues(
        IReadOnlyList<MesPanelDetailRecord> mesRecords,
        string parameterName)
    {
        if (mesRecords is null || mesRecords.Count == 0)
        {
            return [];
        }

        string canonical = CanonicalizeSpcParameterName(parameterName, out string alias);
        ReadOnlySpan<char> canonicalSpan = canonical.AsSpan();
        ReadOnlySpan<char> aliasSpan = alias.AsSpan();
        bool requirePositive = string.Equals(canonical, "contrast", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(canonical, "ntsc", StringComparison.OrdinalIgnoreCase);

        double[] buffer = ArrayPool<double>.Shared.Rent(mesRecords.Count);
        int count = 0;
        try
        {
            for (int i = 0; i < mesRecords.Count; i++)
            {
                var record = mesRecords[i];
                if (record is null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(record.OtpDefectValue) &&
                    TryExtractParameterFromDefectValue(record.OtpDefectValue.AsSpan(), canonicalSpan, aliasSpan, requirePositive, out double extracted))
                {
                    buffer[count++] = extracted;
                    continue;
                }

                var parameters = record.Parameters;
                if (parameters is { Count: > 0 })
                {
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        var row = parameters[p];
                        if (row is null || string.IsNullOrEmpty(row.ParamName) || string.IsNullOrEmpty(row.ParamValue))
                        {
                            continue;
                        }

                        ReadOnlySpan<char> pName = row.ParamName.AsSpan().Trim();
                        if (!IsParamNameMatch(pName, canonicalSpan, aliasSpan))
                        {
                            continue;
                        }

                        ReadOnlySpan<char> pVal = row.ParamValue.AsSpan();
                        if (TryParseOpticalNumericValue(pVal, requirePositive, out double rowVal))
                        {
                            buffer[count++] = rowVal;
                            break;
                        }
                    }
                }
            }

            if (count == 0)
            {
                return [];
            }

            var result = new double[count];
            buffer.AsSpan(0, count).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }
    }

    public double[] ExtractMesParameterValues(
        IReadOnlyList<string> otpDefectValues,
        string parameterName)
    {
        if (otpDefectValues is null || otpDefectValues.Count == 0)
        {
            return [];
        }

        string canonical = CanonicalizeSpcParameterName(parameterName, out string alias);
        ReadOnlySpan<char> canonicalSpan = canonical.AsSpan();
        ReadOnlySpan<char> aliasSpan = alias.AsSpan();
        bool requirePositive = string.Equals(canonical, "contrast", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(canonical, "ntsc", StringComparison.OrdinalIgnoreCase);

        double[] buffer = ArrayPool<double>.Shared.Rent(otpDefectValues.Count);
        int count = 0;
        try
        {
            for (int i = 0; i < otpDefectValues.Count; i++)
            {
                string? raw = otpDefectValues[i];
                if (!string.IsNullOrEmpty(raw) &&
                    TryExtractParameterFromDefectValue(raw.AsSpan(), canonicalSpan, aliasSpan, requirePositive, out double val))
                {
                    buffer[count++] = val;
                }
            }

            if (count == 0)
            {
                return [];
            }

            var result = new double[count];
            buffer.AsSpan(0, count).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }
    }

    public SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<double> values,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0)
    {
        if (values is null || values.Count == 0)
        {
            return GetSpcHistogramData(ReadOnlySpan<double>.Empty, parameterName, lsl, usl, binCount);
        }

        if (values is double[] arr)
        {
            return GetSpcHistogramData(arr.AsSpan(), parameterName, lsl, usl, binCount);
        }

        if (values is List<double> list)
        {
            return GetSpcHistogramData(CollectionsMarshal.AsSpan(list), parameterName, lsl, usl, binCount);
        }

        double[] rented = ArrayPool<double>.Shared.Rent(values.Count);
        try
        {
            for (int i = 0; i < values.Count; i++)
            {
                rented[i] = values[i];
            }
            return GetSpcHistogramData(rented.AsSpan(0, values.Count), parameterName, lsl, usl, binCount);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    public SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<MesPanelDetailRecord> mesRecords,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0)
    {
        double[] values = ExtractMesParameterValues(mesRecords, parameterName);
        return GetSpcHistogramData(values.AsSpan(), parameterName, lsl, usl, binCount);
    }

    public SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<UnitRecord> units,
        string parameterName = "White_lv",
        IReadOnlyList<MesPanelDetailRecord>? mesRecords = null,
        double? lsl = null,
        double? usl = null,
        int binCount = 0)
    {
        if (mesRecords is { Count: > 0 })
        {
            if (units is { Count: > 0 })
            {
                var panelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u is not null && !string.IsNullOrWhiteSpace(u.PanelName))
                    {
                        panelSet.Add(u.PanelName.Trim());
                    }
                }

                if (panelSet.Count > 0)
                {
                    var matchedRecords = new List<MesPanelDetailRecord>(mesRecords.Count);
                    for (int i = 0; i < mesRecords.Count; i++)
                    {
                        var r = mesRecords[i];
                        if (r is not null && !string.IsNullOrWhiteSpace(r.PanelName) && panelSet.Contains(r.PanelName.Trim()))
                        {
                            matchedRecords.Add(r);
                        }
                    }

                    if (matchedRecords.Count > 0)
                    {
                        return GetSpcHistogramData(matchedRecords, parameterName, lsl, usl, binCount);
                    }
                }
            }
            else
            {
                return GetSpcHistogramData(mesRecords, parameterName, lsl, usl, binCount);
            }
        }

        if (units is null || units.Count == 0)
        {
            return GetSpcHistogramData(ReadOnlySpan<double>.Empty, parameterName, lsl, usl, binCount);
        }

        string canonical = CanonicalizeSpcParameterName(parameterName, out string alias);
        ReadOnlySpan<char> canonicalSpan = canonical.AsSpan();
        ReadOnlySpan<char> aliasSpan = alias.AsSpan();
        bool requirePositive = string.Equals(canonical, "contrast", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(canonical, "ntsc", StringComparison.OrdinalIgnoreCase);

        double[] buffer = ArrayPool<double>.Shared.Rent(units.Count);
        int count = 0;
        try
        {
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u is null || string.IsNullOrEmpty(u.MesResult))
                {
                    continue;
                }

                if (TryExtractParameterFromDefectValue(u.MesResult.AsSpan(), canonicalSpan, aliasSpan, requirePositive, out double val))
                {
                    buffer[count++] = val;
                }
            }

            return GetSpcHistogramData(buffer.AsSpan(0, count), parameterName, lsl, usl, binCount);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }
    }

    public SpcHistogramData GetSpcHistogramData(
        ReadOnlySpan<double> values,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0)
    {
        string canonical = CanonicalizeSpcParameterName(parameterName, out _);
        ResolveDefaultSpcSpecLimits(canonical, values, lsl, usl, out double? resolvedLsl, out double? resolvedUsl, out string specDisplay);

        SpcMetrics metrics = ComputeSpcMetrics(values, resolvedLsl, resolvedUsl, useSampleStdDev: true);
        if (metrics.TotalCount == 0)
        {
            return new SpcHistogramData
            {
                ParameterName = canonical,
                SpecDisplay = specDisplay,
                Metrics = metrics
            };
        }

        // Auto-select between 20 and 30 frequency bins when binCount <= 0
        int targetBins = binCount > 0
            ? Math.Clamp(binCount, 1, 200)
            : Math.Clamp((int)Math.Ceiling(Math.Sqrt(metrics.TotalCount)), 20, 30);

        double min = metrics.Min;
        double max = metrics.Max;
        double dataSpan = max - min;

        double histMin;
        double histMax;
        if (dataSpan <= 1e-12)
        {
            double halfPad = Math.Max(Math.Abs(min) * 0.02, 0.5);
            histMin = min - halfPad;
            histMax = max + halfPad;
        }
        else
        {
            histMin = min;
            histMax = max;
        }

        double binWidth = (histMax - histMin) / targetBins;
        if (binWidth <= 1e-12)
        {
            binWidth = 1.0;
        }

        int[]? rentedCounts = targetBins > 128 ? ArrayPool<int>.Shared.Rent(targetBins) : null;
        int[]? rentedOutCounts = targetBins > 128 ? ArrayPool<int>.Shared.Rent(targetBins) : null;
        Span<int> binCounts = rentedCounts is not null ? rentedCounts.AsSpan(0, targetBins) : stackalloc int[targetBins];
        Span<int> binOutCounts = rentedOutCounts is not null ? rentedOutCounts.AsSpan(0, targetBins) : stackalloc int[targetBins];
        binCounts.Clear();
        binOutCounts.Clear();

        try
        {
            if (dataSpan <= 1e-12)
            {
                int midBin = targetBins / 2;
                binCounts[midBin] = metrics.TotalCount;
                binOutCounts[midBin] = metrics.OutOfSpecCount;
            }
            else
            {
                double invBinWidth = 1.0 / binWidth;
                for (int i = 0; i < values.Length; i++)
                {
                    double x = values[i];
                    if (!double.IsFinite(x))
                    {
                        continue;
                    }

                    int idx = (int)((x - histMin) * invBinWidth);
                    if (idx < 0)
                    {
                        idx = 0;
                    }
                    else if (idx >= targetBins)
                    {
                        idx = targetBins - 1;
                    }

                    binCounts[idx]++;
                    bool inSpec = (!resolvedLsl.HasValue || x >= resolvedLsl.Value) &&
                                  (!resolvedUsl.HasValue || x <= resolvedUsl.Value);
                    if (!inSpec)
                    {
                        binOutCounts[idx]++;
                    }
                }
            }

            string numFormat = DetermineValueFormat(canonical, histMax - histMin);
            var bins = new List<HistogramBin>(targetBins);
            int maxBinCount = 0;
            double invTotalPct = 100.0 / metrics.TotalCount;

            for (int b = 0; b < targetBins; b++)
            {
                double startVal = histMin + (b * binWidth);
                double endVal = (b == targetBins - 1) ? histMax : (histMin + ((b + 1) * binWidth));
                double centerVal = (startVal + endVal) * 0.5;
                int cnt = binCounts[b];
                if (cnt > maxBinCount)
                {
                    maxBinCount = cnt;
                }

                bool binWithinSpec = cnt > 0
                    ? (binOutCounts[b] == 0)
                    : ((!resolvedLsl.HasValue || startVal >= resolvedLsl.Value) &&
                       (!resolvedUsl.HasValue || endVal <= resolvedUsl.Value));

                string rangeLabel = $"{startVal.ToString(numFormat, CultureInfo.InvariantCulture)} ~ {endVal.ToString(numFormat, CultureInfo.InvariantCulture)}";

                bins.Add(new HistogramBin
                {
                    BinIndex = b,
                    StartValue = startVal,
                    EndValue = endVal,
                    CenterValue = centerVal,
                    Count = cnt,
                    Percentage = Math.Round(cnt * invTotalPct, 2),
                    IsWithinSpec = binWithinSpec,
                    RangeLabel = rangeLabel
                });
            }

            // Compute X plot range covering histogram bins, Gaussian +/- 3.2 sigma, and LSL/USL limits
            double rawPlotMin = histMin;
            double rawPlotMax = histMax;
            if (metrics.StdDev > 1e-12)
            {
                rawPlotMin = Math.Min(rawPlotMin, metrics.Mean - (3.2 * metrics.StdDev));
                rawPlotMax = Math.Max(rawPlotMax, metrics.Mean + (3.2 * metrics.StdDev));
            }

            if (resolvedLsl.HasValue)
            {
                rawPlotMin = Math.Min(rawPlotMin, resolvedLsl.Value);
                rawPlotMax = Math.Max(rawPlotMax, resolvedLsl.Value);
            }

            if (resolvedUsl.HasValue)
            {
                rawPlotMin = Math.Min(rawPlotMin, resolvedUsl.Value);
                rawPlotMax = Math.Max(rawPlotMax, resolvedUsl.Value);
            }

            double rawSpan = Math.Max(1e-6, rawPlotMax - rawPlotMin);
            double plotMinX = rawPlotMin - (rawSpan * 0.04);
            double plotMaxX = rawPlotMax + (rawSpan * 0.04);

            // Compute smooth Gaussian bell curve coordinate points across [plotMinX, plotMaxX]
            var gaussianPoints = new List<GaussianCurvePoint>();
            double maxGaussScaled = 0.0;

            if (metrics.StdDev > 1e-12)
            {
                const int curveSamples = 80;
                double curveSpan = plotMaxX - plotMinX;
                double stepX = curveSpan / curveSamples;
                double scaleFactor = metrics.TotalCount * binWidth;
                bool insertedMean = false;

                for (int s = 0; s <= curveSamples; s++)
                {
                    double x = plotMinX + (s * stepX);
                    if (!insertedMean && x > metrics.Mean)
                    {
                        double meanPdf = EvaluateGaussianPdf(metrics.Mean, metrics.Mean, metrics.StdDev);
                        double meanScaled = meanPdf * scaleFactor;
                        if (meanScaled > maxGaussScaled) maxGaussScaled = meanScaled;
                        gaussianPoints.Add(new GaussianCurvePoint(metrics.Mean, meanPdf, meanScaled));
                        insertedMean = true;
                    }

                    if (Math.Abs(x - metrics.Mean) <= stepX * 0.01)
                    {
                        insertedMean = true;
                    }

                    double pdf = EvaluateGaussianPdf(x, metrics.Mean, metrics.StdDev);
                    double scaled = pdf * scaleFactor;
                    if (scaled > maxGaussScaled)
                    {
                        maxGaussScaled = scaled;
                    }

                    gaussianPoints.Add(new GaussianCurvePoint(x, pdf, scaled));
                }
            }

            return new SpcHistogramData
            {
                ParameterName = canonical,
                SpecDisplay = specDisplay,
                Metrics = metrics,
                Bins = bins,
                GaussianPoints = gaussianPoints,
                MaxBinCount = maxBinCount,
                MaxGaussianScaledCount = maxGaussScaled,
                BinWidth = binWidth,
                PlotMinX = plotMinX,
                PlotMaxX = plotMaxX
            };
        }
        finally
        {
            if (rentedCounts is not null) ArrayPool<int>.Shared.Return(rentedCounts);
            if (rentedOutCounts is not null) ArrayPool<int>.Shared.Return(rentedOutCounts);
        }
    }

    private static string DetermineValueFormat(string canonicalParam, double span)
    {
        if (string.Equals(canonicalParam, "whitex", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "whitey", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "redx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "redy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "greenx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "greeny", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "bluex", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalParam, "bluey", StringComparison.OrdinalIgnoreCase))
        {
            return "F4";
        }

        if (span < 0.1) return "F4";
        if (span < 2.0) return "F3";
        if (span < 50.0) return "F2";
        if (span < 500.0) return "F1";
        return "F0";
    }
}

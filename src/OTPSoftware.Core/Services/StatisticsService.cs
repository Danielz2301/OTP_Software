namespace OTPSoftware.Core.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

/// <summary>
/// Calculation engine for KPI statistics, PMNT fixture defect rankings, and cumulative error chart data.
/// </summary>
public sealed class StatisticsService : IStatisticsService
{
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

    private static bool MatchesPmntGroupFilter(string pmnt, PmntGroupFilter groupFilter) =>
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

        var rawNgUnits = new List<UnitRecord>();

        foreach (var u in units)
        {
            if (u == null)
            {
                continue;
            }

            if (!u.IsNg)
            {
                continue;
            }

            if (u.Timestamp == null)
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

            rawNgUnits.Add(u);
        }

        if (rawNgUnits.Count == 0)
        {
            return new ErrorChartData();
        }

        // Stable chronological sort by Timestamp ticks + index tie-breaker
        var sortedUnits = rawNgUnits
            .Select((u, index) => (Unit: u, Index: index))
            .OrderBy(x => x.Unit.Timestamp!.Value.Ticks)
            .ThenBy(x => x.Index)
            .Select(x => x.Unit)
            .ToList();

        // First pass: count totals per PMNT
        var totalsByPmnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in sortedUnits)
        {
            string p = string.IsNullOrWhiteSpace(u.Pmnt) ? "UNKNOWN" : u.Pmnt.Trim();
            totalsByPmnt[p] = totalsByPmnt.GetValueOrDefault(p) + 1;
        }

        // Second pass: build cumulative points and per-PMNT series
        var runningCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seriesPointsMap = new Dictionary<string, List<ErrorChartPoint>>(StringComparer.OrdinalIgnoreCase);
        var seriesGroupMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pointsList = new List<ErrorChartPoint>(sortedUnits.Count);

        DateTime? minTime = null;
        DateTime? maxTime = null;
        int maxErrCount = 0;

        foreach (var u in sortedUnits)
        {
            string pmnt = string.IsNullOrWhiteSpace(u.Pmnt) ? "UNKNOWN" : u.Pmnt.Trim();

            int cumCount = runningCounts.GetValueOrDefault(pmnt) + 1;
            runningCounts[pmnt] = cumCount;

            int totalForPmnt = totalsByPmnt[pmnt];
            if (cumCount > maxErrCount)
            {
                maxErrCount = cumCount;
            }

            DateTime ts = u.Timestamp!.Value;
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
}

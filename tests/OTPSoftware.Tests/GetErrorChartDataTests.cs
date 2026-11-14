namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using OTPSoftware.Core.Extensions;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public class GetErrorChartDataTests
{
    private readonly StatisticsService _service = new();

    private static UnitRecord CreateUnit(
        int index = 0,
        string pmnt = "PMNT1001A",
        DateTime? timestamp = null,
        IReadOnlyList<string>? errors = null,
        string panelName = "TL6869",
        string source = "MMAPI120")
    {
        return new UnitRecord
        {
            Index = index,
            Pmnt = pmnt,
            Timestamp = timestamp ?? new DateTime(2026, 11, 8, 9, 0, 0),
            Errors = errors ?? [],
            PanelName = panelName,
            BluName = "BLU001",
            Source = source
        };
    }

    [Fact]
    public void ReturnsEmptyResultWhenNoNgDefects()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: [])
        };

        var res = _service.GetErrorChartData(units);
        Assert.Equal(0, res.TotalErrors);
        Assert.Equal(0, res.UniquePmntCount);
        Assert.Equal(0, res.MaxErrorCount);
        Assert.Empty(res.Points);
        Assert.Empty(res.Series);
    }

    [Fact]
    public void CalculatesCumulativeDefectCountsOverTimeForEachPmnt()
    {
        var t0 = new DateTime(2026, 11, 8, 8, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(5), errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(10), errors: ["FLICKER"]),
            CreateUnit(index: 3, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(15), errors: ["POWERON"]),
            CreateUnit(index: 4, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(20), errors: ["XYLV"])
        };

        var res = _service.GetErrorChartData(units);
        Assert.Equal(4, res.TotalErrors);
        Assert.Equal(2, res.UniquePmntCount);
        Assert.Equal(3, res.MaxErrorCount);
        Assert.Equal("PMNT1001A", res.TopPmnt);
        Assert.Equal(3, res.TopPmntCount);

        Assert.Equal("PMNT1001A", res.Points[0].Pmnt);
        Assert.Equal(1, res.Points[0].ErrorCount);
        Assert.Equal("PMNT1002B", res.Points[1].Pmnt);
        Assert.Equal(1, res.Points[1].ErrorCount);
        Assert.Equal("PMNT1001A", res.Points[2].Pmnt);
        Assert.Equal(2, res.Points[2].ErrorCount);
        Assert.Equal("PMNT1001A", res.Points[3].Pmnt);
        Assert.Equal(3, res.Points[3].ErrorCount);
        Assert.True(res.Points[3].IsPeakForPmnt);

        // Verify Series sorting: descending by TotalErrors
        Assert.Equal(2, res.Series.Count);
        Assert.Equal("PMNT1001A", res.Series[0].Pmnt);
        Assert.Equal(3, res.Series[0].TotalErrors);
        Assert.Equal("PMNT1002B", res.Series[1].Pmnt);
        Assert.Equal(1, res.Series[1].TotalErrors);
    }

    [Fact]
    public void FiltersCorrectlyByPmntGroupAndDefectType()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(1), errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(2), errors: ["FLICKER"]),
            CreateUnit(index: 3, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(3), errors: ["XYLV"])
        };

        var onlyA = _service.GetErrorChartData(units, PmntGroupFilter.A);
        Assert.Equal(1, onlyA.TotalErrors);
        Assert.Equal("PMNT1001A", onlyA.Points[0].Pmnt);

        var onlyB = _service.GetErrorChartData(units, PmntGroupFilter.B);
        Assert.Equal(2, onlyB.TotalErrors);
        Assert.Equal(2, onlyB.MaxErrorCount);

        var onlyFlicker = _service.GetErrorChartData(units, defectFilter: DefectFilterType.Flicker);
        Assert.Equal(1, onlyFlicker.TotalErrors);
        Assert.Equal("PMNT1002B", onlyFlicker.Points[0].Pmnt);
        Assert.Equal(1, onlyFlicker.Points[0].ErrorCount);
    }

    [Fact]
    public void DefaultsSourceToMmapi120WhenEmpty()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0, errors: ["XYLV"], source: "")
        };

        var res = _service.GetErrorChartData(units);
        Assert.Single(res.Points);
        Assert.Equal("MMAPI120", res.Points[0].Source);
        Assert.Equal(t0.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), res.Points[0].TimeFormatted);
        Assert.Equal(t0.ToString("HH:mm:ss", CultureInfo.InvariantCulture), res.Points[0].ShortTime);
    }

    [Fact]
    public void ExtensionMethod_BuildErrorChartDataProducesMatchingResult()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0, errors: ["XYLV"])
        };

        var res = units.BuildErrorChartData();
        Assert.Equal(1, res.TotalErrors);
        Assert.Equal("PMNT1001A", res.Points[0].Pmnt);
    }

    [Fact]
    public void GetParetoChartData_ReturnsEmptyResult_WhenNoNgDefects()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: [])
        };

        var res = _service.GetParetoChartData(units);
        Assert.Equal(0, res.TotalDefects);
        Assert.Equal(0, res.MaxCount);
        Assert.Equal(string.Empty, res.TopLabel);
        Assert.Equal(0, res.VitalFewCount);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void GetParetoChartData_CalculatesCorrectPercentages_AndVitalFewCount()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            // PMNT1001A: 5 errors (50%)
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(1), errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(2), errors: ["XYLV"]),
            CreateUnit(index: 3, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(3), errors: ["POWERON"]),
            CreateUnit(index: 4, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(4), errors: ["POWEROFF"]),
            CreateUnit(index: 5, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(5), errors: ["FLICKER"]),
            // PMNT1002B: 3 errors (30%)
            CreateUnit(index: 6, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(6), errors: ["XYLV"]),
            CreateUnit(index: 7, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(7), errors: ["XYLV"]),
            CreateUnit(index: 8, pmnt: "PMNT1002B", timestamp: t0.AddMinutes(8), errors: ["POWERON"]),
            // PMNT1003A: 2 errors (20%)
            CreateUnit(index: 9, pmnt: "PMNT1003A", timestamp: t0.AddMinutes(9), errors: ["FLICKER"]),
            CreateUnit(index: 10, pmnt: "PMNT1003A", timestamp: t0.AddMinutes(10), errors: ["FLICKER"]),
            // OK unit (should be excluded)
            CreateUnit(index: 11, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(11), errors: [])
        };

        var res = _service.GetParetoChartData(units);

        Assert.Equal(10, res.TotalDefects);
        Assert.Equal(5, res.MaxCount);
        Assert.Equal("PMNT1001A", res.TopLabel);
        Assert.Equal(2, res.VitalFewCount); // 50% + 30% = 80% reached at 2nd item
        Assert.Equal(3, res.Items.Count);

        // 1st: PMNT1001A (50%)
        Assert.Equal("PMNT1001A", res.Items[0].Label);
        Assert.Equal("A", res.Items[0].Group);
        Assert.Equal(5, res.Items[0].Count);
        Assert.Equal(50.0, res.Items[0].Percentage);
        Assert.Equal(50.0, res.Items[0].CumulativePercentage);

        // 2nd: PMNT1002B (30%)
        Assert.Equal("PMNT1002B", res.Items[1].Label);
        Assert.Equal("B", res.Items[1].Group);
        Assert.Equal(3, res.Items[1].Count);
        Assert.Equal(30.0, res.Items[1].Percentage);
        Assert.Equal(80.0, res.Items[1].CumulativePercentage);

        // 3rd: PMNT1003A (20%)
        Assert.Equal("PMNT1003A", res.Items[2].Label);
        Assert.Equal("A", res.Items[2].Group);
        Assert.Equal(2, res.Items[2].Count);
        Assert.Equal(20.0, res.Items[2].Percentage);
        Assert.Equal(100.0, res.Items[2].CumulativePercentage);
    }

    [Fact]
    public void GetParetoChartData_RespectsGroupFilterDefectFilterAndTopN()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0, errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1002B", timestamp: t0, errors: ["FLICKER"]),
            CreateUnit(index: 3, pmnt: "PMNT1003A", timestamp: t0, errors: ["XYLV"])
        };

        // Group A filter
        var onlyA = _service.GetParetoChartData(units, groupFilter: PmntGroupFilter.A);
        Assert.Equal(2, onlyA.TotalDefects);
        Assert.Equal(2, onlyA.Items.Count);
        Assert.All(onlyA.Items, item => Assert.Equal("A", item.Group));

        // Defect type filter
        var onlyFlicker = _service.GetParetoChartData(units, defectFilter: DefectFilterType.Flicker);
        Assert.Equal(1, onlyFlicker.TotalDefects);
        Assert.Single(onlyFlicker.Items);
        Assert.Equal("PMNT1002B", onlyFlicker.Items[0].Label);

        // TopN limit
        var top1 = _service.GetParetoChartData(units, topN: 1);
        Assert.Equal(3, top1.TotalDefects);
        Assert.Single(top1.Items);
    }

    [Fact]
    public void GetHourlyTrendData_CalculatesThroughputBucketsAndShiftLabel()
    {
        var dayDate = new DateTime(2026, 11, 8);
        var units = new List<UnitRecord>
        {
            // Hour 08:00 - 2 OK, 1 NG (XYLV)
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: dayDate.AddHours(8).AddMinutes(10), errors: []),
            CreateUnit(index: 2, pmnt: "PMNT1001A", timestamp: dayDate.AddHours(8).AddMinutes(20), errors: []),
            CreateUnit(index: 3, pmnt: "PMNT1002B", timestamp: dayDate.AddHours(8).AddMinutes(30), errors: ["XYLV"]),

            // Hour 09:00 - 1 OK, 1 NG (FLICKER)
            CreateUnit(index: 4, pmnt: "PMNT1001A", timestamp: dayDate.AddHours(9).AddMinutes(15), errors: []),
            CreateUnit(index: 5, pmnt: "PMNT1002B", timestamp: dayDate.AddHours(9).AddMinutes(45), errors: ["FLICKER"])
        };

        var res = _service.GetHourlyTrendData(units, dayShiftStartHour: 8, nightShiftStartHour: 20);

        Assert.Equal(5, res.TotalUnits);
        Assert.Equal(3, res.TotalOk);
        Assert.Equal(2, res.TotalNg);
        Assert.Equal(3, res.MaxHourlyTotal);
        Assert.Equal(50.0, res.MaxNgRatePercent);
        Assert.Equal("DAY SHIFT", res.ShiftLabel);
        Assert.Equal(2, res.Buckets.Count);

        // Bucket 0: 08:00
        var b0 = res.Buckets[0];
        Assert.Equal("08:00", b0.HourLabel);
        Assert.Equal(3, b0.TotalCount);
        Assert.Equal(2, b0.OkCount);
        Assert.Equal(1, b0.NgCount);
        Assert.Equal(33.33, b0.NgRatePercent);
        Assert.Equal(66.67, b0.FpyPercent);

        // Bucket 1: 09:00
        var b1 = res.Buckets[1];
        Assert.Equal("09:00", b1.HourLabel);
        Assert.Equal(2, b1.TotalCount);
        Assert.Equal(1, b1.OkCount);
        Assert.Equal(1, b1.NgCount);
        Assert.Equal(50.0, b1.NgRatePercent);
        Assert.Equal(50.0, b1.FpyPercent);
    }

    [Fact]
    public void GetHourlyTrendData_DetectsNightShiftCorrectly()
    {
        var nightTime = new DateTime(2026, 11, 8, 22, 15, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: nightTime, errors: [])
        };

        var res = _service.GetHourlyTrendData(units, dayShiftStartHour: 8, nightShiftStartHour: 20);
        Assert.Equal("NIGHT SHIFT", res.ShiftLabel);
        Assert.Single(res.Buckets);
        Assert.Equal("22:00", res.Buckets[0].HourLabel);
    }

    [Fact]
    public void ExtensionMethods_BuildParetoAndHourlyTrend_ProduceMatchingResults()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 30, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", timestamp: t0, errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1002B", timestamp: t0, errors: [])
        };

        var pareto = units.BuildParetoChartData();
        Assert.Equal(1, pareto.TotalDefects);
        Assert.Single(pareto.Items);
        Assert.Equal("PMNT1001A", pareto.TopLabel);

        var hourly = units.BuildHourlyTrendData();
        Assert.Equal(2, hourly.TotalUnits);
        Assert.Single(hourly.Buckets);
        Assert.Equal("09:00", hourly.Buckets[0].HourLabel);
    }

    [Fact]
    public void GetErrorChartData_MatchesTopPmntWithFirstSeriesOnTie()
    {
        var t0 = new DateTime(2026, 11, 8, 9, 0, 0);
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT2002B", timestamp: t0, errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1001A", timestamp: t0.AddMinutes(1), errors: ["FLICKER"])
        };

        var res = _service.GetErrorChartData(units);
        Assert.Equal(2, res.Series.Count);
        Assert.Equal(res.Series[0].Pmnt, res.TopPmnt);
        Assert.Equal(1, res.TopPmntCount);
    }
}

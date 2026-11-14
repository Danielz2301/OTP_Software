namespace OTPSoftware.Tests;

using System.Collections.Generic;
using OTPSoftware.Core.Extensions;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public class GetUnitStatisticsTests
{
    private readonly StatisticsService _service = new();

    private static UnitRecord CreateUnit(
        int index = 0,
        string pmnt = "PMNT1001A",
        IReadOnlyList<string>? errors = null)
    {
        return new UnitRecord
        {
            Index = index,
            Pmnt = pmnt,
            Errors = errors ?? []
        };
    }

    [Fact]
    public void EmptyList_ReturnsAllZeroes()
    {
        var s = _service.GetUnitStatistics([]);
        Assert.Equal(0, s.TotalScope);
        Assert.Equal(0, s.DisplayTotal);
        Assert.Equal(0, s.OkCount);
        Assert.Equal(0, s.NgCount);
        Assert.Equal(0.0, s.NgRate);
        Assert.Empty(s.PmntA);
        Assert.Empty(s.PmntB);
        Assert.Empty(s.AlarmPmnts);
    }

    [Fact]
    public void CountsOkNgDefectRateAndFourDefectTypesAccurately()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: []),
            CreateUnit(index: 2, pmnt: "PMNT1001A", errors: ["XYLV", "POWERON"]),
            CreateUnit(index: 3, pmnt: "PMNT1002B", errors: ["POWEROFF"]),
            CreateUnit(index: 4, pmnt: "PMNT1002B", errors: ["FLICKER"])
        };

        var s = _service.GetUnitStatistics(units, UnitStatusFilter.All);
        Assert.Equal(4, s.TotalScope);
        Assert.Equal(4, s.DisplayTotal);
        Assert.Equal(1, s.OkCount);
        Assert.Equal(3, s.NgCount);
        Assert.Equal(75.0, s.NgRate);
        Assert.Equal(1, s.Xylv);
        Assert.Equal(1, s.PowerOn);
        Assert.Equal(1, s.PowerOff);
        Assert.Equal(1, s.Flicker);
        Assert.Single(s.PmntA);
        Assert.Single(s.PmntB);
        Assert.Equal(2, s.PmntB[0].Count);
        Assert.Equal(2, s.PmntB[0].MaxCount);
        Assert.Equal(2, s.PmntBErrorSum);
    }

    [Fact]
    public void SortsFaultyPmntListByMostRecentAppearanceMaxIndex()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1002A", errors: ["XYLV"]),
            CreateUnit(index: 5, pmnt: "PMNT1001A", errors: ["POWERON"])
        };

        var s = _service.GetUnitStatistics(units);
        Assert.Equal(2, s.PmntA.Count);
        Assert.Equal("PMNT1002A", s.PmntA[0].Name);
        Assert.Equal("PMNT1001A", s.PmntA[1].Name);
        Assert.Equal(2, s.PmntA[1].Count);
        Assert.Equal(2, s.PmntA[0].MaxCount);
    }

    [Fact]
    public void FilterStatusNgOnly_DisplayTotalEqualsNgCount_NgRateCalculatedOverTotalScope()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: []),
            CreateUnit(index: 2, pmnt: "PMNT1001A", errors: ["XYLV"])
        };

        var s = _service.GetUnitStatistics(units, UnitStatusFilter.NgOnly);
        Assert.Equal(1, s.DisplayTotal);
        Assert.Equal(1, s.NgCount);
        Assert.Equal(50.0, s.NgRate);
    }

    [Fact]
    public void FilterStatusOkOnly_DoesNotCountNgDefects()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: []),
            CreateUnit(index: 2, pmnt: "PMNT1001A", errors: ["XYLV"])
        };

        var s = _service.GetUnitStatistics(units, UnitStatusFilter.OkOnly);
        Assert.Equal(1, s.DisplayTotal);
        Assert.Equal(1, s.OkCount);
        Assert.Equal(0, s.NgCount);
        Assert.Equal(0.0, s.NgRate);
        Assert.Empty(s.PmntA);
    }

    [Fact]
    public void TracksConsecutiveNgStreakPerPmnt_ResetsOnOk_FlagsAlarmPmntsAtThreshold()
    {
        var units = new List<UnitRecord>
        {
            // PMNT1001A: 2 NGs, then 1 OK (resets streak), then 1 NG -> Streak = 1, MaxStreak = 2, IsAlarm = false
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: ["XYLV"]),
            CreateUnit(index: 2, pmnt: "PMNT1001A", errors: ["XYLV"]),
            CreateUnit(index: 3, pmnt: "PMNT1001A", errors: []),
            CreateUnit(index: 4, pmnt: "PMNT1001A", errors: ["POWERON"]),
            // PMNT1002B: 3 consecutive NGs -> Streak = 3, MaxStreak = 3, IsAlarm = true
            CreateUnit(index: 5, pmnt: "PMNT1002B", errors: ["XYLV"]),
            CreateUnit(index: 6, pmnt: "PMNT1002B", errors: ["FLICKER"]),
            CreateUnit(index: 7, pmnt: "PMNT1002B", errors: ["POWEROFF"])
        };

        var s = _service.GetUnitStatistics(units, consecutiveNgThreshold: 3);
        Assert.Equal("PMNT1001A", s.PmntA[0].Name);
        Assert.Equal(3, s.PmntA[0].Count);
        Assert.Equal(1, s.PmntA[0].Streak);
        Assert.Equal(2, s.PmntA[0].MaxStreak);
        Assert.False(s.PmntA[0].IsAlarm);

        Assert.Equal("PMNT1002B", s.PmntB[0].Name);
        Assert.Equal(3, s.PmntB[0].Count);
        Assert.Equal(3, s.PmntB[0].Streak);
        Assert.Equal(3, s.PmntB[0].MaxStreak);
        Assert.True(s.PmntB[0].IsAlarm);

        Assert.Single(s.AlarmPmnts);
        Assert.Equal("PMNT1002B", s.AlarmPmnts[0].Name);
    }

    [Fact]
    public void ExtensionMethod_ComputeStatisticsProducesMatchingResult()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001A", errors: []),
            CreateUnit(index: 2, pmnt: "PMNT1001A", errors: ["XYLV"])
        };

        var s = units.ComputeStatistics(UnitStatusFilter.NgOnly);
        Assert.Equal(1, s.DisplayTotal);
        Assert.Equal(1, s.NgCount);
        Assert.Equal(50.0, s.NgRate);
    }

    [Fact]
    public void GetUnitStatistics_NegativeConsecutiveThreshold_ClampsAndTracksOtherPmnt()
    {
        var units = new List<UnitRecord>
        {
            CreateUnit(index: 1, pmnt: "PMNT1001C", errors: ["XYLV"])
        };

        var s = _service.GetUnitStatistics(units, consecutiveNgThreshold: -5);
        Assert.Equal(1, s.NgCount);
        Assert.Single(s.PmntOther);
        Assert.Equal("PMNT1001C", s.PmntOther[0].Name);
        Assert.True(s.PmntOther[0].IsAlarm);
    }
}
